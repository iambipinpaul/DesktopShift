using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace DesktopShift.Core.Observation;

/// <summary>
/// What the queue has done since the process started, and how full it is right
/// now.
/// </summary>
/// <remarks>
/// This is the whole of the queue's telemetry. It is read on demand rather than
/// pushed, so nothing is measured, timed, or written while events are flowing —
/// the cost of keeping these numbers is a handful of interlocked increments, and
/// the cost of not reading them is nothing at all.
/// </remarks>
/// <param name="Capacity">How many events may be waiting at once.</param>
/// <param name="Accepted">How many events have been taken for processing.</param>
/// <param name="Read">How many events the processor has drained.</param>
/// <param name="Dropped">
/// How many events were refused because the queue was full. A nonzero value
/// means windows went unassigned, so it is the number worth alarming on.
/// </param>
/// <param name="SaturationEpisodes">
/// How many separate runs of drops there have been, counting one contiguous run
/// as one episode.
/// </param>
/// <param name="CurrentDepth">How many events are waiting right now.</param>
public sealed record WindowEventQueueSnapshot(
    int Capacity,
    long Accepted,
    long Read,
    long Dropped,
    long SaturationEpisodes,
    int CurrentDepth)
{
    /// <summary>
    /// The reading for a host that has no queue at all.
    /// </summary>
    /// <remarks>
    /// A performance report from a host with no observation pipeline says zero
    /// rather than refusing to be built. Zero capacity is what distinguishes it
    /// from a real queue that has simply never been used.
    /// </remarks>
    public static WindowEventQueueSnapshot None { get; } = new(0, 0, 0, 0, 0, 0);
}

public interface IWindowEventQueue
{
    bool TryPublish(WindowEvent windowEvent);

    IAsyncEnumerable<WindowEvent> ReadAllAsync(
        CancellationToken cancellationToken = default);

    WindowEventQueueSnapshot Snapshot { get; }

    void Complete();
}

/// <summary>
/// The single handoff between the WinEvent callbacks Windows raises and the
/// processor that acts on them.
/// </summary>
/// <remarks>
/// <para>
/// <b>Saturation policy: the newest event is dropped, and the publisher never
/// waits.</b> When the queue is full, <see cref="TryPublish"/> refuses the
/// incoming event, records the refusal, and returns immediately. It does not
/// block, does not park a continuation, and does not evict anything already
/// queued.
/// </para>
/// <para>
/// Dropping the newest rather than the oldest is what keeps a burst readable.
/// The events already queued are the earlier ones in a sequence, and a window's
/// created / shown / activated events only make sense in order — discarding the
/// front of that sequence to make room for its tail would leave the processor
/// acting on the second half of a story it never read. Discarding the tail loses
/// the same amount of work and leaves what remains coherent.
/// </para>
/// <para>
/// Not waiting is not a tuning choice. <see cref="TryPublish"/> is called from
/// an unmanaged WinEvent callback on a thread Windows owns, and anything that
/// blocks there stalls the window it is reporting about, so a queue that filled
/// would hang the desktop rather than lose an event. The channel is created with
/// <see cref="BoundedChannelFullMode.Wait"/> only because the write is issued
/// through <see cref="ChannelWriter{T}.TryWrite"/>, which reports a full channel
/// instead of waiting on it; no code path here awaits a write.
/// </para>
/// <para>
/// The depth reservation exists so the refusal is decided before the write is
/// attempted, which makes <see cref="Snapshot"/> readable while events are in
/// flight: the depth a reader sees never exceeds the capacity, so a saturated
/// queue is distinguishable from a busy one.
/// </para>
/// <para>
/// Telemetry is counters and nothing else — no logging, no allocation, and no
/// call out of the queue on the drop path, all of which would put work back on
/// the native callback this type exists to keep free. <see cref="Snapshot"/> is
/// where they are read.
/// </para>
/// </remarks>
public sealed class BoundedWindowEventQueue : IWindowEventQueue
{
    /// <summary>
    /// How many events may wait at once before the queue starts refusing them.
    /// </summary>
    /// <remarks>
    /// Sized for the burst a session restore or an Explorer restart produces,
    /// which is the largest run of window events a normal desktop generates.
    /// Larger would not help: past this point the backlog is longer than the
    /// window handles in it stay valid, so the extra events would be processed
    /// against windows that had already closed.
    /// </remarks>
    public const int DefaultCapacity = 512;

    private readonly Channel<WindowEvent> channel;
    private readonly int capacity;
    private long accepted;
    private long read;
    private long dropped;
    private long saturationEpisodes;
    private int isSaturated;
    private int currentDepth;

    public BoundedWindowEventQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        this.capacity = capacity;
        channel = Channel.CreateBounded<WindowEvent>(
            new BoundedChannelOptions(capacity)
            {
                // The reader must never run on the publisher's thread. That
                // thread belongs to a WinEvent callback, and processing an event
                // there would do process, COM, and disk work inside the callback
                // this queue exists to keep empty.
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    /// <inheritdoc cref="WindowEventQueueSnapshot"/>
    public WindowEventQueueSnapshot Snapshot => new(
        capacity,
        Interlocked.Read(ref accepted),
        Interlocked.Read(ref read),
        Interlocked.Read(ref dropped),
        Interlocked.Read(ref saturationEpisodes),
        Volatile.Read(ref currentDepth));

    /// <summary>
    /// Offers an observed window event to the processor, refusing it rather than
    /// waiting when the queue is full.
    /// </summary>
    /// <remarks>
    /// Safe to call from an unmanaged callback: it never blocks, never allocates
    /// on the refusal path, and never throws for a full or completed queue.
    /// </remarks>
    /// <param name="windowEvent">The event to hand over.</param>
    /// <returns>
    /// Whether the event was queued. <c>false</c> means it was dropped under the
    /// saturation policy and no window will be assigned for it.
    /// </returns>
    public bool TryPublish(WindowEvent windowEvent)
    {
        if (!TryReserveDepth())
        {
            RecordDrop();
            return false;
        }

        if (!channel.Writer.TryWrite(windowEvent))
        {
            Interlocked.Decrement(ref currentDepth);
            RecordDrop();
            return false;
        }

        Interlocked.Increment(ref accepted);

        // Read before writing, so an unsaturated queue — which is every queue
        // almost all of the time — pays a read per event and nothing more.
        if (Volatile.Read(ref isSaturated) != 0)
        {
            Volatile.Write(ref isSaturated, 0);
        }

        return true;
    }

    public async IAsyncEnumerable<WindowEvent> ReadAllAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        await foreach (WindowEvent windowEvent in
            channel.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            Interlocked.Increment(ref read);
            Interlocked.Decrement(ref currentDepth);
            yield return windowEvent;
        }
    }

    public void Complete() => channel.Writer.TryComplete();

    /// <summary>
    /// Counts a refused event, and counts the run of refusals it belongs to.
    /// </summary>
    /// <remarks>
    /// A saturated queue refuses thousands of events in a moment, so the raw
    /// count says how much was lost but not how often losing happens. The
    /// episode count separates the two: one storm reads as one episode, and a
    /// queue that keeps filling reads as many.
    /// </remarks>
    private void RecordDrop()
    {
        Interlocked.Increment(ref dropped);
        if (Interlocked.Exchange(ref isSaturated, 1) == 0)
        {
            Interlocked.Increment(ref saturationEpisodes);
        }
    }

    private bool TryReserveDepth()
    {
        while (true)
        {
            int depth = Volatile.Read(ref currentDepth);
            if (depth >= capacity)
            {
                return false;
            }

            if (Interlocked.CompareExchange(
                ref currentDepth,
                depth + 1,
                depth) == depth)
            {
                return true;
            }
        }
    }
}

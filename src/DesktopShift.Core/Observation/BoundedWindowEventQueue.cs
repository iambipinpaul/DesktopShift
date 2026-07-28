using System.Runtime.CompilerServices;
using System.Threading.Channels;

namespace DesktopShift.Core.Observation;

public sealed record WindowEventQueueSnapshot(
    int Capacity,
    long Accepted,
    long Read,
    long Dropped,
    int CurrentDepth);

public interface IWindowEventQueue
{
    bool TryPublish(WindowEvent windowEvent);

    IAsyncEnumerable<WindowEvent> ReadAllAsync(
        CancellationToken cancellationToken = default);

    WindowEventQueueSnapshot Snapshot { get; }

    void Complete();
}

public sealed class BoundedWindowEventQueue : IWindowEventQueue
{
    public const int DefaultCapacity = 512;

    private readonly Channel<WindowEvent> channel;
    private readonly int capacity;
    private long accepted;
    private long read;
    private long dropped;
    private int currentDepth;

    public BoundedWindowEventQueue(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        this.capacity = capacity;
        channel = Channel.CreateBounded<WindowEvent>(
            new BoundedChannelOptions(capacity)
            {
                AllowSynchronousContinuations = false,
                FullMode = BoundedChannelFullMode.Wait,
                SingleReader = true,
                SingleWriter = false,
            });
    }

    public WindowEventQueueSnapshot Snapshot => new(
        capacity,
        Interlocked.Read(ref accepted),
        Interlocked.Read(ref read),
        Interlocked.Read(ref dropped),
        Volatile.Read(ref currentDepth));

    public bool TryPublish(WindowEvent windowEvent)
    {
        if (!TryReserveDepth())
        {
            Interlocked.Increment(ref dropped);
            return false;
        }

        if (!channel.Writer.TryWrite(windowEvent))
        {
            Interlocked.Decrement(ref currentDepth);
            Interlocked.Increment(ref dropped);
            return false;
        }

        Interlocked.Increment(ref accepted);
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

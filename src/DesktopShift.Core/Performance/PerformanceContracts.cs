using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Performance;

/// <summary>
/// How long one assignment took, split into the part DesktopShift is answerable
/// for and the part it chose to wait.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="EventToCompletion"/> is the honest wall-clock answer to "how long
/// after Windows told us about this window was it on the right desktop": it
/// starts at the moment the WinEvent callback received the event and ends when
/// the move and any switch have returned. It therefore includes the time the
/// event sat in the queue, which is the number a user would feel.
/// </para>
/// <para>
/// <see cref="DeferredByPolicy"/> is the part of that wall clock DesktopShift
/// spent deliberately doing nothing — the grace period a newly opened window
/// waits to see whether the user activates it. It is measured so the budget can
/// be applied to <see cref="PipelineLatency"/> instead. A budget that counted
/// the wait would be a budget on the follow policy, and the only way to pass it
/// would be to stop following windows.
/// </para>
/// </remarks>
/// <param name="Trigger">The window event kind the assignment answered.</param>
/// <param name="MoveOutcome">What happened to the window itself.</param>
/// <param name="EventToCompletion">
/// Event receipt through move completion, including any deliberate wait.
/// </param>
/// <param name="DeferredByPolicy">
/// How much of that was the open-window follow grace period, or zero when the
/// assignment ran without waiting.
/// </param>
/// <param name="AssignmentDuration">
/// How long the assignment itself took once it started, as the assignment
/// recorded it. Kept alongside the latency so a slow pass can be told apart from
/// a long queue.
/// </param>
public readonly record struct AssignmentLatencySample(
    WindowEventKind Trigger,
    WindowMoveOutcome MoveOutcome,
    TimeSpan EventToCompletion,
    TimeSpan DeferredByPolicy,
    TimeSpan AssignmentDuration)
{
    /// <summary>
    /// Event-to-move latency with the deliberate wait taken out. This is what
    /// the performance budget is measured against.
    /// </summary>
    public TimeSpan PipelineLatency =>
        EventToCompletion > DeferredByPolicy
            ? EventToCompletion - DeferredByPolicy
            : TimeSpan.Zero;

    /// <summary>
    /// Whether the window actually changed desktops, as opposed to the
    /// assignment completing and finding nothing to do.
    /// </summary>
    public bool Moved => MoveOutcome == WindowMoveOutcome.Succeeded;
}

/// <summary>
/// Where the pipeline reports its own timings.
/// </summary>
/// <remarks>
/// Every member is called from a path that is already doing real work — an
/// assignment that has just finished, a desktop that has just been created or
/// switched to — so implementations must do no I/O, take no long lock, and never
/// throw. Recording is a handful of interlocked writes; reading is a separate,
/// explicit act through <see cref="IPerformanceReportSource"/>.
/// </remarks>
public interface IPerformanceRecorder
{
    /// <summary>Records one completed assignment's timings.</summary>
    /// <param name="sample">What the assignment took.</param>
    void RecordAssignment(AssignmentLatencySample sample);

    /// <summary>
    /// Records how long Windows took to create one virtual desktop.
    /// </summary>
    /// <remarks>
    /// Kept apart from switching and from assignment latency because it is the
    /// one operation the startup path is supposed to have already paid for. A
    /// nonzero count outside startup is the signal that a desktop was created on
    /// a window's critical path.
    /// </remarks>
    /// <param name="duration">How long the creation call took.</param>
    void RecordDesktopCreation(TimeSpan duration);

    /// <summary>Records how long one desktop switch took.</summary>
    /// <param name="duration">
    /// How long reading the current desktop and switching away from it took
    /// together, which is what the user waits through.
    /// </param>
    void RecordDesktopSwitch(TimeSpan duration);
}

/// <summary>
/// Produces a point-in-time performance report on request.
/// </summary>
/// <remarks>
/// On request and only on request. Nothing here runs on a schedule, because a
/// recurring measurement loop would be exactly the idle cost the report exists
/// to show is absent.
/// </remarks>
public interface IPerformanceReportSource
{
    /// <summary>Reads every counter and renders the current report.</summary>
    /// <returns>What the process has done so far, against its budget.</returns>
    PerformanceReport CreateReport();
}

/// <summary>
/// What a series of durations looks like at the points worth reading.
/// </summary>
/// <param name="ObservedCount">
/// How many durations were recorded in total, including any the retention window
/// has since dropped.
/// </param>
/// <param name="SampleCount">How many are still retained and summarised here.</param>
/// <param name="P50">The median retained duration.</param>
/// <param name="P95">The duration 95 percent of retained samples came in under.</param>
/// <param name="P99">The duration 99 percent of retained samples came in under.</param>
/// <param name="Max">The slowest retained duration.</param>
/// <param name="Mean">The average retained duration.</param>
public sealed record LatencySummary(
    long ObservedCount,
    int SampleCount,
    TimeSpan P50,
    TimeSpan P95,
    TimeSpan P99,
    TimeSpan Max,
    TimeSpan Mean)
{
    /// <summary>A series nothing has been recorded into.</summary>
    public static LatencySummary Empty { get; } = new(
        0,
        0,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero,
        TimeSpan.Zero);

    /// <summary>Whether anything was measured at all.</summary>
    public bool HasSamples => SampleCount > 0;
}

/// <summary>
/// Keeps the most recent durations of one kind and answers percentile questions
/// about them.
/// </summary>
/// <remarks>
/// <para>
/// A ring buffer of fixed size, so a long-running session costs the same as a
/// short one and no measurement can grow without bound. Recording is a lock, an
/// array write, and two integer updates — there is no sorting, no allocation,
/// and no timing done on the recording path.
/// </para>
/// <para>
/// Percentiles are computed when somebody asks, by copying the retained samples
/// and sorting the copy. That puts the whole cost of the measurement on the act
/// of reading it, which happens when a user exports diagnostics or a benchmark
/// asserts a threshold — never while windows are moving.
/// </para>
/// <para>
/// Once the buffer has wrapped, the summary describes the most recent
/// <see cref="DefaultCapacity"/> samples rather than the whole session.
/// <see cref="LatencySummary.ObservedCount"/> is carried alongside so a reader
/// can tell which of the two they are looking at.
/// </para>
/// </remarks>
public sealed class LatencyDigest
{
    /// <summary>
    /// How many durations are retained before the oldest is overwritten.
    /// </summary>
    /// <remarks>
    /// Large enough that a percentile means something — 1024 samples put P99 on
    /// the tenth-slowest rather than on a single outlier — and small enough to be
    /// a few kilobytes that never grow.
    /// </remarks>
    public const int DefaultCapacity = 1024;

    private readonly object syncRoot = new();
    private readonly long[] ticks;
    private long observed;
    private long retainedTicks;
    private int count;
    private int next;

    public LatencyDigest(int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        ticks = new long[capacity];
    }

    /// <summary>How many durations are retained before the oldest is dropped.</summary>
    public int Capacity => ticks.Length;

    /// <summary>How many durations have been recorded in total.</summary>
    public long ObservedCount => Interlocked.Read(ref observed);

    /// <summary>
    /// Records one duration, overwriting the oldest retained one when full.
    /// </summary>
    /// <remarks>
    /// A negative duration is clamped to zero rather than refused. Durations
    /// arrive from <see cref="TimeProvider.GetElapsedTime(long)"/>, and a
    /// substituted clock that runs backwards must not be able to poison a
    /// percentile or throw from an assignment.
    /// </remarks>
    /// <param name="duration">The duration to keep.</param>
    public void Record(TimeSpan duration)
    {
        long value = Math.Max(0L, duration.Ticks);

        lock (syncRoot)
        {
            if (count == ticks.Length)
            {
                retainedTicks -= ticks[next];
            }
            else
            {
                count++;
            }

            ticks[next] = value;
            retainedTicks += value;
            next = (next + 1) % ticks.Length;
            observed++;
        }
    }

    /// <summary>Reads the retained durations and summarises them.</summary>
    /// <returns>The summary, or <see cref="LatencySummary.Empty"/> when nothing
    /// has been recorded.</returns>
    public LatencySummary Summarize()
    {
        long[] retained;
        int retainedCount;
        long totalTicks;
        long observedCount;

        lock (syncRoot)
        {
            if (count == 0)
            {
                return LatencySummary.Empty;
            }

            retainedCount = count;
            totalTicks = retainedTicks;
            observedCount = observed;
            retained = new long[retainedCount];
            Array.Copy(ticks, retained, retainedCount);
        }

        Array.Sort(retained);
        return new LatencySummary(
            observedCount,
            retainedCount,
            Percentile(retained, 0.50),
            Percentile(retained, 0.95),
            Percentile(retained, 0.99),
            TimeSpan.FromTicks(retained[^1]),
            TimeSpan.FromTicks(totalTicks / retainedCount));
    }

    /// <summary>
    /// Drops every retained duration and the total count with it.
    /// </summary>
    /// <remarks>
    /// For a benchmark that wants a clean series, and for the user's Clear
    /// action. Nothing in the running pipeline calls it.
    /// </remarks>
    public void Reset()
    {
        lock (syncRoot)
        {
            Array.Clear(ticks);
            observed = 0;
            retainedTicks = 0;
            count = 0;
            next = 0;
        }
    }

    /// <summary>
    /// Reads the sample at a percentile by nearest rank.
    /// </summary>
    /// <remarks>
    /// Nearest rank rather than interpolation, so every value reported is a
    /// duration something actually took. An interpolated P99 of a bimodal series
    /// is a number that never happened, which is a poor thing to hold a release
    /// against.
    /// </remarks>
    private static TimeSpan Percentile(long[] sorted, double percentile)
    {
        int rank = (int)Math.Ceiling(percentile * sorted.Length);
        return TimeSpan.FromTicks(sorted[Math.Clamp(rank - 1, 0, sorted.Length - 1)]);
    }
}

using System.Globalization;
using System.Text;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Performance;

/// <summary>
/// What the process has cost and how quickly it has answered, as of one moment.
/// </summary>
/// <remarks>
/// <para>
/// Assembled on request from counters the pipeline was already keeping. Nothing
/// in here is produced by a background sampler, which is why the idle figures it
/// reports are believable: the measurement is not part of the load.
/// </para>
/// <para>
/// Two latency series are carried rather than one, because they answer different
/// questions and only one of them is a fair budget.
/// <see cref="EventToMoveLatency"/> is the wall clock a user would feel, grace
/// period included. <see cref="AssignmentLatency"/> takes the deliberate wait out
/// and is the series the budget is applied to.
/// </para>
/// </remarks>
/// <param name="ObservedAtUtc">When the report was built.</param>
/// <param name="MeasuredOver">
/// How long the counters have been accumulating — since the process started, or
/// since a benchmark last reset the baseline.
/// </param>
/// <param name="EventToMoveLatency">
/// Event receipt through move completion, including the open-window follow grace
/// period.
/// </param>
/// <param name="AssignmentLatency">
/// The same series with the grace period taken out. This is what
/// <see cref="Verdict"/> judges.
/// </param>
/// <param name="DesktopCreation">How long creating a virtual desktop took.</param>
/// <param name="DesktopSwitch">How long switching the current desktop took.</param>
/// <param name="AssignmentsMoved">
/// How many of the measured assignments actually changed a window's desktop, as
/// opposed to completing and finding the window already in place.
/// </param>
/// <param name="Queue">
/// What the event queue has done, including the drop count that says whether any
/// window went unassigned.
/// </param>
/// <param name="Coalescing">
/// How much redundant work event coalescing removed.
/// </param>
/// <param name="IdleCpuPercent">
/// The share of the whole machine the process used across
/// <paramref name="MeasuredOver"/>. On a session that was not doing anything,
/// this is the idle cost.
/// </param>
/// <param name="HasCpuMeasurement">
/// Whether the interval was long enough for the processor share to mean anything.
/// </param>
/// <param name="WorkingSetBytes">Physical memory held at the moment of reading.</param>
/// <param name="PrivateMemoryBytes">
/// Committed memory not shared with anything else, at the moment of reading.
/// </param>
/// <param name="Verdict">
/// Whether the budget was met, and the release blockers when it was not.
/// </param>
public sealed record PerformanceReport(
    DateTimeOffset ObservedAtUtc,
    TimeSpan MeasuredOver,
    LatencySummary EventToMoveLatency,
    LatencySummary AssignmentLatency,
    LatencySummary DesktopCreation,
    LatencySummary DesktopSwitch,
    long AssignmentsMoved,
    WindowEventQueueSnapshot Queue,
    WindowCoalescingSnapshot Coalescing,
    double IdleCpuPercent,
    bool HasCpuMeasurement,
    long WorkingSetBytes,
    long PrivateMemoryBytes,
    PerformanceBudgetVerdict Verdict)
{
    /// <summary>How many assignments were measured.</summary>
    public long AssignmentsMeasured => AssignmentLatency.ObservedCount;

    /// <summary>
    /// How many observed events were refused because the queue was full. Any
    /// number above zero means a window was never assigned.
    /// </summary>
    public long QueueDrops => Queue.Dropped;

    /// <summary>
    /// The share of coalescing-eligible events that were recognised as redundant
    /// and skipped, between 0 and 1.
    /// </summary>
    public double CoalescingRate => Coalescing.CoalescingRate;

    /// <summary>
    /// How many virtual desktops were created across the measured interval.
    /// </summary>
    /// <remarks>
    /// Read against the interval to answer one specific question: whether
    /// startup reconciliation really did pre-create the configured destinations.
    /// A creation counted during ordinary window activity means one was made on a
    /// window's critical path instead.
    /// </remarks>
    public long DesktopsCreated => DesktopCreation.ObservedCount;

    /// <summary>
    /// The report as a few lines of plain text, for a bug report or a release
    /// note.
    /// </summary>
    /// <returns>The figures and the verdict, one per line.</returns>
    public string ToPlainText()
    {
        StringBuilder text = new();
        _ = text.AppendLine(string.Create(
            Invariant,
            $"Measured over {Describe(MeasuredOver)}, ending {ObservedAtUtc:u}."));
        _ = text.AppendLine(string.Create(
            Invariant,
            $"Assignments measured: {AssignmentsMeasured}, of which {AssignmentsMoved} moved a window."));
        AppendLatency(text, "Event-to-move latency", EventToMoveLatency);
        AppendLatency(text, "Assignment latency (budgeted)", AssignmentLatency);
        AppendLatency(text, "Desktop creation", DesktopCreation);
        AppendLatency(text, "Desktop switch", DesktopSwitch);
        _ = text.AppendLine(string.Create(
            Invariant,
            $"Queue: {Queue.Accepted} accepted, {Queue.Dropped} dropped over {Queue.SaturationEpisodes} saturation episodes, capacity {Queue.Capacity}."));
        _ = text.AppendLine(string.Create(
            Invariant,
            $"Coalescing: {Coalescing.Coalesced} of {Coalescing.Evaluated} eligible events skipped ({CoalescingRate * 100d:0.#}%)."));
        _ = text.AppendLine(
            HasCpuMeasurement
                ? string.Create(
                    Invariant,
                    $"Idle processor use: {IdleCpuPercent:0.###}% of the machine.")
                : "Idle processor use: not measurable over this interval.");
        _ = text.AppendLine(string.Create(
            Invariant,
            $"Memory: {WorkingSetBytes / 1024d / 1024d:0.#} MB working set, {PrivateMemoryBytes / 1024d / 1024d:0.#} MB private."));
        _ = text.AppendLine(Verdict.Summary);

        foreach (PerformanceBudgetBreach breach in Verdict.Breaches)
        {
            _ = text.AppendLine($"  Release blocker — {breach}");
        }

        return text.ToString();
    }

    private static void AppendLatency(
        StringBuilder text,
        string name,
        LatencySummary summary)
    {
        _ = text.AppendLine(
            summary.HasSamples
                ? string.Create(
                    Invariant,
                    $"{name}: P50 {Describe(summary.P50)}, P95 {Describe(summary.P95)}, P99 {Describe(summary.P99)}, max {Describe(summary.Max)} over {summary.SampleCount} of {summary.ObservedCount} samples.")
                : $"{name}: nothing measured.");
    }

    private static string Describe(TimeSpan value) =>
        string.Create(Invariant, $"{value.TotalMilliseconds:0.###} ms");

    /// <summary>
    /// Every figure is formatted invariantly, so a report reads the same
    /// whichever machine produced it and a maintainer never has to guess whether
    /// a comma was a decimal point.
    /// </summary>
    private static CultureInfo Invariant => CultureInfo.InvariantCulture;
}

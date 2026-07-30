using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Performance;

/// <summary>
/// The one place the pipeline's timings are collected, and the one place a
/// report is built from them.
/// </summary>
/// <remarks>
/// <para>
/// Recording costs a lock over a small array and an increment. Nothing here
/// logs, allocates per sample, writes to disk, or calls out of the process, so an
/// instrumented assignment does the same work an uninstrumented one did plus a
/// few nanoseconds of bookkeeping.
/// </para>
/// <para>
/// Nothing here runs on its own either. There is no sampling timer and no
/// background thread — <see cref="CreateReport"/> is the only thing that reads a
/// counter, and it runs when a user exports diagnostics or a benchmark asks. That
/// is not a detail: an idle-cost report produced by a recurring measurement would
/// be reporting its own overhead.
/// </para>
/// <para>
/// The queue and the coalescer are optional. A host that wired the observation
/// pipeline hands them over and their figures appear; a host that did not — the
/// settings-only test hosts, for instance — still gets a report, with those
/// sections empty rather than a failure to resolve the service.
/// </para>
/// </remarks>
public sealed class PerformanceMonitor :
    IPerformanceRecorder,
    IPerformanceReportSource
{
    /// <summary>
    /// The shortest interval a processor-share figure is reported over.
    /// </summary>
    /// <remarks>
    /// Windows accounts processor time in scheduler ticks of about 16
    /// milliseconds. Over a shorter interval than this, a single tick landing
    /// inside or outside the window swings the answer by more than the one
    /// percent the budget is about, so a short interval reports "not measurable"
    /// instead of a number nobody should act on.
    /// </remarks>
    public static readonly TimeSpan MinimumCpuInterval = TimeSpan.FromSeconds(1);

    private readonly object syncRoot = new();
    private readonly LatencyDigest eventToMoveLatency;
    private readonly LatencyDigest assignmentLatency;
    private readonly LatencyDigest desktopCreation;
    private readonly LatencyDigest desktopSwitch;
    private readonly IProcessResourceSampler resourceSampler;
    private readonly IWindowEventQueue? eventQueue;
    private readonly IWindowCoalescingMetrics? coalescingMetrics;
    private ProcessResourceSample baseline;
    private long assignmentsMoved;

    /// <param name="timeProvider">The clock the report is stamped from.</param>
    /// <param name="resourceSampler">
    /// How the process's own processor and memory use is read, or null to read
    /// the running process.
    /// </param>
    /// <param name="eventQueue">
    /// The queue whose drop and saturation counters the report carries, or null
    /// in a host with no observation pipeline.
    /// </param>
    /// <param name="coalescingMetrics">
    /// The coalescer whose skip counters the report carries, or null in a host
    /// with no observation pipeline.
    /// </param>
    /// <param name="budget">The figures to judge the report against.</param>
    /// <param name="digestCapacity">How many samples each series retains.</param>
    public PerformanceMonitor(
        TimeProvider timeProvider,
        IProcessResourceSampler? resourceSampler = null,
        IWindowEventQueue? eventQueue = null,
        IWindowCoalescingMetrics? coalescingMetrics = null,
        PerformanceBudget? budget = null,
        int digestCapacity = LatencyDigest.DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);

        // The clock is not kept. Everything this type stamps comes from the
        // sampler's own reading, so the report's interval and its processor
        // figure can never be read from two different clocks.
        this.resourceSampler =
            resourceSampler ?? new CurrentProcessResourceSampler(timeProvider);
        this.eventQueue = eventQueue;
        this.coalescingMetrics = coalescingMetrics;
        Budget = budget ?? PerformanceBudget.NormalLocal;
        eventToMoveLatency = new LatencyDigest(digestCapacity);
        assignmentLatency = new LatencyDigest(digestCapacity);
        desktopCreation = new LatencyDigest(digestCapacity);
        desktopSwitch = new LatencyDigest(digestCapacity);
        baseline = this.resourceSampler.Capture();
    }

    /// <summary>The figures every report from this monitor is judged against.</summary>
    public PerformanceBudget Budget { get; }

    public void RecordAssignment(AssignmentLatencySample sample)
    {
        eventToMoveLatency.Record(sample.EventToCompletion);
        assignmentLatency.Record(sample.PipelineLatency);
        if (sample.Moved)
        {
            _ = Interlocked.Increment(ref assignmentsMoved);
        }
    }

    public void RecordDesktopCreation(TimeSpan duration) =>
        desktopCreation.Record(duration);

    public void RecordDesktopSwitch(TimeSpan duration) =>
        desktopSwitch.Record(duration);

    public PerformanceReport CreateReport()
    {
        ProcessResourceSample from;
        lock (syncRoot)
        {
            from = baseline;
        }

        ProcessResourceSample now = resourceSampler.Capture();
        TimeSpan measuredOver = now.CapturedAtUtc - from.CapturedAtUtc;
        double idleCpuPercent = ProcessResourceUsage.ComputeCpuPercent(
            from,
            now,
            resourceSampler.ProcessorCount);
        bool hasCpuMeasurement = measuredOver >= MinimumCpuInterval;
        LatencySummary assignment = assignmentLatency.Summarize();

        return new PerformanceReport(
            now.CapturedAtUtc,
            measuredOver,
            eventToMoveLatency.Summarize(),
            assignment,
            desktopCreation.Summarize(),
            desktopSwitch.Summarize(),
            Interlocked.Read(ref assignmentsMoved),
            eventQueue?.Snapshot ?? WindowEventQueueSnapshot.None,
            coalescingMetrics?.Snapshot ?? WindowCoalescingSnapshot.None,
            idleCpuPercent,
            hasCpuMeasurement,
            now.WorkingSetBytes,
            now.PrivateMemoryBytes,
            PerformanceBudgetVerdict.Evaluate(
                assignment,
                idleCpuPercent,
                hasCpuMeasurement,
                Budget));
    }

    /// <summary>
    /// Drops every timing this monitor holds and starts the measured interval
    /// again from now.
    /// </summary>
    /// <remarks>
    /// For a benchmark that wants one clean run, and for the user's Clear
    /// action. The queue and coalescer counters are not touched — they belong to
    /// those types and cover the whole process run, which is what a reader of a
    /// drop count wants.
    /// </remarks>
    public void Reset()
    {
        eventToMoveLatency.Reset();
        assignmentLatency.Reset();
        desktopCreation.Reset();
        desktopSwitch.Reset();
        _ = Interlocked.Exchange(ref assignmentsMoved, 0);

        ProcessResourceSample fresh = resourceSampler.Capture();
        lock (syncRoot)
        {
            baseline = fresh;
        }
    }
}

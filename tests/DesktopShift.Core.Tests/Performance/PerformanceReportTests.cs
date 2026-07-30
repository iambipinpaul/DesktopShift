using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// Everything the acceptance asks a performance report to carry, in one report.
/// </summary>
/// <remarks>
/// The list is not arbitrary: percentiles say how it feels, queue drops say
/// whether anything was silently lost, the coalescing rate says how much work was
/// avoided, and the idle figures say what leaving it running costs. A report
/// missing any one of them cannot answer "should this ship".
/// </remarks>
[TestClass]
public sealed class PerformanceReportTests
{
    [TestMethod]
    public void AReport_CarriesPercentilesDropsCoalescingAndIdleCost()
    {
        StubProcessResourceSampler sampler = new() { ProcessorCount = 8 };
        sampler.EnqueueInterval(
            TimeSpan.FromMinutes(10),
            cpuPercent: 0.4,
            workingSetBytes: 64L * 1024 * 1024,
            privateMemoryBytes: 40L * 1024 * 1024);

        BoundedWindowEventQueue queue = new(capacity: 2);
        PerformanceMonitor monitor = new(
            TimeProvider.System,
            sampler,
            queue,
            new StubCoalescingMetrics(evaluated: 200, coalesced: 50));

        // Three events offered to a queue of two, so exactly one is refused.
        Assert.IsTrue(queue.TryPublish(Event(1)));
        Assert.IsTrue(queue.TryPublish(Event(2)));
        Assert.IsFalse(queue.TryPublish(Event(3)));

        for (int index = 1; index <= 100; index++)
        {
            monitor.RecordAssignment(
                new AssignmentLatencySample(
                    WindowEventKind.Shown,
                    index % 4 == 0
                        ? WindowMoveOutcome.AlreadyCorrect
                        : WindowMoveOutcome.Succeeded,
                    TimeSpan.FromMilliseconds(index + 150),
                    TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromMilliseconds(index)));
        }

        monitor.RecordDesktopCreation(TimeSpan.FromMilliseconds(90));
        monitor.RecordDesktopSwitch(TimeSpan.FromMilliseconds(20));
        monitor.RecordDesktopSwitch(TimeSpan.FromMilliseconds(40));

        PerformanceReport report = monitor.CreateReport();

        // The percentiles the acceptance names, on the budgeted series.
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), report.AssignmentLatency.P50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(95), report.AssignmentLatency.P95);
        Assert.AreEqual(TimeSpan.FromMilliseconds(99), report.AssignmentLatency.P99);

        // And the wall-clock series alongside it, so the deliberate wait is
        // visible rather than hidden.
        Assert.AreEqual(TimeSpan.FromMilliseconds(200), report.EventToMoveLatency.P50);

        Assert.AreEqual(100L, report.AssignmentsMeasured);
        Assert.AreEqual(75L, report.AssignmentsMoved);

        Assert.AreEqual(1L, report.QueueDrops);
        Assert.AreEqual(1L, report.Queue.SaturationEpisodes);
        Assert.AreEqual(2, report.Queue.Capacity);

        Assert.AreEqual(0.25d, report.CoalescingRate);
        Assert.AreEqual(200L, report.Coalescing.Evaluated);

        Assert.AreEqual(1L, report.DesktopsCreated);
        Assert.AreEqual(TimeSpan.FromMilliseconds(90), report.DesktopCreation.P50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(40), report.DesktopSwitch.P99);

        Assert.IsTrue(report.HasCpuMeasurement);
        Assert.AreEqual(0.4d, report.IdleCpuPercent, 0.0001d);
        Assert.AreEqual(64L * 1024 * 1024, report.WorkingSetBytes);
        Assert.AreEqual(40L * 1024 * 1024, report.PrivateMemoryBytes);
        Assert.AreEqual(TimeSpan.FromMinutes(10), report.MeasuredOver);

        // This series is deliberately slower than the budget at the median and
        // inside it everywhere else, so the report has to name exactly one
        // blocker rather than pass or fail as a whole.
        Assert.IsTrue(report.Verdict.IsTested);
        Assert.IsTrue(report.Verdict.IsReleaseBlocked);
        Assert.HasCount(1, report.Verdict.Breaches);
        Assert.AreEqual("Assignment latency P50", report.Verdict.Breaches[0].Metric);
    }

    [TestMethod]
    public void TheBudgetIsJudgedOnTheSeriesNetOfTheDeliberateWait()
    {
        // The whole point of keeping two series. Every sample here is 170 ms of
        // wall clock, 150 of which is the follow grace period. Judging the wall
        // clock would block a release over the policy working as designed; judging
        // the 20 ms DesktopShift is answerable for passes comfortably.
        StubProcessResourceSampler sampler = new();
        sampler.EnqueueInterval(TimeSpan.FromMinutes(1), cpuPercent: 0d);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        for (int index = 0; index < 50; index++)
        {
            monitor.RecordAssignment(
                new AssignmentLatencySample(
                    WindowEventKind.Shown,
                    WindowMoveOutcome.Succeeded,
                    TimeSpan.FromMilliseconds(170),
                    TimeSpan.FromMilliseconds(150),
                    TimeSpan.FromMilliseconds(20)));
        }

        PerformanceReport report = monitor.CreateReport();

        Assert.AreEqual(TimeSpan.FromMilliseconds(170), report.EventToMoveLatency.P99);
        Assert.AreEqual(TimeSpan.FromMilliseconds(20), report.AssignmentLatency.P99);

        // The wall clock would have missed every latency figure. The budgeted
        // series misses none.
        Assert.IsGreaterThan(
            PerformanceBudget.NormalLocal.AssignmentLatencyP99,
            report.EventToMoveLatency.P99);
        Assert.IsTrue(report.Verdict.IsMet);
    }

    [TestMethod]
    public void AMissedFigure_ReachesTheReportAsAReleaseBlocker()
    {
        StubProcessResourceSampler sampler = new();
        sampler.EnqueueInterval(TimeSpan.FromMinutes(5), cpuPercent: 4.2);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        for (int index = 0; index < 50; index++)
        {
            monitor.RecordAssignment(
                new AssignmentLatencySample(
                    WindowEventKind.ForegroundActivated,
                    WindowMoveOutcome.Succeeded,
                    TimeSpan.FromMilliseconds(400),
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(400)));
        }

        PerformanceReport report = monitor.CreateReport();

        Assert.IsTrue(report.Verdict.IsReleaseBlocked);
        Assert.HasCount(4, report.Verdict.Breaches);

        string text = report.ToPlainText();
        Assert.Contains("Release blocker", text, StringComparison.Ordinal);
        Assert.Contains("Assignment latency P99", text, StringComparison.Ordinal);
        Assert.Contains("Idle processor use", text, StringComparison.Ordinal);
    }

    [TestMethod]
    public void AHostWithNoObservationPipeline_StillProducesAReport()
    {
        // The settings-only test hosts have no queue and no coalescer. A report
        // that could not be built there would make the monitor impossible to
        // register with the foundation, and the instrumented types would each
        // have to guard for its absence.
        StubProcessResourceSampler sampler = new();
        sampler.EnqueueInterval(TimeSpan.FromSeconds(30), cpuPercent: 0d);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        PerformanceReport report = monitor.CreateReport();

        Assert.AreEqual(0, report.Queue.Capacity);
        Assert.AreEqual(0L, report.QueueDrops);
        Assert.AreEqual(0d, report.CoalescingRate);
        Assert.IsFalse(report.AssignmentLatency.HasSamples);
        Assert.IsFalse(report.Verdict.IsTested);
        Assert.IsFalse(report.Verdict.IsReleaseBlocked);
        Assert.IsNotEmpty(report.ToPlainText());
    }

    [TestMethod]
    public void AnIntervalTooShortToMeasure_ReportsNoProcessorFigure()
    {
        StubProcessResourceSampler sampler = new();
        sampler.EnqueueInterval(TimeSpan.FromMilliseconds(30), cpuPercent: 80d);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        PerformanceReport report = monitor.CreateReport();

        Assert.IsFalse(report.HasCpuMeasurement);
        Assert.IsEmpty(report.Verdict.Breaches);
        Assert.Contains("not measurable", report.ToPlainText(), StringComparison.Ordinal);
    }

    [TestMethod]
    public void CreatingAReport_ReadsTheCountersAndNothingElse()
    {
        // Two readings per report and no more: the stored baseline and one fresh
        // capture. Anything that sampled on a schedule would show up here as a
        // capture count that grows without a report being asked for.
        StubProcessResourceSampler sampler = new();
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        Assert.AreEqual(1, sampler.CaptureCount);

        _ = monitor.CreateReport();
        Assert.AreEqual(2, sampler.CaptureCount);

        _ = monitor.CreateReport();
        Assert.AreEqual(3, sampler.CaptureCount);
    }

    [TestMethod]
    public void Reset_StartsTheMeasuredIntervalAgainWithoutTouchingTheQueue()
    {
        // A benchmark wants one clean latency series. A drop count, though,
        // belongs to the whole process run — a reader asking "did anything get
        // lost" means ever, not since the last reset.
        StubProcessResourceSampler sampler = new();
        BoundedWindowEventQueue queue = new(capacity: 1);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler, queue);

        Assert.IsTrue(queue.TryPublish(Event(1)));
        Assert.IsFalse(queue.TryPublish(Event(2)));
        monitor.RecordAssignment(
            new AssignmentLatencySample(
                WindowEventKind.Shown,
                WindowMoveOutcome.Succeeded,
                TimeSpan.FromMilliseconds(9),
                TimeSpan.Zero,
                TimeSpan.FromMilliseconds(9)));
        monitor.RecordDesktopCreation(TimeSpan.FromMilliseconds(80));

        monitor.Reset();
        PerformanceReport report = monitor.CreateReport();

        Assert.AreEqual(0L, report.AssignmentsMeasured);
        Assert.AreEqual(0L, report.AssignmentsMoved);
        Assert.AreEqual(0L, report.DesktopsCreated);
        Assert.AreEqual(1L, report.QueueDrops);
    }

    [TestMethod]
    public void TheMonitorUsesTheDocumentedBudgetUnlessToldOtherwise()
    {
        PerformanceMonitor monitor = new(
            TimeProvider.System,
            new StubProcessResourceSampler());

        Assert.AreSame(PerformanceBudget.NormalLocal, monitor.Budget);
    }

    private static WindowEvent Event(long sequence) =>
        new(sequence, WindowEventKind.Shown, (nint)sequence, DateTimeOffset.UnixEpoch);
}

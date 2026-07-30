using System.Diagnostics;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// The repeatable benchmark for the documented normal-local workload.
/// </summary>
/// <remarks>
/// <para>
/// This is the one place in the suite that measures real elapsed time, so it is
/// also the one place that can fail for reasons unrelated to DesktopShift. The
/// workload runs on a real clock through the real bounded queue, the real
/// coalescer, the real processor, and the real assignment service; only the calls
/// that would reach Windows are substituted, and those are made to return
/// immediately rather than to pretend to be slow.
/// </para>
/// <para>
/// What it therefore measures is DesktopShift's own overhead per window: the
/// queue handoff, the coalescing decision, classification, identity resolution,
/// rule matching, the per-window gate, and the two activity sinks. On a machine
/// that can measure, that overhead has to fit inside the budget with room to
/// spare, because a real move and a real switch will spend most of it.
/// </para>
/// <para>
/// On a machine that cannot measure reliably — no high-resolution timer, or a
/// loaded shared runner where the scheduler cannot honour a short sleep — the
/// timing assertions report inconclusive instead of failing. Absence of a usable
/// clock is not evidence of a regression. The structural assertions run either
/// way.
/// </para>
/// </remarks>
[TestClass]
public sealed class AssignmentLatencyBenchmarkTests
{
    /// <summary>How many windows the documented workload drives.</summary>
    /// <remarks>
    /// Comfortably past <see cref="PerformanceBudget.MinimumSampleCount"/>, so
    /// the run is judged rather than reported as untested, and enough for a P99 to
    /// sit on more than one sample.
    /// </remarks>
    private const int WorkloadWindowCount = 300;

    /// <summary>Windows driven before measurement starts.</summary>
    /// <remarks>
    /// The first few assignments pay for JIT compilation of the whole pipeline and
    /// for the first allocations in every bounded store. Those costs are real but
    /// they are paid once per process, not once per window, and leaving them in
    /// would make the P99 a measure of startup.
    /// </remarks>
    private const int WarmupWindowCount = 50;

    [TestMethod]
    public async Task TheNormalLocalWorkload_MeetsItsBudgetOrRecordsABlocker()
    {
        BenchmarkHarness harness = new();

        await harness.RunAsync(WarmupWindowCount);
        harness.Monitor.Reset();
        await harness.RunAsync(WorkloadWindowCount);

        PerformanceReport report = harness.Monitor.CreateReport();

        // Structural: the run has to have actually happened, and every window has
        // to have been measured and moved. A benchmark that quietly assigned
        // nothing would otherwise report a perfect P99.
        Assert.AreEqual((long)WorkloadWindowCount, report.AssignmentsMeasured);
        Assert.AreEqual((long)WorkloadWindowCount, report.AssignmentsMoved);
        Assert.AreEqual(0L, report.QueueDrops);
        Assert.AreEqual(0L, report.DesktopsCreated);
        Assert.IsTrue(report.Verdict.IsTested);

        if (!CanMeasureReliably())
        {
            Assert.Inconclusive(
                "This machine cannot measure short intervals reliably, so the " +
                "latency thresholds were not asserted. The report reads: " +
                report.ToPlainText());
            return;
        }

        Assert.IsFalse(
            report.Verdict.IsReleaseBlocked,
            "The normal-local workload missed its performance budget:" +
            Environment.NewLine +
            report.ToPlainText());

        // Asserted again as absolute thresholds rather than only through the
        // verdict, so a verdict that stopped evaluating anything could not read
        // as a pass.
        Assert.IsLessThan(
            PerformanceBudget.NormalLocal.AssignmentLatencyP50,
            report.AssignmentLatency.P50);
        Assert.IsLessThan(
            PerformanceBudget.NormalLocal.AssignmentLatencyP95,
            report.AssignmentLatency.P95);
        Assert.IsLessThan(
            PerformanceBudget.NormalLocal.AssignmentLatencyP99,
            report.AssignmentLatency.P99);
    }

    [TestMethod]
    public async Task TheSameWorkloadTwice_ProducesComparableFigures()
    {
        // Repeatability is what makes a threshold a regression gate rather than a
        // coin toss. Two runs in the same process, on the same machine, with the
        // same workload have to land in the same neighbourhood — otherwise a
        // future failure says nothing about the change that provoked it.
        BenchmarkHarness harness = new();

        await harness.RunAsync(WarmupWindowCount);
        harness.Monitor.Reset();
        await harness.RunAsync(WorkloadWindowCount);
        LatencySummary first = harness.Monitor.CreateReport().AssignmentLatency;

        harness.Monitor.Reset();
        await harness.RunAsync(WorkloadWindowCount);
        LatencySummary second = harness.Monitor.CreateReport().AssignmentLatency;

        Assert.AreEqual(WorkloadWindowCount, first.SampleCount);
        Assert.AreEqual(WorkloadWindowCount, second.SampleCount);

        if (!CanMeasureReliably())
        {
            Assert.Inconclusive(
                "This machine cannot measure short intervals reliably, so the " +
                "two runs were not compared.");
            return;
        }

        // A generous factor on purpose. The claim being defended is "the second
        // run is not an order of magnitude worse than the first", which catches a
        // pipeline that degrades as its bounded stores fill without failing over
        // ordinary scheduler noise.
        Assert.IsLessThan(
            first.P95 * 8 + TimeSpan.FromMilliseconds(10),
            second.P95);
    }

    [TestMethod]
    public async Task TheWorkloadRecordsNoCreationAndNoDrop()
    {
        // The two findings that make a latency figure meaningless. A creation
        // means one window paid for a desktop; a drop means one window was never
        // assigned at all, and no percentile can show that.
        BenchmarkHarness harness = new();

        await harness.RunAsync(WorkloadWindowCount);
        PerformanceReport report = harness.Monitor.CreateReport();

        Assert.AreEqual(0L, report.DesktopCreation.ObservedCount);
        Assert.AreEqual(0L, report.Queue.Dropped);
        Assert.AreEqual(0L, report.Queue.SaturationEpisodes);
    }

    [TestMethod]
    public async Task RepeatedEventsForOneWindow_AreCoalescedAndReported()
    {
        // The coalescing rate is one of the figures the report has to carry, and a
        // rate nothing exercises is a rate nobody can trust. A window shown twice
        // inside the coalescing window is the ordinary case that produces one.
        BenchmarkHarness harness = new(coalescingWindow: TimeSpan.FromSeconds(30));

        for (int index = 0; index < 20; index++)
        {
            _ = await harness.ProcessAsync(WindowEventKind.Shown, 4242);
        }

        PerformanceReport report = harness.Monitor.CreateReport();

        Assert.AreEqual(20L, report.Coalescing.Evaluated);
        Assert.AreEqual(19L, report.Coalescing.Coalesced);
        Assert.AreEqual(0.95d, report.CoalescingRate, 0.0001d);

        // Only the first event did any work, which is the whole point of
        // coalescing and the reason the rate is worth reporting.
        Assert.AreEqual(1L, report.AssignmentsMeasured);
    }

    /// <summary>
    /// Whether this machine's clock and scheduler can support a sub-millisecond
    /// claim.
    /// </summary>
    /// <remarks>
    /// Two questions, both about the environment rather than about DesktopShift:
    /// whether the platform has a high-resolution timer at all, and whether a
    /// short wait actually comes back when asked. A shared runner under load fails
    /// the second by a wide margin, and every latency figure taken on it is noise.
    /// </remarks>
    private static bool CanMeasureReliably()
    {
        if (!Stopwatch.IsHighResolution)
        {
            return false;
        }

        // Five short waits, allowed up to eight times their nominal length in
        // total. A machine that cannot honour 50 ms of sleep inside 400 ms is not
        // one whose 30 ms percentile means anything.
        long started = Stopwatch.GetTimestamp();
        for (int index = 0; index < 5; index++)
        {
            Thread.Sleep(10);
        }

        return Stopwatch.GetElapsedTime(started) < TimeSpan.FromMilliseconds(400);
    }

    /// <summary>
    /// The documented normal-local workload, on a real clock.
    /// </summary>
    /// <remarks>
    /// Every window is a distinct handle claimed by one rule that names its
    /// process, arriving as an opening event with the follow grace period switched
    /// off — which is what the documented workload describes once the wait has
    /// been accounted for separately. Nothing is deliberately slowed down, so what
    /// is left in the measurement is DesktopShift's own work.
    /// </remarks>
    private sealed class BenchmarkHarness
    {
        private readonly WindowObservationProcessor processor;
        private long sequence;

        public BenchmarkHarness(TimeSpan? coalescingWindow = null)
        {
            Queue = new BoundedWindowEventQueue();
            Coalescer = new WindowEventCoalescer(
                coalescingWindow ?? TimeSpan.Zero,
                new InertCoalescingScheduler());
            Monitor = new PerformanceMonitor(
                TimeProvider.System,
                new StubProcessResourceSampler(),
                Queue,
                Coalescer);

            WindowAssignmentService assignmentService = new(
                new InstantPlacementService(),
                new BoundMappingService(),
                new BoundedWindowAssignmentActivityStore(),
                TimeProvider.System,
                switchCoordinator: null,
                suppression: new BoundedForegroundSwitchSuppression(
                    TimeProvider.System),
                activationTracker: new BoundedNewWindowActivationTracker());

            processor = new WindowObservationProcessor(
                new AnyWindowClassifier(),
                new OneApplicationIdentityResolver(),
                new OneRuleSource(),
                new NullWindowObservationActivitySink(),
                coalescer: Coalescer,
                assignmentService: assignmentService,
                followGrace: new OpenWindowFollowGrace(TimeSpan.Zero),
                performanceRecorder: Monitor,
                timeProvider: TimeProvider.System);
        }

        public BoundedWindowEventQueue Queue { get; }

        public WindowEventCoalescer Coalescer { get; }

        public PerformanceMonitor Monitor { get; }

        /// <summary>
        /// Drives one window through the real queue and the real processor.
        /// </summary>
        /// <remarks>
        /// The event goes through <see cref="BoundedWindowEventQueue"/> rather than
        /// straight into the processor, so the queue handoff and the receipt
        /// stamping are inside the measurement exactly as they are in the running
        /// application.
        /// </remarks>
        public async ValueTask<WindowObservationActivity> ProcessAsync(
            WindowEventKind kind,
            nint windowHandle)
        {
            WindowEvent windowEvent = new(
                Interlocked.Increment(ref sequence),
                kind,
                windowHandle,
                TimeProvider.System.GetUtcNow(),
                TimeProvider.System.GetTimestamp());

            Assert.IsTrue(
                Queue.TryPublish(windowEvent),
                "The benchmark queue filled, so the workload is not the " +
                "documented one.");

            await foreach (WindowEvent dequeued in Queue.ReadAllAsync())
            {
                return await processor.ProcessAsync(dequeued);
            }

            throw new InvalidOperationException("The queue returned nothing.");
        }

        /// <summary>Drives a run of distinct windows, one at a time.</summary>
        public async Task RunAsync(int windowCount)
        {
            for (int index = 1; index <= windowCount; index++)
            {
                _ = await ProcessAsync(WindowEventKind.Shown, index);
            }
        }

        /// <summary>
        /// Schedules nothing, because the benchmark never waits for a coalescing
        /// entry to expire.
        /// </summary>
        /// <remarks>
        /// The real scheduler would put a <see cref="Timer"/> on the thread pool
        /// per event. Three hundred of those would measure the thread pool rather
        /// than the pipeline, and none of them would ever fire inside the run.
        /// </remarks>
        private sealed class InertCoalescingScheduler : IWindowCoalescingScheduler
        {
            public IDisposable Schedule(TimeSpan dueTime, Action callback) =>
                NullLease.Instance;

            private sealed class NullLease : IDisposable
            {
                public static NullLease Instance { get; } = new();

                public void Dispose()
                {
                }
            }
        }

        private sealed class InstantPlacementService : IWindowDesktopPlacementService
        {
            private static readonly Guid Elsewhere =
                Guid.Parse("99999999-9999-9999-9999-999999999999");

            public ValueTask<DesktopTopologyProviderResult<Guid>>
                GetWindowDesktopIdAsync(
                    nint windowHandle,
                    CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(
                    DesktopTopologyProviderResult<Guid>.Succeeded(Elsewhere));

            public ValueTask<DesktopTopologyProviderResult>
                MoveWindowToDesktopAsync(
                    nint windowHandle,
                    Guid desktopId,
                    CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }

        private sealed class BoundMappingService :
            IManagedDesktopReconciliationService
        {
            public ManagedDesktopReconciliationSnapshot Current { get; } = new(
                DateTimeOffset.UnixEpoch,
                ManagedDesktopReconciliationTrigger.Startup,
                "test.full",
                DesktopTopologyProviderMode.Full,
                ManagedDesktopReconciliationOutcome.Succeeded,
                [
                    new ManagedDesktopRuntimeMapping(
                        "work",
                        "Work",
                        1,
                        true,
                        Guid.Parse("22222222-2222-2222-2222-222222222222"),
                        "Work",
                        0,
                        ManagedDesktopMappingStatus.ReusedPersistedBinding,
                        [],
                        "test.bound",
                        "Bound"),
                ],
                []);

            public event EventHandler<ManagedDesktopReconciliationChangedEventArgs>?
                Changed
            {
                add { }
                remove { }
            }

            public Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
                ManagedDesktopReconciliationTrigger trigger,
                CancellationToken cancellationToken = default) =>
                Task.FromResult(Current);
        }

        private sealed class AnyWindowClassifier : IWindowClassifier
        {
            public WindowQualification Qualify(nint windowHandle) =>
                WindowQualification.Qualified(
                    new QualifiedWindow(
                        windowHandle,
                        windowHandle,
                        checked((uint)(long)windowHandle),
                        "Chrome_WidgetWin_1"));

            public WindowSkipReason ClassifyIdentity(
                QualifiedWindow window,
                WindowIdentity identity) =>
                WindowSkipReason.None;
        }

        private sealed class OneApplicationIdentityResolver :
            IWindowIdentityResolver
        {
            public ValueTask<WindowIdentityResolution> ResolveAsync(
                QualifiedWindow window,
                CancellationToken cancellationToken = default) =>
                ValueTask.FromResult(
                    WindowIdentityResolution.Succeeded(
                        new WindowIdentity(
                            window.ProcessId,
                            "Measured.exe",
                            ExecutablePath: null,
                            PackageFamilyName: null,
                            AppUserModelId: null,
                            "Chrome_WidgetWin_1",
                            WindowTitle: null,
                            CommandLine: null)));
        }

        private sealed class OneRuleSource : IWindowRuleSource
        {
            private static readonly WindowObservationRule[] Rules =
            [
                new WindowObservationRule(
                    "measured-app",
                    "Measured App",
                    IsEnabled: true,
                    "work",
                    [
                        ApplicationRuleTrigger.WindowCreated,
                        ApplicationRuleTrigger.WindowShown,
                    ],
                    DesktopSwitchPolicy.Never,
                    WindowMatchCriteria.ForProcessNames(["Measured.exe"]),
                    Order: 1),
            ];

            public IReadOnlyList<WindowObservationRule> GetRules() => Rules;
        }
    }
}

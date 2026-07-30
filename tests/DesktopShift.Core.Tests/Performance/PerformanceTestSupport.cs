using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// A clock the test advances by hand, on both of the faces the pipeline reads.
/// </summary>
/// <remarks>
/// <para>
/// Latency measurement uses <see cref="TimeProvider.GetTimestamp"/>, not the wall
/// clock, so a test that only substituted <see cref="GetUtcNow"/> would still be
/// measuring the real machine and would assert against however long the test
/// process happened to take. Both faces move together here, and only when
/// <see cref="Advance"/> says so.
/// </para>
/// <para>
/// The timestamp frequency is one tick, so a timestamp unit and a
/// <see cref="TimeSpan"/> tick are the same thing and an advance of 40
/// milliseconds is exactly 40 milliseconds of measured latency.
/// </para>
/// </remarks>
internal sealed class SteppingTimeProvider(DateTimeOffset startUtc) : TimeProvider
{
    /// <summary>
    /// Where the monotonic face starts, and deliberately not zero.
    /// </summary>
    /// <remarks>
    /// A zero <see cref="WindowEvent.ReceivedTimestamp"/> is how an event says it
    /// was never stamped. A clock that began at zero would make every stamped
    /// event in a test look unstamped, and the queue wait would silently vanish
    /// from the measurement being asserted.
    /// </remarks>
    private const long Origin = TimeSpan.TicksPerHour;

    private long ticks = Origin;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow() =>
        startUtc + TimeSpan.FromTicks(Interlocked.Read(ref ticks) - Origin);

    public override long GetTimestamp() => Interlocked.Read(ref ticks);

    /// <summary>Moves both faces of the clock forward.</summary>
    /// <param name="amount">How far to move.</param>
    public void Advance(TimeSpan amount) =>
        Interlocked.Add(ref ticks, amount.Ticks);
}

/// <summary>Keeps every timing the pipeline reported.</summary>
internal sealed class CapturingPerformanceRecorder : IPerformanceRecorder
{
    private readonly object syncRoot = new();
    private readonly List<AssignmentLatencySample> assignments = [];
    private readonly List<TimeSpan> creations = [];
    private readonly List<TimeSpan> switches = [];

    public IReadOnlyList<AssignmentLatencySample> Assignments
    {
        get
        {
            lock (syncRoot)
            {
                return [.. assignments];
            }
        }
    }

    public IReadOnlyList<TimeSpan> Creations
    {
        get
        {
            lock (syncRoot)
            {
                return [.. creations];
            }
        }
    }

    public IReadOnlyList<TimeSpan> Switches
    {
        get
        {
            lock (syncRoot)
            {
                return [.. switches];
            }
        }
    }

    public void RecordAssignment(AssignmentLatencySample sample)
    {
        lock (syncRoot)
        {
            assignments.Add(sample);
        }
    }

    public void RecordDesktopCreation(TimeSpan duration)
    {
        lock (syncRoot)
        {
            creations.Add(duration);
        }
    }

    public void RecordDesktopSwitch(TimeSpan duration)
    {
        lock (syncRoot)
        {
            switches.Add(duration);
        }
    }
}

/// <summary>Returns readings the test dictates.</summary>
/// <remarks>
/// Processor and memory figures come from the real process, which no test can
/// hold still. Substituting the sampler is what lets a test assert that one
/// percent is treated as a pass and two as a release blocker, rather than
/// asserting whatever the build agent was doing.
/// </remarks>
internal sealed class StubProcessResourceSampler : IProcessResourceSampler
{
    private readonly Queue<ProcessResourceSample> readings = [];

    public int ProcessorCount { get; set; } = 4;

    public int CaptureCount { get; private set; }

    public void Enqueue(ProcessResourceSample sample) => readings.Enqueue(sample);

    /// <summary>
    /// Queues a baseline and a later reading that together describe a given
    /// processor share.
    /// </summary>
    /// <param name="over">How long the interval is.</param>
    /// <param name="cpuPercent">The share of the machine to describe.</param>
    /// <param name="workingSetBytes">Physical memory to report at the end.</param>
    /// <param name="privateMemoryBytes">Private memory to report at the end.</param>
    public void EnqueueInterval(
        TimeSpan over,
        double cpuPercent,
        long workingSetBytes = 48L * 1024 * 1024,
        long privateMemoryBytes = 32L * 1024 * 1024)
    {
        DateTimeOffset start = new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);
        TimeSpan processorTime = TimeSpan.FromMilliseconds(
            over.TotalMilliseconds * ProcessorCount * cpuPercent / 100d);

        Enqueue(new ProcessResourceSample(start, TimeSpan.Zero, 1, 1));
        Enqueue(
            new ProcessResourceSample(
                start + over,
                processorTime,
                workingSetBytes,
                privateMemoryBytes));
    }

    public ProcessResourceSample Capture()
    {
        CaptureCount++;
        return readings.Count > 0
            ? readings.Dequeue()
            : new ProcessResourceSample(
                new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero),
                TimeSpan.Zero,
                1,
                1);
    }
}

/// <summary>Reports a coalescing reading the test dictates.</summary>
internal sealed class StubCoalescingMetrics(long evaluated, long coalesced) :
    IWindowCoalescingMetrics
{
    public WindowCoalescingSnapshot Snapshot { get; } =
        new(evaluated, coalesced);
}

/// <summary>
/// Drives the real observation processor and the real assignment service against
/// a clock the test controls, so a latency assertion is arithmetic rather than a
/// race with the machine.
/// </summary>
/// <remarks>
/// Everything below the processor is a fake, but nothing about the measurement
/// path is: the event carries a receipt timestamp the way the WinEvent source
/// stamps it, the processor holds an opened window the way the follow policy
/// holds it, and the assignment service moves and switches through the same
/// calls. What the fakes control is how long each of those appears to take.
/// </remarks>
internal sealed class LatencyPipelineHarness
{
    public const string ManagedKey = "ide-development";

    public static readonly Guid ManagedDesktopId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    public static readonly Guid OtherDesktopId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    private readonly WindowObservationProcessor processor;
    private long sequence;

    /// <param name="gracePeriod">
    /// The follow grace period, or <see cref="TimeSpan.Zero"/> to assign an
    /// opened window inline.
    /// </param>
    public LatencyPipelineHarness(TimeSpan? gracePeriod = null)
    {
        Clock = new SteppingTimeProvider(
            new DateTimeOffset(2026, 7, 30, 9, 0, 0, TimeSpan.Zero));
        Placement = new SlowPlacementService(Clock);
        Topology = new SlowTopologyProvider(Clock);
        Suppression = new BoundedForegroundSwitchSuppression(Clock);

        WindowAssignmentService assignmentService = new(
            Placement,
            new BoundReconciliationService(Clock.GetUtcNow()),
            new BoundedWindowAssignmentActivityStore(),
            Clock,
            new DesktopSwitchCoordinator(
                Topology,
                Suppression,
                Clock,
                Recorder),
            Suppression,
            ActivationTracker);

        processor = new WindowObservationProcessor(
            new AcceptAllClassifier(),
            new FixedIdentityResolver(),
            new SingleRuleSource(),
            Sink,
            assignmentService: assignmentService,
            activationTracker: ActivationTracker,
            switchSuppression: Suppression,
            followGrace: new OpenWindowFollowGrace(
                gracePeriod ?? TimeSpan.Zero,
                FollowScheduler),
            performanceRecorder: Recorder,
            timeProvider: Clock);
    }

    public SteppingTimeProvider Clock { get; }

    public CapturingPerformanceRecorder Recorder { get; } = new();

    public SlowPlacementService Placement { get; }

    public SlowTopologyProvider Topology { get; }

    public BoundedForegroundSwitchSuppression Suppression { get; }

    public BoundedNewWindowActivationTracker ActivationTracker { get; } = new();

    public ManualOpenWindowFollowScheduler FollowScheduler { get; } = new();

    public CapturingWindowObservationActivitySink Sink { get; } = new();

    /// <summary>
    /// Publishes one window event, stamped with the receipt timestamp the
    /// WinEvent source would have stamped it with.
    /// </summary>
    /// <param name="kind">What happened to the window.</param>
    /// <param name="windowHandle">The window it happened to.</param>
    /// <param name="stamped">
    /// Whether to carry a monotonic receipt reading. False stands for the
    /// synthetic events a startup pass or a manual reassignment raises, which
    /// were never in the queue.
    /// </param>
    public ValueTask<WindowObservationActivity> ObserveAsync(
        WindowEventKind kind,
        nint windowHandle,
        bool stamped = true) =>
        ObserveAsync(Stamp(kind, windowHandle, stamped));

    public ValueTask<WindowObservationActivity> ObserveAsync(
        WindowEvent windowEvent) =>
        processor.ProcessAsync(windowEvent);

    /// <summary>
    /// Builds a window event stamped at the clock's current reading, without
    /// publishing it.
    /// </summary>
    /// <remarks>
    /// For the tests whose subject is the queue wait: the event is stamped, the
    /// clock is advanced to stand for the wait, and only then is it processed.
    /// Building and publishing in one step would leave nothing for the wait to
    /// happen between.
    /// </remarks>
    public WindowEvent Stamp(
        WindowEventKind kind,
        nint windowHandle,
        bool stamped = true) =>
        new(
            Interlocked.Increment(ref sequence),
            kind,
            windowHandle,
            Clock.GetUtcNow(),
            stamped ? Clock.GetTimestamp() : 0);

    /// <summary>
    /// Publishes one window event and lets any hold it started run to
    /// completion.
    /// </summary>
    public ValueTask<WindowObservationActivity> ProcessAsync(
        WindowEventKind kind,
        nint windowHandle,
        bool stamped = true) =>
        ProcessAsync(Stamp(kind, windowHandle, stamped));

    /// <inheritdoc cref="ProcessAsync(WindowEventKind, nint, bool)"/>
    public async ValueTask<WindowObservationActivity> ProcessAsync(
        WindowEvent windowEvent)
    {
        WindowObservationActivity observation = await ObserveAsync(windowEvent);
        _ = await FollowScheduler.ReleaseAllAsync();
        return Sink.Last ?? observation;
    }

    /// <summary>
    /// Moves a window, taking as long as the test says a move takes.
    /// </summary>
    internal sealed class SlowPlacementService(SteppingTimeProvider clock) :
        IWindowDesktopPlacementService
    {
        private readonly Dictionary<nint, Guid> currentDesktopIds = [];

        public TimeSpan QueryCost { get; set; } = TimeSpan.Zero;

        public TimeSpan MoveCost { get; set; } = TimeSpan.Zero;

        public List<(nint WindowHandle, Guid DesktopId)> Moves { get; } = [];

        public void SetCurrent(nint windowHandle, Guid desktopId) =>
            currentDesktopIds[windowHandle] = desktopId;

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default)
        {
            clock.Advance(QueryCost);
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    currentDesktopIds.TryGetValue(windowHandle, out Guid desktopId)
                        ? desktopId
                        : OtherDesktopId));
        }

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            clock.Advance(MoveCost);
            Moves.Add((windowHandle, desktopId));
            currentDesktopIds[windowHandle] = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    /// <summary>
    /// Creates and switches desktops, taking as long as the test says each takes.
    /// </summary>
    internal sealed class SlowTopologyProvider(SteppingTimeProvider clock) :
        IDesktopTopologyProvider
    {
        private int nextCreated;

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.full",
            "Test",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: true);

        public VirtualDesktopCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, true);

        public List<VirtualDesktopDescriptor> Desktops { get; } =
        [
            new VirtualDesktopDescriptor(ManagedDesktopId, "IDE Development", 0, false),
            new VirtualDesktopDescriptor(OtherDesktopId, "Desktop 2", 1, true),
        ];

        public Guid CurrentDesktopId { get; set; } = OtherDesktopId;

        public TimeSpan CreateCost { get; set; } = TimeSpan.Zero;

        public TimeSpan SwitchCost { get; set; } = TimeSpan.Zero;

        public int CreateCallCount { get; private set; }

        public int SwitchCallCount { get; private set; }

        public bool CanCreate { get; set; } = true;

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<
            IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<
                    IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                        Desktops.ToArray()));

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetCurrentDesktopIdAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(CurrentDesktopId));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            clock.Advance(CreateCost);
            if (!CanCreate)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult<Guid>.Unsupported(
                        "test.unsupported",
                        "This provider cannot create desktops."));
            }

            nextCreated++;
            Guid created = new($"44444444-4444-4444-4444-{nextCreated:D12}");
            Desktops.Add(
                new VirtualDesktopDescriptor(
                    created,
                    DisplayName: null,
                    Desktops.Count,
                    IsCurrent: false));
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(created));
        }

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            SwitchCallCount++;
            clock.Advance(SwitchCost);
            CurrentDesktopId = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult>
            StartTopologyNotificationsAsync(
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    private sealed class SingleRuleSource : IWindowRuleSource
    {
        private static readonly WindowObservationRule Rule = new(
            "measured-app",
            "Measured App",
            IsEnabled: true,
            ManagedKey,
            [
                ApplicationRuleTrigger.WindowCreated,
                ApplicationRuleTrigger.WindowShown,
                ApplicationRuleTrigger.ForegroundActivated,
                ApplicationRuleTrigger.StartupReconciliation,
                ApplicationRuleTrigger.ManualReassignment,
            ],
            DesktopSwitchPolicy.OnNewWindowActivation,
            WindowMatchCriteria.ForProcessNames(["Measured.exe"]),
            Order: 1);

        public IReadOnlyList<WindowObservationRule> GetRules() => [Rule];
    }

    private sealed class AcceptAllClassifier : IWindowClassifier
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

    private sealed class FixedIdentityResolver : IWindowIdentityResolver
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

    private sealed class BoundReconciliationService(DateTimeOffset observedAt) :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            observedAt,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [
                new ManagedDesktopRuntimeMapping(
                    ManagedKey,
                    "IDE Development",
                    1,
                    true,
                    ManagedDesktopId,
                    "IDE Development",
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
}

/// <summary>
/// A managed-desktop definition set, for the reconciliation timing tests.
/// </summary>
internal static class PerformanceDefinitions
{
    public static ImmutableArray<ManagedDesktopDefinition> Create(
        params string[] semanticKeys) =>
        [
            .. semanticKeys.Select(
                static (key, index) => new ManagedDesktopDefinition(
                    key,
                    key,
                    index + 1,
                    RecreateWhenMissing: true)),
        ];
}

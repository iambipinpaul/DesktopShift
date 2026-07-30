using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Assignments;

[TestClass]
public sealed class ForegroundDesktopSwitchingAcceptanceTests
{
    private static readonly Guid TargetDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task BackgroundEventsAndNeverPolicy_MoveWithoutSwitching()
    {
        SwitchingHarness background = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        background.Placement.SetCurrent(101, OtherDesktopId);

        WindowObservationActivity created = await background.ProcessAsync(
            WindowEventKind.Created,
            101,
            Now);

        Assert.AreEqual(WindowMoveOutcome.Succeeded, created.Assignment!.MoveOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.NotRequested,
            created.Assignment.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
            created.Assignment.SwitchDecisionReason);
        Assert.AreEqual(0, background.Topology.SwitchCallCount);

        SwitchingHarness never = new(DesktopSwitchPolicy.Never);
        never.Placement.SetCurrent(102, OtherDesktopId);
        WindowObservationActivity foreground = await never.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            102,
            Now);

        Assert.AreEqual(WindowMoveOutcome.Succeeded, foreground.Assignment!.MoveOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PolicyNever,
            foreground.Assignment.SwitchDecisionReason);
        Assert.AreEqual(0, never.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task OnForegroundActivation_MovesAndSwitchesWhenBothAreIncorrect()
    {
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        harness.Placement.SetCurrent(201, OtherDesktopId);
        harness.Topology.CurrentDesktopId = OtherDesktopId;

        WindowObservationActivity activity = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            201,
            Now);

        Assert.AreEqual(WindowMoveOutcome.Succeeded, activity.Assignment!.MoveOutcome);
        Assert.AreEqual(DesktopSwitchOutcome.Succeeded, activity.Assignment.SwitchOutcome);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, activity.Assignment.Outcome);
        Assert.AreEqual(1, harness.Topology.SwitchCallCount);
        Assert.AreEqual(TargetDesktopId, harness.Topology.CurrentDesktopId);
    }

    [TestMethod]
    public async Task LaunchedWindowActivatedWhileHeld_IsMovedOnceAndFollowed()
    {
        // Launching an application from another desktop. The window is shown
        // here first and takes the foreground a moment later, and the desktop
        // has to end up where the window did.
        //
        // Moving it the instant it was shown is what used to break this: a
        // window that is no longer on this desktop cannot take the foreground
        // on it, so the activation never arrived and the user was left behind.
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        harness.Placement.SetCurrent(210, OtherDesktopId);
        harness.Topology.CurrentDesktopId = OtherDesktopId;

        await harness.ObserveAsync(WindowEventKind.Shown, 210, Now);

        // Still here, deliberately. The move is waiting to see what happens.
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.AreEqual(1, harness.FollowScheduler.PendingCount);

        WindowObservationActivity activated = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            210,
            Now);

        Assert.AreEqual(
            DesktopSwitchOutcome.Succeeded,
            activated.Assignment!.SwitchOutcome);
        Assert.AreEqual(TargetDesktopId, harness.Topology.CurrentDesktopId);

        // Placed once. The activation took the held move over rather than
        // racing it.
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual(0, harness.FollowScheduler.PendingCount);
    }

    [TestMethod]
    public async Task OpenedWindowNothingActivates_IsMovedWithoutFollowing()
    {
        // The window that appears without being asked for. It is still placed,
        // because placement is total, but the user is not taken anywhere.
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        harness.Placement.SetCurrent(211, OtherDesktopId);
        harness.Topology.CurrentDesktopId = OtherDesktopId;

        await harness.ObserveAsync(WindowEventKind.Shown, 211, Now);
        Assert.IsEmpty(harness.Placement.Moves);

        Assert.AreEqual(1, await harness.FollowScheduler.ReleaseAllAsync());
        WindowAssignmentActivity assignment = harness.Sink.Last!.Assignment!;

        Assert.AreEqual(WindowMoveOutcome.Succeeded, assignment.MoveOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.NotRequested,
            assignment.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
            assignment.SwitchDecisionReason);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
        Assert.AreEqual(OtherDesktopId, harness.Topology.CurrentDesktopId);
    }

    [TestMethod]
    public async Task WindowClosedWhileHeld_IsNeverMoved()
    {
        // A window that came and went inside its grace period. There is nothing
        // left to place, and the handle may already belong to something else.
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        harness.Placement.SetCurrent(212, OtherDesktopId);

        await harness.ObserveAsync(WindowEventKind.Shown, 212, Now);
        await harness.ObserveAsync(WindowEventKind.Destroyed, 212, Now);

        Assert.AreEqual(0, await harness.FollowScheduler.ReleaseAllAsync());
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task CurrentTargetDesktop_IsIdempotentAndDuplicateEventIsCoalesced()
    {
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        harness.Placement.SetCurrent(211, TargetDesktopId);
        harness.Topology.CurrentDesktopId = TargetDesktopId;

        WindowObservationActivity first = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            211,
            Now);
        WindowObservationActivity duplicate = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            211,
            Now.AddMilliseconds(20));

        Assert.AreEqual(WindowMoveOutcome.AlreadyCorrect, first.Assignment!.MoveOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.CurrentDesktopAlreadyTarget,
            first.Assignment.SwitchDecisionReason);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
        Assert.AreEqual(WindowObservationOutcome.Skipped, duplicate.Outcome);
        Assert.AreEqual(WindowSkipReason.Coalesced, duplicate.SkipReason);
        Assert.IsNull(duplicate.Assignment);
    }

    [TestMethod]
    public async Task OnNewWindowActivation_SwitchesOnlyOnFirstGenuineActivationAfterCreateOrShow()
    {
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnNewWindowActivation);
        harness.Placement.SetCurrent(301, OtherDesktopId);
        harness.Topology.CurrentDesktopId = OtherDesktopId;

        WindowObservationActivity created = await harness.ProcessAsync(
            WindowEventKind.Created,
            301,
            Now);
        WindowObservationActivity firstForeground = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            301,
            Now.AddMilliseconds(100));
        WindowObservationActivity selfGenerated = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            301,
            Now.AddMilliseconds(101));

        Assert.AreEqual(
            DesktopSwitchOutcome.NotRequested,
            created.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.Succeeded,
            firstForeground.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.Suppressed,
            selfGenerated.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            firstForeground.Assignment.CorrelationId,
            selfGenerated.Assignment.RelatedCorrelationId);
        Assert.AreEqual(
            WindowAssignmentSkipReason.SelfGeneratedForegroundSuppressed,
            selfGenerated.Assignment.SkipReason);

        // A later Show does not re-arm an HWND whose first activation was
        // already consumed.
        _ = await harness.ProcessAsync(
            WindowEventKind.Shown,
            301,
            Now.AddMilliseconds(150));
        harness.Topology.CurrentDesktopId = OtherDesktopId;
        WindowObservationActivity laterGenuine = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            301,
            Now.AddMilliseconds(250));

        Assert.AreEqual(
            DesktopSwitchOutcome.NotRequested,
            laterGenuine.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PolicyOnNewWindowNotEligible,
            laterGenuine.Assignment.SwitchDecisionReason);
        Assert.AreEqual(1, harness.Topology.SwitchCallCount);

        SwitchingHarness standalone = new(
            DesktopSwitchPolicy.OnNewWindowActivation);
        standalone.Placement.SetCurrent(302, OtherDesktopId);
        WindowObservationActivity standaloneForeground =
            await standalone.ProcessAsync(
                WindowEventKind.ForegroundActivated,
                302,
                Now);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PolicyOnNewWindowNotEligible,
            standaloneForeground.Assignment!.SwitchDecisionReason);
        Assert.AreEqual(0, standalone.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task SelfGeneratedSuppression_IsOneShotAndDoesNotHideLaterActivation()
    {
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation);
        harness.Placement.SetCurrent(401, TargetDesktopId);
        harness.Topology.CurrentDesktopId = OtherDesktopId;

        WindowObservationActivity original = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            401,
            Now);
        WindowObservationActivity suppressed = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            401,
            Now.AddMilliseconds(1));
        harness.Topology.CurrentDesktopId = OtherDesktopId;
        WindowObservationActivity later = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            401,
            Now.AddMilliseconds(100));

        Assert.AreEqual(DesktopSwitchOutcome.Succeeded, original.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.Suppressed,
            suppressed.Assignment!.SwitchOutcome);
        Assert.AreEqual(DesktopSwitchOutcome.Succeeded, later.Assignment!.SwitchOutcome);
        Assert.AreEqual(2, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public void SelfGeneratedSuppression_ExpiresWithoutPolling()
    {
        MutableTimeProvider timeProvider = new(Now);
        BoundedForegroundSwitchSuppression suppression = new(timeProvider);
        Guid correlationId = Guid.NewGuid();

        suppression.Register((nint)451, correlationId);
        Assert.IsTrue(suppression.HasPending((nint)451));

        timeProvider.Advance(
            BoundedForegroundSwitchSuppression.DefaultLifetime +
            TimeSpan.FromMilliseconds(1));

        Assert.IsFalse(suppression.HasPending((nint)451));
        Assert.IsFalse(
            suppression.TryConsume((nint)451, out Guid relatedCorrelationId));
        Assert.AreEqual(Guid.Empty, relatedCorrelationId);
    }

    [TestMethod]
    public async Task DestroyedWindow_ClearsOnNewActivationEligibility()
    {
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnNewWindowActivation);
        harness.Placement.SetCurrent(461, OtherDesktopId);
        _ = await harness.ProcessAsync(
            WindowEventKind.Shown,
            461,
            Now);
        _ = await harness.ProcessAsync(
            WindowEventKind.Destroyed,
            461,
            Now.AddMilliseconds(10));
        harness.Topology.CurrentDesktopId = OtherDesktopId;

        WindowObservationActivity later = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            461,
            Now.AddMilliseconds(100));

        Assert.AreEqual(
            DesktopSwitchDecisionReason.PolicyOnNewWindowNotEligible,
            later.Assignment!.SwitchDecisionReason);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task LimitedMode_ReportsCapabilityWithoutAttemptingSwitch()
    {
        SwitchingHarness harness = new(
            DesktopSwitchPolicy.OnForegroundActivation,
            canSwitch: false);
        harness.Placement.SetCurrent(501, TargetDesktopId);

        WindowObservationActivity activity = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            501,
            Now);

        Assert.AreEqual(DesktopSwitchOutcome.Limited, activity.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.CapabilityUnavailable,
            activity.Assignment.SwitchDecisionReason);
        Assert.AreEqual(
            "assignment.switch_capability_unavailable",
            activity.Assignment.Error!.Code);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task ConcurrentApprovedSwitches_AreSerialized()
    {
        FakeTopologyProvider topology = new(canSwitch: true)
        {
            CurrentDesktopId = OtherDesktopId,
            SwitchDelay = TimeSpan.FromMilliseconds(40),
        };
        BoundedForegroundSwitchSuppression suppression =
            new(TimeProvider.System);
        DesktopSwitchCoordinator coordinator = new(
            topology,
            suppression,
            TimeProvider.System);

        Task<DesktopSwitchResult> first = coordinator.ApplyAsync(
            new DesktopSwitchRequest(
                Guid.NewGuid(),
                WindowEventKind.ForegroundActivated,
                (nint)601,
                Guid.NewGuid(),
                DesktopSwitchPolicy.OnForegroundActivation,
                IsFirstForegroundActivation: false)).AsTask();
        Task<DesktopSwitchResult> second = coordinator.ApplyAsync(
            new DesktopSwitchRequest(
                Guid.NewGuid(),
                WindowEventKind.ForegroundActivated,
                (nint)602,
                Guid.NewGuid(),
                DesktopSwitchPolicy.OnForegroundActivation,
                IsFirstForegroundActivation: false)).AsTask();

        DesktopSwitchResult[] results = await Task.WhenAll(first, second);

        Assert.IsTrue(results.All(
            static result => result.Outcome == DesktopSwitchOutcome.Succeeded));
        Assert.AreEqual(1, topology.MaxConcurrentSwitches);
        Assert.AreEqual(2, topology.SwitchCallCount);
    }

    private sealed class SwitchingHarness
    {
        private long sequence;

        public SwitchingHarness(
            DesktopSwitchPolicy policy,
            bool canSwitch = true)
        {
            Topology = new FakeTopologyProvider(canSwitch)
            {
                CurrentDesktopId = OtherDesktopId,
            };
            Placement = new FakePlacementService();
            BoundedForegroundSwitchSuppression suppression =
                new(TimeProvider.System);
            BoundedNewWindowActivationTracker activationTracker = new();
            BoundedWindowAssignmentActivityStore assignmentStore = new();
            WindowAssignmentService assignmentService = new(
                Placement,
                new ReadyReconciliationService(),
                assignmentStore,
                TimeProvider.System,
                new DesktopSwitchCoordinator(
                    Topology,
                    suppression,
                    TimeProvider.System),
                suppression,
                activationTracker);
            WindowObservationRule rule = new(
                "vscode",
                "Visual Studio Code",
                IsEnabled: true,
                "code",
                [
                    ApplicationRuleTrigger.WindowCreated,
                    ApplicationRuleTrigger.WindowShown,
                    ApplicationRuleTrigger.ForegroundActivated,
                ],
                policy,
                WindowMatchCriteria.ForProcessNames(["Code.exe"]),
                Order: 0);
            Processor = new WindowObservationProcessor(
                new AcceptAllClassifier(),
                new CodeIdentityResolver(),
                new FixedRuleSource(rule),
                Sink,
                assignmentService: assignmentService,
                activationTracker: activationTracker,
                switchSuppression: suppression,
                followGrace: new OpenWindowFollowGrace(
                    scheduler: FollowScheduler));
        }

        public FakeTopologyProvider Topology { get; }

        public FakePlacementService Placement { get; }

        public ManualOpenWindowFollowScheduler FollowScheduler { get; } = new();

        public CapturingWindowObservationActivitySink Sink { get; } = new();

        private WindowObservationProcessor Processor { get; }

        /// <summary>
        /// Observes one window event and settles whatever it started, so an
        /// opened window that nothing activates ends up placed.
        /// </summary>
        public async ValueTask<WindowObservationActivity> ProcessAsync(
            WindowEventKind kind,
            long handle,
            DateTimeOffset observedAt)
        {
            WindowObservationActivity observation = await ObserveAsync(
                kind,
                handle,
                observedAt);
            await FollowScheduler.ReleaseAllAsync();
            return Sink.Last ?? observation;
        }

        /// <summary>
        /// Observes one window event and leaves any hold it started standing,
        /// for tests where the next event is the point.
        /// </summary>
        public ValueTask<WindowObservationActivity> ObserveAsync(
            WindowEventKind kind,
            long handle,
            DateTimeOffset observedAt) =>
            Processor.ProcessAsync(
                new WindowEvent(
                    Interlocked.Increment(ref sequence),
                    kind,
                    (nint)handle,
                    observedAt));
    }

    private sealed class ReadyReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            Now,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "code",
                "Code",
                1,
                true,
                TargetDesktopId,
                "Code",
                0,
                ManagedDesktopMappingStatus.ReusedPersistedBinding,
                [],
                "test.bound",
                "Bound")],
            []);

        public event EventHandler<ManagedDesktopReconciliationChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
            ManagedDesktopReconciliationTrigger trigger,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }

    private sealed class FakePlacementService : IWindowDesktopPlacementService
    {
        private readonly Dictionary<nint, Guid> current = [];

        public List<(nint WindowHandle, Guid DesktopId)> Moves { get; } = [];

        public void SetCurrent(nint windowHandle, Guid desktopId) =>
            current[windowHandle] = desktopId;

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    current.TryGetValue(windowHandle, out Guid desktopId)
                        ? desktopId
                        : OtherDesktopId));
        }

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Moves.Add((windowHandle, desktopId));
            current[windowHandle] = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    private sealed class FakeTopologyProvider(bool canSwitch) :
        IDesktopTopologyProvider
    {
        private int concurrentSwitches;

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            canSwitch ? "test.full" : "test.limited",
            "Test",
            "1",
            canSwitch
                ? DesktopTopologyProviderMode.Full
                : DesktopTopologyProviderMode.Limited,
            UsesPrivateApis: canSwitch);

        public VirtualDesktopCapabilities Capabilities { get; } = new(
            true,
            true,
            canSwitch,
            canSwitch,
            false,
            canSwitch,
            false);

        public Guid CurrentDesktopId { get; set; }

        public int SwitchCallCount { get; private set; }

        public int MaxConcurrentSwitches { get; private set; }

        public TimeSpan SwitchDelay { get; init; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                    []));

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(CurrentDesktopId));
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Unsupported(
                    "test.unsupported",
                    "Unsupported"));

        public async ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            int active = Interlocked.Increment(ref concurrentSwitches);
            MaxConcurrentSwitches = Math.Max(MaxConcurrentSwitches, active);
            SwitchCallCount++;
            try
            {
                if (SwitchDelay > TimeSpan.Zero)
                {
                    await Task.Delay(SwitchDelay, cancellationToken);
                }

                CurrentDesktopId = desktopId;
                return DesktopTopologyProviderResult.Succeeded();
            }
            finally
            {
                Interlocked.Decrement(ref concurrentSwitches);
            }
        }

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    private sealed class AcceptAllClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    checked((uint)(long)windowHandle),
                    "CodeWindow"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class CodeIdentityResolver : IWindowIdentityResolver
    {
        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        "Code.exe",
                        ExecutablePath: null,
                        PackageFamilyName: null,
                        AppUserModelId: null,
                        "CodeWindow",
                        WindowTitle: null,
                        CommandLine: null)));
    }

    private sealed class FixedRuleSource(
        WindowObservationRule rule) : IWindowRuleSource
    {
        public IReadOnlyList<WindowObservationRule> GetRules() => [rule];
    }

    private sealed class MutableTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset current = utcNow;

        public override DateTimeOffset GetUtcNow() => current;

        public void Advance(TimeSpan duration) => current += duration;
    }
}

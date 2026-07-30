using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Observation;

/// <summary>
/// The three answers window placement gives, and nothing in between: a rule that
/// names an application and targets a Managed Desktop moves its windows, a rule
/// that names it and says Anywhere leaves them alone, and a window no enabled
/// rule names is swept to the first desktop.
/// </summary>
[TestClass]
public sealed class UnmanagedWindowSweepTests
{
    private const string ManagedKey = "ide-development";

    private static readonly Guid FirstDesktopId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ManagedDesktopId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherDesktopId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    [DataRow(WindowEventKind.StartupReconciliation)]
    [DataRow(WindowEventKind.ManualReassignment)]
    public async Task UnnamedWindow_IsSweptToTheFirstDesktop(
        WindowEventKind eventKind)
    {
        SweepHarness harness = new();
        harness.Placement.SetCurrent(10, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(eventKind, 10);

        Assert.AreEqual(WindowObservationOutcome.Matched, observation.Outcome);
        Assert.AreEqual(UnmanagedWindowSweep.RuleId, observation.RuleId);
        Assert.AreEqual(
            UnmanagedWindowSweep.TargetDesktopKey,
            observation.TargetDesktopKey);

        // Nothing matched, so nothing selected a signal. A swept window must not
        // look like it was claimed by identity.
        Assert.IsNull(observation.MatchedOn);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            observation.Assignment!.Outcome);
        Assert.AreEqual(FirstDesktopId, observation.Assignment.TargetDesktopId);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual(FirstDesktopId, harness.Placement.Moves[0].DesktopId);
    }

    [TestMethod]
    public async Task UnnamedWindow_IsNeverSweptOnForegroundActivation()
    {
        // A window must never be moved out from under a click. If the sweep
        // answered foreground events, clicking a stray window would teleport it
        // mid-interaction and deliberate manual placement would be impossible.
        //
        // A rule naming a different application is in force, so the reason is
        // "no rule matched" rather than "nothing is configured".
        SweepHarness harness = new(
            CreateRule(
                ApplicationRuleAction.MoveToDesktop,
                processName: "SomethingElse.exe"));
        harness.Placement.SetCurrent(11, OtherDesktopId);

        WindowObservationActivity observation = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            11);

        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(WindowSkipReason.NoMatchingRule, observation.SkipReason);
        Assert.IsNull(observation.Assignment);
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.AreEqual(0, harness.Locator.CallCount);
    }

    [TestMethod]
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    public async Task WindowSweptAtOpenTime_TakesTheUserWithItWhenActivated(
        WindowEventKind eventKind)
    {
        // Otherwise the window silently vanishes and the launch looks like it
        // failed. The activation is what says the user asked for this window,
        // and it arrives while the sweep is still holding the move back.
        SweepHarness harness = new();
        harness.Placement.SetCurrent(12, OtherDesktopId);

        await harness.ObserveAsync(eventKind, 12);
        WindowObservationActivity observation = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            12);

        Assert.AreEqual(
            DesktopSwitchOutcome.Succeeded,
            observation.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PolicyApproved,
            observation.Assignment.SwitchDecisionReason);
        Assert.AreEqual(FirstDesktopId, harness.Topology.CurrentDesktopId);
        Assert.AreEqual(1, harness.Topology.SwitchCallCount);

        // The held move was dropped rather than run, so the window is placed
        // once, by the activation.
        Assert.HasCount(1, harness.Placement.Moves);

        // The self-generated foreground event the switch raises is registered for
        // suppression exactly as a rule-driven switch registers it.
        Assert.IsTrue(harness.Suppression.HasPending((nint)12));
    }

    [TestMethod]
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    public async Task WindowSweptWithoutBeingActivated_IsMovedButNotFollowed(
        WindowEventKind eventKind)
    {
        // An updater, a helper window, an application restoring itself. Nobody
        // asked for it, so it is tidied away to the first desktop and the user
        // is left where they were.
        SweepHarness harness = new();
        harness.Placement.SetCurrent(21, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(eventKind, 21);

        Assert.AreEqual(
            WindowMoveOutcome.Succeeded,
            observation.Assignment!.MoveOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.NotRequested,
            observation.Assignment.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
            observation.Assignment.SwitchDecisionReason);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task ClickingAStrayWindowLongAfterItOpened_NeverSweepsIt()
    {
        // The reason the sweep does not answer foreground activations at all.
        // Once the grace period has passed, an activation is the user working
        // with the window where it is, and moving it would be moving it out from
        // under their click.
        SweepHarness harness = new(
            CreateRule(
                ApplicationRuleAction.MoveToDesktop,
                processName: "SomethingElse.exe"));
        harness.Placement.SetCurrent(22, OtherDesktopId);

        await harness.ProcessAsync(WindowEventKind.Shown, 22);
        harness.Placement.SetCurrent(22, OtherDesktopId);
        int movesAfterSweep = harness.Placement.Moves.Count;

        WindowObservationActivity observation = await harness.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            22);

        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(WindowSkipReason.NoMatchingRule, observation.SkipReason);
        Assert.HasCount(movesAfterSweep, harness.Placement.Moves);
    }

    [TestMethod]
    [DataRow(WindowEventKind.StartupReconciliation)]
    [DataRow(WindowEventKind.ManualReassignment)]
    public async Task WindowSweptInABatch_DoesNotMoveTheUser(
        WindowEventKind eventKind)
    {
        // Sign-in would bounce the user across desktops, and a batch would thrash.
        SweepHarness harness = new();
        harness.Placement.SetCurrent(13, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(eventKind, 13);

        Assert.AreEqual(
            WindowMoveOutcome.Succeeded,
            observation.Assignment!.MoveOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.NotRequested,
            observation.Assignment.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
            observation.Assignment.SwitchDecisionReason);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task WindowAlreadyOnTheFirstDesktop_IsAlreadyCorrectAndDoesNotSwitch()
    {
        // The sweep is not re-evaluated, so a window that is already at position 0
        // is simply correct and nothing loops.
        SweepHarness harness = new();
        harness.Placement.SetCurrent(14, FirstDesktopId);
        harness.Topology.CurrentDesktopId = FirstDesktopId;

        WindowObservationActivity observation =
            await harness.ProcessAsync(WindowEventKind.Created, 14);

        Assert.AreEqual(
            WindowMoveOutcome.AlreadyCorrect,
            observation.Assignment!.MoveOutcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            observation.Assignment.SkipReason);
        Assert.IsEmpty(harness.Placement.Moves);

        // Nothing activated the window, so the switch is declined before the
        // current desktop is even consulted. The window being already correct
        // makes no difference to that.
        Assert.AreEqual(
            DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
            observation.Assignment.SwitchDecisionReason);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    [DataRow(WindowEventKind.ForegroundActivated)]
    [DataRow(WindowEventKind.StartupReconciliation)]
    [DataRow(WindowEventKind.ManualReassignment)]
    public async Task AnywhereRule_LeavesItsWindowsWhereTheyOpened(
        WindowEventKind eventKind)
    {
        SweepHarness harness = new(
            CreateRule(ApplicationRuleAction.AllowAnywhere));
        harness.Placement.SetCurrent(15, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(eventKind, 15);

        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(WindowSkipReason.AllowedAnywhere, observation.SkipReason);
        Assert.IsNull(observation.Assignment);
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
        Assert.AreEqual(0, harness.Locator.CallCount);
    }

    [TestMethod]
    public async Task RuleNamingTheAppWithoutTheFiringTrigger_LeavesTheWindowAlone()
    {
        // The sweep test is identity-only. If it reused the trigger-filtered
        // match, narrowing a rule's triggers would stop being a harmless no-op
        // and start banishing the application's windows to the first desktop.
        SweepHarness harness = new(
            CreateRule(
                ApplicationRuleAction.MoveToDesktop,
                triggers: [ApplicationRuleTrigger.WindowCreated]));
        harness.Placement.SetCurrent(16, OtherDesktopId);

        WindowObservationActivity observation = await harness.ProcessAsync(
            WindowEventKind.ManualReassignment,
            16);

        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(WindowSkipReason.NoMatchingRule, observation.SkipReason);
        Assert.IsNull(observation.Assignment);
        Assert.IsEmpty(harness.Placement.Moves);
    }

    [TestMethod]
    public async Task DisabledRule_DoesNotProtectItsApplication()
    {
        // A disabled rule is inert everywhere else, so making it half-alive here
        // would be a special case with no explanation. The stated alternative is
        // the Anywhere destination.
        SweepHarness harness = new(
            CreateRule(ApplicationRuleAction.MoveToDesktop, isEnabled: false));
        harness.Placement.SetCurrent(17, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(WindowEventKind.Shown, 17);

        Assert.AreEqual(UnmanagedWindowSweep.RuleId, observation.RuleId);
        Assert.AreEqual(FirstDesktopId, harness.Placement.Moves[0].DesktopId);
    }

    [TestMethod]
    public async Task NamedWindowTargetingAManagedDesktop_IsStillMovedThere()
    {
        // Tier 1 is unchanged, and it must not be reachable through the sweep.
        SweepHarness harness = new(CreateRule(ApplicationRuleAction.MoveToDesktop));
        harness.Placement.SetCurrent(18, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(WindowEventKind.Shown, 18);

        Assert.AreEqual("named", observation.RuleId);
        Assert.AreEqual(ManagedKey, observation.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, observation.MatchedOn);
        Assert.AreEqual(ManagedDesktopId, harness.Placement.Moves[0].DesktopId);
        Assert.AreEqual(0, harness.Locator.CallCount);
    }

    [TestMethod]
    public void IsNamedByAnyRule_IgnoresTriggersSwitchPolicyAndDestination()
    {
        WindowRuleMatcher matcher = new();
        WindowIdentity identity = CreateIdentity();

        Assert.IsTrue(matcher.IsNamedByAnyRule(
            identity,
            [ToObservationRule(
                CreateRule(
                    ApplicationRuleAction.MoveToDesktop,
                    triggers: [ApplicationRuleTrigger.WindowCreated]))]));
        Assert.IsTrue(matcher.IsNamedByAnyRule(
            identity,
            [ToObservationRule(CreateRule(ApplicationRuleAction.AllowAnywhere))]));
        Assert.IsTrue(matcher.IsNamedByAnyRule(
            identity,
            [ToObservationRule(
                CreateRule(ApplicationRuleAction.MoveToDesktop) with
                {
                    Triggers = [],
                    SwitchPolicy = DesktopSwitchPolicy.Never,
                })]));

        // Enabled is the one thing it does honor.
        Assert.IsFalse(matcher.IsNamedByAnyRule(
            identity,
            [ToObservationRule(
                CreateRule(
                    ApplicationRuleAction.MoveToDesktop,
                    isEnabled: false))]));
        Assert.IsFalse(matcher.IsNamedByAnyRule(identity, []));
    }

    [TestMethod]
    public void TheSweepRule_AnswersFourTriggersAndNeverForegroundActivation()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                ApplicationRuleTrigger.WindowCreated,
                ApplicationRuleTrigger.WindowShown,
                ApplicationRuleTrigger.StartupReconciliation,
                ApplicationRuleTrigger.ManualReassignment,
            },
            UnmanagedWindowSweep.Triggers.ToArray());
        Assert.DoesNotContain(
            ApplicationRuleTrigger.ForegroundActivated,
            UnmanagedWindowSweep.Triggers);

        Assert.IsTrue(UnmanagedWindowSweep.AnswersEvent(WindowEventKind.Created));
        Assert.IsTrue(UnmanagedWindowSweep.AnswersEvent(WindowEventKind.Shown));
        Assert.IsTrue(UnmanagedWindowSweep.AnswersEvent(
            WindowEventKind.StartupReconciliation));
        Assert.IsTrue(UnmanagedWindowSweep.AnswersEvent(
            WindowEventKind.ManualReassignment));
        Assert.IsFalse(UnmanagedWindowSweep.AnswersEvent(
            WindowEventKind.ForegroundActivated));

        // The sweep no longer decides for itself whether the desktop follows a
        // window. It carries a switch policy like any other rule, and the answer
        // comes from whether the user activated the window.
        Assert.AreEqual(
            DesktopSwitchPolicy.OnNewWindowActivation,
            UnmanagedWindowSweep.Rule.SwitchPolicy);
    }

    [TestMethod]
    public void TheSweepRuleId_CannotCollideWithAUserAuthoredRule()
    {
        // A swept window's activity has to stay attributable to the sweep rather
        // than to something the user wrote.
        Assert.IsFalse(
            ApplicationRuleShape.IsValidRuleId(UnmanagedWindowSweep.RuleId));
        Assert.IsTrue(
            UnmanagedWindowSweep.IsSweptRuleId(UnmanagedWindowSweep.RuleId));
        Assert.IsFalse(UnmanagedWindowSweep.IsSweptRuleId("ide-development"));
        Assert.IsFalse(UnmanagedWindowSweep.IsSweptRuleId(null));
    }

    [TestMethod]
    public async Task TheFirstDesktop_IsFoundByPositionRatherThanByName()
    {
        // It may be a desktop the user named themselves before installing
        // DesktopShift, and it is never renamed, so only its position can find it.
        FakeTopologyProvider topology = new();
        topology.Desktops.Clear();
        topology.Desktops.Add(
            new VirtualDesktopDescriptor(ManagedDesktopId, "Desktop 1", 4, false));
        topology.Desktops.Add(
            new VirtualDesktopDescriptor(FirstDesktopId, "Whatever I called it", 0, true));

        DesktopTopologyProviderResult<Guid> result =
            await new FirstDesktopLocator(topology).GetFirstDesktopIdAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(FirstDesktopId, result.Value);
    }

    [TestMethod]
    public async Task NoResolvableFirstDesktop_DegradesLikeAnyOtherAssignment()
    {
        // If the topology cannot be read the sweep records the same failure a
        // rule-driven assignment records. There is no new failure path.
        FakeTopologyProvider topology = new();
        topology.Desktops.Clear();

        DesktopTopologyProviderResult<Guid> result =
            await new FirstDesktopLocator(topology).GetFirstDesktopIdAsync();

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("first_desktop.not_found", result.Error!.Code);

        SweepHarness harness = new(locator: new FailingFirstDesktopLocator());
        harness.Placement.SetCurrent(19, OtherDesktopId);

        WindowObservationActivity observation =
            await harness.ProcessAsync(WindowEventKind.Shown, 19);

        Assert.AreEqual(
            WindowAssignmentSkipReason.TargetDesktopUnresolved,
            observation.Assignment!.SkipReason);
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public void TheFirstDesktop_IsNotPartOfTheManagedDesktopCatalog()
    {
        // Position 0 stays outside the configured topology, so nothing recreates
        // it, renames it, or binds a semantic key to it.
        ConfigurationDocument defaults = ConfigurationDefaults.Create();

        Assert.IsFalse(
            defaults.ManagedDesktops.Any(static desktop =>
                desktop.SemanticKey.Contains(
                    "first",
                    StringComparison.OrdinalIgnoreCase)));
        Assert.IsFalse(
            defaults.ManagedDesktops.Any(static desktop =>
                string.Equals(
                    desktop.SemanticKey,
                    UnmanagedWindowSweep.TargetDesktopKey,
                    StringComparison.OrdinalIgnoreCase)));
    }

    private static WindowIdentity CreateIdentity() =>
        new(
            1,
            "Discord.exe",
            ExecutablePath: @"C:\private\Discord.exe",
            PackageFamilyName: null,
            AppUserModelId: null,
            "Chrome_WidgetWin_1",
            WindowTitle: null,
            CommandLine: null);

    private static ApplicationRule CreateRule(
        ApplicationRuleAction action,
        bool isEnabled = true,
        ImmutableArray<ApplicationRuleTrigger> triggers = default,
        string processName = "Discord.exe") =>
        new(
            "named",
            "Named application",
            isEnabled,
            ManagedKey,
            [processName],
            triggers.IsDefault ? ConfigurationDefaults.DefaultTriggers : triggers,
            DesktopSwitchPolicy.Never,
            Action: action);

    private static WindowObservationRule ToObservationRule(ApplicationRule rule) =>
        new ConfigurationWindowRuleSource(() => new ConfigurationDocument(
            ConfigurationDefaults.CurrentSchemaVersion,
            [],
            [rule],
            ConfigurationDefaults.Create().Behavior)).GetRules()[0];

    /// <summary>
    /// Drives the real observation processor and the real assignment service, so
    /// what these tests prove is what the running application decides.
    /// </summary>
    private sealed class SweepHarness
    {
        private readonly WindowObservationProcessor processor;

        public SweepHarness(
            ApplicationRule? rule = null,
            CountingLocator? locator = null)
        {
            Locator = locator ?? new CountingFirstDesktopLocator(Topology);
            WindowAssignmentService assignmentService = new(
                Placement,
                new BoundReconciliationService(),
                new BoundedWindowAssignmentActivityStore(),
                TimeProvider.System,
                new DesktopSwitchCoordinator(
                    Topology,
                    Suppression,
                    TimeProvider.System),
                Suppression,
                ActivationTracker,
                Locator);

            processor = new WindowObservationProcessor(
                new AcceptAllClassifier(),
                new DiscordIdentityResolver(),
                new DocumentRuleSource(rule),
                Sink,
                assignmentService: assignmentService,
                activationTracker: ActivationTracker,
                switchSuppression: Suppression,
                followGrace: new OpenWindowFollowGrace(
                    scheduler: FollowScheduler));
        }

        /// <summary>
        /// Shared between the observer and the assignment, as the host shares it.
        /// </summary>
        /// <remarks>
        /// The observer is what marks a window new, and the assignment is what
        /// asks whether this is its first activation. Two trackers would make
        /// every activation look like a repeat, and no swept window would ever
        /// be followed.
        /// </remarks>
        public BoundedNewWindowActivationTracker ActivationTracker { get; } = new();

        public FakePlacementService Placement { get; } = new();

        public FakeTopologyProvider Topology { get; } = new();

        public BoundedForegroundSwitchSuppression Suppression { get; } =
            new(TimeProvider.System);

        public CountingLocator Locator { get; }

        public ManualOpenWindowFollowScheduler FollowScheduler { get; } = new();

        public CapturingWindowObservationActivitySink Sink { get; } = new();

        /// <summary>
        /// Observes one window event and settles whatever it started.
        /// </summary>
        /// <remarks>
        /// An opened window is held rather than assigned, waiting to see whether
        /// it takes the foreground. Releasing here is what a test means by "and
        /// nothing activated it": the window was left alone for its grace period
        /// and then placed. A test about activation drives the foreground event
        /// before this runs, and finds nothing left to release.
        /// </remarks>
        public async ValueTask<WindowObservationActivity> ProcessAsync(
            WindowEventKind eventKind,
            nint windowHandle)
        {
            WindowObservationActivity observation = await ObserveAsync(
                eventKind,
                windowHandle);
            await FollowScheduler.ReleaseAllAsync();
            return Sink.Last ?? observation;
        }

        /// <summary>
        /// Observes one window event and leaves any hold it started standing.
        /// </summary>
        /// <remarks>
        /// For tests about what happens <em>during</em> the grace period, where
        /// the next event is the point.
        /// </remarks>
        public ValueTask<WindowObservationActivity> ObserveAsync(
            WindowEventKind eventKind,
            nint windowHandle) =>
            processor.ProcessAsync(
                new WindowEvent(1, eventKind, windowHandle, Now));
    }

    private sealed class DocumentRuleSource(ApplicationRule? rule) :
        IWindowRuleSource
    {
        public IReadOnlyList<WindowObservationRule> GetRules() =>
            rule is null ? [] : [ToObservationRule(rule)];
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

    private sealed class DiscordIdentityResolver : IWindowIdentityResolver
    {
        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    CreateIdentity() with { ProcessId = window.ProcessId }));
    }

    /// <summary>
    /// Counts every ask, so a test can prove the sweep's destination was never
    /// even looked up on the paths that must not sweep.
    /// </summary>
    private abstract class CountingLocator : IFirstDesktopLocator
    {
        public int CallCount { get; private set; }

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetFirstDesktopIdAsync(CancellationToken cancellationToken = default)
        {
            CallCount++;
            return ResolveAsync(cancellationToken);
        }

        protected abstract ValueTask<DesktopTopologyProviderResult<Guid>>
            ResolveAsync(CancellationToken cancellationToken);
    }

    private sealed class CountingFirstDesktopLocator(
        FakeTopologyProvider topology) : CountingLocator
    {
        private readonly FirstDesktopLocator inner = new(topology);

        protected override ValueTask<DesktopTopologyProviderResult<Guid>>
            ResolveAsync(CancellationToken cancellationToken) =>
            inner.GetFirstDesktopIdAsync(cancellationToken);
    }

    private sealed class FailingFirstDesktopLocator : CountingLocator
    {
        protected override ValueTask<DesktopTopologyProviderResult<Guid>>
            ResolveAsync(CancellationToken cancellationToken) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    "first_desktop.not_found",
                    "No first desktop."));
    }

    private sealed class BoundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            Now,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                ManagedKey,
                "IDE Development",
                1,
                true,
                ManagedDesktopId,
                "IDE Development",
                1,
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
        private readonly Dictionary<nint, Guid> currentDesktopIds = [];

        public List<(nint WindowHandle, Guid DesktopId)> Moves { get; } = [];

        public void SetCurrent(nint windowHandle, Guid desktopId) =>
            currentDesktopIds[windowHandle] = desktopId;

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    currentDesktopIds.TryGetValue(windowHandle, out Guid desktopId)
                        ? desktopId
                        : OtherDesktopId));

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            Moves.Add((windowHandle, desktopId));
            currentDesktopIds[windowHandle] = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    private sealed class FakeTopologyProvider : IDesktopTopologyProvider
    {
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
            new VirtualDesktopDescriptor(FirstDesktopId, "Desktop 1", 0, false),
            new VirtualDesktopDescriptor(
                ManagedDesktopId,
                "IDE Development",
                1,
                false),
            new VirtualDesktopDescriptor(OtherDesktopId, "Desktop 3", 2, true),
        ];

        public Guid CurrentDesktopId { get; set; } = OtherDesktopId;

        public int SwitchCallCount { get; private set; }

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
                    Desktops.ToArray()));

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(CurrentDesktopId));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Unsupported(
                    "test.unsupported",
                    "Unsupported"));

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            SwitchCallCount++;
            CurrentDesktopId = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }
}

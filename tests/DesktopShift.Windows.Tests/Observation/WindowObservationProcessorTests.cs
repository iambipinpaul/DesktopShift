using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowObservationProcessorTests
{
    [TestMethod]
    public async Task MultipleWindowsFromSameProcess_AreEachMatched()
    {
        RecordingActivitySink sink = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        WindowObservationActivity first = await processor.ProcessAsync(
            new WindowEvent(1, WindowEventKind.Shown, (nint)101, now));
        WindowObservationActivity second = await processor.ProcessAsync(
            new WindowEvent(2, WindowEventKind.Shown, (nint)102, now));

        Assert.AreEqual(WindowObservationOutcome.Matched, first.Outcome);
        Assert.AreEqual(WindowObservationOutcome.Matched, second.Outcome);
        CollectionAssert.AreEqual(
            new nint[] { (nint)101, (nint)102 },
            sink.Activities
                .Select(static item => item.WindowHandle)
                .ToArray());
    }

    [TestMethod]
    public async Task RepeatedAppearanceEvents_AreCoalescedWithoutPolling()
    {
        RecordingActivitySink sink = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        await processor.ProcessAsync(
            new WindowEvent(1, WindowEventKind.Created, (nint)101, now));
        WindowObservationActivity duplicate = await processor.ProcessAsync(
            new WindowEvent(
                2,
                WindowEventKind.Created,
                (nint)101,
                now.AddMilliseconds(25)));
        WindowObservationActivity foreground = await processor.ProcessAsync(
            new WindowEvent(
                3,
                WindowEventKind.ForegroundActivated,
                (nint)101,
                now.AddMilliseconds(30)));

        Assert.AreEqual(WindowSkipReason.Coalesced, duplicate.SkipReason);
        Assert.AreEqual(WindowObservationOutcome.Matched, foreground.Outcome);
    }

    [TestMethod]
    public void Coalescer_CreatedAndShownUseIndependentKeys()
    {
        using WindowEventCoalescer coalescer =
            new(TimeSpan.FromMilliseconds(50));
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.IsTrue(
            coalescer.ShouldProcess(
                new WindowEvent(
                    1,
                    WindowEventKind.Shown,
                    (nint)101,
                    now)));
        Assert.IsTrue(
            coalescer.ShouldProcess(
                new WindowEvent(
                    2,
                    WindowEventKind.Created,
                    (nint)101,
                    now.AddMilliseconds(10))));
        Assert.AreEqual(2, coalescer.TrackedEntryCount);
    }

    [TestMethod]
    public void Coalescer_ExpiresWithoutAFollowUpEvent()
    {
        ManualCoalescingScheduler scheduler = new();
        using WindowEventCoalescer coalescer = new(
            TimeSpan.FromMilliseconds(50),
            scheduler);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        Assert.IsTrue(
            coalescer.ShouldProcess(
                new WindowEvent(
                    1,
                    WindowEventKind.Shown,
                    (nint)101,
                    now)));
        Assert.AreEqual(1, coalescer.TrackedEntryCount);

        scheduler.FireAll();

        Assert.AreEqual(0, coalescer.TrackedEntryCount);
        Assert.IsTrue(
            coalescer.ShouldProcess(
                new WindowEvent(
                    2,
                    WindowEventKind.Shown,
                    (nint)101,
                    now.AddMilliseconds(60))));
    }

    [TestMethod]
    public async Task IdentityRaceAndAccessDenied_BecomeStructuredSkips()
    {
        RecordingActivitySink sink = new();
        WindowObservationProcessor staleProcessor = CreateProcessor(
            new PassthroughClassifier(),
            new FailedResolver(
                WindowIdentityResolutionFailure.ProcessExited,
                87),
            sink);

        WindowObservationActivity stale = await staleProcessor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)101,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowSkipReason.StaleWindow, stale.SkipReason);
        Assert.AreEqual(87, stale.NativeErrorCode);

        WindowObservationProcessor deniedProcessor = CreateProcessor(
            new PassthroughClassifier(),
            new FailedResolver(
                WindowIdentityResolutionFailure.AccessDenied,
                5),
            sink);
        WindowObservationActivity denied = await deniedProcessor.ProcessAsync(
            new WindowEvent(
                2,
                WindowEventKind.Shown,
                (nint)102,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowSkipReason.IdentityAccessDenied, denied.SkipReason);
        Assert.AreEqual(5, denied.NativeErrorCode);
    }

    [TestMethod]
    public async Task MatchedActivity_OmitsSensitivePathTitleAndCommandLine()
    {
        RecordingActivitySink sink = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink);

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)101,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual("Code.exe", activity.Identity!.ProcessName);
        Assert.IsNull(
            activity.Identity.GetType().GetProperty(
                nameof(WindowIdentity.ExecutablePath)));
        Assert.IsNull(
            activity.Identity.GetType().GetProperty(
                nameof(WindowIdentity.WindowTitle)));
        Assert.IsNull(
            activity.Identity.GetType().GetProperty(
                nameof(WindowIdentity.CommandLine)));
    }

    [TestMethod]
    public async Task DestroyEvent_CleansCoalescingStateWithoutIdentityWork()
    {
        RecordingActivitySink sink = new();
        CountingResolver resolver = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            resolver,
            sink);
        DateTimeOffset now = DateTimeOffset.UnixEpoch;

        await processor.ProcessAsync(
            new WindowEvent(1, WindowEventKind.Shown, (nint)101, now));
        WindowObservationActivity cleanup = await processor.ProcessAsync(
            new WindowEvent(
                2,
                WindowEventKind.Destroyed,
                (nint)101,
                now.AddMilliseconds(10)));
        WindowObservationActivity recreated = await processor.ProcessAsync(
            new WindowEvent(
                3,
                WindowEventKind.Shown,
                (nint)101,
                now.AddMilliseconds(20)));

        Assert.AreEqual(WindowSkipReason.CleanupEvent, cleanup.SkipReason);
        Assert.AreEqual(WindowObservationOutcome.Matched, recreated.Outcome);
        Assert.AreEqual(2, resolver.Count);
    }

    private static readonly WindowObservationRule DefaultRule = new(
        "vscode",
        "Visual Studio Code",
        IsEnabled: true,
        "code",
        [
            ApplicationRuleTrigger.WindowCreated,
            ApplicationRuleTrigger.WindowShown,
            ApplicationRuleTrigger.ForegroundActivated,
        ],
        DesktopSwitchPolicy.OnForegroundActivation,
        WindowMatchCriteria.ForProcessNames(["Code.exe"]),
        Order: 0);

    private static WindowObservationProcessor CreateProcessor(
        IWindowClassifier classifier,
        IWindowIdentityResolver resolver,
        RecordingActivitySink sink,
        ITilingTrigger? tilingTrigger = null,
        IWindowAssignmentService? assignmentService = null,
        IWindowDesktopPlacementService? placementService = null,
        WindowObservationRule? rule = null)
    {
        return new WindowObservationProcessor(
            classifier,
            resolver,
            new FixedRuleSource([rule ?? DefaultRule]),
            sink,
            assignmentService: assignmentService,
            placementService: placementService,
            tilingTrigger: tilingTrigger);
    }

    [TestMethod]
    public async Task MoveSizeEnd_NotifiesTilingOnceAndNeverReachesRuleMatching()
    {
        RecordingActivitySink sink = new();
        CountingResolver resolver = new();
        RecordingTilingTrigger trigger = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            resolver,
            sink,
            trigger);

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.MoveSizeEnded,
                (nint)101,
                DateTimeOffset.UnixEpoch));

        // The drag end belongs to tiling, not to the assignment rules: no
        // identity is resolved, and the pipeline records why it stepped aside.
        Assert.HasCount(1, trigger.MoveSizeEnded);
        Assert.AreEqual((nint)101, trigger.MoveSizeEnded[0]);
        Assert.AreEqual(0, resolver.Count);
        Assert.AreEqual(
            WindowSkipReason.TilingMoveSizeEndObserved,
            activity.SkipReason);
    }

    [TestMethod]
    public async Task Destroy_ClearsTheTilingToken()
    {
        RecordingTilingTrigger trigger = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            new RecordingActivitySink(),
            trigger);

        await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Destroyed,
                (nint)101,
                DateTimeOffset.UnixEpoch));

        Assert.HasCount(1, trigger.Destroyed);
        Assert.AreEqual((nint)101, trigger.Destroyed[0]);
    }

    [TestMethod]
    public async Task VisibilityStateEvents_RequestTilingReconciliation()
    {
        RecordingTilingTrigger trigger = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            new RecordingActivitySink(),
            trigger);
        WindowEventKind[] kinds =
        [
            WindowEventKind.Hidden,
            WindowEventKind.Minimized,
            WindowEventKind.Restored,
            WindowEventKind.Cloaked,
            WindowEventKind.Uncloaked,
            WindowEventKind.StateChanged,
        ];

        for (int index = 0; index < kinds.Length; index++)
        {
            await processor.ProcessAsync(new WindowEvent(
                index + 1,
                kinds[index],
                (nint)(101 + index),
                DateTimeOffset.UnixEpoch.AddMilliseconds(index)));
        }

        CollectionAssert.AreEqual(
            new nint[] { 101, 103, 104, 105, 106 },
            trigger.StateChanged.ToArray());
        CollectionAssert.AreEqual(
            new nint[] { 102 },
            trigger.Minimized.ToArray());
    }

    [TestMethod]
    public async Task DesktopShiftAppearanceVouchesForTilingWithoutAssignment()
    {
        RecordingTilingTrigger trigger = new();
        WindowObservationProcessor processor = CreateProcessor(
            new DesktopShiftClassifier(),
            new CountingResolver(),
            new RecordingActivitySink(),
            trigger);

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)101,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowSkipReason.DesktopShiftWindow, activity.SkipReason);
        CollectionAssert.AreEqual(
            new nint[] { 101 },
            trigger.StateChanged.ToArray());
        Assert.IsEmpty(trigger.Assignments);
    }

    [TestMethod]
    public async Task MatchedAssignment_AdmitsTheWindowIntoTilingWithItsDisposition()
    {
        RecordingTilingTrigger movedTrigger = new();
        WindowObservationProcessor movedProcessor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            new RecordingActivitySink(),
            movedTrigger,
            StubAssignment(Activity(
                WindowAssignmentOutcome.Succeeded,
                WindowAssignmentSkipReason.None,
                WindowMoveOutcome.Succeeded)));

        await movedProcessor.ProcessAsync(
            new WindowEvent(
                1,
                // An activation completes immediately; a Shown event would be
                // held by the open-window grace period instead.
                WindowEventKind.ForegroundActivated,
                (nint)101,
                DateTimeOffset.UnixEpoch));

        TilingAssignmentNotification moved =
            movedTrigger.Assignments.Single();
        Assert.AreEqual((nint)101, moved.WindowHandle);
        Assert.AreEqual("code", moved.TargetDesktopKey);
        Assert.AreEqual(
            TilingAssignmentDisposition.PlacedOnTargetDesktop,
            moved.Disposition);

        RecordingTilingTrigger alreadyTrigger = new();
        WindowObservationProcessor alreadyProcessor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            new RecordingActivitySink(),
            alreadyTrigger,
            StubAssignment(Activity(
                WindowAssignmentOutcome.Skipped,
                WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
                WindowMoveOutcome.AlreadyCorrect)));
        await alreadyProcessor.ProcessAsync(
            new WindowEvent(
                2,
                WindowEventKind.ForegroundActivated,
                (nint)102,
                DateTimeOffset.UnixEpoch));
        Assert.AreEqual(
            TilingAssignmentDisposition.AlreadyInPlace,
            alreadyTrigger.Assignments.Single().Disposition);

        RecordingTilingTrigger failedTrigger = new();
        WindowObservationProcessor failedProcessor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            new RecordingActivitySink(),
            failedTrigger,
            StubAssignment(Activity(
                WindowAssignmentOutcome.Failed,
                WindowAssignmentSkipReason.None,
                WindowMoveOutcome.Failed)));
        await failedProcessor.ProcessAsync(
            new WindowEvent(
                3,
                WindowEventKind.ForegroundActivated,
                (nint)103,
                DateTimeOffset.UnixEpoch));
        Assert.AreEqual(
            TilingAssignmentDisposition.Unconfirmed,
            failedTrigger.Assignments.Single().Disposition);
    }

    [TestMethod]
    public async Task PinRule_RunsTheAssignmentAndNeverMovesTheWindow()
    {
        RecordingActivitySink sink = new();
        RecordingTilingTrigger trigger = new();
        RecordingAssignmentService assignment = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            trigger,
            assignment,
            rule: CreatePinRule());

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                // Manual reassignment is not held for an opening window, so the
                // assignment this test is about runs before the call returns.
                WindowEventKind.ManualReassignment,
                (nint)301,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowObservationOutcome.Matched, activity.Outcome);
        Assert.AreEqual(
            WindowRuleDestination.PinnedToAllDesktops,
            activity.Destination);
        Assert.HasCount(1, assignment.Requests);
        Assert.AreEqual(
            WindowRuleDestination.PinnedToAllDesktops,
            assignment.Requests[0].Rule.Destination);
        Assert.AreEqual(
            TilingAssignmentDisposition.AlreadyInPlace,
            trigger.Assignments.Single().Disposition,
            "A pinned window keeps the tile it already owns.");
    }

    [TestMethod]
    public async Task PinRule_WhoseStoredTriggersDoNotAnswerThisEvent_StillPins()
    {
        RecordingActivitySink sink = new();
        RecordingAssignmentService assignment = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            assignmentService: assignment,
            rule: CreatePinRule() with
            {
                Triggers = [ApplicationRuleTrigger.WindowCreated],
            });

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.ForegroundActivated,
                (nint)302,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowObservationOutcome.Matched, activity.Outcome);
        Assert.HasCount(
            1,
            assignment.Requests,
            "A pin rule keeps its application pinned without reading its stored triggers.");
        Assert.AreEqual(
            WindowRuleDestination.PinnedToAllDesktops,
            assignment.Requests[0].Rule.Destination);
    }

    [TestMethod]
    public async Task AnywhereRule_ReleasesAHeldPinOnARepairEvent()
    {
        RecordingActivitySink sink = new();
        RecordingPlacementService placement = new(isPinned: true);
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            placementService: placement,
            rule: CreateAnywhereRule());

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)303,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowSkipReason.AllowedAnywhere, activity.SkipReason);
        Assert.AreEqual(1, placement.UnpinCallCount);
    }

    [TestMethod]
    public async Task AnywhereRule_DoesNotReleaseAHeldPinOnAForegroundActivation()
    {
        RecordingActivitySink sink = new();
        RecordingPlacementService placement = new(isPinned: true);
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            placementService: placement,
            rule: CreateAnywhereRule());

        await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.ForegroundActivated,
                (nint)304,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(
            0,
            placement.UnpinCallCount,
            "Switching to a window must not change its placement.");
    }

    [TestMethod]
    public async Task AnywhereRule_WhenTheReleaseIsRefused_SaysTheWindowMayStillBePinned()
    {
        RecordingActivitySink sink = new();
        RecordingPlacementService placement = new(
            isPinned: true,
            pinStateResult: DesktopTopologyProviderResult<bool>.Failed(
                "window_placement.pin_state_failed",
                "Windows refused to report the window's pin state.",
                hResult: unchecked((int)0x80004005)));
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            placementService: placement,
            rule: CreateAnywhereRule());

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)305,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(
            WindowSkipReason.PinReleaseFailed,
            activity.SkipReason,
            "An Anywhere rule that could not release a held pin must not read as though the window were free of it.");
        Assert.AreEqual(0, placement.UnpinCallCount);
    }

    [TestMethod]
    public async Task DeferredPinAssignment_LeavesTilingsOwnershipAlone()
    {
        // A lifecycle event is not a placement repair, so a move rule that
        // answers one defers while a pin is still held. Tiling must not read
        // that deferral as a window that was never placed: the window keeps
        // whatever tile it already owns.
        RecordingTilingTrigger trigger = new();
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            new RecordingActivitySink(),
            trigger,
            StubAssignment(Activity(
                WindowAssignmentOutcome.Skipped,
                WindowAssignmentSkipReason.PinHeldUntilRepairEvent,
                WindowMoveOutcome.NotAttempted)),
            rule: DefaultRule with
            {
                Triggers = [ApplicationRuleTrigger.ManualReassignment],
            });

        await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Hidden,
                (nint)306,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(
            TilingAssignmentDisposition.Unconfirmed,
            trigger.Assignments.Single().Disposition,
            "A deferred pin preserves existing tiling ownership rather than forgetting the window.");
    }

    [TestMethod]
    public async Task NamedRuleThatDoesNotAnswerTheEvent_StillReleasesAHeldPinOnARepairEvent()
    {
        // The pin rule is gone — one still in force would have answered this
        // repair event — but another enabled rule names the application, so the
        // sweep must not take the window. The repair event is still the moment
        // the stale pin is dropped.
        RecordingActivitySink sink = new();
        RecordingPlacementService placement = new(isPinned: true);
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            placementService: placement,
            rule: DefaultRule with
            {
                Triggers = [ApplicationRuleTrigger.ForegroundActivated],
            });

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)307,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(WindowObservationOutcome.Skipped, activity.Outcome);
        Assert.AreEqual(WindowSkipReason.NoMatchingRule, activity.SkipReason);
        Assert.AreEqual(
            1,
            placement.UnpinCallCount,
            "A stale pin is dropped on the repair events even when no rule answers them.");
    }

    [TestMethod]
    public async Task NamedRuleThatDoesNotAnswerTheEvent_WhenTheReleaseIsRefused_SaysSo()
    {
        RecordingActivitySink sink = new();
        RecordingPlacementService placement = new(
            isPinned: true,
            pinStateResult: DesktopTopologyProviderResult<bool>.Failed(
                "window_placement.pin_state_failed",
                "Windows refused to report the window's pin state.",
                hResult: unchecked((int)0x80004005)));
        WindowObservationProcessor processor = CreateProcessor(
            new PassthroughClassifier(),
            new SuccessfulResolver(),
            sink,
            placementService: placement,
            rule: DefaultRule with
            {
                Triggers = [ApplicationRuleTrigger.ForegroundActivated],
            });

        WindowObservationActivity activity = await processor.ProcessAsync(
            new WindowEvent(
                1,
                WindowEventKind.Shown,
                (nint)308,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(
            WindowSkipReason.PinReleaseFailed,
            activity.SkipReason,
            "A release that is refused must be reported rather than read as an unanswered event.");
        Assert.AreEqual(0, placement.UnpinCallCount);
    }

    private static WindowObservationRule CreatePinRule() =>
        new(
            "music",
            "Music",
            IsEnabled: true,
            string.Empty,
            [
                ApplicationRuleTrigger.WindowCreated,
                ApplicationRuleTrigger.WindowShown,
                ApplicationRuleTrigger.ForegroundActivated,
                ApplicationRuleTrigger.ManualReassignment,
            ],
            DesktopSwitchPolicy.Never,
            WindowMatchCriteria.ForProcessNames(["Code.exe"]),
            Order: 0,
            WindowRuleDestination.PinnedToAllDesktops);

    private static WindowObservationRule CreateAnywhereRule() =>
        CreatePinRule() with
        {
            Id = "anywhere",
            Destination = WindowRuleDestination.Anywhere,
        };

    private sealed class RecordingAssignmentService : IWindowAssignmentService
    {
        public List<WindowAssignmentRequest> Requests { get; } = [];

        public ValueTask<WindowAssignmentActivity> AssignAsync(
            WindowAssignmentRequest request,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(request);
            WindowMoveOutcome moveOutcome =
                request.Rule.Destination ==
                    WindowRuleDestination.PinnedToAllDesktops
                    ? WindowMoveOutcome.PinnedToAllDesktops
                    : WindowMoveOutcome.Succeeded;
            return ValueTask.FromResult(
                Activity(
                    WindowAssignmentOutcome.Succeeded,
                    WindowAssignmentSkipReason.None,
                    moveOutcome) with
                {
                    RuleId = request.Rule.Id,
                    TargetDesktopKey = request.Rule.TargetDesktopKey,
                });
        }
    }

    private sealed class RecordingPlacementService(
        bool isPinned,
        DesktopTopologyProviderResult<bool>? pinStateResult = null) :
        IWindowDesktopPlacementService
    {
        public int UnpinCallCount { get; private set; }

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(Guid.NewGuid()));

        public ValueTask<DesktopTopologyProviderResult>
            MoveWindowToDesktopAsync(
                nint windowHandle,
                Guid desktopId,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<bool>>
            GetWindowPinnedAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                pinStateResult ??
                DesktopTopologyProviderResult<bool>.Succeeded(isPinned));

        public ValueTask<DesktopTopologyProviderResult> UnpinWindowAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            UnpinCallCount++;
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Succeeded());
        }
    }

    private static WindowAssignmentActivity Activity(
        WindowAssignmentOutcome outcome,
        WindowAssignmentSkipReason skipReason,
        WindowMoveOutcome moveOutcome) =>
        new(
            Guid.NewGuid(),
            DateTimeOffset.UnixEpoch,
            TimeSpan.FromMilliseconds(5),
            WindowEventKind.Shown,
            0,
            outcome,
            skipReason,
            "vscode",
            "code",
            TargetDesktopId: null,
            PreviousDesktopId: null,
            Identity: new WindowSafeIdentity(
                "Code.exe",
                null,
                null,
                "Chrome_WidgetWin_1"),
            Error: null,
            MoveOutcome: moveOutcome);

    private static IWindowAssignmentService StubAssignment(
        WindowAssignmentActivity result) =>
        new FixedAssignmentService(result);

    private sealed class PassthroughClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    10,
                    "Chrome_WidgetWin_1"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class DesktopShiftClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Skipped(WindowSkipReason.DesktopShiftWindow);

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.DesktopShiftWindow;
    }

    private class SuccessfulResolver : IWindowIdentityResolver
    {
        public virtual ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        "Code.exe",
                        @"C:\Users\person\secret\Code.exe",
                        null,
                        null,
                        window.WindowClass,
                        "Secret project - Visual Studio Code",
                        "Code.exe C:\\Users\\person\\secret")));
    }

    private sealed class CountingResolver : SuccessfulResolver
    {
        public int Count { get; private set; }

        public override ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            Count++;
            return base.ResolveAsync(window, cancellationToken);
        }
    }

    private sealed class FailedResolver : IWindowIdentityResolver
    {
        private readonly WindowIdentityResolutionFailure failure;
        private readonly int nativeError;

        public FailedResolver(
            WindowIdentityResolutionFailure failure,
            int nativeError)
        {
            this.failure = failure;
            this.nativeError = nativeError;
        }

        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                WindowIdentityResolution.Failed(failure, nativeError));
    }

    private sealed class FixedAssignmentService(WindowAssignmentActivity result)
        : IWindowAssignmentService
    {
        public ValueTask<WindowAssignmentActivity> AssignAsync(
            WindowAssignmentRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);
    }

    private sealed class RecordingTilingTrigger : ITilingTrigger
    {
        public List<nint> Minimized { get; } = [];

        public List<nint> MoveSizeEnded { get; } = [];

        public List<nint> Destroyed { get; } = [];

        public List<nint> StateChanged { get; } = [];

        public List<TilingAssignmentNotification> Assignments { get; } = [];

        public void NotifyWindowMinimized(nint windowHandle) =>
            Minimized.Add(windowHandle);

        public void NotifyMoveSizeEnded(nint windowHandle) =>
            MoveSizeEnded.Add(windowHandle);

        public void NotifyWindowDestroyed(nint windowHandle) =>
            Destroyed.Add(windowHandle);

        public void NotifyWindowStateChanged(nint windowHandle) =>
            StateChanged.Add(windowHandle);

        public void NotifyAssignmentCompleted(
            in TilingAssignmentNotification notification) =>
            Assignments.Add(notification);
    }

    private sealed class FixedRuleSource : IWindowRuleSource
    {
        private readonly IReadOnlyList<WindowObservationRule> rules;

        public FixedRuleSource(IReadOnlyList<WindowObservationRule> rules)
        {
            this.rules = rules;
        }

        public IReadOnlyList<WindowObservationRule> GetRules() => rules;
    }

    private sealed class RecordingActivitySink :
        IWindowObservationActivitySink
    {
        public List<WindowObservationActivity> Activities { get; } = [];

        public ValueTask RecordAsync(
            WindowObservationActivity activity,
            CancellationToken cancellationToken = default)
        {
            Activities.Add(activity);
            return ValueTask.CompletedTask;
        }
    }

    private sealed class ManualCoalescingScheduler :
        IWindowCoalescingScheduler
    {
        private readonly List<ManualLease> leases = [];

        public IDisposable Schedule(TimeSpan dueTime, Action callback)
        {
            ManualLease lease = new(callback);
            leases.Add(lease);
            return lease;
        }

        public void FireAll()
        {
            foreach (ManualLease lease in leases.ToArray())
            {
                lease.Fire();
            }
        }

        private sealed class ManualLease : IDisposable
        {
            private readonly Action callback;
            private bool disposed;

            public ManualLease(Action callback)
            {
                this.callback = callback;
            }

            public void Fire()
            {
                if (!disposed)
                {
                    callback();
                }
            }

            public void Dispose() => disposed = true;
        }
    }
}

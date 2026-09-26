using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Assignments;

[TestClass]
public sealed class WindowAssignmentServiceTests
{
    private static readonly Guid TargetDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();

    [TestMethod]
    public async Task StalePlacementLookup_IsSkippedWithoutAttemptingAMove()
    {
        const string errorCode = "window_placement.stale_window_handle";
        const int hResult = unchecked((int)0x8002802B);
        const int nativeErrorCode = 1400;
        DesktopTopologyProviderError placementError = new(
            errorCode,
            "The window is no longer tracked by virtual desktops.",
            hResult,
            nativeErrorCode);
        StubPlacementService placement = new(
            new DesktopTopologyProviderResult<Guid>(
                DesktopTopologyResultOutcome.Failed,
                Error: placementError));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            new WindowAssignmentRequest(
                WindowEventKind.Shown,
                (nint)123,
                CreateRule(),
                new WindowSafeIdentity(
                    "Code.exe",
                    PackageFamilyName: null,
                    AppUserModelId: null,
                    "Chrome_WidgetWin_1")));

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.WindowNotTracked,
            result.SkipReason);
        Assert.AreEqual(WindowMoveOutcome.NotAttempted, result.MoveOutcome);
        Assert.AreEqual(errorCode, result.Error?.Code);
        Assert.AreEqual(placementError.Message, result.Error?.Message);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(nativeErrorCode, result.Error?.NativeErrorCode);
        Assert.AreEqual(0, placement.MoveCallCount);
        Assert.HasCount(1, activities.Snapshot);
        Assert.AreSame(result, activities.Snapshot[0]);
    }

    /// <summary>
    /// A window Windows has not placed on a desktop yet is moved, not abandoned.
    /// A window shown before the Shell registers it reached this path and was
    /// left where it opened until it next took focus.
    /// </summary>
    [TestMethod]
    public async Task WindowWindowsHasNotPlacedYet_IsMovedRatherThanAbandoned()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Failed(
                "window_placement.window_not_tracked",
                "Windows was not tracking this window on a virtual desktop.",
                hResult: 0));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)126));

        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
        Assert.AreEqual(TargetDesktopId, result.TargetDesktopId);
        Assert.IsNull(result.PreviousDesktopId);
        Assert.IsNull(result.Error);
    }

    /// <summary>
    /// The same unplaced window, reported as a query that succeeded while
    /// carrying no identifier. Both shapes mean the window has no desktop yet.
    /// </summary>
    [TestMethod]
    public async Task QuerySucceedingWithNoDesktopId_IsMovedRatherThanAbandoned()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(Guid.Empty));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)127));

        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
        Assert.IsNull(result.PreviousDesktopId);
    }

    [TestMethod]
    public async Task ForegroundActivationWithNoDesktopId_DoesNotMoveExistingWindow()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Failed(
                "window_placement.window_not_tracked",
                "Windows temporarily stopped reporting the window's desktop.",
                hResult: 0));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)129) with
            {
                Trigger = WindowEventKind.ForegroundActivated,
            });

        Assert.AreEqual(0, placement.MoveCallCount,
            "A focus change must not move an existing window after one inconclusive desktop read.");
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.WindowNotTracked,
            result.SkipReason);
        Assert.AreEqual(WindowMoveOutcome.WindowUnavailable, result.MoveOutcome);
    }

    /// <summary>
    /// Attempting the move on an unplaced window does not invent a success. A
    /// window Windows still will not place is reported with the refusal Windows
    /// gave, not with a code DesktopShift synthesized before trying.
    /// </summary>
    [TestMethod]
    public async Task UnplacedWindowWhoseMoveIsRefused_ReportsTheRefusal()
    {
        const int hResult = unchecked((int)0x8002802B);
        DesktopTopologyProviderError moveError = new(
            "window_placement.move_failed",
            "Windows refused to move the window to the requested virtual desktop.",
            hResult);
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(Guid.Empty),
            new DesktopTopologyProviderResult(
                DesktopTopologyResultOutcome.Failed,
                moveError));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)128));

        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Failed, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Failed, result.MoveOutcome);
        Assert.AreEqual(moveError.Code, result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.IsNull(result.PreviousDesktopId);
    }

    [TestMethod]
    public async Task UnplacedWindowTheShellHasNotRegisteredYet_IsSkippedNotFailed()
    {
        // The window the Shell has not caught up with fails both calls: the
        // query has no desktop to report, and the move has no view to move. The
        // next event places it, so neither is a failure the user can act on, and
        // Activity must not show one.
        DesktopTopologyProviderError notTracked = new(
            "window_placement.window_not_tracked",
            "The Windows Shell had not registered this window yet, so it was left where it is.",
            unchecked((int)0x8002802B));
        StubPlacementService placement = new(
            new DesktopTopologyProviderResult<Guid>(
                DesktopTopologyResultOutcome.Failed,
                Guid.Empty,
                notTracked),
            new DesktopTopologyProviderResult(
                DesktopTopologyResultOutcome.Failed,
                notTracked));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)130));

        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.WindowNotTracked,
            result.SkipReason);
        Assert.AreEqual(
            WindowMoveOutcome.WindowUnavailable,
            result.MoveOutcome);
        Assert.IsNull(result.PreviousDesktopId);
    }

    [TestMethod]
    public async Task WindowBecomesStaleDuringMove_IsSkippedWithDiagnostics()
    {
        const int hResult = unchecked((int)0x8002802B);
        DesktopTopologyProviderError placementError = new(
            "window_placement.stale_window_handle",
            "The window handle became stale before it could be moved.",
            hResult);
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            new DesktopTopologyProviderResult(
                DesktopTopologyResultOutcome.Failed,
                placementError));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            new WindowAssignmentRequest(
                WindowEventKind.Shown,
                (nint)124,
                CreateRule(),
                new WindowSafeIdentity(
                    "Code.exe",
                    PackageFamilyName: null,
                    AppUserModelId: null,
                    "Chrome_WidgetWin_1")));

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.WindowNotTracked,
            result.SkipReason);
        Assert.AreEqual(
            WindowMoveOutcome.WindowUnavailable,
            result.MoveOutcome);
        Assert.AreEqual(OtherDesktopId, result.PreviousDesktopId);
        Assert.AreEqual(placementError.Code, result.Error?.Code);
        Assert.AreEqual(placementError.Message, result.Error?.Message);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.HasCount(1, activities.Snapshot);
        Assert.AreSame(result, activities.Snapshot[0]);
    }

    [TestMethod]
    public async Task MoveAccessDenied_RemainsFailed()
    {
        const int hResult = unchecked((int)0x80070005);
        const int nativeErrorCode = 5;
        DesktopTopologyProviderError placementError = new(
            "window_placement.move_access_denied",
            "Windows denied access to move the window.",
            hResult,
            nativeErrorCode);
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            new DesktopTopologyProviderResult(
                DesktopTopologyResultOutcome.Failed,
                placementError));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            new WindowAssignmentRequest(
                WindowEventKind.Shown,
                (nint)125,
                CreateRule(),
                new WindowSafeIdentity(
                    "Code.exe",
                    PackageFamilyName: null,
                    AppUserModelId: null,
                    "Chrome_WidgetWin_1")));

        Assert.AreEqual(WindowAssignmentOutcome.Failed, result.Outcome);
        Assert.AreEqual(WindowAssignmentSkipReason.None, result.SkipReason);
        Assert.AreEqual(WindowMoveOutcome.Failed, result.MoveOutcome);
        Assert.AreEqual(placementError.Code, result.Error?.Code);
        Assert.AreEqual(placementError.Message, result.Error?.Message);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(nativeErrorCode, result.Error?.NativeErrorCode);
        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.HasCount(1, activities.Snapshot);
        Assert.AreSame(result, activities.Snapshot[0]);
    }

    // ------------------------------------------------------------------
    // Show on all desktops: the action that pins instead of moving, and the
    // release a window gets once its pin rule is gone.
    // ------------------------------------------------------------------

    [TestMethod]
    public async Task PinRule_PinsTheWindowWithoutResolvingADesktopMovingItOrSwitching()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Failed(
                "window_placement.window_not_tracked",
                "A pin never asks which desktop the window is on.",
                hResult: 0),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(false));
        BoundedWindowAssignmentActivityStore activities = new();
        RecordingSwitchCoordinator switchCoordinator = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System,
            switchCoordinator);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreatePinRequest((nint)201));

        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(
            WindowMoveOutcome.PinnedToAllDesktops,
            result.MoveOutcome);
        Assert.AreEqual(WindowAssignmentSkipReason.None, result.SkipReason);
        Assert.AreEqual(1, placement.PinCallCount);
        Assert.AreEqual(0, placement.MoveCallCount);
        Assert.AreEqual(
            0,
            placement.DesktopQueryCount,
            "A pin is the placement, so no desktop is resolved for it.");
        Assert.IsNull(result.TargetDesktopId);
        Assert.IsNull(result.PreviousDesktopId);
        Assert.IsNull(result.Error);
        Assert.AreEqual(DesktopSwitchOutcome.NotRequested, result.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PinnedToAllDesktops,
            result.SwitchDecisionReason);
        Assert.AreEqual(
            0,
            switchCoordinator.ApplyCallCount,
            "A pin never consults the switch coordinator: nothing moves, so there is nothing for the desktop to follow.");
        Assert.HasCount(1, activities.Snapshot);
        Assert.AreSame(result, activities.Snapshot[0]);
    }

    [TestMethod]
    public async Task PinRule_WhenTheWindowIsAlreadyPinned_SaysSoWithoutPinningAgain()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreatePinRequest((nint)202));

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyPinnedToAllDesktops,
            result.SkipReason);
        Assert.AreEqual(
            WindowMoveOutcome.AlreadyPinnedToAllDesktops,
            result.MoveOutcome);
        Assert.AreEqual(0, placement.PinCallCount);
        Assert.IsNull(result.Error);
    }

    [TestMethod]
    public async Task PinRule_OnAHostThatCannotPin_IsSkippedWithAnHonestReason()
    {
        // The stub answers the pin query the way a Limited Mode host does.
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreatePinRequest((nint)203));

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.PinUnavailable,
            result.SkipReason);
        Assert.AreEqual(WindowMoveOutcome.NotAttempted, result.MoveOutcome);
        Assert.AreEqual(
            "window_placement.pin_unsupported",
            result.Error?.Code);
        Assert.AreEqual(0, placement.PinCallCount);
        Assert.AreEqual(0, placement.MoveCallCount);
    }

    [TestMethod]
    public async Task PinRule_WhenTheShellRefusesThePin_ReportsTheRefusal()
    {
        const int hResult = unchecked((int)0x80004005);
        DesktopTopologyProviderError pinError = new(
            "window_placement.pin_failed",
            "Windows refused to pin the window to every desktop.",
            hResult);
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(false),
            pinResult: new DesktopTopologyProviderResult(
                DesktopTopologyResultOutcome.Failed,
                pinError));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreatePinRequest((nint)204));

        Assert.AreEqual(WindowAssignmentOutcome.Failed, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.NotAttempted, result.MoveOutcome);
        Assert.AreEqual(pinError.Code, result.Error?.Code);
        Assert.AreEqual(pinError.Message, result.Error?.Message);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, placement.PinCallCount);
    }

    [TestMethod]
    public async Task MoveRule_ReleasesAHeldPinBeforeMovingTheWindow()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)205));

        CollectionAssert.AreEqual(
            new[] { "unpin", "move" },
            placement.Calls,
            "The pin has to be released before the window reaches the mover.");
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
    }

    [TestMethod]
    public async Task MoveRule_WhenTheReleaseIsRefused_LeavesTheWindowAloneAndSaysWhy()
    {
        DesktopTopologyProviderError unpinError = new(
            "window_placement.pin_failed",
            "Windows refused to release the window's pin.",
            unchecked((int)0x80004005));
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true),
            unpinResult: new DesktopTopologyProviderResult(
                DesktopTopologyResultOutcome.Failed,
                unpinError));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)206));

        Assert.AreEqual(WindowAssignmentOutcome.Failed, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Failed, result.MoveOutcome);
        Assert.AreEqual(unpinError.Code, result.Error?.Code);
        Assert.AreEqual(
            0,
            placement.MoveCallCount,
            "A window that is still pinned must not be handed to the mover.");
        CollectionAssert.AreEqual(new[] { "unpin" }, placement.Calls);
    }

    [TestMethod]
    public async Task MoveRule_OnAHostThatCannotPin_MovesWithoutTouchingThePinSurface()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)207));

        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
        CollectionAssert.AreEqual(new[] { "move" }, placement.Calls);
        Assert.AreEqual(0, placement.UnpinCallCount);
    }

    [TestMethod]
    public async Task ForegroundActivation_DoesNotReleaseAPinWhenNoMoveIsNeeded()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)208) with
            {
                Trigger = WindowEventKind.ForegroundActivated,
            });

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            result.SkipReason);
        Assert.AreEqual(
            0,
            placement.UnpinCallCount,
            "Switching to a window must not change its placement.");
        Assert.AreEqual(0, placement.MoveCallCount);
    }

    [TestMethod]
    public async Task RepairEvent_ReleasesAPinLeftByAnEarlierRuleEvenWhenNoMoveIsNeeded()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)209));

        CollectionAssert.AreEqual(new[] { "unpin" }, placement.Calls);
        Assert.AreEqual(1, placement.UnpinCallCount);
        Assert.AreEqual(0, placement.MoveCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            result.SkipReason);
    }

    [TestMethod]
    public async Task ForegroundActivation_LeavesAHeldPinAloneEvenWhenTheRuleWantsAMove()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)210) with
            {
                Trigger = WindowEventKind.ForegroundActivated,
            });

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.PinHeldUntilRepairEvent,
            result.SkipReason);
        Assert.AreEqual(
            0,
            placement.UnpinCallCount,
            "Releasing a pin changes placement, so a foreground activation must never do it.");
        Assert.AreEqual(
            0,
            placement.MoveCallCount,
            "The window keeps its place until an event that repairs placement arrives.");
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PinnedToAllDesktops,
            result.SwitchDecisionReason);
    }

    [TestMethod]
    public async Task ForegroundActivation_StillMovesAWindowThatHoldsNoPin()
    {
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(false));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)211) with
            {
                Trigger = WindowEventKind.ForegroundActivated,
            });

        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.AreEqual(0, placement.UnpinCallCount);
    }

    [TestMethod]
    public async Task LifecycleEvent_LeavesAHeldPinAloneEvenWhenTheRuleWantsAMove()
    {
        // Every event kind without a trigger of its own maps to manual
        // reassignment, so a move rule answers a lifecycle event too. That
        // event is not a repair: a window whose pin is still held keeps it —
        // and its place — until an event that repairs placement arrives.
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)212) with
            {
                Trigger = WindowEventKind.Hidden,
            });

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.PinHeldUntilRepairEvent,
            result.SkipReason);
        Assert.AreEqual(
            0,
            placement.UnpinCallCount,
            "A lifecycle event is not a repair, so it must not release a pin.");
        Assert.AreEqual(
            0,
            placement.MoveCallCount,
            "The move waits for an event that repairs placement.");
        Assert.AreEqual(
            DesktopSwitchDecisionReason.PinnedToAllDesktops,
            result.SwitchDecisionReason);
    }

    [TestMethod]
    public async Task LifecycleEvent_StillMovesAWindowThatHoldsNoPin()
    {
        // Only a window that actually holds a pin is deferred; without one
        // the event moves the window exactly as it always did.
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(false));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)213) with
            {
                Trigger = WindowEventKind.Hidden,
            });

        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
        Assert.AreEqual(1, placement.MoveCallCount);
        Assert.AreEqual(0, placement.UnpinCallCount);
    }

    [TestMethod]
    public async Task RepairEvent_StillReleasesAHeldPinWhenTheDestinationDoesNotResolve()
    {
        // The rule cannot place the window — its desktop key no longer resolves
        // — but the repair event still drops the pin an earlier rule left: the
        // window must not stay on every desktop just because the move cannot
        // happen yet.
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new UnboundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)214));

        Assert.AreEqual(1, placement.UnpinCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.TargetDesktopUnresolved,
            result.SkipReason);
        Assert.AreEqual(0, placement.MoveCallCount);
    }

    [TestMethod]
    public async Task ForegroundActivation_DoesNotReleaseAHeldPinWhenTheDestinationDoesNotResolve()
    {
        // The release belongs to the events that repair placement only: an
        // activation leaves the pin — and the unresolved destination — alone
        // until the next event that does repair placement.
        StubPlacementService placement = new(
            DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId),
            pinStateResult: DesktopTopologyProviderResult<bool>.Succeeded(true));
        BoundedWindowAssignmentActivityStore activities = new();
        WindowAssignmentService service = new(
            placement,
            new UnboundReconciliationService(),
            activities,
            TimeProvider.System);

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)215) with
            {
                Trigger = WindowEventKind.ForegroundActivated,
            });

        Assert.AreEqual(0, placement.UnpinCallCount);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, result.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.TargetDesktopUnresolved,
            result.SkipReason);
    }

    private static WindowAssignmentRequest CreateRequest(nint windowHandle) =>
        new(
            WindowEventKind.Shown,
            windowHandle,
            CreateRule(),
            new WindowSafeIdentity(
                "Code.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Chrome_WidgetWin_1"));

    private static WindowObservationRule CreateRule() =>
        new(
            "vscode",
            "Visual Studio Code",
            IsEnabled: true,
            "code",
            [ApplicationRuleTrigger.WindowShown],
            DesktopSwitchPolicy.Never,
            WindowMatchCriteria.ForProcessNames(["Code.exe"]),
            Order: 0);

    private static WindowAssignmentRequest CreatePinRequest(nint windowHandle) =>
        new(
            WindowEventKind.Shown,
            windowHandle,
            CreatePinRule(),
            new WindowSafeIdentity(
                "Music.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "WinUIDesktopWin32WindowClass"));

    /// <summary>
    /// A rule that keeps its application on every desktop. The stored target
    /// key is deliberately one a move rule would use, because switching an
    /// existing rule's destination to a pin leaves that key in place — and the
    /// pin path must never read it.
    /// </summary>
    private static WindowObservationRule CreatePinRule() =>
        new(
            "music",
            "Music",
            IsEnabled: true,
            "code",
            [ApplicationRuleTrigger.WindowShown],
            DesktopSwitchPolicy.Never,
            WindowMatchCriteria.ForProcessNames(["Music.exe"]),
            Order: 0,
            WindowRuleDestination.PinnedToAllDesktops);

    private sealed class StubPlacementService(
        DesktopTopologyProviderResult<Guid> currentDesktopResult,
        DesktopTopologyProviderResult? moveResult = null,
        DesktopTopologyProviderResult<bool>? pinStateResult = null,
        DesktopTopologyProviderResult? pinResult = null,
        DesktopTopologyProviderResult? unpinResult = null) :
        IWindowDesktopPlacementService
    {
        public int MoveCallCount { get; private set; }

        public int PinCallCount { get; private set; }

        public int UnpinCallCount { get; private set; }

        public int DesktopQueryCount { get; private set; }

        /// <summary>
        /// The placement calls in the order they were made, so a test can prove
        /// a release happened before the move rather than merely alongside it.
        /// </summary>
        public List<string> Calls { get; } = [];

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default)
        {
            DesktopQueryCount++;
            return ValueTask.FromResult(currentDesktopResult);
        }

        public ValueTask<DesktopTopologyProviderResult>
            MoveWindowToDesktopAsync(
                nint windowHandle,
                Guid desktopId,
                CancellationToken cancellationToken = default)
        {
            MoveCallCount++;
            Calls.Add("move");
            return ValueTask.FromResult(
                moveResult ?? DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult<bool>>
            GetWindowPinnedAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                pinStateResult ??
                DesktopTopologyProviderResult<bool>.Unsupported(
                    "window_placement.pin_unsupported",
                    "This host cannot report whether a window is shown on every desktop."));

        public ValueTask<DesktopTopologyProviderResult> PinWindowAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            PinCallCount++;
            Calls.Add("pin");
            return ValueTask.FromResult(
                pinResult ?? DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult> UnpinWindowAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            UnpinCallCount++;
            Calls.Add("unpin");
            return ValueTask.FromResult(
                unpinResult ?? DesktopTopologyProviderResult.Succeeded());
        }
    }

    /// <summary>
    /// Records whether the switch path was reached at all, so "a pin never
    /// switches" is proved rather than merely unobserved.
    /// </summary>
    private sealed class RecordingSwitchCoordinator : IDesktopSwitchCoordinator
    {
        public int ApplyCallCount { get; private set; }

        public ValueTask<DesktopSwitchResult> ApplyAsync(
            DesktopSwitchRequest request,
            CancellationToken cancellationToken = default)
        {
            ApplyCallCount++;
            return ValueTask.FromResult(new DesktopSwitchResult(
                DesktopSwitchOutcome.Succeeded,
                DesktopSwitchDecisionReason.PolicyApproved,
                TimeSpan.Zero));
        }
    }

    private sealed class BoundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "code",
                "Code",
                PreferredOrder: 0,
                RecreateWhenMissing: true,
                TargetDesktopId,
                "Code",
                RuntimePosition: 0,
                ManagedDesktopMappingStatus.ReusedPersistedBinding,
                [],
                "test.bound",
                "Bound")],
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

    private sealed class UnboundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [],
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

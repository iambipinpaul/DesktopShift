using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.VirtualDesktops;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.Tests.Assignments;

[TestClass]
public sealed class WindowsWindowDesktopPlacementServiceTests
{
    private static readonly nint WindowHandle = (nint)0x1234;
    private static readonly Guid DesktopId =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task GetDesktop_RejectsZeroHandleBeforeCallingCom()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<Guid> result =
            await service.GetWindowDesktopIdAsync(0);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.invalid_window_handle",
            result.Error?.Code);
        Assert.AreEqual(0, desktopManager.GetCount);
    }

    [TestMethod]
    public async Task GetDesktop_RejectsStaleAndNonTopLevelHandles()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService staleService = CreateService(
            new FakeWindowHandleApi { IsLive = false },
            desktopManager);

        DesktopTopologyProviderResult<Guid> stale =
            await staleService.GetWindowDesktopIdAsync(WindowHandle);

        Assert.AreEqual(
            "window_placement.stale_window_handle",
            stale.Error?.Code);

        FakeWindowHandleApi childWindowApi = new()
        {
            RootWindow = (nint)0x9999,
        };
        using WindowsWindowDesktopPlacementService childService = CreateService(
            childWindowApi,
            desktopManager);

        DesktopTopologyProviderResult<Guid> child =
            await childService.GetWindowDesktopIdAsync(WindowHandle);

        Assert.AreEqual(
            "window_placement.not_top_level_window",
            child.Error?.Code);
        Assert.Contains((nint)0x9999, childWindowApi.ValidatedHandles);
        Assert.AreEqual(0, desktopManager.GetCount);
    }

    [TestMethod]
    public async Task GetDesktop_ReturnsDocumentedManagerGuid()
    {
        FakeDesktopManagerApi desktopManager = new()
        {
            GetResult = new DocumentedDesktopIdResult(
                DesktopId,
                0,
                "GetWindowDesktopId"),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<Guid> result =
            await service.GetWindowDesktopIdAsync(WindowHandle);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DesktopId, result.Value);
        Assert.AreEqual(WindowHandle, desktopManager.LastWindowHandle);
        Assert.AreEqual(1, desktopManager.GetCount);
    }

    [TestMethod]
    public async Task GetDesktop_ReportsAnEmptySuccessfulGuidAsAnUnplacedWindow()
    {
        FakeDesktopManagerApi desktopManager = new()
        {
            GetResult = new DocumentedDesktopIdResult(
                Guid.Empty,
                0,
                "GetWindowDesktopId"),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<Guid> result =
            await service.GetWindowDesktopIdAsync(WindowHandle);

        // An S_OK with no identifier says the same thing the not-tracked
        // HRESULT says, and it is reported the same way. The HRESULT carried is
        // the one Windows returned, never a synthesized one.
        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.window_not_tracked",
            result.Error?.Code);
        Assert.AreEqual(0, result.Error?.HResult);
    }

    [TestMethod]
    public async Task GetDesktop_PreservesStructuredHResultAndActivationStage()
    {
        int hResult = unchecked((int)0x80040154);
        FakeDesktopManagerApi desktopManager = new()
        {
            GetResult = new DocumentedDesktopIdResult(
                Guid.Empty,
                hResult,
                "ManagerActivation"),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<Guid> result =
            await service.GetWindowDesktopIdAsync(WindowHandle);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.manager_activation_failed",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
    }

    [TestMethod]
    public async Task GetDesktop_ReportsATransientWindowThatWindowsIsNotTracking()
    {
        int hResult = unchecked((int)0x8002802B);
        FakeDesktopManagerApi desktopManager = new()
        {
            GetResult = new DocumentedDesktopIdResult(
                Guid.Empty,
                hResult,
                "GetWindowDesktopId"),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<Guid> result =
            await service.GetWindowDesktopIdAsync(WindowHandle);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.window_not_tracked",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
    }

    [TestMethod]
    public async Task Move_RejectsEmptyDesktopIdBeforeCallingCom()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(
                WindowHandle,
                Guid.Empty);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.invalid_desktop_id",
            result.Error?.Code);
        Assert.AreEqual(0, desktopManager.MoveCount);
    }

    [TestMethod]
    public async Task Move_PassesValidatedHandleAndKnownGuidToDocumentedManager()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(
                WindowHandle,
                DesktopId);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(1, desktopManager.MoveCount);
        Assert.AreEqual(WindowHandle, desktopManager.LastWindowHandle);
        Assert.AreEqual(DesktopId, desktopManager.LastDesktopId);
    }

    [TestMethod]
    public async Task Move_PreservesStructuredComFailureWithoutRetry()
    {
        int hResult = unchecked((int)0x80070057);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(hResult),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(
                WindowHandle,
                DesktopId);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual("window_placement.move_failed", result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, desktopManager.MoveCount);
    }

    [TestMethod]
    public async Task Move_ReportsAnAccessDenialAsItsOwnFailure()
    {
        // E_ACCESSDENIED is a stated answer, not a guess, so Activity can say
        // access was denied rather than only that a move failed.
        int hResult = unchecked((int)0x80070005);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(hResult),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.move_access_denied",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, desktopManager.MoveCount);
    }

    [TestMethod]
    public async Task Move_ReportsAWindowThatClosedUnderneathTheMoveAsStale()
    {
        // An owned reconnection or credential dialog dismisses itself while the
        // move is in flight. That is a race, not something the user can fix, so
        // it must not be reported as a refused move.
        int hResult = unchecked((int)0x80070006);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(hResult),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi { LiveUntilCall = 2 },
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(
            "window_placement.stale_window_handle",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, desktopManager.MoveCount);
    }

    [TestMethod]
    public async Task Move_RefusedForALiveWindow_IsReportedOnceWithItsHResult()
    {
        // A full-screen remote session frame lands here: Windows is managing the
        // window itself and refuses the move. Repeating the identical call
        // cannot change that answer, so exactly one attempt is made and the
        // HRESULT Windows gave is carried through unaltered.
        int hResult = unchecked((int)0x8002802B);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(hResult),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult first =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);
        DesktopTopologyProviderResult second =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual("window_placement.move_failed", first.Error?.Code);
        Assert.AreEqual(hResult, first.Error?.HResult);
        Assert.AreEqual(first.Error?.Code, second.Error?.Code);

        // Two callers, two attempts. Neither call retried on its own.
        Assert.AreEqual(2, desktopManager.MoveCount);
    }

    [TestMethod]
    public async Task Move_ForAWindowTheShellHasNotRegisteredYet_IsNotARefusal()
    {
        // A window shown a few milliseconds before the Shell registers an
        // application view for it fails here. The very next event places it, so
        // reporting a refusal would put a failure in Activity for a window that
        // is about to be correct. The stage is what says so: the same HRESULT
        // from the move itself stays a refusal.
        int hResult = unchecked((int)0x8002802B);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(
                hResult,
                stage: "ApplicationViewLookup",
                code: "native.application_view_lookup",
                message:
                    "The Windows Shell could not resolve an application view for the window."),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.window_not_tracked",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, desktopManager.MoveCount);
    }

    [TestMethod]
    public async Task Move_PreservesTheManagerActivationStage()
    {
        int hResult = unchecked((int)0x80040154);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(hResult, "ManagerActivation"),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(
            "window_placement.manager_activation_failed",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
    }

    [TestMethod]
    public async Task Move_UnavailableReportsThatShellWasNotCalled()
    {
        int hResult = unchecked((int)0x80070032);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(
                hResult,
                "ProviderSelection",
                "native.window_move_unavailable",
                "No validated adapter is active."),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(
            "window_placement.move_unavailable",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        StringAssert.Contains(
            result.Error?.Message,
            "could not attempt");
        StringAssert.Contains(
            result.Error?.Message,
            "No validated adapter is active.");
    }

    [TestMethod]
    public async Task Move_LocalExceptionIsNotReportedAsAWindowsRefusal()
    {
        int hResult = unchecked((int)0x8000FFFF);
        FakeDesktopManagerApi desktopManager = new()
        {
            MoveResult = FailedMove(
                hResult,
                "WindowMove",
                "native.window_move_exception",
                "The interop call threw locally."),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(
            "window_placement.move_exception",
            result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        StringAssert.Contains(
            result.Error?.Message,
            "could not complete");
        StringAssert.Contains(
            result.Error?.Message,
            "The interop call threw locally.");
    }

    [TestMethod]
    public async Task GetPinned_ReportsTheMoversAnswer()
    {
        FakeDesktopManagerApi desktopManager = new()
        {
            PinQueryResult = NativeBridgeResult<bool>.Succeeded(true),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<bool> result =
            await service.GetWindowPinnedAsync(WindowHandle);

        Assert.IsTrue(result.IsSuccess);
        Assert.IsTrue(result.Value);
        Assert.AreEqual(WindowHandle, desktopManager.LastWindowHandle);
        Assert.AreEqual(1, desktopManager.PinQueryCount);
    }

    [TestMethod]
    public async Task Pin_AppliesThePinOnceThroughTheMover()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.PinWindowAsync(WindowHandle);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(WindowHandle, desktopManager.LastWindowHandle);
        Assert.AreEqual(1, desktopManager.PinCount);
    }

    [TestMethod]
    public async Task Unpin_ReleasesThePinOnceThroughTheMover()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.UnpinWindowAsync(WindowHandle);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(WindowHandle, desktopManager.LastWindowHandle);
        Assert.AreEqual(1, desktopManager.UnpinCount);
    }

    [TestMethod]
    public async Task PinnedOperations_ReportAHostWithoutAProvedPinSurfaceAsUnsupported()
    {
        FakeDesktopManagerApi desktopManager = new()
        {
            PinQueryResult = NativeBridgeResult<bool>.Failed(UnsupportedPin()),
            PinResult = NativeBridgeResult.Failed(UnsupportedPin()),
            UnpinResult = NativeBridgeResult.Failed(UnsupportedPin()),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<bool> query =
            await service.GetWindowPinnedAsync(WindowHandle);
        DesktopTopologyProviderResult pin =
            await service.PinWindowAsync(WindowHandle);
        DesktopTopologyProviderResult unpin =
            await service.UnpinWindowAsync(WindowHandle);

        // Unavailable is not "not pinned". A host that cannot ask the question
        // must not look like a host whose answer is no, and assignment records
        // a deliberate skip rather than a failure nobody can act on.
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, query.Outcome);
        Assert.AreEqual(
            "window_placement.pin_unsupported",
            query.Error?.Code);
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, pin.Outcome);
        Assert.AreEqual(
            "window_placement.pin_unsupported",
            pin.Error?.Code);
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, unpin.Outcome);
        Assert.AreEqual(
            "window_placement.pin_unsupported",
            unpin.Error?.Code);
        StringAssert.Contains(
            pin.Error?.Message,
            "has not proved a pinning surface");
    }

    [TestMethod]
    public async Task PinnedOperations_ReportAWindowTheShellHasNotRegisteredYetAsNotTracked()
    {
        int hResult = unchecked((int)0x8002802B);
        NativeBridgeError notTracked = new(
            "native.application_view_lookup",
            "ApplicationViewLookup",
            "The Windows Shell could not resolve an application view for the window.",
            hResult);
        FakeDesktopManagerApi desktopManager = new()
        {
            PinQueryResult = NativeBridgeResult<bool>.Failed(notTracked),
            PinResult = NativeBridgeResult.Failed(notTracked),
            UnpinResult = NativeBridgeResult.Failed(notTracked),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<bool> query =
            await service.GetWindowPinnedAsync(WindowHandle);
        DesktopTopologyProviderResult pin =
            await service.PinWindowAsync(WindowHandle);
        DesktopTopologyProviderResult unpin =
            await service.UnpinWindowAsync(WindowHandle);

        // A window the Shell has not registered yet is not a refusal, so it is
        // reported under the code the desktop query already uses for it.
        Assert.AreEqual(
            "window_placement.window_not_tracked",
            query.Error?.Code);
        Assert.AreEqual(hResult, query.Error?.HResult);
        Assert.AreEqual(
            "window_placement.window_not_tracked",
            pin.Error?.Code);
        Assert.AreEqual(hResult, pin.Error?.HResult);
        Assert.AreEqual(
            "window_placement.window_not_tracked",
            unpin.Error?.Code);
        Assert.AreEqual(hResult, unpin.Error?.HResult);
    }

    [TestMethod]
    public async Task Pin_RefusedForALiveWindowIsReportedOnceWithItsHResult()
    {
        // A pin the Shell refuses is an answer, not a transient fault, and
        // repeating the identical call cannot change it.
        int hResult = unchecked((int)0x80004005);
        FakeDesktopManagerApi desktopManager = new()
        {
            PinResult = FailedPin(hResult),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult first =
            await service.PinWindowAsync(WindowHandle);
        DesktopTopologyProviderResult second =
            await service.PinWindowAsync(WindowHandle);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, first.Outcome);
        Assert.AreEqual("window_placement.pin_failed", first.Error?.Code);
        Assert.AreEqual(hResult, first.Error?.HResult);
        StringAssert.Contains(
            first.Error?.Message,
            "refused to keep the window");
        Assert.AreEqual(first.Error?.Code, second.Error?.Code);

        // Two callers, two attempts. Neither call retried on its own.
        Assert.AreEqual(2, desktopManager.PinCount);
    }

    [TestMethod]
    public async Task Unpin_PreservesStructuredFailureWithoutRetry()
    {
        int hResult = unchecked((int)0x80070057);
        FakeDesktopManagerApi desktopManager = new()
        {
            UnpinResult = FailedPin(hResult),
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult result =
            await service.UnpinWindowAsync(WindowHandle);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual("window_placement.pin_failed", result.Error?.Code);
        Assert.AreEqual(hResult, result.Error?.HResult);
        Assert.AreEqual(1, desktopManager.UnpinCount);
    }

    [TestMethod]
    public async Task PinnedOperations_RejectStaleHandlesBeforeAskingTheMover()
    {
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi { IsLive = false },
            desktopManager);

        DesktopTopologyProviderResult<bool> query =
            await service.GetWindowPinnedAsync(WindowHandle);
        DesktopTopologyProviderResult pin =
            await service.PinWindowAsync(WindowHandle);
        DesktopTopologyProviderResult unpin =
            await service.UnpinWindowAsync(WindowHandle);

        Assert.AreEqual(
            "window_placement.stale_window_handle",
            query.Error?.Code);
        Assert.AreEqual(
            "window_placement.stale_window_handle",
            pin.Error?.Code);
        Assert.AreEqual(
            "window_placement.stale_window_handle",
            unpin.Error?.Code);
        Assert.AreEqual(0, desktopManager.PinQueryCount);
        Assert.AreEqual(0, desktopManager.PinCount);
        Assert.AreEqual(0, desktopManager.UnpinCount);
    }

    [TestMethod]
    public async Task PinnedOperations_ReportASeamThatThrewAsAPlacementFailure()
    {
        InvalidOperationException thrown = new(
            "The validated adapter was disposed underneath the call.");
        FakeDesktopManagerApi desktopManager = new()
        {
            PinException = thrown,
        };
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            desktopManager);

        DesktopTopologyProviderResult<bool> query =
            await service.GetWindowPinnedAsync(WindowHandle);
        DesktopTopologyProviderResult pin =
            await service.PinWindowAsync(WindowHandle);
        DesktopTopologyProviderResult unpin =
            await service.UnpinWindowAsync(WindowHandle);

        // A throw is not a Windows refusal, so it gets a code of its own and
        // carries the exception rather than a Shell HRESULT.
        Assert.AreEqual("window_placement.pin_exception", query.Error?.Code);
        Assert.AreEqual("window_placement.pin_exception", pin.Error?.Code);
        Assert.AreEqual("window_placement.pin_exception", unpin.Error?.Code);
        Assert.AreEqual(thrown.HResult, pin.Error?.HResult);
        StringAssert.Contains(pin.Error?.Message, thrown.Message);
        Assert.AreEqual(1, desktopManager.PinCount);
    }

    [TestMethod]
    public async Task Pin_WithoutAMoverThatCanPin_IsUnsupportedRatherThanNotPinned()
    {
        using WindowsWindowDesktopPlacementService service = CreateService(
            new FakeWindowHandleApi(),
            new MoveOnlyDesktopManagerApi());

        DesktopTopologyProviderResult result =
            await service.PinWindowAsync(WindowHandle);

        // The seam's own default answers here, so a mover that never learned to
        // pin reports pinning as unavailable instead of failing the operation.
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, result.Outcome);
        Assert.AreEqual(
            "window_placement.pin_unsupported",
            result.Error?.Code);
    }

    [TestMethod]
    public async Task CancelledCall_DoesNotReachValidationOrCom()
    {
        FakeWindowHandleApi windowApi = new();
        FakeDesktopManagerApi desktopManager = new();
        using WindowsWindowDesktopPlacementService service = CreateService(
            windowApi,
            desktopManager);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            async () =>
                await service.MoveWindowToDesktopAsync(
                    WindowHandle,
                    DesktopId,
                    cancellation.Token));

        Assert.AreEqual(0, windowApi.ValidationCount);
        Assert.AreEqual(0, desktopManager.MoveCount);
    }

    private static WindowsWindowDesktopPlacementService CreateService(
        IWindowHandleApi windowApi,
        IDocumentedVirtualDesktopManagerApi desktopManager) =>
        new(
            windowApi,
            desktopManager,
            (IValidatedWindowDesktopMover)desktopManager);

    private static NativeBridgeResult FailedMove(
        int hResult,
        string stage = "WindowMove",
        string code = "native.window_move",
        string message = "Test window move failure.") =>
        NativeBridgeResult.Failed(
            new NativeBridgeError(
                code,
                stage,
                message,
                hResult));

    private static NativeBridgeResult FailedPin(
        int hResult,
        string code = "native.window_pin",
        string message = "Test window pin failure.",
        string stage = "WindowPin") =>
        NativeBridgeResult.Failed(
            new NativeBridgeError(
                code,
                stage,
                message,
                hResult));

    /// <summary>
    /// The refusal the pin seam gives on a host that never proved a pin
    /// surface.
    /// </summary>
    private static NativeBridgeError UnsupportedPin() =>
        new(
            "native.pin_unsupported",
            "WindowPin",
            "This mover cannot keep windows on every virtual desktop.",
            unchecked((int)0x80004001));

    private sealed class FakeWindowHandleApi : IWindowHandleApi
    {
        private int liveChecks;

        public bool IsLive { get; init; } = true;

        /// <summary>
        /// The number of liveness checks that see a live window before it
        /// reports as closed, so a window can be made to disappear between
        /// validation and the check that follows a refused move.
        /// </summary>
        public int LiveUntilCall { get; init; } = int.MaxValue;

        public nint RootWindow { get; init; } = WindowHandle;

        public int ValidationCount { get; private set; }

        public List<nint> ValidatedHandles { get; } = [];

        public bool IsWindow(nint windowHandle)
        {
            Assert.IsTrue(
                windowHandle == WindowHandle ||
                windowHandle == RootWindow);
            ValidatedHandles.Add(windowHandle);
            ValidationCount++;
            liveChecks++;
            return IsLive && liveChecks <= LiveUntilCall;
        }

        public nint GetRootWindow(nint windowHandle)
        {
            Assert.AreEqual(WindowHandle, windowHandle);
            ValidationCount++;
            return RootWindow;
        }
    }

    private sealed class FakeDesktopManagerApi :
        IDocumentedVirtualDesktopManagerApi,
        IValidatedWindowDesktopMover
    {
        public DocumentedDesktopIdResult GetResult { get; init; } =
            new(DesktopId, 0, "GetWindowDesktopId");

        public NativeBridgeResult MoveResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult<bool> PinQueryResult { get; init; } =
            NativeBridgeResult<bool>.Succeeded(false);

        public NativeBridgeResult PinResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult UnpinResult { get; init; } =
            NativeBridgeResult.Succeeded;

        /// <summary>
        /// What the pin members throw instead of answering, or null to answer
        /// normally. A seam that fails by throwing must not be reported as a
        /// Windows refusal.
        /// </summary>
        public Exception? PinException { get; init; }

        public int GetCount { get; private set; }

        public int MoveCount { get; private set; }

        public int PinQueryCount { get; private set; }

        public int PinCount { get; private set; }

        public int UnpinCount { get; private set; }

        public nint LastWindowHandle { get; private set; }

        public Guid LastDesktopId { get; private set; }

        public DocumentedDesktopIdResult GetWindowDesktopId(
            nint windowHandle)
        {
            GetCount++;
            LastWindowHandle = windowHandle;
            return GetResult;
        }

        public NativeBridgeResult MoveWindowToDesktop(
            nint windowHandle,
            Guid desktopId)
        {
            MoveCount++;
            LastWindowHandle = windowHandle;
            LastDesktopId = desktopId;
            return MoveResult;
        }

        public NativeBridgeResult<bool> IsWindowPinned(nint windowHandle)
        {
            PinQueryCount++;
            LastWindowHandle = windowHandle;
            if (PinException is not null)
            {
                throw PinException;
            }

            return PinQueryResult;
        }

        public NativeBridgeResult PinWindow(nint windowHandle)
        {
            PinCount++;
            LastWindowHandle = windowHandle;
            if (PinException is not null)
            {
                throw PinException;
            }

            return PinResult;
        }

        public NativeBridgeResult UnpinWindow(nint windowHandle)
        {
            UnpinCount++;
            LastWindowHandle = windowHandle;
            if (PinException is not null)
            {
                throw PinException;
            }

            return UnpinResult;
        }

        public void Dispose()
        {
        }
    }

    /// <summary>
    /// A manager that answers desktop queries and moves, and nothing else.
    /// </summary>
    /// <remarks>
    /// It deliberately does not implement the pin members, so a test can prove
    /// what the seam's own default answers on behalf of an implementation that
    /// never learned to pin.
    /// </remarks>
    private sealed class MoveOnlyDesktopManagerApi :
        IDocumentedVirtualDesktopManagerApi,
        IValidatedWindowDesktopMover
    {
        public DocumentedDesktopIdResult GetWindowDesktopId(nint windowHandle)
        {
            _ = windowHandle;
            return new DocumentedDesktopIdResult(
                DesktopId,
                0,
                "GetWindowDesktopId");
        }

        public NativeBridgeResult MoveWindowToDesktop(
            nint windowHandle,
            Guid desktopId)
        {
            _ = windowHandle;
            _ = desktopId;
            return NativeBridgeResult.Succeeded;
        }

        public void Dispose()
        {
        }
    }
}

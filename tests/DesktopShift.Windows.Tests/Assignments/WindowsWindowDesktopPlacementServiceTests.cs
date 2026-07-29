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

        public int GetCount { get; private set; }

        public int MoveCount { get; private set; }

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

        public void Dispose()
        {
        }
    }
}

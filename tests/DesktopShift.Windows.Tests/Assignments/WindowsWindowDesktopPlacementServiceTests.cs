using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Assignments;

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

        using WindowsWindowDesktopPlacementService childService = CreateService(
            new FakeWindowHandleApi { RootWindow = (nint)0x9999 },
            desktopManager);

        DesktopTopologyProviderResult<Guid> child =
            await childService.GetWindowDesktopIdAsync(WindowHandle);

        Assert.AreEqual(
            "window_placement.not_top_level_window",
            child.Error?.Code);
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
    public async Task GetDesktop_RejectsEmptySuccessfulGuid()
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

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.invalid_desktop_result",
            result.Error?.Code);
        Assert.AreEqual(unchecked((int)0x8000FFFF), result.Error?.HResult);
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
            MoveResult = new DocumentedDesktopOperationResult(
                hResult,
                "MoveWindowToDesktop"),
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
        new(windowApi, desktopManager);

    private sealed class FakeWindowHandleApi : IWindowHandleApi
    {
        public bool IsLive { get; init; } = true;

        public nint RootWindow { get; init; } = WindowHandle;

        public int ValidationCount { get; private set; }

        public bool IsWindow(nint windowHandle)
        {
            Assert.AreEqual(WindowHandle, windowHandle);
            ValidationCount++;
            return IsLive;
        }

        public nint GetRootWindow(nint windowHandle)
        {
            Assert.AreEqual(WindowHandle, windowHandle);
            ValidationCount++;
            return RootWindow;
        }
    }

    private sealed class FakeDesktopManagerApi :
        IDocumentedVirtualDesktopManagerApi
    {
        public DocumentedDesktopIdResult GetResult { get; init; } =
            new(DesktopId, 0, "GetWindowDesktopId");

        public DocumentedDesktopOperationResult MoveResult { get; init; } =
            new(0, "MoveWindowToDesktop");

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

        public DocumentedDesktopOperationResult MoveWindowToDesktop(
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

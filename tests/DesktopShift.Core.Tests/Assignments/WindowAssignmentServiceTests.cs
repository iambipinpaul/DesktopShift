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
    [DataRow("window_placement.window_not_tracked")]
    [DataRow("window_placement.stale_window_handle")]
    public async Task TransientPlacementLookupFailure_IsSkippedWithDiagnostics(
        string errorCode)
    {
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

    private sealed class StubPlacementService(
        DesktopTopologyProviderResult<Guid> currentDesktopResult,
        DesktopTopologyProviderResult? moveResult = null) :
        IWindowDesktopPlacementService
    {
        public int MoveCallCount { get; private set; }

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(currentDesktopResult);

        public ValueTask<DesktopTopologyProviderResult>
            MoveWindowToDesktopAsync(
                nint windowHandle,
                Guid desktopId,
                CancellationToken cancellationToken = default)
        {
            MoveCallCount++;
            return ValueTask.FromResult(
                moveResult ?? DesktopTopologyProviderResult.Succeeded());
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
}

using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.VirtualDesktops;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.Tests.Assignments;

/// <summary>
/// Proves the Windows placement layer's structured refusals survive the whole
/// way to an activity record: the code, the <c>HRESULT</c>, and the privacy-safe
/// identity all arrive intact, and nothing else does.
/// </summary>
[TestClass]
public sealed class WindowPlacementDiagnosticsTests
{
    private static readonly nint WindowHandle = (nint)0x1234;
    private static readonly Guid CodeDesktopId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly Guid OtherDesktopId =
        Guid.Parse("33333333-3333-3333-3333-333333333333");

    [TestMethod]
    public async Task DeniedMove_ReachesTheActivityRecordWithItsHResult()
    {
        ImmutableArray<ActivityRecord> records = await AssignAsync(
            FailedMove(unchecked((int)0x80070005)));

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Failed, move.Result);
        Assert.AreEqual("move.failed", move.ResultCode);
        Assert.AreEqual(
            "window_placement.move_access_denied",
            move.Error!.Code);
        Assert.AreEqual("0x80070005", move.Error.HResultText);
    }

    [TestMethod]
    public async Task ManagerActivationFailure_KeepsItsCodeAndHResult()
    {
        ImmutableArray<ActivityRecord> records = await AssignAsync(
            FailedMove(
                unchecked((int)0x80040154),
                "ManagerActivation"));

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(
            "window_placement.manager_activation_failed",
            move.Error!.Code);
        Assert.AreEqual("0x80040154", move.Error.HResultText);
    }

    [TestMethod]
    public async Task RefusedMove_KeepsTheExactHResultWindowsReturned()
    {
        ImmutableArray<ActivityRecord> records = await AssignAsync(
            FailedMove(unchecked((int)0x8002802B)));

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual("window_placement.move_failed", move.Error!.Code);
        Assert.AreEqual("0x8002802B", move.Error.HResultText);
    }

    [TestMethod]
    public async Task SuccessfulMove_IsRecordedWithoutAnError()
    {
        ImmutableArray<ActivityRecord> records = await AssignAsync(
            NativeBridgeResult.Succeeded);

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Succeeded, move.Result);
        Assert.IsNull(move.Error);
    }

    [TestMethod]
    public async Task RecordedFailures_CarryOnlyPrivacySafeIdentity()
    {
        ImmutableArray<ActivityRecord> records = await AssignAsync(
            FailedMove(unchecked((int)0x80070005)));

        foreach (ActivityRecord record in records)
        {
            Assert.AreEqual("Code.exe", record.Identity!.ProcessName);
            Assert.AreEqual("CodeWindow", record.Identity.WindowClass);
            Assert.DoesNotContain("Salaries", record.Summary);
            Assert.DoesNotContain(
                "Salaries",
                record.Error?.Message ?? string.Empty);
        }
    }

    private static async Task<ImmutableArray<ActivityRecord>> AssignAsync(
        NativeBridgeResult moveResult)
    {
        FakeDesktopManagerApi desktopManager = new()
        {
            GetResult = new DocumentedDesktopIdResult(
                OtherDesktopId,
                0,
                "GetWindowDesktopId"),
            MoveResult = moveResult,
        };
        using WindowsWindowDesktopPlacementService placement = new(
            new FakeWindowHandleApi(),
            desktopManager,
            desktopManager);
        BoundedWindowAssignmentActivityStore store = new();
        WindowAssignmentService service = new(
            placement,
            new BoundReconciliationService(),
            store,
            TimeProvider.System);

        WindowAssignmentActivity activity = await service.AssignAsync(
            new WindowAssignmentRequest(
                WindowEventKind.Created,
                WindowHandle,
                new ConfigurationWindowRuleSource(ConfigurationDefaults.Create)
                    .GetRules()
                    .Single(static rule => rule.TargetDesktopKey == "ide-development"),
                new WindowSafeIdentity(
                    "Code.exe",
                    PackageFamilyName: null,
                    AppUserModelId: null,
                    "CodeWindow"),
                Guid.NewGuid()));

        return ActivityRecordFactory.FromAssignment(activity, Guid.NewGuid());
    }

    private static NativeBridgeResult FailedMove(
        int hResult,
        string stage = "WindowMove") =>
        NativeBridgeResult.Failed(
            new NativeBridgeError(
                "native.window_move",
                stage,
                "Test window move failure.",
                hResult));

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
                "ide-development",
                "Code",
                1,
                true,
                CodeDesktopId,
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

    private sealed class FakeWindowHandleApi : IWindowHandleApi
    {
        public bool IsWindow(nint windowHandle) => true;

        public nint GetRootWindow(nint windowHandle) => WindowHandle;
    }

    private sealed class FakeDesktopManagerApi :
        IDocumentedVirtualDesktopManagerApi,
        IValidatedWindowDesktopMover
    {
        public DocumentedDesktopIdResult GetResult { get; init; } =
            new(OtherDesktopId, 0, "GetWindowDesktopId");

        public NativeBridgeResult MoveResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public DocumentedDesktopIdResult GetWindowDesktopId(nint windowHandle) =>
            GetResult;

        public NativeBridgeResult MoveWindowToDesktop(
            nint windowHandle,
            Guid desktopId) =>
            MoveResult;

        public void Dispose()
        {
        }
    }
}

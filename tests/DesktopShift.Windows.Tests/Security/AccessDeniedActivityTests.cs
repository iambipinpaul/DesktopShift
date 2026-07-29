using System.Runtime.InteropServices;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.Observation;
using Microsoft.Win32.SafeHandles;

namespace DesktopShift.Windows.Tests.Security;

/// <summary>
/// Follows an elevated application's window from the Windows refusal to the row
/// a user reads.
/// </summary>
/// <remarks>
/// <para>
/// An application running with higher rights than DesktopShift refuses both
/// questions DesktopShift asks about it: which desktop its window is on, and
/// whether the window may move. Windows answers <c>E_ACCESSDENIED</c> to the
/// first and <c>ERROR_ACCESS_DENIED</c> to the second, and both answers have to
/// survive the trip into Activity — the exact code, so it can be looked up, and
/// an explanation, so it does not read as a bug.
/// </para>
/// <para>
/// The same trip must carry nothing else. The window's title, its executable
/// path, and its command line are all known at the point of failure and none of
/// them may appear in the record.
/// </para>
/// </remarks>
[TestClass]
public sealed class AccessDeniedActivityTests
{
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int ErrorAccessDenied = 5;
    private static readonly nint WindowHandle = (nint)0x2468;
    private static readonly Guid DesktopId =
        Guid.Parse("22222222-2222-2222-2222-222222222222");

    [TestMethod]
    public async Task DeniedDesktopQuery_IsReportedAsDeniedRatherThanUnknown()
    {
        using WindowsWindowDesktopPlacementService service = new(
            new LiveWindowApi(),
            new DenyingDesktopManagerApi());

        DesktopTopologyProviderResult<Guid> result =
            await service.GetWindowDesktopIdAsync(WindowHandle);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            "window_placement.query_access_denied",
            result.Error!.Code);
        Assert.AreEqual(AccessDeniedHResult, result.Error.HResult);
        StringAssert.Contains(result.Error.Message, "denied access");
        StringAssert.Contains(
            result.Error.Message,
            PrivilegeBoundary.DeniedWindowExplanation);
    }

    [TestMethod]
    public async Task DeniedMove_ExplainsTheElevatedApplicationAndKeepsTheHResult()
    {
        using WindowsWindowDesktopPlacementService service = new(
            new LiveWindowApi(),
            new DenyingDesktopManagerApi());

        DesktopTopologyProviderResult result =
            await service.MoveWindowToDesktopAsync(WindowHandle, DesktopId);

        Assert.AreEqual(
            "window_placement.move_access_denied",
            result.Error!.Code);
        Assert.AreEqual(AccessDeniedHResult, result.Error.HResult);
        StringAssert.Contains(
            result.Error.Message,
            "running with higher privileges than DesktopShift");
        StringAssert.Contains(
            result.Error.Message,
            "does not request elevation");
    }

    [TestMethod]
    public void DeniedMove_ReachesActivityWithItsHResultAndNoSensitiveDetail()
    {
        WindowIdentity sensitive = new(
            4242,
            "elevated-app.exe",
            @"C:\Users\marguerite\Tools\elevated-app.exe",
            PackageFamilyName: null,
            AppUserModelId: null,
            "ElevatedAppWindow",
            "Payroll 2026 — Confidential",
            "--token=hunter2 --url=https://intranet.example/payroll");

        WindowAssignmentActivity activity = new(
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            TimeSpan.FromMilliseconds(3),
            WindowEventKind.Created,
            WindowHandle,
            WindowAssignmentOutcome.Failed,
            WindowAssignmentSkipReason.None,
            "elevated-tools",
            "tools",
            DesktopId,
            Guid.NewGuid(),
            sensitive.ToSafeIdentity(),
            new WindowAssignmentError(
                "window_placement.move_access_denied",
                "Windows denied access to the window, so it was not moved to the requested virtual desktop. " +
                PrivilegeBoundary.DeniedWindowExplanation,
                AccessDeniedHResult),
            WindowMoveOutcome.Failed);

        ActivityRecord move = ActivityRecordFactory
            .FromAssignment(activity, Guid.NewGuid())
            .Single(static record => record.Source == ActivityEventSource.Move);

        Assert.AreEqual(ActivityResult.Failed, move.Result);
        Assert.AreEqual("move.failed", move.ResultCode);
        Assert.AreEqual(AccessDeniedHResult, move.Error!.HResult);

        // The conventional hexadecimal form is what a user can search for.
        Assert.AreEqual("0x80070005", move.Error.HResultText);
        StringAssert.Contains(move.Error.Message, "does not request elevation");

        AssertNothingSensitiveEscaped(
            ActivityRecordDisplay.Create(move).CopyText,
            sensitive);
    }

    [TestMethod]
    public void DeniedIdentity_IsSkippedWithTheWindowsErrorAndNoIdentityGuess()
    {
        // An elevated process refuses even the limited query handle, so nothing
        // beyond the window handle is ever known. The row has to say that
        // plainly instead of inventing a name.
        WindowObservationActivity activity = new(
            DateTimeOffset.UtcNow,
            42,
            WindowEventKind.Shown,
            WindowHandle,
            WindowObservationOutcome.Skipped,
            WindowSkipReason.IdentityAccessDenied,
            RuleId: null,
            TargetDesktopKey: null,
            Identity: null,
            NativeErrorCode: ErrorAccessDenied,
            CorrelationId: Guid.NewGuid());

        ActivityRecord record =
            ActivityRecordFactory.FromObservation(activity, Guid.NewGuid());

        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual(
            "observation.skipped.identity_access_denied",
            record.ResultCode);
        Assert.AreEqual(ErrorAccessDenied, record.Error!.NativeErrorCode);
        Assert.IsNull(record.Identity);
        Assert.IsNull(record.Application);

        ActivityRecordDisplay display = ActivityRecordDisplay.Create(record);
        StringAssert.Contains(display.ErrorDetails, "Windows error 5");
        StringAssert.Contains(display.Summary, "Identity access denied");
    }

    [TestMethod]
    public async Task DeniedIdentity_NeverWidensTheRequestedRightOnRetry()
    {
        // The tempting fix for a denial is a bigger access mask. The resolver
        // must refuse that: one attempt, one right, and a stated failure.
        DenyingProcessApi processApi = new();
        WindowsProcessIdentityResolver resolver = new(
            new LiveIdentityWindowApi(),
            processApi);
        QualifiedWindow window = new(WindowHandle, WindowHandle, 4242, "Elevated");

        WindowIdentityResolution first = await resolver.ResolveAsync(window);
        WindowIdentityResolution second = await resolver.ResolveAsync(window);

        Assert.AreEqual(
            WindowIdentityResolutionFailure.AccessDenied,
            first.Failure);
        Assert.AreEqual(ErrorAccessDenied, first.NativeErrorCode);
        Assert.AreEqual(first.Failure, second.Failure);
        Assert.AreEqual(2, processApi.OpenCount);
        Assert.IsEmpty(
            processApi.RequestedAccessMasks
                .Where(
                    static mask =>
                        mask != ProcessAccessRights.QueryLimitedInformation)
                .ToArray());
    }

    private static void AssertNothingSensitiveEscaped(
        string text,
        WindowIdentity sensitive)
    {
        Assert.DoesNotContain(sensitive.WindowTitle!, text);
        Assert.DoesNotContain(sensitive.CommandLine!, text);
        Assert.DoesNotContain(sensitive.ExecutablePath!, text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("https://", text);
        Assert.DoesNotContain("marguerite", text);
        Assert.Contains(sensitive.ProcessName, text);
    }

    private sealed class LiveWindowApi : IWindowHandleApi
    {
        public bool IsWindow(nint windowHandle) => true;

        public nint GetRootWindow(nint windowHandle) => windowHandle;
    }

    private sealed class DenyingDesktopManagerApi :
        IDocumentedVirtualDesktopManagerApi
    {
        public DocumentedDesktopIdResult GetWindowDesktopId(nint windowHandle) =>
            new(Guid.Empty, AccessDeniedHResult, "GetWindowDesktopId");

        public DocumentedDesktopOperationResult MoveWindowToDesktop(
            nint windowHandle,
            Guid desktopId) =>
            new(AccessDeniedHResult, "MoveWindowToDesktop");

        public void Dispose()
        {
        }
    }

    private sealed class DenyingProcessApi : IWindowsProcessIdentityApi
    {
        public List<uint> RequestedAccessMasks { get; } = [];

        public int OpenCount => RequestedAccessMasks.Count;

        public SafeProcessHandle OpenProcess(uint desiredAccess, uint processId)
        {
            RequestedAccessMasks.Add(desiredAccess);
            Marshal.SetLastPInvokeError(ErrorAccessDenied);
            return new SafeProcessHandle(nint.Zero, ownsHandle: false);
        }

        public string? TryGetExecutablePath(SafeProcessHandle process) => null;

        public string? TryGetPackageFamilyName(SafeProcessHandle process) => null;

        public string? TryGetAppUserModelId(SafeProcessHandle process) => null;
    }

    private sealed class LiveIdentityWindowApi : IWindowsWindowNativeApi
    {
        public bool IsWindow(nint windowHandle) => true;

        public bool IsWindowVisible(nint windowHandle) => true;

        public nint GetRootOwner(nint windowHandle) => windowHandle;

        public nint GetOwner(nint windowHandle) => 0;

        public long GetWindowStyle(nint windowHandle) => 0;

        public long GetWindowExtendedStyle(nint windowHandle) => 0;

        public bool IsCloaked(nint windowHandle) => false;

        public nint GetShellWindow() => 0;

        public nint GetDesktopWindow() => 0;

        public uint GetWindowProcessId(nint windowHandle) => 4242;

        public string GetWindowClass(nint windowHandle) => "Elevated";

        public string? GetWindowTitle(nint windowHandle)
        {
            Assert.Fail("A window title must never be read.");
            return null;
        }
    }
}

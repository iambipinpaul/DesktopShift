using DesktopShift.Core.Observation;
using DesktopShift.Windows.Observation;
using Microsoft.Win32.SafeHandles;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowsProcessIdentityResolverTests
{
    [TestMethod]
    public async Task Resolve_UsesQueryLimitedInformation_AndSensitiveFieldsAreOptIn()
    {
        FakeWindowApi windowApi = new();
        FakeProcessApi processApi = new();
        WindowsProcessIdentityResolver resolver = new(windowApi, processApi);
        QualifiedWindow window = new(
            (nint)10,
            (nint)10,
            123,
            "ApplicationWindow");

        WindowIdentityResolution result = await resolver.ResolveAsync(window);

        Assert.IsTrue(result.IsSuccessful);
        Assert.AreEqual(0x1000U, processApi.RequestedAccess);
        Assert.AreEqual(123U, processApi.RequestedProcessId);
        Assert.AreEqual("Code.exe", result.Identity!.ProcessName);
        Assert.AreEqual(
            "Package_family_hash",
            result.Identity.PackageFamilyName);
        Assert.AreEqual("Package!App", result.Identity.AppUserModelId);
        Assert.IsNull(result.Identity.WindowTitle);
        Assert.IsNull(result.Identity.CommandLine);
        Assert.AreEqual(0, windowApi.TitleReadCount);
    }

    [TestMethod]
    public async Task Resolve_HwndReusedByAnotherProcessDuringRead_ReturnsStale()
    {
        FakeWindowApi windowApi = new();
        windowApi.ProcessIds.Enqueue(123);
        windowApi.ProcessIds.Enqueue(456);
        FakeProcessApi processApi = new();
        WindowsProcessIdentityResolver resolver = new(windowApi, processApi);
        QualifiedWindow window = new(
            (nint)10,
            (nint)10,
            123,
            "ApplicationWindow");

        WindowIdentityResolution result = await resolver.ResolveAsync(window);

        Assert.IsFalse(result.IsSuccessful);
        Assert.AreEqual(
            WindowIdentityResolutionFailure.StaleWindow,
            result.Failure);
        Assert.AreEqual(1, processApi.OpenCount);
    }

    private sealed class FakeProcessApi : IWindowsProcessIdentityApi
    {
        public uint RequestedAccess { get; private set; }

        public uint RequestedProcessId { get; private set; }

        public int OpenCount { get; private set; }

        public SafeProcessHandle OpenProcess(
            uint desiredAccess,
            uint processId)
        {
            OpenCount++;
            RequestedAccess = desiredAccess;
            RequestedProcessId = processId;
            return new SafeProcessHandle((nint)1, ownsHandle: false);
        }

        public string? TryGetExecutablePath(SafeProcessHandle process) =>
            @"C:\Apps\Code.exe";

        public string? TryGetPackageFamilyName(
            SafeProcessHandle process) =>
            "Package_family_hash";

        public string? TryGetAppUserModelId(SafeProcessHandle process) =>
            "Package!App";
    }

    private sealed class FakeWindowApi : IWindowsWindowNativeApi
    {
        public Queue<uint> ProcessIds { get; } = [];

        public int TitleReadCount { get; private set; }

        public bool IsWindow(nint windowHandle) => true;

        public bool IsWindowVisible(nint windowHandle) => true;

        public nint GetRootOwner(nint windowHandle) => windowHandle;

        public long GetWindowStyle(nint windowHandle) => 0;

        public long GetWindowExtendedStyle(nint windowHandle) => 0;

        public bool IsCloaked(nint windowHandle) => false;

        public nint GetShellWindow() => 0;

        public nint GetDesktopWindow() => 0;

        public uint GetWindowProcessId(nint windowHandle) =>
            ProcessIds.TryDequeue(out uint processId)
                ? processId
                : 123;

        public string GetWindowClass(nint windowHandle) =>
            "ApplicationWindow";

        public string? GetWindowTitle(nint windowHandle)
        {
            TitleReadCount++;
            return "Sensitive title";
        }
    }
}

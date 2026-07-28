using DesktopShift.Core.Observation;
using DesktopShift.Windows.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowsWindowClassifierTests
{
    [TestMethod]
    public void Qualify_NormalWindow_ReturnsRootOwner()
    {
        FakeWindowApi api = FakeWindowApi.Normal();
        api.RootOwner = (nint)100;
        api.ProcessId = 77;
        WindowsWindowClassifier classifier = new(api, currentProcessId: 99);

        WindowQualification result = classifier.Qualify((nint)101);

        Assert.IsTrue(result.IsQualified);
        Assert.AreEqual((nint)101, result.Window!.OriginalWindowHandle);
        Assert.AreEqual((nint)100, result.Window.RootWindowHandle);
        Assert.AreEqual(77U, result.Window.ProcessId);
    }

    [TestMethod]
    public void Qualify_OwnedDialog_NormalizesToVisibleRootOwner()
    {
        FakeWindowApi api = FakeWindowApi.Normal();
        api.RootOwner = (nint)50;
        WindowsWindowClassifier classifier = new(api, currentProcessId: 99);

        WindowQualification result = classifier.Qualify((nint)51);

        Assert.AreEqual((nint)50, result.Window!.RootWindowHandle);
        Assert.AreEqual(WindowSkipReason.None, result.SkipReason);
    }

    [TestMethod]
    [DataRow(0x40000000L, 0L, false, true, "ApplicationWindow", WindowSkipReason.ChildWindow)]
    [DataRow(0L, 0x80L, false, true, "ApplicationWindow", WindowSkipReason.ToolWindow)]
    [DataRow(0L, 0L, true, true, "ApplicationWindow", WindowSkipReason.CloakedWindow)]
    [DataRow(0L, 0L, false, false, "ApplicationWindow", WindowSkipReason.InvisibleWindow)]
    [DataRow(0L, 0L, false, true, "tooltips_class32", WindowSkipReason.TransientWindow)]
    [DataRow(0L, 0L, false, true, "Shell_TrayWnd", WindowSkipReason.ShellWindow)]
    public void Qualify_NonApplicationSurface_IsSkipped(
        long style,
        long extendedStyle,
        bool cloaked,
        bool visible,
        string windowClass,
        WindowSkipReason expected)
    {
        FakeWindowApi api = FakeWindowApi.Normal();
        api.Style = style;
        api.ExtendedStyle = extendedStyle;
        api.Cloaked = cloaked;
        api.Visible = visible;
        api.WindowClass = windowClass;

        WindowQualification result =
            new WindowsWindowClassifier(api, currentProcessId: 99)
                .Qualify((nint)10);

        Assert.AreEqual(expected, result.SkipReason);
    }

    [TestMethod]
    public void Qualify_StaleHandle_IsSkippedWithoutFurtherNativeInspection()
    {
        FakeWindowApi api = FakeWindowApi.Normal();
        api.WindowExists = false;

        WindowQualification result =
            new WindowsWindowClassifier(api, currentProcessId: 99)
                .Qualify((nint)10);

        Assert.AreEqual(WindowSkipReason.StaleWindow, result.SkipReason);
        Assert.AreEqual(0, api.RootOwnerCalls);
    }

    [TestMethod]
    public void ClassifyIdentity_SystemAndDesktopShiftProcesses_AreSkipped()
    {
        WindowsWindowClassifier classifier =
            new(FakeWindowApi.Normal(), currentProcessId: 99);
        QualifiedWindow window = new((nint)1, (nint)1, 20, "Window");

        Assert.AreEqual(
            WindowSkipReason.SystemWindow,
            classifier.ClassifyIdentity(
                window,
                CreateIdentity("StartMenuExperienceHost.exe")));
        Assert.AreEqual(
            WindowSkipReason.DesktopShiftWindow,
            classifier.ClassifyIdentity(
                window,
                CreateIdentity("DesktopShift.App.exe")));
    }

    private static WindowIdentity CreateIdentity(string processName) =>
        new(20, processName, null, null, null, "Window", null, null);

    private sealed class FakeWindowApi : IWindowsWindowNativeApi
    {
        public bool WindowExists { get; set; }

        public bool Visible { get; set; }

        public nint RootOwner { get; set; }

        public long Style { get; set; }

        public long ExtendedStyle { get; set; }

        public bool Cloaked { get; set; }

        public nint ShellWindow { get; set; }

        public nint DesktopWindow { get; set; }

        public uint ProcessId { get; set; }

        public string WindowClass { get; set; } = string.Empty;

        public int RootOwnerCalls { get; private set; }

        public static FakeWindowApi Normal() => new()
        {
            WindowExists = true,
            Visible = true,
            RootOwner = (nint)10,
            ProcessId = 20,
            WindowClass = "ApplicationWindow",
            ShellWindow = (nint)800,
            DesktopWindow = (nint)900,
        };

        public bool IsWindow(nint windowHandle) => WindowExists;

        public bool IsWindowVisible(nint windowHandle) => Visible;

        public nint GetRootOwner(nint windowHandle)
        {
            RootOwnerCalls++;
            return RootOwner;
        }

        public long GetWindowStyle(nint windowHandle) => Style;

        public long GetWindowExtendedStyle(nint windowHandle) => ExtendedStyle;

        public bool IsCloaked(nint windowHandle) => Cloaked;

        public nint GetShellWindow() => ShellWindow;

        public nint GetDesktopWindow() => DesktopWindow;

        public uint GetWindowProcessId(nint windowHandle) => ProcessId;

        public string GetWindowClass(nint windowHandle) => WindowClass;

        public string? GetWindowTitle(nint windowHandle) => "Sensitive title";
    }
}

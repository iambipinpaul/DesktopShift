using DesktopShift.Windows.Hotkeys;

namespace DesktopShift.Windows.Tests.Hotkeys;

[TestClass]
public sealed class WindowsForegroundWindowProviderTests
{
    [TestMethod]
    public void TheProvider_ReturnsTheWindowReportedByUser32()
    {
        StubForegroundWindowNativeApi native = new((nint)0x1234);
        WindowsForegroundWindowProvider provider = new(native);

        nint window = provider.GetForegroundWindow();

        Assert.AreEqual((nint)0x1234, window);
        Assert.AreEqual(1, native.CallCount);
    }

    private sealed class StubForegroundWindowNativeApi(nint windowHandle)
        : IWindowsForegroundWindowNativeApi
    {
        public int CallCount { get; private set; }

        public nint GetForegroundWindow()
        {
            CallCount++;
            return windowHandle;
        }
    }
}

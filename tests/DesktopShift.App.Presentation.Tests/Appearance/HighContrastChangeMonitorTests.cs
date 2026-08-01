using System.Runtime.InteropServices;
using DesktopShift.App.Appearance;

namespace DesktopShift.App.Presentation.Tests.Appearance;

[TestClass]
public sealed class HighContrastChangeMonitorTests
{
    private const int ElementNotFoundHResult = unchecked((int)0x80070490);

    [TestMethod]
    public void ElementNotFoundWhileSubscribing_DoesNotPreventStartup()
    {
        int unsubscribeCalls = 0;

        using HighContrastChangeMonitor monitor = new(
            () => false,
            () => throw new COMException("Element not found", ElementNotFoundHResult),
            () => unsubscribeCalls++);

        Assert.IsFalse(monitor.IsChangeNotificationAvailable);
        Assert.IsFalse(monitor.IsHighContrast);
        Assert.AreEqual(0, unsubscribeCalls);
    }

    [TestMethod]
    public void ElementNotFoundWhileReadingState_SafelySuppressesCustomColors()
    {
        using HighContrastChangeMonitor monitor = new(
            () => throw new COMException("Element not found", ElementNotFoundHResult),
            () => { },
            () => { });

        Assert.IsTrue(monitor.IsHighContrast);
    }

    [TestMethod]
    public void SuccessfulSubscription_IsReleasedExactlyOnce()
    {
        int unsubscribeCalls = 0;
        HighContrastChangeMonitor monitor = new(
            () => true,
            () => { },
            () => unsubscribeCalls++);

        Assert.IsTrue(monitor.IsChangeNotificationAvailable);
        Assert.IsTrue(monitor.IsHighContrast);

        monitor.Dispose();
        monitor.Dispose();

        Assert.AreEqual(1, unsubscribeCalls);
    }
}

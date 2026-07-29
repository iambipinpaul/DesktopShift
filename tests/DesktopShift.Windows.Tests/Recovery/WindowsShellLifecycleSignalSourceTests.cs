using DesktopShift.Windows.Recovery;

namespace DesktopShift.Windows.Tests.Recovery;

[TestClass]
public sealed class WindowsShellLifecycleSignalSourceTests
{
    [TestMethod]
    public void SeparateListeners_NeverReuseAnInstanceBoundWindowClass()
    {
        using WindowsShellLifecycleSignalSource first = new();
        using WindowsShellLifecycleSignalSource second = new();

        Assert.AreNotEqual(first.WindowClassName, second.WindowClassName);
        StringAssert.StartsWith(
            first.WindowClassName,
            "DesktopShift.ShellLifecycleListener.");
        StringAssert.StartsWith(
            second.WindowClassName,
            "DesktopShift.ShellLifecycleListener.");
    }
}

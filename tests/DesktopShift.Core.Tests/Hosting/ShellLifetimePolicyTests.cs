using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

[TestClass]
public sealed class ShellLifetimePolicyTests
{
    [TestMethod]
    public void ResolveLaunch_StartMinimizedAfterFirstRunStaysInNotificationArea()
    {
        BehaviorSettings behavior = new(
            StartWithWindows: true,
            StartMinimized: true,
            CloseToTray: true);

        Assert.AreEqual(
            ShellLaunchDisposition.StayInNotificationArea,
            ShellLifetimePolicy.ResolveLaunch(behavior, isFirstRunComplete: true));
    }

    [TestMethod]
    public void ResolveLaunch_StartMinimizedBeforeFirstRunStillShowsTheWindow()
    {
        BehaviorSettings behavior = new(
            StartWithWindows: true,
            StartMinimized: true,
            CloseToTray: true);

        // Setup lives inside the main window, so a hidden first launch would
        // leave the user with no way to complete it.
        Assert.AreEqual(
            ShellLaunchDisposition.ShowWindow,
            ShellLifetimePolicy.ResolveLaunch(behavior, isFirstRunComplete: false));
    }

    [TestMethod]
    public void ResolveLaunch_WithoutStartMinimizedShowsTheWindow()
    {
        BehaviorSettings behavior = new(
            StartWithWindows: false,
            StartMinimized: false,
            CloseToTray: true);

        Assert.AreEqual(
            ShellLaunchDisposition.ShowWindow,
            ShellLifetimePolicy.ResolveLaunch(behavior, isFirstRunComplete: true));
    }

    [TestMethod]
    public void ResolveClose_FollowsTheCloseToTraySetting()
    {
        BehaviorSettings closeToTray = new(
            StartWithWindows: false,
            StartMinimized: false,
            CloseToTray: true);
        BehaviorSettings closeExits = closeToTray with { CloseToTray = false };

        Assert.AreEqual(
            ShellCloseDisposition.HideToNotificationArea,
            ShellLifetimePolicy.ResolveClose(closeToTray));
        Assert.AreEqual(
            ShellCloseDisposition.ExitApplication,
            ShellLifetimePolicy.ResolveClose(closeExits));
    }
}

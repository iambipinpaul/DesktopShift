using DesktopShift.App.Tray;

namespace DesktopShift.App.Presentation.Tests.Tray;

[TestClass]
public sealed class TrayMenuPresentationTests
{
    [TestMethod]
    public void Create_ProvidesOpenReassignPauseRecentErrorAndExit()
    {
        TrayMenuModel menu = TrayMenuPresentation.Create(
            isPaused: false,
            isReassignmentRunning: false,
            recentIssue: null);

        CollectionAssert.AreEqual(
            new[]
            {
                TrayCommand.Open,
                TrayCommand.ReassignAll,
                TrayCommand.TogglePause,
                TrayCommand.ShowRecentIssue,
                TrayCommand.Exit,
            },
            menu.Items.Select(item => item.Command).ToArray());
        Assert.AreEqual(TrayMenuPresentation.OpenLabel, menu.Items[0].Label);
        Assert.AreEqual(TrayMenuPresentation.ExitLabel, menu.Items[^1].Label);
        Assert.IsTrue(menu.Items[^1].HasSeparatorBefore);
    }

    [TestMethod]
    public void Create_PausedMenuOffersResume()
    {
        TrayMenuModel menu = TrayMenuPresentation.Create(
            isPaused: true,
            isReassignmentRunning: false,
            recentIssue: null);

        Assert.AreEqual(TrayMenuPresentation.ResumeLabel, menu.Items[2].Label);
        Assert.IsTrue(menu.Items[2].IsEnabled);
        Assert.Contains("paused", menu.Tooltip);
    }

    [TestMethod]
    public void Create_RecentIssueBecomesTheEntryLabel()
    {
        RecentIssue issue = new(
            RecentIssueKind.AssignmentFailure,
            "Move failed",
            "Couldn't move Code.exe to code.",
            DateTimeOffset.UnixEpoch);

        TrayMenuModel menu = TrayMenuPresentation.Create(
            isPaused: false,
            isReassignmentRunning: false,
            issue);

        Assert.AreEqual("Move failed", menu.Items[3].Label);
        Assert.IsTrue(menu.Items[3].IsEnabled);
        Assert.Contains("Move failed", menu.Tooltip);
    }

    [TestMethod]
    public void Create_TooltipStaysWithinTheShellLimit()
    {
        RecentIssue issue = new(
            RecentIssueKind.AssignmentFailure,
            new string('x', 400),
            "detail",
            DateTimeOffset.UnixEpoch);

        TrayMenuModel menu = TrayMenuPresentation.Create(
            isPaused: false,
            isReassignmentRunning: false,
            issue);

        Assert.AreEqual(TrayMenuPresentation.MaxTooltipLength, menu.Tooltip.Length);
    }
}

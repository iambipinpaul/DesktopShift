using DesktopShift.App.Tray;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;

namespace DesktopShift.App.Presentation.Tests.Tray;

/// <summary>
/// Drives the notification-area shell end to end: open, hide, restore, pause,
/// reassign, startup state, and exit.
/// </summary>
/// <remarks>
/// These are the packaged smoke tests, run against the coordinator rather than
/// a real icon. Every decision the packaged application makes lives here; what
/// the packaged run adds is the Win32 plumbing — the icon, the popup menu, and
/// the window handle — which no automated test on this machine may exercise,
/// because doing so would move the developer's real desktops and windows.
/// </remarks>
[TestClass]
public sealed class NotificationAreaSmokeTests
{
    [TestMethod]
    public async Task Start_ShowsTheWindowAndPlacesTheIcon()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();

        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        Assert.IsTrue(fixture.Window.IsVisible);
        Assert.AreEqual(1, fixture.Window.ShowCount);
        Assert.IsNotNull(fixture.TrayIcon.LastMenu);
        Assert.HasCount(5, fixture.TrayIcon.LastMenu.Items);
    }

    [TestMethod]
    public async Task Start_StartMinimizedLeavesOnlyTheNotificationArea()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();

        coordinator.Start(ShellLaunchDisposition.StayInNotificationArea);

        Assert.IsFalse(fixture.Window.IsVisible);
        Assert.AreEqual(0, fixture.Window.ShowCount);
        Assert.AreEqual(1, fixture.Window.HideCount);
        Assert.IsNotNull(fixture.TrayIcon.LastMenu);
    }

    [TestMethod]
    public async Task Close_WithCloseToTrayHidesTheWindowAndRestoreReusesIt()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        ShellCloseDisposition disposition = coordinator.HandleWindowClosing(
            Behavior(closeToTray: true));

        Assert.AreEqual(ShellCloseDisposition.HideToNotificationArea, disposition);
        Assert.IsFalse(fixture.Window.IsVisible);

        fixture.TrayIcon.Click(TrayCommand.Open);
        await coordinator.PendingCommand;

        Assert.IsTrue(fixture.Window.IsVisible);

        // The same surface was shown again; nothing built a second shell.
        Assert.AreEqual(2, fixture.Window.ShowCount);
        Assert.AreEqual(1, fixture.Window.HideCount);
    }

    [TestMethod]
    public async Task Close_WithoutCloseToTrayExits()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        ShellCloseDisposition disposition = coordinator.HandleWindowClosing(
            Behavior(closeToTray: false));

        Assert.AreEqual(ShellCloseDisposition.ExitApplication, disposition);

        // The window is left alone: the close it was already handling proceeds.
        Assert.AreEqual(0, fixture.Window.HideCount);
        Assert.AreEqual(0, fixture.ExitCount);
    }

    [TestMethod]
    public async Task Activation_FromASecondInstanceRestoresTheExistingWindow()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.StayInNotificationArea);

        coordinator.HandleActivationRequested();

        Assert.IsTrue(fixture.Window.IsVisible);
        Assert.AreEqual(1, fixture.Window.ShowCount);
    }

    [TestMethod]
    public async Task Pause_TogglesTheStateAndTheMenuLabel()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        Assert.AreEqual(
            TrayMenuPresentation.PauseLabel,
            fixture.TrayIcon.ItemFor(TrayCommand.TogglePause).Label);

        fixture.TrayIcon.Click(TrayCommand.TogglePause);
        await coordinator.PendingCommand;

        Assert.IsTrue(coordinator.IsPaused);
        Assert.IsTrue(fixture.Pause.IsPaused);
        Assert.AreEqual(
            TrayMenuPresentation.ResumeLabel,
            fixture.TrayIcon.ItemFor(TrayCommand.TogglePause).Label);
        Assert.Contains("paused", fixture.TrayIcon.LastMenu!.Tooltip);

        fixture.TrayIcon.Click(TrayCommand.TogglePause);
        await coordinator.PendingCommand;

        Assert.IsFalse(coordinator.IsPaused);
        Assert.AreEqual(
            TrayMenuPresentation.PauseLabel,
            fixture.TrayIcon.ItemFor(TrayCommand.TogglePause).Label);
    }

    [TestMethod]
    public async Task ReassignAll_RunsWhilePausedAndNeverResumes()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);
        fixture.Pause.Pause();

        fixture.TrayIcon.Click(TrayCommand.ReassignAll);
        await coordinator.PendingCommand;

        Assert.AreEqual(1, fixture.Reassignment.ReassignAllCount);
        Assert.IsTrue(coordinator.IsPaused);
    }

    [TestMethod]
    public async Task ReassignAll_IsDisabledWhileABatchIsRunning()
    {
        TaskCompletionSource gate = new(TaskCreationOptions.RunContinuationsAsynchronously);
        NotificationAreaFixture fixture = new(new FakeReassignmentService(gate));
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        fixture.TrayIcon.Click(TrayCommand.ReassignAll);
        Task running = coordinator.PendingCommand;

        Assert.IsFalse(fixture.TrayIcon.ItemFor(TrayCommand.ReassignAll).IsEnabled);
        Assert.AreEqual(
            TrayMenuPresentation.ReassignRunningLabel,
            fixture.TrayIcon.ItemFor(TrayCommand.ReassignAll).Label);

        // A second click while the batch runs must not start another one.
        fixture.TrayIcon.Click(TrayCommand.ReassignAll);
        await coordinator.PendingCommand;

        gate.SetResult();
        await running;

        Assert.AreEqual(1, fixture.Reassignment.ReassignAllCount);
        Assert.IsTrue(fixture.TrayIcon.ItemFor(TrayCommand.ReassignAll).IsEnabled);
    }

    [TestMethod]
    public async Task ReassignAll_FailureIsReportedRatherThanThrown()
    {
        FakeReassignmentService reassignment = new()
        {
            FailWith = new InvalidOperationException("The provider went away."),
        };
        NotificationAreaFixture fixture = new(reassignment);
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        fixture.TrayIcon.Click(TrayCommand.ReassignAll);
        await coordinator.PendingCommand;

        Assert.HasCount(1, fixture.TrayIcon.Notifications);
        Assert.AreEqual(
            TrayNotificationSeverity.Error,
            fixture.TrayIcon.Notifications[0].Severity);
        Assert.Contains("The provider went away.", fixture.TrayIcon.Notifications[0].Message);
        Assert.IsTrue(fixture.TrayIcon.ItemFor(TrayCommand.ReassignAll).IsEnabled);
    }

    [TestMethod]
    public async Task RecentIssue_StartsEmptyAndOpensTheActivityPageOnceAFailureExists()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        Assert.AreEqual(
            TrayMenuPresentation.NoRecentIssueLabel,
            fixture.TrayIcon.ItemFor(TrayCommand.ShowRecentIssue).Label);
        Assert.IsFalse(fixture.TrayIcon.ItemFor(TrayCommand.ShowRecentIssue).IsEnabled);

        await fixture.AssignmentActivity.RecordAsync(
            AssignmentActivities.Failed(DateTimeOffset.UnixEpoch));

        Assert.IsTrue(fixture.TrayIcon.ItemFor(TrayCommand.ShowRecentIssue).IsEnabled);
        Assert.AreEqual(
            "Move failed",
            fixture.TrayIcon.ItemFor(TrayCommand.ShowRecentIssue).Label);

        fixture.TrayIcon.Click(TrayCommand.ShowRecentIssue);
        await coordinator.PendingCommand;

        Assert.IsTrue(fixture.Window.IsVisible);
        CollectionAssert.AreEqual(
            new[] { NotificationAreaCoordinator.RecentIssueDestinationKey },
            fixture.Window.Navigations);
    }

    [TestMethod]
    public async Task SuccessfulAssignments_NeverNotifyButFailuresDo()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        await fixture.AssignmentActivity.RecordAsync(
            AssignmentActivities.Succeeded(DateTimeOffset.UnixEpoch));
        await fixture.AssignmentActivity.RecordAsync(
            AssignmentActivities.AlreadyCorrect(DateTimeOffset.UnixEpoch.AddSeconds(1)));

        Assert.IsEmpty(fixture.TrayIcon.Notifications);

        await fixture.AssignmentActivity.RecordAsync(
            AssignmentActivities.Failed(DateTimeOffset.UnixEpoch.AddSeconds(2)));

        Assert.HasCount(1, fixture.TrayIcon.Notifications);
        Assert.AreEqual(
            TrayNotificationSeverity.Error,
            fixture.TrayIcon.Notifications[0].Severity);
    }

    [TestMethod]
    public async Task CompatibilityWarning_Notifies()
    {
        NotificationAreaFixture fixture = new();
        await using NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        fixture.Compatibility.Publish(
            CompatibilityStatuses.LimitedModePassed(DateTimeOffset.UnixEpoch));

        Assert.HasCount(1, fixture.TrayIcon.Notifications);
        Assert.AreEqual(
            TrayNotificationSeverity.Warning,
            fixture.TrayIcon.Notifications[0].Severity);
        Assert.AreEqual(
            "Running in Limited Mode",
            fixture.TrayIcon.ItemFor(TrayCommand.ShowRecentIssue).Label);
    }

    [TestMethod]
    public async Task Exit_RunsTheExitHandlerAndReleasesTheIcon()
    {
        NotificationAreaFixture fixture = new();
        NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();
        coordinator.Start(ShellLaunchDisposition.ShowWindow);

        fixture.TrayIcon.Click(TrayCommand.Exit);
        await coordinator.PendingCommand;

        Assert.AreEqual(1, fixture.ExitCount);

        await coordinator.DisposeAsync();

        Assert.IsTrue(fixture.TrayIcon.IsDisposed);

        // Nothing raised after disposal may reach a torn-down shell.
        int menusBefore = fixture.TrayIcon.ShownMenus.Count;
        fixture.TrayIcon.Click(TrayCommand.Open);
        fixture.Pause.Pause();
        await fixture.AssignmentActivity.RecordAsync(
            AssignmentActivities.Failed(DateTimeOffset.UnixEpoch));

        Assert.AreEqual(menusBefore, fixture.TrayIcon.ShownMenus.Count);
        Assert.AreEqual(1, fixture.ExitCount);
        Assert.IsEmpty(fixture.TrayIcon.Notifications);
    }

    [TestMethod]
    public async Task DisposeAsync_IsIdempotent()
    {
        NotificationAreaFixture fixture = new();
        NotificationAreaCoordinator coordinator = fixture.CreateCoordinator();

        await coordinator.DisposeAsync();
        await coordinator.DisposeAsync();

        Assert.IsTrue(fixture.TrayIcon.IsDisposed);
    }

    private static BehaviorSettings Behavior(bool closeToTray) => new(
        StartWithWindows: true,
        StartMinimized: false,
        CloseToTray: closeToTray);

    private sealed class NotificationAreaFixture
    {
        public NotificationAreaFixture(FakeReassignmentService? reassignment = null)
        {
            Reassignment = reassignment ?? new FakeReassignmentService();
        }

        public FakeTrayIconHost TrayIcon { get; } = new();

        public FakeShellWindowSurface Window { get; } = new();

        public FakePauseController Pause { get; } = new();

        public FakeReassignmentService Reassignment { get; }

        public BoundedWindowAssignmentActivityStore AssignmentActivity { get; } = new();

        public FakeCompatibilityCoordinator Compatibility { get; } = new();

        public int ExitCount { get; private set; }

        public NotificationAreaCoordinator CreateCoordinator() => new(
            TrayIcon,
            Window,
            Pause,
            Reassignment,
            AssignmentActivity,
            Compatibility,
            _ =>
            {
                ExitCount++;
                return Task.CompletedTask;
            });
    }
}

using DesktopShift.App.Tray;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.App.Presentation.Tests.Tray;

[TestClass]
public sealed class TrayNotificationPolicyTests
{
    [TestMethod]
    public void ForAssignment_SuccessIsSilentByPolicy()
    {
        Assert.IsNull(
            TrayNotificationPolicy.ForAssignment(
                AssignmentActivities.Succeeded(DateTimeOffset.UnixEpoch),
                TrayNotificationPreferences.Default));
        Assert.IsNull(
            TrayNotificationPolicy.ForAssignment(
                AssignmentActivities.AlreadyCorrect(DateTimeOffset.UnixEpoch),
                TrayNotificationPreferences.Default));
    }

    [TestMethod]
    public void ForAssignment_FailureNotifiesWithoutPrivateWindowContent()
    {
        TrayNotification? notification = TrayNotificationPolicy.ForAssignment(
            AssignmentActivities.Failed(DateTimeOffset.UnixEpoch),
            TrayNotificationPreferences.Default);

        Assert.IsNotNull(notification);
        Assert.AreEqual(TrayNotificationSeverity.Error, notification.Severity);
        Assert.Contains("Code.exe", notification.Message);
        Assert.DoesNotContain("Visual Studio Code", notification.Message);
    }

    [TestMethod]
    public void ForAssignment_FailureStaysSilentWhenNotConfigured()
    {
        Assert.IsNull(
            TrayNotificationPolicy.ForAssignment(
                AssignmentActivities.Failed(DateTimeOffset.UnixEpoch),
                TrayNotificationPreferences.Silent));
    }

    [TestMethod]
    public void ForAssignment_WindowNotTrackedSkipNeverNotifiesOrBecomesARecentIssue()
    {
        WindowAssignmentActivity activity =
            AssignmentActivities.AlreadyCorrect(DateTimeOffset.UnixEpoch) with
            {
                SkipReason = WindowAssignmentSkipReason.WindowNotTracked,
                MoveOutcome = WindowMoveOutcome.NotAttempted,
                Error = new WindowAssignmentError(
                    "window_placement.window_not_tracked",
                    "Windows was not tracking this window on a virtual desktop.",
                    HResult: unchecked((int)0x8002802B)),
            };

        Assert.IsNull(
            TrayNotificationPolicy.ForAssignment(
                activity,
                TrayNotificationPreferences.Default));
        Assert.IsNull(
            TrayNotificationPolicy.ForAssignment(
                activity with { MoveOutcome = WindowMoveOutcome.WindowUnavailable },
                TrayNotificationPreferences.Default));
        Assert.IsNull(
            RecentIssueProjection.Project(
                [
                    activity,
                    activity with { MoveOutcome = WindowMoveOutcome.WindowUnavailable },
                ],
                CompatibilityStatuses.FullModePassed()));
    }

    [TestMethod]
    public void ForCompatibility_PassingFullModeIsSilent()
    {
        Assert.IsNull(
            TrayNotificationPolicy.ForCompatibility(
                CompatibilityStatuses.FullModePassed(),
                TrayNotificationPreferences.Default));
    }

    [TestMethod]
    public void ForCompatibility_LimitedModeWarnsAndAFailedTestErrors()
    {
        TrayNotification? limited = TrayNotificationPolicy.ForCompatibility(
            CompatibilityStatuses.LimitedModePassed(DateTimeOffset.UnixEpoch),
            TrayNotificationPreferences.Default);
        TrayNotification? failed = TrayNotificationPolicy.ForCompatibility(
            CompatibilityStatuses.TestFailed(DateTimeOffset.UnixEpoch),
            TrayNotificationPreferences.Default);

        Assert.IsNotNull(limited);
        Assert.AreEqual(TrayNotificationSeverity.Warning, limited.Severity);
        Assert.IsNotNull(failed);
        Assert.AreEqual(TrayNotificationSeverity.Error, failed.Severity);
        Assert.IsNull(
            TrayNotificationPolicy.ForCompatibility(
                CompatibilityStatuses.TestFailed(DateTimeOffset.UnixEpoch),
                TrayNotificationPreferences.Silent));
    }
}

[TestClass]
public sealed class RecentIssueProjectionTests
{
    [TestMethod]
    public void Project_WithNothingWrongReturnsNothing()
    {
        Assert.IsNull(
            RecentIssueProjection.Project([], CompatibilityStatuses.FullModePassed()));
    }

    [TestMethod]
    public void Project_IgnoresSuccessfulAssignments()
    {
        WindowAssignmentActivity[] assignments =
        [
            AssignmentActivities.Succeeded(DateTimeOffset.UnixEpoch),
            AssignmentActivities.AlreadyCorrect(DateTimeOffset.UnixEpoch.AddSeconds(1)),
        ];

        Assert.IsNull(
            RecentIssueProjection.Project(
                assignments,
                CompatibilityStatuses.FullModePassed()));
    }

    [TestMethod]
    public void Project_ReturnsTheLatestFailure()
    {
        DateTimeOffset newest = DateTimeOffset.UnixEpoch.AddSeconds(30);
        WindowAssignmentActivity[] assignments =
        [
            AssignmentActivities.Failed(DateTimeOffset.UnixEpoch),
            AssignmentActivities.Succeeded(DateTimeOffset.UnixEpoch.AddSeconds(10)),
            AssignmentActivities.Failed(newest),
        ];

        RecentIssue? issue = RecentIssueProjection.Project(
            assignments,
            CompatibilityStatuses.FullModePassed());

        Assert.IsNotNull(issue);
        Assert.AreEqual(RecentIssueKind.AssignmentFailure, issue.Kind);
        Assert.AreEqual(newest, issue.ObservedAtUtc);
    }

    [TestMethod]
    public void Project_PrefersWhicheverIssueIsMoreRecent()
    {
        DateTimeOffset early = DateTimeOffset.UnixEpoch;
        DateTimeOffset late = DateTimeOffset.UnixEpoch.AddMinutes(5);

        RecentIssue? compatibilityWins = RecentIssueProjection.Project(
            [AssignmentActivities.Failed(early)],
            CompatibilityStatuses.TestFailed(late));
        RecentIssue? assignmentWins = RecentIssueProjection.Project(
            [AssignmentActivities.Failed(late)],
            CompatibilityStatuses.TestFailed(early));

        Assert.IsNotNull(compatibilityWins);
        Assert.AreEqual(RecentIssueKind.CompatibilityWarning, compatibilityWins.Kind);
        Assert.IsNotNull(assignmentWins);
        Assert.AreEqual(RecentIssueKind.AssignmentFailure, assignmentWins.Kind);
    }
}

using System;
using System.Collections.Generic;
using System.Linq;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.App.Tray;

public enum RecentIssueKind
{
    AssignmentFailure,
    CompatibilityWarning,
}

public sealed record RecentIssue(
    RecentIssueKind Kind,
    string Summary,
    string Detail,
    DateTimeOffset ObservedAtUtc);

/// <summary>
/// Derives the "recent error" the notification area shows.
/// </summary>
/// <remarks>
/// Reads the existing assignment activity and compatibility projections rather
/// than keeping a second error store. A parallel store would have to be kept in
/// step with the Activity page, and the two would eventually disagree about
/// what the last failure was — which is exactly the moment a user checks.
/// </remarks>
public static class RecentIssueProjection
{
    public static RecentIssue? Project(
        IReadOnlyList<WindowAssignmentActivity> assignments,
        CompatibilityStatus compatibility)
    {
        ArgumentNullException.ThrowIfNull(assignments);
        ArgumentNullException.ThrowIfNull(compatibility);

        RecentIssue? assignmentIssue = ProjectAssignmentFailure(assignments);
        RecentIssue? compatibilityIssue = ProjectCompatibilityWarning(compatibility);

        if (assignmentIssue is null)
        {
            return compatibilityIssue;
        }

        if (compatibilityIssue is null)
        {
            return assignmentIssue;
        }

        return assignmentIssue.ObservedAtUtc >= compatibilityIssue.ObservedAtUtc
            ? assignmentIssue
            : compatibilityIssue;
    }

    /// <summary>
    /// Whether an assignment counts as a failure worth surfacing.
    /// </summary>
    /// <remarks>
    /// A window that moved but could not switch desktops is still a failure the
    /// user asked for and did not get, so it is included alongside outright
    /// assignment failures.
    /// </remarks>
    public static bool IsFailure(WindowAssignmentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        return activity.Outcome == WindowAssignmentOutcome.Failed ||
            activity.MoveOutcome == WindowMoveOutcome.Failed ||
            activity.SwitchOutcome == DesktopSwitchOutcome.Failed;
    }

    private static RecentIssue? ProjectAssignmentFailure(
        IReadOnlyList<WindowAssignmentActivity> assignments)
    {
        WindowAssignmentActivity? latest = assignments
            .Where(IsFailure)
            .OrderBy(activity => activity.StartedAtUtc)
            .LastOrDefault();

        if (latest is null)
        {
            return null;
        }

        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(latest);

        return new RecentIssue(
            RecentIssueKind.AssignmentFailure,
            presentation.Outcome,
            // The presentation layer already strips window titles and command
            // lines, so reusing it keeps private window content out of a surface
            // that is visible without opening the application.
            $"{presentation.Decision}. {presentation.Diagnostic}",
            latest.StartedAtUtc);
    }

    private static RecentIssue? ProjectCompatibilityWarning(
        CompatibilityStatus compatibility)
    {
        CompatibilityTestResult test = compatibility.LastTest;

        if (test.Outcome == CompatibilityTestOutcome.Failed)
        {
            return new RecentIssue(
                RecentIssueKind.CompatibilityWarning,
                "Compatibility test failed",
                test.Summary,
                test.TestedAtUtc ?? DateTimeOffset.MinValue);
        }

        if (compatibility.IsLimitedMode)
        {
            return new RecentIssue(
                RecentIssueKind.CompatibilityWarning,
                "Running in Limited Mode",
                compatibility.Provider.Explanation,
                test.TestedAtUtc ?? DateTimeOffset.MinValue);
        }

        return null;
    }
}

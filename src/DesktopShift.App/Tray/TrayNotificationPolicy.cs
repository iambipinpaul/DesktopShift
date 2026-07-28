using System;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.App.Tray;

/// <summary>
/// Which events are allowed to interrupt the user with a notification.
/// </summary>
public sealed record TrayNotificationPreferences(
    bool NotifyOnAssignmentFailure,
    bool NotifyOnCompatibilityWarning)
{
    public static TrayNotificationPreferences Default { get; } = new(
        NotifyOnAssignmentFailure: true,
        NotifyOnCompatibilityWarning: true);

    public static TrayNotificationPreferences Silent { get; } = new(
        NotifyOnAssignmentFailure: false,
        NotifyOnCompatibilityWarning: false);
}

/// <summary>
/// Decides whether an event earns a notification-area balloon.
/// </summary>
/// <remarks>
/// A window-assignment utility acts many times a minute on an ordinary day. If
/// every success announced itself the notifications would be pure noise and the
/// user would turn them off, taking the failures with them. So success is
/// silent by policy, stated here as a rule rather than left as a consequence of
/// nobody having written the notification call — only a failure the user asked
/// to hear about, or a compatibility warning that changes what the application
/// can do at all, is allowed through.
/// </remarks>
public static class TrayNotificationPolicy
{
    public static TrayNotification? ForAssignment(
        WindowAssignmentActivity activity,
        TrayNotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(activity);
        ArgumentNullException.ThrowIfNull(preferences);

        if (!RecentIssueProjection.IsFailure(activity))
        {
            // Succeeded, already-correct, and every skip stay silent.
            return null;
        }

        if (!preferences.NotifyOnAssignmentFailure)
        {
            return null;
        }

        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(activity);

        return new TrayNotification(
            $"DesktopShift: {presentation.Outcome}",
            $"{presentation.Decision}. {presentation.Diagnostic}",
            TrayNotificationSeverity.Error);
    }

    public static TrayNotification? ForCompatibility(
        CompatibilityStatus status,
        TrayNotificationPreferences preferences)
    {
        ArgumentNullException.ThrowIfNull(status);
        ArgumentNullException.ThrowIfNull(preferences);

        if (!preferences.NotifyOnCompatibilityWarning)
        {
            return null;
        }

        if (status.LastTest.Outcome == CompatibilityTestOutcome.Failed)
        {
            return new TrayNotification(
                "DesktopShift compatibility test failed",
                status.LastTest.Summary,
                TrayNotificationSeverity.Error);
        }

        if (status.IsLimitedMode && status.LastTest.IsSuccessful)
        {
            return new TrayNotification(
                "DesktopShift is running in Limited Mode",
                status.Provider.Explanation,
                TrayNotificationSeverity.Warning);
        }

        return null;
    }
}

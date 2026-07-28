using System.Collections.Immutable;
using DesktopShift.Core;

namespace DesktopShift.App.Tray;

/// <summary>
/// Builds the notification-area menu from the shell's current state.
/// </summary>
/// <remarks>
/// The menu is rebuilt whole on every state change rather than patched item by
/// item. A tray menu is small, and a rebuild removes any chance of the pause
/// label disagreeing with the pause state.
/// </remarks>
public static class TrayMenuPresentation
{
    public const string OpenLabel = "Open DesktopShift";
    public const string ReassignAllLabel = "Reassign all windows";
    public const string ReassignRunningLabel = "Reassigning all windows…";
    public const string PauseLabel = "Pause automatic assignment";
    public const string ResumeLabel = "Resume automatic assignment";
    public const string NoRecentIssueLabel = "No recent errors";
    public const string ExitLabel = "Exit";

    /// <summary>
    /// Shell_NotifyIcon truncates a tooltip past 127 characters plus its
    /// terminator, so the text is trimmed where it can be tested rather than
    /// silently cut by the shell.
    /// </summary>
    internal const int MaxTooltipLength = 127;

    public static TrayMenuModel Create(
        bool isPaused,
        bool isReassignmentRunning,
        RecentIssue? recentIssue)
    {
        ImmutableArray<TrayMenuItem> items =
        [
            new TrayMenuItem(TrayCommand.Open, OpenLabel, IsEnabled: true),
            new TrayMenuItem(
                TrayCommand.ReassignAll,
                isReassignmentRunning ? ReassignRunningLabel : ReassignAllLabel,
                IsEnabled: !isReassignmentRunning),
            new TrayMenuItem(
                TrayCommand.TogglePause,
                isPaused ? ResumeLabel : PauseLabel,
                IsEnabled: true),
            new TrayMenuItem(
                TrayCommand.ShowRecentIssue,
                recentIssue is null ? NoRecentIssueLabel : recentIssue.Summary,
                // With nothing to show, the entry stays visible but inert so the
                // menu keeps a stable shape and "no recent errors" is an answer
                // rather than a missing row.
                IsEnabled: recentIssue is not null,
                HasSeparatorBefore: true),
            new TrayMenuItem(
                TrayCommand.Exit,
                ExitLabel,
                IsEnabled: true,
                HasSeparatorBefore: true),
        ];

        return new TrayMenuModel(CreateTooltip(isPaused, recentIssue), items);
    }

    private static string CreateTooltip(bool isPaused, RecentIssue? recentIssue)
    {
        string state = isPaused
            ? "Automatic assignment paused"
            : "Assigning windows automatically";
        string tooltip = $"{ProductInfo.ApplicationName} — {state}";

        if (recentIssue is not null)
        {
            tooltip = $"{tooltip}\n{recentIssue.Summary}";
        }

        return tooltip.Length <= MaxTooltipLength
            ? tooltip
            : tooltip[..MaxTooltipLength];
    }
}

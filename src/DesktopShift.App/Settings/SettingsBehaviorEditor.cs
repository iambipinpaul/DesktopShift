using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.App.Settings;

/// <summary>
/// Applies one Settings-page edit without rebuilding the behavior record.
/// </summary>
/// <remarks>
/// The behavior document grows as Settings grows. Centralizing the copy
/// operations here makes it impossible for a three-toggle handler to reset
/// appearance, notifications, assignment, or hotkeys to constructor defaults.
/// </remarks>
public static class SettingsBehaviorEditor
{
    public static BehaviorSettings WithStartup(
        BehaviorSettings current,
        bool startWithWindows,
        bool startMinimized,
        bool closeToTray) =>
        current with
        {
            StartWithWindows = startWithWindows,
            StartMinimized = startMinimized,
            CloseToTray = closeToTray,
        };

    public static BehaviorSettings WithAssignment(
        BehaviorSettings current,
        bool startAssignmentPaused) =>
        current with { StartAssignmentPaused = startAssignmentPaused };

    public static BehaviorSettings WithNotifications(
        BehaviorSettings current,
        bool notifyOnAssignmentFailure,
        bool notifyOnCompatibilityWarning) =>
        current with
        {
            NotifyOnAssignmentFailure = notifyOnAssignmentFailure,
            NotifyOnCompatibilityWarning = notifyOnCompatibilityWarning,
        };

    public static BehaviorSettings WithDesktopNaming(
        BehaviorSettings current,
        bool nameWindowsDesktops) =>
        current with { NameWindowsDesktops = nameWindowsDesktops };

    public static BehaviorSettings WithAppearance(
        BehaviorSettings current,
        AppTheme theme,
        AppAccent accent) =>
        current with { Theme = theme, Accent = accent };

    public static BehaviorSettings WithHotkeys(
        BehaviorSettings current,
        bool areHotkeysEnabled,
        IEnumerable<HotkeyBinding> bindings)
    {
        ArgumentNullException.ThrowIfNull(bindings);
        return current with
        {
            AreHotkeysEnabled = areHotkeysEnabled,
            Hotkeys = [.. bindings],
        };
    }

    public static BehaviorSettings WithDesktopSwitchShortcuts(
        BehaviorSettings current,
        DesktopSwitchShortcutSettings shortcuts)
    {
        ArgumentNullException.ThrowIfNull(shortcuts);
        return current with
        {
            AreDesktopSwitchShortcutsEnabled = shortcuts.IsEnabled,
            DesktopSwitchProfile = shortcuts.Profile,
            DesktopSwitchCustomModifiers = shortcuts.CustomModifiers,
        };
    }
}

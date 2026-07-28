using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hosting;

public enum ShellLaunchDisposition
{
    ShowWindow,
    StayInNotificationArea,
}

public enum ShellCloseDisposition
{
    HideToNotificationArea,
    ExitApplication,
}

/// <summary>
/// Turns persisted behavior settings into the two lifetime decisions a quiet
/// notification-area utility has to make: what a launch does, and what closing
/// the main window does.
/// </summary>
public static class ShellLifetimePolicy
{
    /// <summary>
    /// Decides whether a launch shows the shell or goes straight to the
    /// notification area.
    /// </summary>
    /// <remarks>
    /// Start-minimized is honoured only once first run is complete. Before
    /// that, the setup dialog lives inside the main window, so starting hidden
    /// would leave a user who enabled the option with no way to finish setup
    /// other than guessing that the tray icon exists.
    /// </remarks>
    public static ShellLaunchDisposition ResolveLaunch(
        BehaviorSettings behavior,
        bool isFirstRunComplete)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        return behavior.StartMinimized && isFirstRunComplete
            ? ShellLaunchDisposition.StayInNotificationArea
            : ShellLaunchDisposition.ShowWindow;
    }

    /// <summary>
    /// Decides whether closing the main window hides it or exits the process.
    /// </summary>
    public static ShellCloseDisposition ResolveClose(BehaviorSettings behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        return behavior.CloseToTray
            ? ShellCloseDisposition.HideToNotificationArea
            : ShellCloseDisposition.ExitApplication;
    }
}

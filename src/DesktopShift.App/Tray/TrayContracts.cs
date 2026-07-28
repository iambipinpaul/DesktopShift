using System;
using System.Collections.Immutable;

namespace DesktopShift.App.Tray;

/// <summary>
/// The complete set of actions the notification area exposes.
/// </summary>
public enum TrayCommand
{
    Open,
    ReassignAll,
    TogglePause,
    ShowRecentIssue,
    Exit,
}

public sealed record TrayMenuItem(
    TrayCommand Command,
    string Label,
    bool IsEnabled,
    bool HasSeparatorBefore = false);

public sealed record TrayMenuModel(
    string Tooltip,
    ImmutableArray<TrayMenuItem> Items);

public enum TrayNotificationSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record TrayNotification(
    string Title,
    string Message,
    TrayNotificationSeverity Severity);

public sealed class TrayCommandEventArgs : EventArgs
{
    public TrayCommandEventArgs(TrayCommand command)
    {
        Command = command;
    }

    public TrayCommand Command { get; }
}

/// <summary>
/// The notification-area surface: one icon, one menu, and balloon notifications.
/// </summary>
/// <remarks>
/// Every Win32 shell call needed to own a notification-area icon lives behind
/// this interface so the menu and command logic can be driven — and tested —
/// without a real icon, a real window, or a real message loop.
/// </remarks>
public interface ITrayIconHost : IDisposable
{
    event EventHandler<TrayCommandEventArgs>? CommandInvoked;

    /// <summary>
    /// Adds the icon on the first call and applies <paramref name="model"/> on
    /// every call.
    /// </summary>
    void Show(TrayMenuModel model);

    void Notify(TrayNotification notification);
}

/// <summary>
/// The shell window as the notification area needs to see it.
/// </summary>
/// <remarks>
/// Deliberately free of any WinUI type. The coordinator holds exactly one
/// surface for its whole lifetime, which is what makes restoring reuse the
/// existing window rather than build a second one.
/// </remarks>
public interface IShellWindowSurface
{
    bool IsVisible { get; }

    void ShowAndActivate();

    void Hide();

    void NavigateTo(string destinationKey);
}

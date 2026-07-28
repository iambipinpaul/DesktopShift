using System;

namespace DesktopShift.App.Tray;

/// <summary>
/// Presents the one <see cref="MainWindow"/> as a shell surface.
/// </summary>
/// <remarks>
/// The adapter is bound to a single window instance for its whole lifetime.
/// Restoring therefore always reaches the window that is already running,
/// with its navigation state and loaded projections intact, instead of
/// constructing a second shell over the first.
/// </remarks>
internal sealed class MainWindowShellSurface : IShellWindowSurface
{
    private readonly MainWindow _window;

    public MainWindowShellSurface(MainWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        _window = window;
    }

    public bool IsVisible => _window.AppWindow.IsVisible;

    public void ShowAndActivate()
    {
        _window.AppWindow.Show();

        // Show alone leaves a window behind whatever the user is doing, so the
        // window is activated as well to bring it forward.
        _window.Show();
    }

    public void Hide() => _window.AppWindow.Hide();

    public void NavigateTo(string destinationKey) => _window.NavigateTo(destinationKey);
}

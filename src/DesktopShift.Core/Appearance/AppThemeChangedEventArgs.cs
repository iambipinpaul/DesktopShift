namespace DesktopShift.Core.Appearance;

public sealed class AppThemeChangedEventArgs(
    AppTheme previousTheme,
    AppTheme currentTheme) : EventArgs
{
    public AppTheme PreviousTheme { get; } = previousTheme;

    public AppTheme CurrentTheme { get; } = currentTheme;
}

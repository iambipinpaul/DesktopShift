namespace DesktopShift.Core.Appearance;

public interface IThemePreferenceService
{
    AppTheme CurrentTheme { get; }

    event EventHandler<AppThemeChangedEventArgs>? ThemeChanged;

    void SetTheme(AppTheme theme);
}

using DesktopShift.Core.Appearance;

namespace DesktopShift.Infrastructure.Appearance;

public sealed class InMemoryThemePreferenceService : IThemePreferenceService
{
    private readonly object _syncRoot = new();
    private AppTheme _currentTheme;

    public InMemoryThemePreferenceService()
        : this(AppTheme.System)
    {
    }

    public InMemoryThemePreferenceService(AppTheme initialTheme)
    {
        ThrowIfUndefined(initialTheme);
        _currentTheme = initialTheme;
    }

    public AppTheme CurrentTheme
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentTheme;
            }
        }
    }

    public event EventHandler<AppThemeChangedEventArgs>? ThemeChanged;

    public void SetTheme(AppTheme theme)
    {
        ThrowIfUndefined(theme);

        AppTheme previousTheme;
        lock (_syncRoot)
        {
            previousTheme = _currentTheme;
            if (previousTheme == theme)
            {
                return;
            }

            _currentTheme = theme;
        }

        ThemeChanged?.Invoke(this, new(previousTheme, theme));
    }

    private static void ThrowIfUndefined(AppTheme theme)
    {
        if (!Enum.IsDefined(theme))
        {
            throw new ArgumentOutOfRangeException(nameof(theme), theme, "The application theme is not defined.");
        }
    }
}

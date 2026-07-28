using DesktopShift.Core.Appearance;
using DesktopShift.Infrastructure.Appearance;

namespace DesktopShift.Core.Tests.Appearance;

[TestClass]
public sealed class InMemoryThemePreferenceServiceTests
{
    [TestMethod]
    public void SetTheme_ChangesPreferenceAndRaisesEvent()
    {
        InMemoryThemePreferenceService service = new();
        AppThemeChangedEventArgs? change = null;
        service.ThemeChanged += (_, eventArgs) => change = eventArgs;

        service.SetTheme(AppTheme.Dark);

        Assert.AreEqual(AppTheme.Dark, service.CurrentTheme);
        Assert.IsNotNull(change);
        Assert.AreEqual(AppTheme.System, change.PreviousTheme);
        Assert.AreEqual(AppTheme.Dark, change.CurrentTheme);
    }

    [TestMethod]
    public void SetTheme_WithCurrentTheme_DoesNotRaiseEvent()
    {
        InMemoryThemePreferenceService service = new(AppTheme.Light);
        int eventCount = 0;
        service.ThemeChanged += (_, _) => eventCount++;

        service.SetTheme(AppTheme.Light);

        Assert.AreEqual(0, eventCount);
    }
}

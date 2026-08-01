using System.Collections.ObjectModel;

namespace DesktopShift.Core.Navigation;

/// <summary>
/// Defines the stable navigation model for the DesktopShift shell.
/// </summary>
public static class ShellNavigationCatalog
{
    private static readonly ReadOnlyCollection<ShellDestination> NavigationDestinations =
        Array.AsReadOnly(
        new ShellDestination[]
        {
            new("overview", "Home", 0),
            new("rules", "Application rules", 1),
            new("desktops", "Managed desktops", 2),
            new("activity", "Activity", 3),
            new("settings", "System settings", 4),
            new("about", "About", 5),
        });

    public static IReadOnlyList<ShellDestination> Destinations => NavigationDestinations;
}

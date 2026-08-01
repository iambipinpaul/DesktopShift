using DesktopShift.Core.Navigation;

namespace DesktopShift.Core.Tests.Navigation;

[TestClass]
public sealed class ShellNavigationCatalogTests
{
    [TestMethod]
    public void Destinations_AreOrderedAndUnique()
    {
        string[] expectedLabels =
        [
            "Home",
            "Application rules",
            "Managed desktops",
            "Activity",
            "System settings",
            "About",
        ];

        IReadOnlyList<ShellDestination> destinations = ShellNavigationCatalog.Destinations;

        CollectionAssert.AreEqual(expectedLabels, destinations.Select(destination => destination.Label).ToArray());
        CollectionAssert.AreEqual(
            Enumerable.Range(0, expectedLabels.Length).ToArray(),
            destinations.Select(destination => destination.Order).ToArray());
        Assert.HasCount(destinations.Count, destinations.Select(destination => destination.Key).Distinct(StringComparer.Ordinal));
    }
}

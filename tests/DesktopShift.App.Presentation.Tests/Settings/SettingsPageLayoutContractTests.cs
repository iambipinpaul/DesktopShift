using System.Globalization;
using System.Xml.Linq;

namespace DesktopShift.App.Presentation.Tests.Settings;

[TestClass]
public sealed class SettingsPageLayoutContractTests
{
    private static readonly XNamespace Presentation =
        "http://schemas.microsoft.com/winfx/2006/xaml/presentation";
    private static readonly XNamespace Xaml =
        "http://schemas.microsoft.com/winfx/2006/xaml";

    [TestMethod]
    public void GlobalShortcutRows_StackControlsBelowCopyAtCompactWidths()
    {
        string? root = TryFindRepositoryRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return;
        }

        XDocument settingsPage = XDocument.Load(
            Path.Combine(root, "src", "DesktopShift.App", "Pages", "SettingsPage.xaml"));
        XElement template = settingsPage
            .Descendants(Presentation + "DataTemplate")
            .Single(element =>
                string.Equals(
                    element.Attribute(Xaml + "DataType")?.Value,
                    "settings:HotkeyBindingEditor",
                    StringComparison.Ordinal));
        XElement layout = template
            .Descendants(Presentation + "Grid")
            .Single(element =>
                string.Equals(
                    element.Attribute(Xaml + "Name")?.Value,
                    "HotkeyEditorLayout",
                    StringComparison.Ordinal));
        XElement controls = template
            .Descendants(Presentation + "StackPanel")
            .Single(element =>
                string.Equals(
                    element.Attribute(Xaml + "Name")?.Value,
                    "HotkeyEditorControls",
                    StringComparison.Ordinal));

        Assert.AreEqual(
            2,
            layout
                .Element(Presentation + "Grid.RowDefinitions")?
                .Elements(Presentation + "RowDefinition")
                .Count(),
            "The compact editor needs a separate row for its controls so the " +
            "description cannot collapse to a few pixels and wrap vertically.");
        Assert.AreEqual("1", controls.Attribute("Grid.Row")?.Value);
        Assert.AreEqual("2", controls.Attribute("Grid.ColumnSpan")?.Value);

        XElement wideState = template
            .Descendants(Presentation + "VisualState")
            .Single(element =>
                string.Equals(
                    element.Attribute(Xaml + "Name")?.Value,
                    "WideHotkeyEditor",
                    StringComparison.Ordinal));
        XElement trigger = wideState
            .Descendants(Presentation + "AdaptiveTrigger")
            .Single();
        double threshold = double.Parse(
            trigger.Attribute("MinWindowWidth")!.Value,
            CultureInfo.InvariantCulture);
        Assert.IsGreaterThanOrEqualTo(
            1200,
            threshold,
            "The side-by-side editor must not return while the navigation pane " +
            "still leaves too little room for the description and controls.");

        Dictionary<string, string?> setters = wideState
            .Descendants(Presentation + "Setter")
            .ToDictionary(
                element => element.Attribute("Target")!.Value,
                element => element.Attribute("Value")?.Value,
                StringComparer.Ordinal);
        Assert.AreEqual("0", setters["HotkeyEditorControls.(Grid.Row)"]);
        Assert.AreEqual("1", setters["HotkeyEditorControls.(Grid.Column)"]);
        Assert.AreEqual("1", setters["HotkeyEditorControls.(Grid.ColumnSpan)"]);
    }

    private static string? TryFindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);

        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "DesktopShift.slnx")) &&
                Directory.Exists(Path.Combine(directory.FullName, "src")))
            {
                return directory.FullName;
            }

            directory = directory.Parent;
        }

        return null;
    }
}

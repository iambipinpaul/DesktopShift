namespace DesktopShift.App.Presentation.Tests.Appearance;

[TestClass]
public sealed class MainWindowCaptionButtonContractTests
{
    [TestMethod]
    public void CustomTitleBar_SynchronizesCaptionButtonColorsWithAppTheme()
    {
        string? root = TryFindRepositoryRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return;
        }

        string mainWindowSource = File.ReadAllText(
            Path.Combine(root, "src", "DesktopShift.App", "MainWindow.xaml.cs"));

        Assert.IsTrue(
            mainWindowSource.Contains(
                "ApplyCaptionButtonColors(theme)",
                StringComparison.Ordinal),
            "Changing the app theme must also refresh the custom title bar's " +
            "Windows caption buttons; otherwise light mode can render white " +
            "buttons on a white title bar.");
        Assert.IsTrue(
            mainWindowSource.Contains(
                "AppWindow.TitleBar.ButtonForegroundColor",
                StringComparison.Ordinal),
            "The custom title bar must explicitly set a visible caption-button " +
            "foreground color.");
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

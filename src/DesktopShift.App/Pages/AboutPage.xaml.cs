using System.Reflection;
using System.Runtime.InteropServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class AboutPage : Page
{
    public AboutPage()
    {
        InitializeComponent();
        Version? version = Assembly.GetExecutingAssembly().GetName().Version;
        VersionSummary.Text =
            $"Version {version?.ToString(3) ?? "unknown"} • WinUI 3";
        RuntimeValue.Text = RuntimeInformation.FrameworkDescription;
        ArchitectureValue.Text = RuntimeInformation.ProcessArchitecture.ToString();
        WindowsBuildValue.Text = Environment.OSVersion.Version.Build.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        bool isCompact = args.NewSize.Width < 680;

        Place(PlatformBadge, isCompact ? 1 : 0, isCompact ? 1 : 2);
        Grid.SetRow(ProductLinksPanel, isCompact ? 3 : 2);
        ProductLinksPanel.Orientation = isCompact
            ? Orientation.Vertical
            : Orientation.Horizontal;

        RuntimeFactsGrid.ColumnDefinitions[1].Width = isCompact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Place(ArchitectureFactCard, isCompact ? 1 : 0, isCompact ? 0 : 1);
        Place(WindowsBuildFactCard, isCompact ? 2 : 1, 0);
        Place(SupportedBuildFactCard, isCompact ? 3 : 1, isCompact ? 0 : 1);

        CompatibilityCardsGrid.ColumnDefinitions[1].Width = isCompact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Place(LimitedModeCard, isCompact ? 1 : 0, isCompact ? 0 : 1);

        PrivacyPointsGrid.ColumnDefinitions[1].Width = isCompact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        PrivacyPointsGrid.ColumnDefinitions[2].Width = isCompact
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        Place(PrivacyLocalPoint, isCompact ? 1 : 0, isCompact ? 0 : 1);
        Place(PrivacyExcludedPoint, isCompact ? 2 : 0, isCompact ? 0 : 2);
    }

    private static void Place(FrameworkElement element, int row, int column)
    {
        Grid.SetRow(element, row);
        Grid.SetColumn(element, column);
    }
}

using DesktopShift.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class SettingsPage : Page
{
    private Func<CancellationToken, Task<CompatibilityPresentation>>? _runCompatibilityTestAsync;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public void Update(
        CompatibilityPresentation presentation,
        Func<CancellationToken, Task<CompatibilityPresentation>> runCompatibilityTestAsync)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        _runCompatibilityTestAsync =
            runCompatibilityTestAsync ?? throw new ArgumentNullException(nameof(runCompatibilityTestAsync));

        ApplyPresentation(presentation);
    }

    private async void OnRunCompatibilityTestClick(object sender, RoutedEventArgs args)
    {
        if (_runCompatibilityTestAsync is null)
        {
            return;
        }

        RunCompatibilityTestButton.IsEnabled = false;
        try
        {
            CompatibilityPresentation presentation =
                await _runCompatibilityTestAsync(CancellationToken.None);
            ApplyPresentation(presentation);
        }
        catch (Exception exception)
        {
            TestOutcomeValue.Text = "Compatibility test failed";
            TestSummaryValue.Text = exception.Message;
            TestedAtValue.Text = string.Empty;
        }
        finally
        {
            RunCompatibilityTestButton.IsEnabled = true;
        }
    }

    private void ApplyPresentation(CompatibilityPresentation presentation)
    {
        ModeValue.Text = presentation.Mode;
        BuildValue.Text = presentation.Build;
        BuildSupportValue.Text = presentation.BuildSupport;
        ProviderValue.Text = presentation.Provider;
        CapabilitiesValue.Text = presentation.Capabilities;
        LimitedModeExplanation.Message = presentation.Explanation;
        TestOutcomeValue.Text = presentation.TestOutcome;
        TestSummaryValue.Text = presentation.TestSummary;
        TestedAtValue.Text = presentation.TestedAt;
    }
}

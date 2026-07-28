using DesktopShift.App.ViewModels;
using DesktopShift.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class SettingsPage : Page
{
    private Func<CancellationToken, Task<CompatibilityPresentation>>? _runCompatibilityTestAsync;
    private BehaviorSettingsCommand? _behaviorSettingsCommand;
    private CancellationToken _windowCancellationToken;
    private bool _isApplyingBehavior;

    public SettingsPage()
    {
        InitializeComponent();
    }

    public void Update(
        CompatibilityPresentation presentation,
        Func<CancellationToken, Task<CompatibilityPresentation>> runCompatibilityTestAsync,
        BehaviorSettingsCommand behaviorSettingsCommand,
        CancellationToken windowCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(presentation);
        ArgumentNullException.ThrowIfNull(behaviorSettingsCommand);
        _runCompatibilityTestAsync =
            runCompatibilityTestAsync ?? throw new ArgumentNullException(nameof(runCompatibilityTestAsync));
        _behaviorSettingsCommand = behaviorSettingsCommand;
        _windowCancellationToken = windowCancellationToken;

        ApplyPresentation(presentation);
        _ = LoadBehaviorAsync();
    }

    private async Task LoadBehaviorAsync()
    {
        if (_behaviorSettingsCommand is null || _windowCancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            BehaviorSettingsPresentation behavior =
                await _behaviorSettingsCommand.LoadAsync(_windowCancellationToken);
            ApplyBehavior(behavior);
        }
        catch (OperationCanceledException) when (_windowCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowBehaviorMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private async void OnBehaviorToggled(object sender, RoutedEventArgs args)
    {
        // Applying a setting writes the toggles back, which raises Toggled
        // again. The guard keeps that echo from starting a second save.
        if (_isApplyingBehavior ||
            _behaviorSettingsCommand is null ||
            _windowCancellationToken.IsCancellationRequested)
        {
            return;
        }

        BehaviorSettings requested = new(
            StartWithWindowsToggle.IsOn,
            StartMinimizedToggle.IsOn,
            CloseToTrayToggle.IsOn);

        try
        {
            BehaviorSettingsPresentation behavior =
                await _behaviorSettingsCommand.ApplyAsync(requested, _windowCancellationToken);
            ApplyBehavior(behavior);
        }
        catch (OperationCanceledException) when (_windowCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            ShowBehaviorMessage(exception.Message, InfoBarSeverity.Error);
        }
    }

    private void ApplyBehavior(BehaviorSettingsPresentation behavior)
    {
        _isApplyingBehavior = true;
        try
        {
            StartWithWindowsToggle.IsOn = behavior.StartWithWindows;
            StartWithWindowsToggle.IsEnabled = behavior.IsStartupToggleEnabled;
            StartMinimizedToggle.IsOn = behavior.StartMinimized;
            CloseToTrayToggle.IsOn = behavior.CloseToTray;
            StartupStateValue.Text = behavior.StartupStateDescription;
        }
        finally
        {
            _isApplyingBehavior = false;
        }

        if (behavior.IsStartupToggleEnabled)
        {
            BehaviorMessage.IsOpen = false;
            return;
        }

        ShowBehaviorMessage(behavior.StartupStateDescription, InfoBarSeverity.Warning);
    }

    private void ShowBehaviorMessage(string message, InfoBarSeverity severity)
    {
        BehaviorMessage.Message = message;
        BehaviorMessage.Severity = severity;
        BehaviorMessage.IsOpen = true;
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

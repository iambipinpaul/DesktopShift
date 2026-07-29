using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using DesktopShift.App.Settings;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace DesktopShift.App.Pages;

/// <summary>The complete Settings experience.</summary>
public sealed partial class SettingsPage : Page
{
    private readonly ObservableCollection<HotkeyBindingEditor> hotkeyBindings = [];
    private readonly SemaphoreSlim behaviorSaveGate = new(1, 1);
    private SettingsPageServices? services;
    private Func<CancellationToken, Task<CompatibilityPresentation>>?
        runCompatibilityTestAsync;
    private BehaviorSettingsCommand? behaviorSettingsCommand;
    private BehaviorSettings currentBehavior = ConfigurationDefaults.Create().Behavior;
    private CancellationToken windowCancellationToken;
    private long behaviorEditVersion;
    private bool isApplyingPresentation;

    public SettingsPage()
    {
        InitializeComponent();
        HotkeyList.ItemsSource = hotkeyBindings;
    }

    /// <summary>
    /// Connects all seven sections to their application services.
    /// </summary>
    public void Update(
        SettingsPageServices pageServices,
        CompatibilityPresentation compatibility,
        Func<CancellationToken, Task<CompatibilityPresentation>>
            compatibilityTest,
        CancellationToken cancellationToken)
    {
        services = pageServices ??
            throw new ArgumentNullException(nameof(pageServices));
        behaviorSettingsCommand = pageServices.BehaviorSettings;
        runCompatibilityTestAsync = compatibilityTest ??
            throw new ArgumentNullException(nameof(compatibilityTest));
        windowCancellationToken = cancellationToken;

        ApplyCompatibility(compatibility);
        DiagnosticLogLocation.Text =
            $"Log location: {pageServices.Diagnostics.LogLocation.DisplayPath}";
        ApplyLivePause(pageServices.AssignmentPause.IsPaused);
        ShowHotkeyState(pageServices.Hotkeys.Current, showSuccess: false);
        _ = LoadBehaviorAsync();
    }

    /// <summary>
    /// Transitional overload used until the shell supplies
    /// <see cref="SettingsPageServices"/>. Startup and compatibility continue
    /// to work; the remaining controls report that they are not connected.
    /// </summary>
    public void Update(
        CompatibilityPresentation compatibility,
        Func<CancellationToken, Task<CompatibilityPresentation>>
            compatibilityTest,
        BehaviorSettingsCommand command,
        CancellationToken cancellationToken)
    {
        behaviorSettingsCommand = command ??
            throw new ArgumentNullException(nameof(command));
        runCompatibilityTestAsync = compatibilityTest ??
            throw new ArgumentNullException(nameof(compatibilityTest));
        windowCancellationToken = cancellationToken;

        ApplyCompatibility(compatibility);
        _ = LoadBehaviorAsync();
    }

    private async Task LoadBehaviorAsync()
    {
        if (behaviorSettingsCommand is null ||
            windowCancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            long requestedAtEditVersion =
                Volatile.Read(ref behaviorEditVersion);
            BehaviorSettingsPresentation presentation =
                await behaviorSettingsCommand.LoadAsync(windowCancellationToken);
            if (requestedAtEditVersion ==
                Volatile.Read(ref behaviorEditVersion))
            {
                ApplyBehavior(presentation);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Settings could not be loaded", exception.Message);
        }
    }

    private async void OnPersistedBehaviorChanged(
        object sender,
        RoutedEventArgs args)
    {
        if (isApplyingPresentation)
        {
            return;
        }

        BehaviorSettings requested = SettingsBehaviorEditor.WithStartup(
            currentBehavior,
            StartWithWindowsToggle.IsOn,
            StartMinimizedToggle.IsOn,
            CloseToTrayToggle.IsOn);
        requested = SettingsBehaviorEditor.WithAssignment(
            requested,
            StartAssignmentPausedToggle.IsOn);
        requested = SettingsBehaviorEditor.WithNotifications(
            requested,
            AssignmentFailureNotificationToggle.IsOn,
            CompatibilityWarningNotificationToggle.IsOn);

        _ = await PersistBehaviorAsync(requested);
    }

    private async void OnThemeSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (isApplyingPresentation ||
            ThemePicker.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out AppTheme theme))
        {
            return;
        }

        BehaviorSettingsPresentation? presentation =
            await PersistBehaviorAsync(
                SettingsBehaviorEditor.WithAppearance(currentBehavior, theme));
        _ = presentation;
    }

    private async Task<BehaviorSettingsPresentation?> PersistBehaviorAsync(
        BehaviorSettings requested)
    {
        if (behaviorSettingsCommand is null)
        {
            ReportDisconnected();
            return null;
        }

        currentBehavior = requested;
        long editVersion = Interlocked.Increment(ref behaviorEditVersion);
        bool enteredGate = false;
        try
        {
            await behaviorSaveGate.WaitAsync(windowCancellationToken);
            enteredGate = true;

            BehaviorSettingsPresentation presentation =
                await behaviorSettingsCommand.ApplyAsync(
                    requested,
                    windowCancellationToken);
            bool isLatest =
                editVersion == Volatile.Read(ref behaviorEditVersion);
            if (isLatest)
            {
                ApplyBehavior(presentation);
            }

            if (presentation.Accepted && isLatest)
            {
                ApplyAcceptedBehavior(presentation.ActiveBehavior);
            }
            else if (!presentation.Accepted && isLatest)
            {
                Report(
                    InfoBarSeverity.Warning,
                    "Settings need correction",
                    FormatIssues(presentation.Issues));
            }

            return presentation;
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Settings could not be saved", exception.Message);
            return null;
        }
        finally
        {
            if (enteredGate)
            {
                behaviorSaveGate.Release();
            }
        }
    }

    private void ApplyBehavior(BehaviorSettingsPresentation presentation)
    {
        currentBehavior = presentation.Behavior;
        isApplyingPresentation = true;
        try
        {
            StartWithWindowsToggle.IsOn = presentation.StartWithWindows;
            StartWithWindowsToggle.IsEnabled = presentation.IsStartupToggleEnabled;
            StartMinimizedToggle.IsOn = currentBehavior.StartMinimized;
            CloseToTrayToggle.IsOn = currentBehavior.CloseToTray;
            StartAssignmentPausedToggle.IsOn =
                currentBehavior.StartAssignmentPaused;
            AssignmentFailureNotificationToggle.IsOn =
                currentBehavior.NotifyOnAssignmentFailure;
            CompatibilityWarningNotificationToggle.IsOn =
                currentBehavior.NotifyOnCompatibilityWarning;
            ThemePicker.SelectedIndex = currentBehavior.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };
            HotkeysEnabledToggle.IsOn = currentBehavior.AreHotkeysEnabled;
            StartupStateValue.Text = presentation.StartupStateDescription;
            ReplaceHotkeys(currentBehavior.ToHotkeySettings());
        }
        finally
        {
            isApplyingPresentation = false;
        }
    }

    private void ReplaceHotkeys(HotkeySettings settings)
    {
        hotkeyBindings.Clear();
        foreach (HotkeyBinding binding in settings.Bindings)
        {
            hotkeyBindings.Add(new HotkeyBindingEditor(binding));
        }
    }

    private void OnLivePauseToggled(object sender, RoutedEventArgs args)
    {
        if (isApplyingPresentation)
        {
            return;
        }

        if (services is null)
        {
            ReportDisconnected();
            ApplyLivePause(false);
            return;
        }

        if (AssignmentPausedToggle.IsOn)
        {
            services.AssignmentPause.Pause();
        }
        else
        {
            services.AssignmentPause.Resume();
        }

        ApplyLivePause(services.AssignmentPause.IsPaused);
    }

    private void ApplyLivePause(bool isPaused)
    {
        isApplyingPresentation = true;
        try
        {
            AssignmentPausedToggle.IsOn = isPaused;
        }
        finally
        {
            isApplyingPresentation = false;
        }
    }

    private async void OnReassignAllClick(object sender, RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        ReassignAllButton.IsEnabled = false;
        try
        {
            var result = await services.WindowReassignment.ReassignAllAsync(
                windowCancellationToken);
            int failed = result.Assignments.Count(
                static assignment =>
                    assignment.Outcome ==
                    DesktopShift.Core.Assignments.WindowAssignmentOutcome.Failed);
            Report(
                failed == 0 ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                "Reassignment complete",
                $"{result.Assignments.Length} matching windows were processed; {failed} failed.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Reassignment failed", exception.Message);
        }
        finally
        {
            ReassignAllButton.IsEnabled = true;
        }
    }

    private async void OnRunCompatibilityTestClick(
        object sender,
        RoutedEventArgs args)
    {
        if (runCompatibilityTestAsync is null)
        {
            ReportDisconnected();
            return;
        }

        RunCompatibilityTestButton.IsEnabled = false;
        try
        {
            ApplyCompatibility(
                await runCompatibilityTestAsync(windowCancellationToken));
        }
        catch (OperationCanceledException)
        {
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

    private void ApplyCompatibility(CompatibilityPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);
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

    private async void OnImportConfigurationClick(
        object sender,
        RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        StorageFile? file = await PickOpenFileAsync(".json");
        if (file is null)
        {
            return;
        }

        ImportConfigurationButton.IsEnabled = false;
        try
        {
            await using Stream stream = await file.OpenStreamForReadAsync();
            ConfigurationImportResult result =
                await services.ConfigurationExchange.ImportAsync(
                    stream,
                    windowCancellationToken);
            if (result.Accepted &&
                result.State?.Active?.Behavior is BehaviorSettings behavior)
            {
                BehaviorSettingsPresentation presentation =
                    await services.BehaviorSettings.ApplyAsync(
                        behavior,
                        windowCancellationToken);
                ApplyAcceptedBehavior(presentation.ActiveBehavior);
            }

            await LoadBehaviorAsync();

            Report(
                result.Accepted ? InfoBarSeverity.Success : InfoBarSeverity.Warning,
                result.Accepted
                    ? "Configuration imported"
                    : "Imported configuration needs correction",
                result.Accepted
                    ? $"{file.Name} is now active."
                    : FormatIssues(result.Issues));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Configuration import failed", exception.Message);
        }
        finally
        {
            ImportConfigurationButton.IsEnabled = true;
        }
    }

    private async void OnExportConfigurationClick(
        object sender,
        RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        StorageFile? file = await PickSaveFileAsync(
            "DesktopShift-settings",
            ".json",
            "JSON configuration");
        if (file is null)
        {
            return;
        }

        ExportConfigurationButton.IsEnabled = false;
        try
        {
            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            ConfigurationExportResult result =
                await services.ConfigurationExchange.ExportAsync(
                    stream,
                    windowCancellationToken);
            Report(
                InfoBarSeverity.Success,
                "Configuration exported",
                $"{result.ApplicationRuleCount} rules and {result.HotkeyCount} shortcuts were written to {file.Name}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Configuration export failed", exception.Message);
        }
        finally
        {
            ExportConfigurationButton.IsEnabled = true;
        }
    }

    private async void OnExportDiagnosticsClick(
        object sender,
        RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        StorageFile? file = await PickSaveFileAsync(
            "DesktopShift-diagnostics",
            ".zip",
            "ZIP archive");
        if (file is null)
        {
            return;
        }

        ExportDiagnosticsButton.IsEnabled = false;
        try
        {
            await using Stream stream = await file.OpenStreamForWriteAsync();
            stream.SetLength(0);
            var summary = await services.Diagnostics.ExportBundleAsync(
                stream,
                windowCancellationToken);
            Report(
                InfoBarSeverity.Success,
                "Diagnostic bundle exported",
                $"{summary.ActivityRecordCount} events and {summary.LogFileCount} log files were written to {file.Name}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Diagnostic export failed", exception.Message);
        }
        finally
        {
            ExportDiagnosticsButton.IsEnabled = true;
        }
    }

    private void OnOpenLogLocationClick(object sender, RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        try
        {
            string path = services.Diagnostics.LogLocation.DirectoryPath;
            _ = Directory.CreateDirectory(path);
            using Process? process = Process.Start(
                new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Log location could not be opened", exception.Message);
        }
    }

    private void OnClearActivityClick(object sender, RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        services.Diagnostics.ClearLocalActivity();
        Report(
            InfoBarSeverity.Success,
            "Local activity cleared",
            "DesktopShift's activity journal and rolling log files were removed.");
    }

    private async void OnApplyHotkeysClick(object sender, RoutedEventArgs args)
    {
        if (services is null)
        {
            ReportDisconnected();
            return;
        }

        BehaviorSettings requested = SettingsBehaviorEditor.WithHotkeys(
            currentBehavior,
            HotkeysEnabledToggle.IsOn,
            hotkeyBindings.Select(static binding => binding.ToBinding()));
        BehaviorSettingsPresentation? presentation =
            await PersistBehaviorAsync(requested);
        if (presentation is null)
        {
            return;
        }

        if (!presentation.Accepted)
        {
            HotkeyStatus.Message =
                "The shortcut candidate was retained for correction. The previous registrations are still active.";
            HotkeyStatus.Severity = InfoBarSeverity.Warning;
            HotkeyStatus.IsOpen = true;
            return;
        }

        ShowHotkeyState(services.Hotkeys.Current, showSuccess: true);
    }

    private void ShowHotkeyState(
        HotkeyState state,
        bool showSuccess)
    {
        if (state.Issues.IsEmpty && !showSuccess)
        {
            HotkeyStatus.IsOpen = false;
            return;
        }

        HotkeyStatus.Message = state.Issues.IsEmpty
            ? state.IsEnabled
                ? $"{state.RegisteredCount} global shortcuts are registered."
                : "Global shortcuts are disabled; no combinations are registered."
            : FormatIssues(state.Issues);
        HotkeyStatus.Severity = state.Issues.IsEmpty
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        HotkeyStatus.IsOpen = true;
    }

    private async Task<StorageFile?> PickOpenFileAsync(string extension)
    {
        nint windowHandle = GetWindowHandle();
        if (windowHandle == 0)
        {
            throw new InvalidOperationException(
                "The file picker cannot open until the Settings page is hosted in the shell window.");
        }

        FileOpenPicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
        };
        picker.FileTypeFilter.Add(extension);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSingleFileAsync();
    }

    private async Task<StorageFile?> PickSaveFileAsync(
        string suggestedName,
        string extension,
        string fileType)
    {
        nint windowHandle = GetWindowHandle();
        if (windowHandle == 0)
        {
            throw new InvalidOperationException(
                "The file picker cannot open until the Settings page is hosted in the shell window.");
        }

        FileSavePicker picker = new()
        {
            SuggestedStartLocation = PickerLocationId.DocumentsLibrary,
            SuggestedFileName = suggestedName,
        };
        picker.FileTypeChoices.Add(fileType, [extension]);
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);
        return await picker.PickSaveFileAsync();
    }

    private nint GetWindowHandle()
    {
        if (XamlRoot?.ContentIslandEnvironment is not ContentIslandEnvironment
            environment)
        {
            return 0;
        }

        return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }

    private void ReportDisconnected() =>
        Report(
            InfoBarSeverity.Warning,
            "Settings are not connected",
            "Return to this page after the application finishes starting.");

    private void ApplyAcceptedBehavior(BehaviorSettings behavior)
    {
        if (services is null)
        {
            return;
        }

        if (services.ApplyAcceptedBehavior is not null)
        {
            services.ApplyAcceptedBehavior(behavior);
            return;
        }

        services.ThemePreference.SetTheme(behavior.Theme);
        _ = services.Hotkeys.Apply(behavior.ToHotkeySettings());
    }

    private void Report(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        SettingsStatus.Severity = severity;
        SettingsStatus.Title = title;
        SettingsStatus.Message = message;
        SettingsStatus.IsOpen = true;
    }

    private static string FormatIssues(
        IEnumerable<ConfigurationValidationIssue> issues)
    {
        string message = string.Join(
            Environment.NewLine,
            issues.Select(static issue => $"{issue.Path}: {issue.Message}"));
        return string.IsNullOrWhiteSpace(message)
            ? "The candidate was retained for correction; the previous valid configuration remains active."
            : message;
    }
}

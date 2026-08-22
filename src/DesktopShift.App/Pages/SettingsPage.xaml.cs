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
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        const double horizontalPageMargin = 60;
        SettingsContent.Width = Math.Max(
            0,
            Math.Min(1000, args.NewSize.Width - horizontalPageMargin));
    }

    private readonly SemaphoreSlim behaviorSaveGate = new(1, 1);
    private DesktopSwitchShortcutEditor? desktopSwitchEditor;
    private SettingsPageServices? services;
    private Func<CancellationToken, Task<CompatibilityPresentation>>?
        runCompatibilityTestAsync;
    private BehaviorSettingsCommand? behaviorSettingsCommand;
    private TilingSettingsCommand? tilingSettingsCommand;
    private BehaviorSettings currentBehavior = ConfigurationDefaults.Create().Behavior;
    private TilingSettings currentTiling = TilingSettings.Disabled;
    private IReadOnlyList<RunningApplicationCandidate> runningApplications = [];
    private CancellationToken windowCancellationToken;
    private long behaviorEditVersion;
    private bool isApplyingPresentation;

    public SettingsPage()
    {
        InitializeComponent();
    }

    /// <summary>
    /// Connects all Settings sections to their application services.
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
        tilingSettingsCommand = pageServices.TilingSettings;
        runCompatibilityTestAsync = compatibilityTest ??
            throw new ArgumentNullException(nameof(compatibilityTest));
        windowCancellationToken = cancellationToken;

        ApplyCompatibility(compatibility);
        DiagnosticLogLocation.Text =
            $"Log location: {pageServices.Diagnostics.LogLocation.DisplayPath}";
        ApplyLivePause(pageServices.AssignmentPause.IsPaused);
        ShowDesktopSwitchState(
            pageServices.DesktopSwitchHotkeys?.Current,
            showSuccess: false);
        _ = LoadBehaviorAsync();
        _ = LoadTilingAsync();
        _ = LoadTilingApplicationsAsync();
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

    private async Task LoadTilingAsync()
    {
        if (tilingSettingsCommand is null ||
            windowCancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            TilingSettingsPresentation presentation =
                await tilingSettingsCommand.LoadAsync(windowCancellationToken);
            ApplyTiling(presentation);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            Report(InfoBarSeverity.Error, "Tiling settings could not be loaded", exception.Message);
        }
    }

    private void ApplyTiling(TilingSettingsPresentation presentation)
    {
        currentTiling = presentation.Settings;
        isApplyingPresentation = true;
        try
        {
            TilingEnabledToggle.IsOn = currentTiling.IsEnabled;
            TilingOuterGapNumber.Value = currentTiling.OuterGap;
            TilingInnerGapNumber.Value = currentTiling.InnerGap;
            TilingMinimumWidthNumber.Value = currentTiling.MinimumTileWidth;
            TilingMinimumHeightNumber.Value = currentTiling.MinimumTileHeight;
            ApplyTilingExceptions();
            SetTilingControlsEnabled(currentTiling.IsEnabled);
        }
        finally
        {
            isApplyingPresentation = false;
        }

        if (!presentation.Accepted)
        {
            TilingStatus.Message = FormatIssues(presentation.Issues);
            TilingStatus.Severity = InfoBarSeverity.Warning;
            TilingStatus.IsOpen = true;
        }
    }

    private async Task LoadTilingApplicationsAsync()
    {
        if (services is null || windowCancellationToken.IsCancellationRequested)
        {
            return;
        }

        try
        {
            RunningApplicationSnapshot snapshot = await services
                .RunningApplications
                .ReadAsync(windowCancellationToken);
            runningApplications = snapshot.Applications;
            TilingApplicationPicker.ItemsSource = runningApplications;
            if (runningApplications.Count > 0)
            {
                TilingApplicationPicker.SelectedIndex = 0;
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            TilingStatus.Message = exception.Message;
            TilingStatus.Severity = InfoBarSeverity.Warning;
            TilingStatus.IsOpen = true;
        }
    }

    private void ApplyTilingExceptions()
    {
        IReadOnlyList<TilingExceptionPresentation> items =
            TilingExceptionEditor.Present(currentTiling);
        TilingExceptionList.ItemsSource = null;
        TilingExceptionList.ItemsSource = items;
        TilingExceptionsEmptyText.Visibility = items.Count == 0
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    private async void OnRefreshTilingApplicationsClick(
        object sender,
        RoutedEventArgs args) =>
        await LoadTilingApplicationsAsync();

    private void OnAddTilingExceptionClick(object sender, RoutedEventArgs args)
    {
        if (TilingApplicationPicker.SelectedItem is not
            RunningApplicationCandidate candidate)
        {
            TilingStatus.Message = "Select an open application first.";
            TilingStatus.Severity = InfoBarSeverity.Warning;
            TilingStatus.IsOpen = true;
            return;
        }

        TilingExceptionDisposition disposition =
            TilingExceptionDispositionPicker.SelectedIndex == 1
                ? TilingExceptionDisposition.Ignore
                : TilingExceptionDisposition.Float;
        TilingSettings changed = TilingExceptionEditor.Add(
            currentTiling,
            candidate,
            disposition);
        if (changed == currentTiling)
        {
            TilingStatus.Message =
                $"{candidate.DisplayName} already has this exception.";
            TilingStatus.Severity = InfoBarSeverity.Informational;
            TilingStatus.IsOpen = true;
            return;
        }

        currentTiling = changed;
        ApplyTilingExceptions();
        TilingStatus.Message =
            $"{candidate.DisplayName} was added. Select Save and apply to keep this change.";
        TilingStatus.Severity = InfoBarSeverity.Informational;
        TilingStatus.IsOpen = true;
    }

    private void OnRemoveTilingExceptionClick(object sender, RoutedEventArgs args)
    {
        if (sender is not Button { Tag: string id })
        {
            return;
        }

        currentTiling = TilingExceptionEditor.Remove(currentTiling, id);
        ApplyTilingExceptions();
        TilingStatus.Message =
            "The exception was removed. Select Save and apply to keep this change.";
        TilingStatus.Severity = InfoBarSeverity.Informational;
        TilingStatus.IsOpen = true;
    }

    private void OnTilingEnabledToggled(object sender, RoutedEventArgs args)
    {
        if (!isApplyingPresentation)
        {
            SetTilingControlsEnabled(TilingEnabledToggle.IsOn);
        }
    }

    private void SetTilingControlsEnabled(bool isEnabled)
    {
        TilingOuterGapNumber.IsEnabled = isEnabled;
        TilingInnerGapNumber.IsEnabled = isEnabled;
        TilingMinimumWidthNumber.IsEnabled = isEnabled;
        TilingMinimumHeightNumber.IsEnabled = isEnabled;
    }

    private async void OnApplyTilingClick(object sender, RoutedEventArgs args)
    {
        if (tilingSettingsCommand is null)
        {
            ReportDisconnected();
            return;
        }

        ApplyTilingButton.IsEnabled = false;
        try
        {
            TilingSettings requested = currentTiling with
            {
                IsEnabled = TilingEnabledToggle.IsOn,
                OuterGap = ReadWholeNumber(TilingOuterGapNumber, "Outer gap"),
                InnerGap = ReadWholeNumber(TilingInnerGapNumber, "Inner gap"),
                MinimumTileWidth = ReadWholeNumber(
                    TilingMinimumWidthNumber,
                    "Minimum tile width"),
                MinimumTileHeight = ReadWholeNumber(
                    TilingMinimumHeightNumber,
                    "Minimum tile height"),
            };
            TilingSettingsPresentation presentation =
                await tilingSettingsCommand.ApplyAsync(
                    requested,
                    windowCancellationToken);
            ApplyTiling(presentation);
            if (!presentation.Accepted)
            {
                return;
            }

            if (services?.ReconcileTiling is not null)
            {
                await services.ReconcileTiling(windowCancellationToken);
            }

            TilingStatus.Message = requested.IsEnabled
                ? "The settings were saved and applied to open windows."
                : "Native window tiling is disabled.";
            TilingStatus.Severity = InfoBarSeverity.Success;
            TilingStatus.IsOpen = true;
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            TilingStatus.Message = exception.Message;
            TilingStatus.Severity = InfoBarSeverity.Error;
            TilingStatus.IsOpen = true;
        }
        finally
        {
            ApplyTilingButton.IsEnabled = true;
        }
    }

    private static int ReadWholeNumber(NumberBox numberBox, string fieldName)
    {
        if (double.IsNaN(numberBox.Value) ||
            numberBox.Value < int.MinValue ||
            numberBox.Value > int.MaxValue)
        {
            throw new InvalidOperationException($"{fieldName} must be a whole number.");
        }

        double rounded = Math.Round(numberBox.Value);
        if (Math.Abs(numberBox.Value - rounded) > double.Epsilon)
        {
            throw new InvalidOperationException($"{fieldName} must be a whole number.");
        }

        return checked((int)rounded);
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
        requested = SettingsBehaviorEditor.WithDesktopNaming(
            requested,
            NameWindowsDesktopsToggle.IsOn);
        requested = SettingsBehaviorEditor.WithNotifications(
            requested,
            AssignmentFailureNotificationToggle.IsOn,
            CompatibilityWarningNotificationToggle.IsOn);
        requested = SettingsBehaviorEditor.WithDiagnostics(
            requested,
            RecordLocalActivityToggle.IsOn);

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
                SettingsBehaviorEditor.WithAppearance(
                    currentBehavior,
                    theme,
                    currentBehavior.Accent));
        _ = presentation;
    }

    private async void OnAccentSelectionChanged(
        object sender,
        SelectionChangedEventArgs args)
    {
        if (isApplyingPresentation ||
            AccentPicker.SelectedItem is not ComboBoxItem { Tag: string tag } ||
            !Enum.TryParse(tag, ignoreCase: true, out AppAccent accent))
        {
            return;
        }

        BehaviorSettingsPresentation? presentation =
            await PersistBehaviorAsync(
                SettingsBehaviorEditor.WithAppearance(
                    currentBehavior,
                    currentBehavior.Theme,
                    accent));
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
            NameWindowsDesktopsToggle.IsOn = currentBehavior.NameWindowsDesktops;
            AssignmentFailureNotificationToggle.IsOn =
                currentBehavior.NotifyOnAssignmentFailure;
            CompatibilityWarningNotificationToggle.IsOn =
                currentBehavior.NotifyOnCompatibilityWarning;
            RecordLocalActivityToggle.IsOn =
                currentBehavior.RecordLocalActivity;
            ThemePicker.SelectedIndex = currentBehavior.Theme switch
            {
                AppTheme.Light => 1,
                AppTheme.Dark => 2,
                _ => 0,
            };
            AccentPicker.SelectedIndex = currentBehavior.Accent switch
            {
                AppAccent.AditiKraftBlue => 1,
                _ => 0,
            };
            StartupStateValue.Text = presentation.StartupStateDescription;
            ReplaceDesktopSwitchShortcuts(
                currentBehavior.ToDesktopSwitchShortcutSettings());
        }
        finally
        {
            isApplyingPresentation = false;
        }
    }

    /// <summary>
    /// Loads the profile into the radio buttons and modifier boxes, then brings
    /// the derived text alongside them up to date.
    /// </summary>
    private void ReplaceDesktopSwitchShortcuts(
        DesktopSwitchShortcutSettings settings)
    {
        desktopSwitchEditor = new DesktopSwitchShortcutEditor(settings);

        DesktopSwitchEnabledToggle.IsOn = desktopSwitchEditor.IsEnabled;
        DesktopSwitchCtrlAltOption.IsChecked =
            desktopSwitchEditor.Profile == DesktopSwitchShortcutProfile.CtrlAlt;
        DesktopSwitchCustomOption.IsChecked = desktopSwitchEditor.IsCustom;
        DesktopSwitchControlModifier.IsChecked = desktopSwitchEditor.Control;
        DesktopSwitchAltModifier.IsChecked = desktopSwitchEditor.Alt;
        DesktopSwitchShiftModifier.IsChecked = desktopSwitchEditor.Shift;
        DesktopSwitchWindowsModifier.IsChecked = desktopSwitchEditor.Windows;

        RefreshDesktopSwitchPresentation();
    }

    /// <summary>
    /// Keeps the summary line and the modifier boxes' availability matching
    /// whichever profile is selected.
    /// </summary>
    private void RefreshDesktopSwitchPresentation()
    {
        if (desktopSwitchEditor is null)
        {
            return;
        }

        // Set per box rather than on the panel: a Panel is not a Control, so it
        // has no enabled state to inherit from.
        bool isCustom = desktopSwitchEditor.IsCustom;
        DesktopSwitchControlModifier.IsEnabled = isCustom;
        DesktopSwitchAltModifier.IsEnabled = isCustom;
        DesktopSwitchShiftModifier.IsEnabled = isCustom;
        DesktopSwitchWindowsModifier.IsEnabled = isCustom;
        DesktopSwitchSummary.Text = desktopSwitchEditor.Summary;
    }

    /// <remarks>
    /// Guarded against <c>isApplyingPresentation</c> like every other handler on
    /// this page. Loading a saved profile assigns <c>IsChecked</c> on both radio
    /// buttons in turn, and each assignment raises this event — so without the
    /// guard, restoring settings re-entrantly rewrites the summary in the middle
    /// of applying a presentation, against an editor that is being replaced
    /// underneath it.
    /// </remarks>
    private void OnDesktopSwitchProfileChecked(object sender, RoutedEventArgs args)
    {
        if (isApplyingPresentation || desktopSwitchEditor is null)
        {
            return;
        }

        desktopSwitchEditor.Profile = ReadSelectedDesktopSwitchProfile();
        RefreshDesktopSwitchPresentation();
    }

    private void OnDesktopSwitchModifierChanged(object sender, RoutedEventArgs args)
    {
        if (isApplyingPresentation || desktopSwitchEditor is null)
        {
            return;
        }

        desktopSwitchEditor.Control = DesktopSwitchControlModifier.IsChecked is true;
        desktopSwitchEditor.Alt = DesktopSwitchAltModifier.IsChecked is true;
        desktopSwitchEditor.Shift = DesktopSwitchShiftModifier.IsChecked is true;
        desktopSwitchEditor.Windows = DesktopSwitchWindowsModifier.IsChecked is true;
        RefreshDesktopSwitchPresentation();
    }

    private DesktopSwitchShortcutProfile ReadSelectedDesktopSwitchProfile()
    {
        return DesktopSwitchCustomOption.IsChecked is true
            ? DesktopSwitchShortcutProfile.Custom
            : DesktopSwitchShortcutProfile.CtrlAlt;
    }

    private async void OnApplyDesktopSwitchClick(object sender, RoutedEventArgs args)
    {
        if (services is null || desktopSwitchEditor is null)
        {
            ReportDisconnected();
            return;
        }

        desktopSwitchEditor.IsEnabled = DesktopSwitchEnabledToggle.IsOn;
        BehaviorSettings requested =
            SettingsBehaviorEditor.WithDesktopSwitchShortcuts(
                currentBehavior,
                desktopSwitchEditor.ToSettings());
        BehaviorSettingsPresentation? presentation =
            await PersistBehaviorAsync(requested);
        if (presentation is null)
        {
            return;
        }

        if (!presentation.Accepted)
        {
            DesktopSwitchStatus.Message =
                "The desktop switching candidate was retained for correction. The previous registrations are still active.";
            DesktopSwitchStatus.Severity = InfoBarSeverity.Warning;
            DesktopSwitchStatus.IsOpen = true;
            return;
        }

        ShowDesktopSwitchState(
            services.DesktopSwitchHotkeys?.Current,
            showSuccess: true);
    }

    /// <summary>
    /// Reports what Windows actually handed over, which is the only part of this
    /// the user cannot work out by reading their own settings.
    /// </summary>
    private void ShowDesktopSwitchState(
        DesktopSwitchHotkeyState? state,
        bool showSuccess)
    {
        if (state is null)
        {
            if (showSuccess)
            {
                DesktopSwitchStatus.Message =
                    "The profile was saved. Desktop switching shortcuts are not connected in this session.";
                DesktopSwitchStatus.Severity = InfoBarSeverity.Informational;
                DesktopSwitchStatus.IsOpen = true;
            }

            return;
        }

        if (state.Issues.IsEmpty && !showSuccess)
        {
            DesktopSwitchStatus.IsOpen = false;
            return;
        }

        DesktopSwitchStatus.Message = state.Issues.IsEmpty
            ? state.IsEnabled
                ? $"{state.RegisteredCount} of {DesktopSwitchShortcuts.MaxDesktopOrdinal} desktop switching shortcuts are registered."
                : "Desktop switching shortcuts are disabled; no combinations are registered."
            : FormatIssues(state.Issues);
        DesktopSwitchStatus.Severity = state.Issues.IsEmpty
            ? InfoBarSeverity.Success
            : InfoBarSeverity.Warning;
        DesktopSwitchStatus.IsOpen = true;
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
                $"{result.ApplicationRuleCount} rules were written to {file.Name}.");
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
        _ = services.DesktopSwitchHotkeys?.Apply(
            behavior.ToDesktopSwitchShortcutSettings());
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

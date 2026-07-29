using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Linq;
using DesktopShift.App.FirstRun;
using DesktopShift.App.Pages;
using DesktopShift.App.Rules;
using DesktopShift.App.Settings;
using DesktopShift.App.ViewModels;
using DesktopShift.Core;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Hotkeys;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Navigation;
using DesktopShift.Core.Observation;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace DesktopShift.App;

public sealed partial class MainWindow : Window
{
    private static readonly IReadOnlyDictionary<string, Type> PageTypes =
        new Dictionary<string, Type>(StringComparer.OrdinalIgnoreCase)
        {
            ["overview"] = typeof(OverviewPage),
            ["rules"] = typeof(RulesPage),
            ["desktops"] = typeof(DesktopsPage),
            ["activity"] = typeof(ActivityPage),
            ["settings"] = typeof(SettingsPage),
            ["about"] = typeof(AboutPage),
        };

    private readonly IThemePreferenceService _themePreferenceService;
    private readonly IFirstRunService _firstRunService;
    private readonly IOverviewConfigurationProjection _overviewProjection;
    private readonly ICompatibilityCoordinator _compatibilityCoordinator;
    private readonly IDesktopTopologyProvider _desktopTopologyProvider;
    private readonly IWindowObservationActivityProjection _activityProjection;
    private readonly IConfigurationService _configurationService;
    private readonly IManagedDesktopReconciliationService _managedDesktopReconciliationService;
    private readonly IManagedDesktopMaintenanceService _managedDesktopMaintenanceService;
    private readonly IWindowAssignmentActivityProjection _assignmentActivityProjection;
    private readonly IWindowReassignmentService _windowReassignmentService;
    private readonly BehaviorSettingsCommand _behaviorSettingsCommand;
    private readonly IStartupRegistration _startupRegistration;
    private readonly IDiagnosticsCoordinator _diagnosticsCoordinator;
    private readonly RulesPageServices _rulesPageServices;
    private readonly SettingsPageServices _settingsPageServices;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private FirstRunState? _firstRunState;
    private bool _isApplyingTheme;
    private bool _isShellStateLoaded;

    public MainWindow(
        IThemePreferenceService themePreferenceService,
        IFirstRunService firstRunService,
        IOverviewConfigurationProjection overviewProjection,
        ICompatibilityCoordinator compatibilityCoordinator,
        IDesktopTopologyProvider desktopTopologyProvider,
        IWindowObservationActivityProjection activityProjection,
        IConfigurationService configurationService,
        IManagedDesktopReconciliationService managedDesktopReconciliationService,
        IManagedDesktopMaintenanceService managedDesktopMaintenanceService,
        IWindowAssignmentActivityProjection assignmentActivityProjection,
        IWindowReassignmentService windowReassignmentService,
        IStartupRegistration startupRegistration,
        IDiagnosticsCoordinator diagnosticsCoordinator,
        IConfigurationExchangeService configurationExchangeService,
        IGlobalHotkeyCoordinator globalHotkeyCoordinator,
        IAutomaticAssignmentPauseController assignmentPauseController,
        IRunningApplicationInventory runningApplicationInventory,
        IApplicationIconReader applicationIconReader,
        TimeProvider timeProvider)
    {
        _themePreferenceService = themePreferenceService ?? throw new ArgumentNullException(nameof(themePreferenceService));
        _firstRunService = firstRunService ?? throw new ArgumentNullException(nameof(firstRunService));
        _overviewProjection = overviewProjection ?? throw new ArgumentNullException(nameof(overviewProjection));
        _compatibilityCoordinator =
            compatibilityCoordinator ?? throw new ArgumentNullException(nameof(compatibilityCoordinator));
        _desktopTopologyProvider =
            desktopTopologyProvider ?? throw new ArgumentNullException(nameof(desktopTopologyProvider));
        _activityProjection =
            activityProjection ?? throw new ArgumentNullException(nameof(activityProjection));
        _configurationService =
            configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        _managedDesktopReconciliationService =
            managedDesktopReconciliationService ??
            throw new ArgumentNullException(nameof(managedDesktopReconciliationService));
        _managedDesktopMaintenanceService =
            managedDesktopMaintenanceService ??
            throw new ArgumentNullException(nameof(managedDesktopMaintenanceService));
        _assignmentActivityProjection =
            assignmentActivityProjection ??
            throw new ArgumentNullException(nameof(assignmentActivityProjection));
        _windowReassignmentService =
            windowReassignmentService ??
            throw new ArgumentNullException(nameof(windowReassignmentService));
        _startupRegistration =
            startupRegistration ?? throw new ArgumentNullException(nameof(startupRegistration));
        _diagnosticsCoordinator =
            diagnosticsCoordinator ??
            throw new ArgumentNullException(nameof(diagnosticsCoordinator));
        _behaviorSettingsCommand = new BehaviorSettingsCommand(
            _configurationService,
            _startupRegistration);
        _rulesPageServices = new RulesPageServices(
            _configurationService,
            _activityProjection,
            runningApplicationInventory ??
                throw new ArgumentNullException(nameof(runningApplicationInventory)),
            applicationIconReader ??
                throw new ArgumentNullException(nameof(applicationIconReader)),
            _windowReassignmentService,
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider)));
        _settingsPageServices = new SettingsPageServices(
            _behaviorSettingsCommand,
            configurationExchangeService ??
                throw new ArgumentNullException(nameof(configurationExchangeService)),
            globalHotkeyCoordinator ??
                throw new ArgumentNullException(nameof(globalHotkeyCoordinator)),
            _compatibilityCoordinator,
            _diagnosticsCoordinator,
            assignmentPauseController ??
                throw new ArgumentNullException(nameof(assignmentPauseController)),
            _windowReassignmentService,
            _themePreferenceService,
            timeProvider,
            ApplyAcceptedBehavior);

        InitializeComponent();

        Title = ProductInfo.ApplicationName;
        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);
        SystemBackdrop = new MicaBackdrop();
        AppWindow.Resize(new global::Windows.Graphics.SizeInt32(1180, 760));

        _themePreferenceService.ThemeChanged += OnThemePreferenceChanged;
        _compatibilityCoordinator.StatusChanged += OnCompatibilityStatusChanged;
        _managedDesktopReconciliationService.Changed +=
            OnManagedDesktopReconciliationChanged;
        RootLayout.Loaded += OnRootLayoutLoaded;
        AppWindow.Closing += OnAppWindowClosing;
        Closed += OnClosed;

        ApplyTheme(_themePreferenceService.CurrentTheme);
        NavigateTo("overview");
    }

    public string CurrentDestinationKey { get; private set; } = "overview";

    /// <summary>
    /// Decides what the window's close button means.
    /// </summary>
    /// <remarks>
    /// Set by the application once the notification-area coordinator exists.
    /// The decision itself belongs to the coordinator, which can be tested;
    /// the code-behind only carries the answer back to the close event.
    /// </remarks>
    public Func<BehaviorSettings, ShellCloseDisposition>? CloseRequestHandler { get; set; }

    /// <summary>
    /// Lets the application shell update services it owns outside this window,
    /// such as notification-area preferences.
    /// </summary>
    public Action<BehaviorSettings>? AcceptedBehaviorHandler { get; set; }

    public void Show()
    {
        Activate();
    }

    public void NavigateTo(string destinationKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationKey);

        ShellDestination destination = ShellNavigationCatalog.Destinations.FirstOrDefault(
            item => string.Equals(item.Key, destinationKey, StringComparison.OrdinalIgnoreCase))
            ?? throw new ArgumentOutOfRangeException(
                nameof(destinationKey),
                destinationKey,
                "The destination is not registered in the shell navigation catalog.");

        if (!PageTypes.TryGetValue(destination.Key, out Type? pageType))
        {
            throw new InvalidOperationException($"No page is registered for shell destination '{destination.Key}'.");
        }

        if (ContentFrame.CurrentSourcePageType != pageType)
        {
            ContentFrame.Navigate(pageType);
        }

        CurrentDestinationKey = destination.Key;
        RefreshCurrentPage();
        NavigationViewItem? matchingItem = FindNavigationItem(destination.Key);
        if (matchingItem is not null && !ReferenceEquals(ShellNavigation.SelectedItem, matchingItem))
        {
            ShellNavigation.SelectedItem = matchingItem;
        }
    }

    private void OnNavigationSelectionChanged(
        NavigationView sender,
        NavigationViewSelectionChangedEventArgs args)
    {
        if (args.SelectedItemContainer?.Tag is string destinationKey)
        {
            NavigateTo(destinationKey);
        }
    }

    private async void OnThemeSelectionChanged(object sender, SelectionChangedEventArgs args)
    {
        if (_isApplyingTheme || ThemeSelector.SelectedItem is not ComboBoxItem selectedItem)
        {
            return;
        }

        if (selectedItem.Tag is string themeName &&
            Enum.TryParse(themeName, ignoreCase: true, out AppTheme theme))
        {
            try
            {
                BehaviorSettings current =
                    _configurationService.CurrentState.Candidate.Behavior ??
                    BehaviorSettingsCommand.ResolveBehavior(
                        _configurationService.CurrentState);
                BehaviorSettingsPresentation saved =
                    await _behaviorSettingsCommand.ApplyAsync(
                        current with { Theme = theme },
                        _lifetimeCancellation.Token);
                if (saved.Accepted)
                {
                    ApplyAcceptedBehavior(saved.ActiveBehavior);
                }
                else
                {
                    ApplyTheme(saved.ActiveBehavior.Theme);
                }
            }
            catch (OperationCanceledException)
            {
            }
            catch (Exception exception)
            {
                Debug.WriteLine(
                    $"DesktopShift could not save the theme preference: {exception}");
                ApplyTheme(
                    BehaviorSettingsCommand.ResolveBehavior(
                        _configurationService.CurrentState).Theme);
            }
        }
    }

    private void ApplyAcceptedBehavior(BehaviorSettings behavior)
    {
        _themePreferenceService.SetTheme(behavior.Theme);
        _ = _settingsPageServices.Hotkeys.Apply(
            behavior.ToHotkeySettings());
        AcceptedBehaviorHandler?.Invoke(behavior);
    }

    private void OnThemePreferenceChanged(object? sender, AppThemeChangedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyTheme(args.CurrentTheme);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() => ApplyTheme(args.CurrentTheme));
    }

    private void ApplyTheme(AppTheme theme)
    {
        _isApplyingTheme = true;
        try
        {
            RootLayout.RequestedTheme = theme switch
            {
                AppTheme.Light => ElementTheme.Light,
                AppTheme.Dark => ElementTheme.Dark,
                _ => ElementTheme.Default,
            };

            foreach (ComboBoxItem item in ThemeSelector.Items.Cast<ComboBoxItem>())
            {
                if (item.Tag is string themeName &&
                    string.Equals(themeName, theme.ToString(), StringComparison.OrdinalIgnoreCase))
                {
                    ThemeSelector.SelectedItem = item;
                    break;
                }
            }
        }
        finally
        {
            _isApplyingTheme = false;
        }
    }

    private NavigationViewItem? FindNavigationItem(string destinationKey)
    {
        return ShellNavigation.MenuItems
            .Concat(ShellNavigation.FooterMenuItems)
            .OfType<NavigationViewItem>()
            .FirstOrDefault(item =>
                item.Tag is string key &&
                string.Equals(key, destinationKey, StringComparison.OrdinalIgnoreCase));
    }

    private async void OnRootLayoutLoaded(object sender, RoutedEventArgs args)
    {
        if (_isShellStateLoaded)
        {
            return;
        }

        _isShellStateLoaded = true;
        RootLayout.Loaded -= OnRootLayoutLoaded;

        try
        {
            _firstRunState = await _firstRunService.GetStateAsync(_lifetimeCancellation.Token);
            _ = await _compatibilityCoordinator.RunCompatibilityTestAsync(
                _lifetimeCancellation.Token);
            RefreshCurrentPage();

            if (!_firstRunState.IsCompleted)
            {
                await ShowFirstRunAsync(_firstRunState);
            }
        }
        catch (OperationCanceledException) when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    private async Task ShowFirstRunAsync(FirstRunState state)
    {
        FirstRunViewModel viewModel = CreateFirstRunViewModel(state);
        FirstRunDialog dialog = new(viewModel, ApplyFirstRunAsync)
        {
            XamlRoot = RootLayout.XamlRoot,
            RequestedTheme = RootLayout.RequestedTheme,
        };

        _ = await dialog.ShowAsync();
    }

    private FirstRunViewModel CreateFirstRunViewModel(FirstRunState state)
    {
        List<FirstRunMappingViewModel> mappings = [];

        foreach (ApplicationRule rule in state.Candidate.ApplicationRules)
        {
            ManagedDesktopDefinition? desktop = state.Candidate.ManagedDesktops.FirstOrDefault(
                item => string.Equals(
                    item.SemanticKey,
                    rule.TargetDesktopKey,
                    StringComparison.OrdinalIgnoreCase));

            mappings.Add(
                new FirstRunMappingViewModel(
                    rule.TargetDesktopKey,
                    rule.Id,
                    rule.DisplayName,
                    desktop?.DisplayName ?? rule.TargetDesktopKey,
                    string.Join(", ", rule.ProcessNames),
                    rule.IsEnabled));
        }

        FirstRunViewModel viewModel = new(
            mappings,
            state.StartWithWindows,
            CreateCompatibilityPresentation(_compatibilityCoordinator.Current));
        viewModel.ValidationSummary = FormatValidationIssues(state.Issues);
        return viewModel;
    }

    private async Task<FirstRunApplyResult> ApplyFirstRunAsync(
        FirstRunViewModel viewModel,
        CancellationToken cancellationToken)
    {
        if (_firstRunState is null)
        {
            return new FirstRunApplyResult(
                false,
                ["The first-run configuration has not finished loading."]);
        }

        ConfigurationDocument original = _firstRunState.Candidate;
        ImmutableArray<ManagedDesktopDefinition> desktops = original.ManagedDesktops
            .Select(desktop =>
            {
                FirstRunMappingViewModel? mapping = viewModel.Mappings.FirstOrDefault(
                    item => string.Equals(
                        item.SemanticKey,
                        desktop.SemanticKey,
                        StringComparison.OrdinalIgnoreCase));
                return mapping is null
                    ? desktop
                    : desktop with { DisplayName = mapping.DesktopName.Trim() };
            })
            .ToImmutableArray();
        ImmutableArray<ApplicationRule> rules = original.ApplicationRules
            .Select(rule =>
            {
                FirstRunMappingViewModel? mapping = viewModel.Mappings.FirstOrDefault(
                    item => string.Equals(item.RuleId, rule.Id, StringComparison.OrdinalIgnoreCase));
                return mapping is null
                    ? rule
                    : rule with
                    {
                        IsEnabled = mapping.IsEnabled,
                        ProcessNames = mapping.ExecutableNames
                            .Split(
                                ',',
                                StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                            .ToImmutableArray(),
                    };
            })
            .ToImmutableArray();
        ConfigurationDocument candidate = original with
        {
            ManagedDesktops = desktops,
            ApplicationRules = rules,
            Behavior = original.Behavior with
            {
                StartWithWindows = viewModel.StartWithWindows,
            },
        };

        FirstRunCompletionResult result = await _firstRunService.CompleteAsync(
            candidate,
            viewModel.StartWithWindows,
            cancellationToken);
        _firstRunState = result.State;

        if (result.Accepted)
        {
            // Saving the document only records the request. The registration is
            // what actually makes DesktopShift start with Windows, so it is
            // applied as soon as the choice is accepted rather than waiting for
            // the user to find the Settings page.
            _ = await _startupRegistration.SetEnabledAsync(
                viewModel.StartWithWindows,
                cancellationToken);
            _ = await _managedDesktopReconciliationService.ReconcileAsync(
                ManagedDesktopReconciliationTrigger.ConfigurationAccepted,
                cancellationToken);
            NavigateTo("overview");
            RefreshCurrentPage();
        }

        string validationSummary = FormatValidationIssues(result.Issues);
        IReadOnlyList<string> messages = string.IsNullOrWhiteSpace(validationSummary)
            ? []
            : validationSummary.Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
        return new FirstRunApplyResult(result.Accepted, messages);
    }

    private async Task<CompatibilityPresentation> RunCompatibilityTestAsync(
        CancellationToken cancellationToken)
    {
        _ = await _compatibilityCoordinator.RunCompatibilityTestAsync(cancellationToken);
        CompatibilityPresentation presentation =
            CreateCompatibilityPresentation(_compatibilityCoordinator.Current);
        RefreshCurrentPage();
        return presentation;
    }

    private void OnCompatibilityStatusChanged(
        object? sender,
        CompatibilityStatusChangedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshCurrentPage();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(RefreshCurrentPage);
    }

    private void OnManagedDesktopReconciliationChanged(
        object? sender,
        ManagedDesktopReconciliationChangedEventArgs args)
    {
        if (_lifetimeCancellation.IsCancellationRequested)
        {
            return;
        }

        if (DispatcherQueue.HasThreadAccess)
        {
            RefreshCurrentPage();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (!_lifetimeCancellation.IsCancellationRequested)
            {
                RefreshCurrentPage();
            }
        });
    }

    private void RefreshCurrentPage()
    {
        CompatibilityPresentation compatibility =
            CreateCompatibilityPresentation(_compatibilityCoordinator.Current);
        ManagedDesktopMappingPresentationSnapshot managedDesktopMappings =
            ManagedDesktopMappingPresentationProjection.Project(
                _managedDesktopReconciliationService.Current,
                _configurationService.CurrentState.Active);

        if (ContentFrame.Content is OverviewPage overviewPage)
        {
            ConfigurationOverview overview = _overviewProjection.GetSnapshot();
            overviewPage.Update(
                new OverviewPresentation(
                    overview.EnabledRuleCount,
                    overview.ManagedDesktopCount,
                    _firstRunState?.IsCompleted == true,
                    compatibility));
            overviewPage.UpdateMappings(managedDesktopMappings);
            overviewPage.UpdateAssignments(
                _assignmentActivityProjection,
                _windowReassignmentService,
                _lifetimeCancellation.Token);
        }
        else if (ContentFrame.Content is SettingsPage settingsPage)
        {
            settingsPage.Update(
                _settingsPageServices,
                compatibility,
                RunCompatibilityTestAsync,
                _lifetimeCancellation.Token);
        }
        else if (ContentFrame.Content is RulesPage rulesPage)
        {
            rulesPage.Update(_rulesPageServices, _lifetimeCancellation.Token);
        }
        else if (ContentFrame.Content is DesktopsPage desktopsPage)
        {
            desktopsPage.Update(
                _desktopTopologyProvider,
                _compatibilityCoordinator.Current.Provider,
                _lifetimeCancellation.Token);
            desktopsPage.UpdateMaintenance(
                _managedDesktopMaintenanceService,
                _lifetimeCancellation.Token);
            desktopsPage.UpdateMappings(managedDesktopMappings);
        }
        else if (ContentFrame.Content is ActivityPage activityPage)
        {
            activityPage.UpdateDiagnostics(
                _diagnosticsCoordinator,
                _lifetimeCancellation.Token);
            activityPage.Update(_activityProjection);
            activityPage.UpdateAssignments(_assignmentActivityProjection);
        }
    }


    private static CompatibilityPresentation CreateCompatibilityPresentation(
        CompatibilityStatus status)
    {
        string build =
            $"{status.BuildAssessment.DisplayVersion} • {status.Build.Architecture}";
        string buildSupport =
            $"{FormatBuildSupport(status.BuildAssessment.Support)} — {status.BuildAssessment.Explanation}";
        string provider =
            $"{status.Provider.Identity.DisplayName} ({status.Provider.Identity.Id}, version {status.Provider.Identity.Version}) • {status.Provider.Availability}";
        string mode = $"{status.Provider.Identity.Mode} Mode";
        string capabilities = FormatCapabilities(status.Provider.Capabilities);
        string testOutcome = status.LastTest.Outcome switch
        {
            CompatibilityTestOutcome.NotRun => "Not tested",
            CompatibilityTestOutcome.PassedLimitedMode => "Limited Mode test passed",
            CompatibilityTestOutcome.PassedFullMode => "Full Mode test passed",
            CompatibilityTestOutcome.Failed => "Compatibility test failed",
            _ => status.LastTest.Outcome.ToString(),
        };
        string testedAt = status.LastTest.TestedAtUtc is DateTimeOffset timestamp
            ? $"Tested {timestamp.UtcDateTime:u}"
            : "No test timestamp";

        return new CompatibilityPresentation(
            build,
            buildSupport,
            provider,
            mode,
            capabilities,
            status.Provider.Explanation,
            testOutcome,
            status.LastTest.Summary,
            testedAt);
    }

    private static string FormatCapabilities(VirtualDesktopCapabilities capabilities)
    {
        List<string> available = [];
        List<string> unavailable = [];

        AddCapability(
            capabilities.CanGetWindowDesktopId,
            "get a window's desktop ID",
            available,
            unavailable);
        AddCapability(
            capabilities.CanMoveWindowToDesktop,
            "move a window to a known desktop",
            available,
            unavailable);
        AddCapability(capabilities.CanEnumerateDesktops, "enumerate desktops", available, unavailable);
        AddCapability(capabilities.CanGetCurrentDesktop, "get the current desktop", available, unavailable);
        AddCapability(capabilities.CanCreateDesktop, "create desktops", available, unavailable);
        AddCapability(capabilities.CanSwitchDesktop, "switch desktops", available, unavailable);
        AddCapability(
            capabilities.CanObserveTopologyChanges,
            "receive topology notifications",
            available,
            unavailable);

        return $"Available: {string.Join(", ", available)}. Unavailable: {string.Join(", ", unavailable)}.";
    }

    private static void AddCapability(
        bool isAvailable,
        string label,
        ICollection<string> available,
        ICollection<string> unavailable)
    {
        (isAvailable ? available : unavailable).Add(label);
    }

    private static string FormatBuildSupport(WindowsBuildSupport support)
    {
        return support switch
        {
            WindowsBuildSupport.UnsupportedPlatform => "Unsupported platform",
            WindowsBuildSupport.UnsupportedWindowsVersion => "Unsupported Windows version",
            WindowsBuildSupport.Supported => "Supported build",
            WindowsBuildSupport.SupportedForEnterpriseOnly => "Enterprise support only",
            WindowsBuildSupport.UnknownWindows11Build => "Unrecognized Windows 11 build",
            _ => support.ToString(),
        };
    }

    private static string FormatValidationIssues(
        IEnumerable<ConfigurationValidationIssue> issues)
    {
        return string.Join(
            Environment.NewLine,
            issues.Select(issue => $"{issue.Path}: {issue.Message}"));
    }

    private void OnAppWindowClosing(AppWindow sender, AppWindowClosingEventArgs args)
    {
        BehaviorSettings behavior =
            BehaviorSettingsCommand.ResolveBehavior(_configurationService.CurrentState);

        // Cancelling the close is the only way a WinUI window survives its own
        // close button, so the disposition has to be decided here rather than in
        // Closed, which fires when the window is already gone.
        if (CloseRequestHandler?.Invoke(behavior) == ShellCloseDisposition.HideToNotificationArea)
        {
            args.Cancel = true;
        }
    }

    private void OnClosed(object sender, WindowEventArgs args)
    {
        _lifetimeCancellation.Cancel();
        _themePreferenceService.ThemeChanged -= OnThemePreferenceChanged;
        _compatibilityCoordinator.StatusChanged -= OnCompatibilityStatusChanged;
        _managedDesktopReconciliationService.Changed -=
            OnManagedDesktopReconciliationChanged;
        RootLayout.Loaded -= OnRootLayoutLoaded;
        AppWindow.Closing -= OnAppWindowClosing;
        Closed -= OnClosed;
        _lifetimeCancellation.Dispose();
    }
}

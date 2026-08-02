using DesktopShift.App.ViewModels;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.ManagedDesktops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class OverviewPage : Page
{
    private ManagedDesktopMappingPresentationSnapshot? _mappingSnapshot;
    private IWindowAssignmentActivityProjection? _assignmentProjection;
    private IWindowReassignmentService? _reassignmentService;
    private IAutomaticAssignmentPauseController? _assignmentPauseController;
    private IManagedDesktopMaintenanceService? _managedDesktopMaintenanceService;
    private WindowReassignmentCommand? _reassignmentCommand;
    private Action<string>? _navigate;
    private CancellationToken _windowCancellationToken;
    private int _mappingUpdateVersion;
    private string _compatibilityMode = "Limited Mode";
    private bool _isFirstRunComplete;
    private bool _isApplyingAutomaticAssignment;
    private bool _isApplyingRecreationPolicy;
    private bool _isUpdatingRecreationPolicy;

    public OverviewPage()
    {
        InitializeComponent();
        ReassignNowButton.IsEnabled = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        const double horizontalPageMargin = 60;
        HomeContent.Width = Math.Max(
            0,
            Math.Min(1000, args.NewSize.Width - horizontalPageMargin));

        bool useCompactHeader = args.NewSize.Width < 540;
        Grid.SetRow(HomeHeaderActions, useCompactHeader ? 1 : 0);
        Grid.SetColumn(HomeHeaderActions, useCompactHeader ? 0 : 1);
        HomeHeaderGrid.RowSpacing = useCompactHeader ? 14 : 0;

        bool useCompactStatus = args.NewSize.Width < 560;
        Grid.SetRow(ModeStatusBadge, useCompactStatus ? 1 : 0);
        Grid.SetColumn(ModeStatusBadge, useCompactStatus ? 1 : 2);
        ModeStatusBadge.HorizontalAlignment = useCompactStatus
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        ModeStatusBadge.Margin = useCompactStatus
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(0);

        bool stackMetrics = args.NewSize.Width < 620;
        MetricColumn0.Width = new GridLength(1, GridUnitType.Star);
        MetricColumn1.Width = stackMetrics
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        MetricColumn2.Width = stackMetrics
            ? new GridLength(0)
            : new GridLength(1, GridUnitType.Star);
        PositionMetricCard(EnabledRulesMetricCard, 0, stackMetrics);
        PositionMetricCard(ManagedDesktopsMetricCard, 1, stackMetrics);
        PositionMetricCard(LastAssignmentMetricCard, 2, stackMetrics);
    }

    private static void PositionMetricCard(
        FrameworkElement card,
        int index,
        bool stack)
    {
        Grid.SetRow(card, stack ? index : 0);
        Grid.SetColumn(card, stack ? 0 : index);
    }

    public void Update(OverviewPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        EnabledRuleCount.Text = presentation.EnabledRuleCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        ManagedDesktopCount.Text = presentation.ManagedDesktopCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        WorkspaceRuleCount.Text = presentation.EnabledRuleCount == 1
            ? "1 rule"
            : $"{presentation.EnabledRuleCount} rules";
        WorkspaceDesktopCount.Text = presentation.ManagedDesktopCount == 1
            ? "1 mapped"
            : $"{presentation.ManagedDesktopCount} mapped";
        _isFirstRunComplete = presentation.IsFirstRunComplete;
        _compatibilityMode = presentation.Compatibility.Mode;
        ModeStatus.Text = presentation.Compatibility.Mode;
        ApplyWorkspaceStatus();
    }

    public void UpdateQuickSettings(
        IAutomaticAssignmentPauseController assignmentPauseController,
        IManagedDesktopMaintenanceService managedDesktopMaintenanceService,
        BehaviorSettings behavior,
        Action<string> navigate,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignmentPauseController);
        ArgumentNullException.ThrowIfNull(managedDesktopMaintenanceService);
        ArgumentNullException.ThrowIfNull(behavior);
        ArgumentNullException.ThrowIfNull(navigate);

        if (!ReferenceEquals(_assignmentPauseController, assignmentPauseController))
        {
            UnsubscribeAutomaticAssignment();
            _assignmentPauseController = assignmentPauseController;
            if (IsLoaded)
            {
                SubscribeAutomaticAssignment();
            }
        }

        _navigate = navigate;
        _managedDesktopMaintenanceService = managedDesktopMaintenanceService;
        _windowCancellationToken = cancellationToken;
        AppearanceSummary.Text = DescribeAppearance(behavior);
        ApplyAutomaticAssignment(assignmentPauseController.IsPaused);
        ApplyRecreationPolicy();
    }

    public void UpdateAssignments(
        IWindowAssignmentActivityProjection assignmentProjection,
        IWindowReassignmentService reassignmentService,
        CancellationToken windowCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(assignmentProjection);
        ArgumentNullException.ThrowIfNull(reassignmentService);

        if (!ReferenceEquals(_assignmentProjection, assignmentProjection))
        {
            UnsubscribeAssignments();
            _assignmentProjection = assignmentProjection;

            if (IsLoaded)
            {
                SubscribeAssignments();
            }
        }

        if (!ReferenceEquals(_reassignmentService, reassignmentService))
        {
            _reassignmentService = reassignmentService;
            _reassignmentCommand = new WindowReassignmentCommand(reassignmentService);
        }

        _windowCancellationToken = windowCancellationToken;
        ReassignNowButton.IsEnabled =
            !windowCancellationToken.IsCancellationRequested &&
            _reassignmentCommand?.IsExecuting == false;
        RefreshAssignmentSnapshot();
    }

    public void UpdateMappings(ManagedDesktopMappingPresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        int version = Interlocked.Increment(ref _mappingUpdateVersion);
        Volatile.Write(ref _mappingSnapshot, snapshot);

        if (DispatcherQueue.HasThreadAccess)
        {
            if (IsLoaded && version == Volatile.Read(ref _mappingUpdateVersion))
            {
                ApplyMappingSnapshot(snapshot);
            }

            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && version == Volatile.Read(ref _mappingUpdateVersion))
            {
                ApplyMappingSnapshot(snapshot);
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeAssignments();
        SubscribeAutomaticAssignment();
        RefreshAssignmentSnapshot();
        if (_reassignmentCommand?.IsExecuting != true)
        {
            ReassignmentProgress.IsActive = false;
            ReassignmentProgress.Visibility = Visibility.Collapsed;
            ReassignNowButton.IsEnabled =
                _reassignmentCommand is not null &&
                !_windowCancellationToken.IsCancellationRequested;
        }

        ManagedDesktopMappingPresentationSnapshot? snapshot =
            Volatile.Read(ref _mappingSnapshot);
        if (snapshot is not null)
        {
            ApplyMappingSnapshot(snapshot);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnsubscribeAssignments();
        UnsubscribeAutomaticAssignment();
    }

    private void SubscribeAutomaticAssignment()
    {
        if (_assignmentPauseController is null)
        {
            return;
        }

        _assignmentPauseController.PauseStateChanged -=
            OnAutomaticAssignmentPauseChanged;
        _assignmentPauseController.PauseStateChanged +=
            OnAutomaticAssignmentPauseChanged;
    }

    private void UnsubscribeAutomaticAssignment()
    {
        if (_assignmentPauseController is not null)
        {
            _assignmentPauseController.PauseStateChanged -=
                OnAutomaticAssignmentPauseChanged;
        }
    }

    private void OnAutomaticAssignmentPauseChanged(
        object? sender,
        AutomaticAssignmentPauseChangedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            ApplyAutomaticAssignment(args.IsPaused);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(
            () => ApplyAutomaticAssignment(args.IsPaused));
    }

    private void ApplyAutomaticAssignment(bool isPaused)
    {
        _isApplyingAutomaticAssignment = true;
        try
        {
            AutomaticAssignmentToggle.IsOn = !isPaused;
        }
        finally
        {
            _isApplyingAutomaticAssignment = false;
        }

        ApplyWorkspaceStatus();
    }

    private void ApplyWorkspaceStatus()
    {
        if (!_isFirstRunComplete)
        {
            WorkspaceStatusTitle.Text = "DesktopShift needs setup";
            WorkspaceSummary.Text =
                "Review and apply the starter mappings before DesktopShift moves windows.";
            return;
        }

        if (_compatibilityMode.Contains("Limited", StringComparison.OrdinalIgnoreCase))
        {
            WorkspaceStatusTitle.Text = "DesktopShift is in Limited Mode";
            WorkspaceSummary.Text =
                "Supported movement remains available; desktop creation, switching, or topology features may be limited on this Windows build.";
            return;
        }

        if (_assignmentPauseController?.IsPaused == true)
        {
            WorkspaceStatusTitle.Text = "Automatic assignment is paused";
            WorkspaceSummary.Text =
                "Manual reassignment remains available while window events are paused.";
            return;
        }

        WorkspaceStatusTitle.Text = "Your workspace is organized";
        WorkspaceSummary.Text =
            "All managed desktops are mapped and automatic assignment is active.";
    }

    private void OnAutomaticAssignmentToggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingAutomaticAssignment || _assignmentPauseController is null)
        {
            return;
        }

        if (AutomaticAssignmentToggle.IsOn)
        {
            _assignmentPauseController.Resume();
        }
        else
        {
            _assignmentPauseController.Pause();
        }

        ApplyAutomaticAssignment(_assignmentPauseController.IsPaused);
    }

    private void ApplyRecreationPolicy()
    {
        ManagedDesktopCatalog catalog =
            _managedDesktopMaintenanceService?.GetCatalog() ??
            ManagedDesktopCatalog.Empty;
        _isApplyingRecreationPolicy = true;
        try
        {
            RecreateMissingToggle.IsEnabled =
                !catalog.Entries.IsEmpty && !_isUpdatingRecreationPolicy;
            RecreateMissingToggle.IsOn =
                !catalog.Entries.IsEmpty &&
                catalog.Entries.All(static entry => entry.RecreateWhenMissing);
        }
        finally
        {
            _isApplyingRecreationPolicy = false;
        }
    }

    private async void OnRecreateMissingToggled(object sender, RoutedEventArgs args)
    {
        if (_isApplyingRecreationPolicy ||
            _isUpdatingRecreationPolicy ||
            _managedDesktopMaintenanceService is null)
        {
            return;
        }

        _isUpdatingRecreationPolicy = true;
        RecreateMissingToggle.IsEnabled = false;
        bool recreateWhenMissing = RecreateMissingToggle.IsOn;
        try
        {
            ManagedDesktopCatalog catalog =
                _managedDesktopMaintenanceService.GetCatalog();
            foreach (ManagedDesktopCatalogEntry entry in catalog.Entries)
            {
                if (entry.RecreateWhenMissing != recreateWhenMissing)
                {
                    _ = await _managedDesktopMaintenanceService
                        .SetRecreationPolicyAsync(
                            entry.SemanticKey,
                            recreateWhenMissing,
                            _windowCancellationToken);
                }
            }
        }
        catch (OperationCanceledException)
            when (_windowCancellationToken.IsCancellationRequested)
        {
        }
        finally
        {
            _isUpdatingRecreationPolicy = false;
            ApplyRecreationPolicy();
        }
    }

    private void OnNavigateClick(object sender, RoutedEventArgs args)
    {
        if (sender is Button { Tag: string destination })
        {
            _navigate?.Invoke(destination);
        }
    }

    private static string DescribeAppearance(BehaviorSettings behavior)
    {
        string theme = behavior.Theme switch
        {
            AppTheme.Light => "Light theme",
            AppTheme.Dark => "Dark theme",
            _ => "Windows theme",
        };
        string accent = behavior.Accent switch
        {
            AppAccent.AditiKraftBlue => "Aditi Kraft Blue",
            _ => "Windows accent color",
        };
        return $"{theme} and {accent}.";
    }

    private void SubscribeAssignments()
    {
        if (_assignmentProjection is not null)
        {
            _assignmentProjection.ActivityRecorded -= OnAssignmentActivityRecorded;
            _assignmentProjection.ActivityRecorded += OnAssignmentActivityRecorded;
        }
    }

    private void UnsubscribeAssignments()
    {
        if (_assignmentProjection is not null)
        {
            _assignmentProjection.ActivityRecorded -= OnAssignmentActivityRecorded;
        }
    }

    private void OnAssignmentActivityRecorded(
        object? sender,
        WindowAssignmentActivityRecordedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            if (IsLoaded)
            {
                ApplyLastAssignment(args.Activity);
            }

            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                ApplyLastAssignment(args.Activity);
            }
        });
    }

    private void RefreshAssignmentSnapshot()
    {
        WindowAssignmentActivity? latest =
            _assignmentProjection?.Snapshot.LastOrDefault();
        if (latest is null)
        {
            LastAssignmentMetric.Text = "—";
            LastAssignmentMetricCaption.Text = "No completed assignment";
            return;
        }

        ApplyLastAssignment(latest);
    }

    private void ApplyLastAssignment(WindowAssignmentActivity activity)
    {
        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(activity);
        LastAssignmentMetric.Text = presentation.Duration;
        LastAssignmentMetricCaption.Text = presentation.Trigger.ToLowerInvariant();
        AutomationProperties.SetName(
            LastAssignmentMetric,
            $"Last assignment. {presentation.AutomationName}");
    }

    private async void OnReassignNowClick(object sender, RoutedEventArgs args)
    {
        WindowReassignmentCommand? command = _reassignmentCommand;
        if (command is null ||
            _windowCancellationToken.IsCancellationRequested ||
            command.IsExecuting)
        {
            return;
        }

        ReassignNowButton.IsEnabled = false;
        ReassignmentProgress.IsActive = true;
        ReassignmentProgress.Visibility = Visibility.Visible;
        ReassignmentResult.IsOpen = false;

        try
        {
            ReassignmentBatchPresentation result =
                await command.ExecuteAsync(_windowCancellationToken);
            if (_windowCancellationToken.IsCancellationRequested || !IsLoaded)
            {
                return;
            }

            ApplyReassignmentResult(result);
            RefreshAssignmentSnapshot();
        }
        catch (OperationCanceledException)
            when (_windowCancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception)
        {
            if (IsLoaded)
            {
                ApplyReassignmentResult(
                    new ReassignmentBatchPresentation(
                        "Reassignment failed",
                        "An unexpected error prevented the reassignment batch from completing. No private window content is shown here.",
                        ReassignmentResultTone.Error));
            }
        }
        finally
        {
            if (IsLoaded)
            {
                ReassignmentProgress.IsActive = false;
                ReassignmentProgress.Visibility = Visibility.Collapsed;
                ReassignNowButton.IsEnabled =
                    !_windowCancellationToken.IsCancellationRequested;
            }
        }
    }

    private void ApplyReassignmentResult(ReassignmentBatchPresentation presentation)
    {
        ReassignmentResult.Title = presentation.Title;
        ReassignmentResult.Message = presentation.Message;
        ReassignmentResult.Severity = presentation.Tone switch
        {
            ReassignmentResultTone.Success => InfoBarSeverity.Success,
            ReassignmentResultTone.Warning => InfoBarSeverity.Warning,
            ReassignmentResultTone.Error => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Informational,
        };
        ReassignmentResult.IsOpen = true;
    }

    private void ApplyMappingSnapshot(ManagedDesktopMappingPresentationSnapshot snapshot)
    {
        WorkspaceDesktopCount.Text = snapshot.MappedCount == 1
            ? "1 mapped"
            : $"{snapshot.MappedCount} mapped";
        ApplyRecreationPolicy();
    }
}

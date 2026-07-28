using DesktopShift.App.ViewModels;
using DesktopShift.Core.Assignments;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class OverviewPage : Page
{
    private ManagedDesktopMappingPresentationSnapshot? _mappingSnapshot;
    private IWindowAssignmentActivityProjection? _assignmentProjection;
    private IWindowReassignmentService? _reassignmentService;
    private WindowReassignmentCommand? _reassignmentCommand;
    private CancellationToken _windowCancellationToken;
    private int _mappingUpdateVersion;

    public OverviewPage()
    {
        InitializeComponent();
        ReassignNowButton.IsEnabled = false;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Update(OverviewPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        EnabledRuleCount.Text = presentation.EnabledRuleCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        ManagedDesktopCount.Text = presentation.ManagedDesktopCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        WorkspaceSummary.Text = presentation.IsFirstRunComplete
            ? "Your reviewed configuration is saved. DesktopShift will only act within the capabilities shown below."
            : "Setup has not been completed. DesktopShift will not move any windows until you review and apply the starter mappings.";
        ModeStatus.Text = presentation.Compatibility.Mode;
        ProviderName.Text = presentation.Compatibility.Provider;
        BuildInfo.Text =
            $"{presentation.Compatibility.Build} • {presentation.Compatibility.BuildSupport}";
        TestOutcome.Text = presentation.Compatibility.TestOutcome;
        CompatibilityExplanation.Text = presentation.Compatibility.Explanation;
        CapabilitySummary.Text = presentation.Compatibility.Capabilities;
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
            LastAssignmentSummary.Text = "No assignment has completed yet.";
            LastAssignmentDetails.Visibility = Visibility.Collapsed;
            AutomationProperties.SetName(
                AssignmentCard,
                "Last assignment. No assignment has completed yet.");
            return;
        }

        ApplyLastAssignment(latest);
    }

    private void ApplyLastAssignment(WindowAssignmentActivity activity)
    {
        AssignmentActivityPresentation presentation =
            AssignmentActivityPresentation.Create(activity);
        LastAssignmentSummary.Text = presentation.Decision;
        LastAssignmentOutcome.Text = presentation.Outcome;
        LastAssignmentTime.Text = presentation.OccurredAt;
        LastAssignmentProcess.Text = presentation.ProcessName;
        LastAssignmentTarget.Text = presentation.TargetDesktop;
        LastAssignmentDuration.Text = presentation.Duration;
        LastAssignmentCorrelation.Text = activity.CorrelationId.ToString("D");
        LastAssignmentMovement.Text = presentation.Movement;
        LastAssignmentNavigation.Text = presentation.DesktopNavigation;
        LastAssignmentSwitchPolicy.Text = presentation.SwitchPolicy;
        LastAssignmentSwitchDuration.Text = presentation.SwitchDuration;
        LastAssignmentRelatedCorrelation.Text =
            activity.RelatedCorrelationId?.ToString("D") ?? string.Empty;
        LastAssignmentRelatedCorrelationPanel.Visibility =
            activity.RelatedCorrelationId.HasValue
                ? Visibility.Visible
                : Visibility.Collapsed;
        LastAssignmentDetails.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            AssignmentCard,
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
        ManagedMappingSummary.Text = snapshot.Summary;
        ManagedMappingOutcome.Text = snapshot.Outcome;
        MappedDesktopCount.Text = snapshot.MappedCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        MappingAttentionCount.Text = snapshot.AttentionCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        ManagedMappingProvider.Text =
            $"{snapshot.ProviderSummary} • {snapshot.ObservedAt}";
        ManagedMappingCard.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            ManagedMappingCard,
            $"Managed desktop mapping. {snapshot.Outcome}. {snapshot.Summary}");
    }
}

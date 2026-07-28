using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.ApplicationModel.DataTransfer;

namespace DesktopShift.App.Pages;

/// <summary>
/// The Activity view: a bounded, virtualized, privacy-safe window onto what
/// DesktopShift decided and did.
/// </summary>
/// <remarks>
/// The code-behind holds no logic worth proving. Filtering, projection to
/// display strings, redaction, log rotation, and bundle assembly all live in
/// <see cref="DesktopShift.Core.Diagnostics"/>, which is testable without a
/// XAML host; this file only binds them to controls.
/// </remarks>
public sealed partial class ActivityPage : Page
{
    private const int MaximumDisplayedActivities = 2000;
    private const string AllApplicationsTag = "all";
    private const string AllRulesTag = "all";

    private readonly ObservableCollection<ActivityRecordDisplay> visible = [];
    private readonly List<ActivityRecord> records = [];

    private IWindowObservationActivityProjection? observationProjection;
    private IWindowAssignmentActivityProjection? assignmentProjection;
    private IDiagnosticsCoordinator? diagnostics;
    private IActivityJournalProjection? journal;
    private CancellationToken lifetimeToken = CancellationToken.None;
    private ImmutableArray<string> applicationOptions = [];
    private ImmutableArray<string> ruleOptions = [];
    private bool isApplyingFilterOptions;

    public ActivityPage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = visible;
        ResultFilter.SelectedIndex = 0;
        DateFilter.SelectedIndex = 0;
        RefreshFilterOptions(force: true);
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
        UpdateState();
    }

    /// <summary>
    /// Points the view at the diagnostics services.
    /// </summary>
    /// <remarks>
    /// This is the wiring the shell supplies. Without it the view still works,
    /// falling back to the observation and assignment projections, but the log
    /// location and bundle export have nothing to act on and stay disabled.
    /// </remarks>
    /// <param name="coordinator">The diagnostics services to bind to.</param>
    /// <param name="cancellationToken">The shell's lifetime token.</param>
    public void UpdateDiagnostics(
        IDiagnosticsCoordinator coordinator,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(coordinator);

        lifetimeToken = cancellationToken;

        if (!ReferenceEquals(diagnostics, coordinator))
        {
            UnsubscribeJournal();
            diagnostics = coordinator;
            journal = coordinator.Activity;

            if (IsLoaded)
            {
                SubscribeJournal();
            }
        }

        RefreshSnapshot();
    }

    public void Update(IWindowObservationActivityProjection activityProjection)
    {
        ArgumentNullException.ThrowIfNull(activityProjection);

        if (!ReferenceEquals(observationProjection, activityProjection))
        {
            UnsubscribeObservations();
            observationProjection = activityProjection;

            if (IsLoaded)
            {
                SubscribeObservations();
            }
        }

        RefreshSnapshot();
    }

    public void UpdateAssignments(
        IWindowAssignmentActivityProjection windowAssignmentProjection)
    {
        ArgumentNullException.ThrowIfNull(windowAssignmentProjection);

        if (!ReferenceEquals(assignmentProjection, windowAssignmentProjection))
        {
            UnsubscribeAssignments();
            assignmentProjection = windowAssignmentProjection;

            if (IsLoaded)
            {
                SubscribeAssignments();
            }
        }

        RefreshSnapshot();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeJournal();
        SubscribeObservations();
        SubscribeAssignments();
        RefreshSnapshot();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        UnsubscribeJournal();
        UnsubscribeObservations();
        UnsubscribeAssignments();
    }

    private void SubscribeJournal()
    {
        if (journal is not null)
        {
            journal.RecordAdded -= OnRecordAdded;
            journal.RecordAdded += OnRecordAdded;
            journal.Cleared -= OnJournalCleared;
            journal.Cleared += OnJournalCleared;
        }
    }

    private void UnsubscribeJournal()
    {
        if (journal is not null)
        {
            journal.RecordAdded -= OnRecordAdded;
            journal.Cleared -= OnJournalCleared;
        }
    }

    private void SubscribeObservations()
    {
        if (observationProjection is not null)
        {
            observationProjection.ActivityRecorded -= OnObservationRecorded;
            observationProjection.ActivityRecorded += OnObservationRecorded;
        }
    }

    private void UnsubscribeObservations()
    {
        if (observationProjection is not null)
        {
            observationProjection.ActivityRecorded -= OnObservationRecorded;
        }
    }

    private void SubscribeAssignments()
    {
        if (assignmentProjection is not null)
        {
            assignmentProjection.ActivityRecorded -= OnAssignmentRecorded;
            assignmentProjection.ActivityRecorded += OnAssignmentRecorded;
        }
    }

    private void UnsubscribeAssignments()
    {
        if (assignmentProjection is not null)
        {
            assignmentProjection.ActivityRecorded -= OnAssignmentRecorded;
        }
    }

    private void OnRecordAdded(object? sender, ActivityRecordedEventArgs args) =>
        RunOnDispatcher(RefreshSnapshot);

    private void OnJournalCleared(object? sender, EventArgs args) =>
        RunOnDispatcher(RefreshSnapshot);

    private void OnObservationRecorded(
        object? sender,
        WindowObservationActivityRecordedEventArgs args) =>
        RunOnDispatcher(RefreshSnapshot);

    private void OnAssignmentRecorded(
        object? sender,
        WindowAssignmentActivityRecordedEventArgs args) =>
        RunOnDispatcher(RefreshSnapshot);

    private void RunOnDispatcher(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                action();
            }
        });
    }

    private void RefreshSnapshot()
    {
        records.Clear();
        records.AddRange(CollectRecords());
        RefreshFilterOptions(force: false);
        ApplyFilter();
    }

    /// <summary>
    /// Reads the journal when the shell has wired it, and otherwise projects
    /// the observation and assignment stores so the view is never blank merely
    /// because diagnostics have not been supplied.
    /// </summary>
    private IEnumerable<ActivityRecord> CollectRecords()
    {
        if (journal is not null)
        {
            return journal.Snapshot.TakeLast(MaximumDisplayedActivities);
        }

        Guid sessionId = Guid.Empty;
        List<ActivityRecord> projected = [];

        if (observationProjection is not null)
        {
            projected.AddRange(
                observationProjection.Snapshot.Select(
                    activity => ActivityRecordFactory.FromObservation(
                        activity,
                        sessionId)));
        }

        if (assignmentProjection is not null)
        {
            foreach (WindowAssignmentActivity activity in
                assignmentProjection.Snapshot)
            {
                projected.AddRange(
                    ActivityRecordFactory.FromAssignment(activity, sessionId));
            }
        }

        return projected
            .OrderBy(static record => record.OccurredAt)
            .TakeLast(MaximumDisplayedActivities);
    }

    private void RefreshFilterOptions(bool force)
    {
        ActivityFilterOptions options = ActivityFilterOptions.From(records);
        bool applicationsChanged =
            force || !options.Applications.SequenceEqual(applicationOptions);
        bool rulesChanged = force || !options.RuleIds.SequenceEqual(ruleOptions);

        if (!applicationsChanged && !rulesChanged)
        {
            return;
        }

        isApplyingFilterOptions = true;
        try
        {
            if (applicationsChanged)
            {
                applicationOptions = options.Applications;
                Repopulate(
                    ApplicationFilter,
                    "All applications",
                    AllApplicationsTag,
                    applicationOptions);
            }

            if (rulesChanged)
            {
                ruleOptions = options.RuleIds;
                Repopulate(RuleFilter, "All rules", AllRulesTag, ruleOptions);
            }
        }
        finally
        {
            isApplyingFilterOptions = false;
        }
    }

    private static void Repopulate(
        ComboBox target,
        string allLabel,
        string allTag,
        ImmutableArray<string> values)
    {
        string? selectedTag = (target.SelectedItem as ComboBoxItem)?.Tag as string;
        target.Items.Clear();
        target.Items.Add(new ComboBoxItem { Content = allLabel, Tag = allTag });

        foreach (string value in values)
        {
            target.Items.Add(new ComboBoxItem { Content = value, Tag = value });
        }

        int selectedIndex = 0;
        if (selectedTag is not null)
        {
            for (int index = 0; index < target.Items.Count; index++)
            {
                if (target.Items[index] is ComboBoxItem item &&
                    string.Equals(
                        item.Tag as string,
                        selectedTag,
                        StringComparison.OrdinalIgnoreCase))
                {
                    selectedIndex = index;
                    break;
                }
            }
        }

        target.SelectedIndex = selectedIndex;
    }

    private ActivityFilter BuildFilter() =>
        new()
        {
            Results = ReadResultFilter(),
            Application = ReadTag(ApplicationFilter, AllApplicationsTag),
            RuleId = ReadTag(RuleFilter, AllRulesTag),
            Date = ReadDateFilter(),
            SessionId = journal?.SessionId,
        };

    private ImmutableArray<ActivityResult> ReadResultFilter() =>
        ReadTag(ResultFilter, "all") switch
        {
            "succeeded" => [ActivityResult.Succeeded],
            "skipped" => [ActivityResult.Skipped],
            "failed" => [ActivityResult.Failed],
            _ => [],
        };

    private ActivityDateFilter ReadDateFilter() =>
        ReadTag(DateFilter, "all") switch
        {
            "session" => ActivityDateFilter.CurrentSession,
            "hour" => ActivityDateFilter.LastHour,
            "today" => ActivityDateFilter.Today,
            "week" => ActivityDateFilter.LastSevenDays,
            _ => ActivityDateFilter.AllTime,
        };

    private static string? ReadTag(ComboBox source, string allTag)
    {
        string? tag = (source.SelectedItem as ComboBoxItem)?.Tag as string;
        return string.Equals(tag, allTag, StringComparison.OrdinalIgnoreCase)
            ? null
            : tag;
    }

    private void ApplyFilter()
    {
        ActivityFilter filter = BuildFilter();
        DateTimeOffset now = DateTimeOffset.UtcNow;

        visible.Clear();
        foreach (ActivityRecord record in filter
            .Apply(records, now)
            .Reverse())
        {
            visible.Add(ActivityRecordDisplay.Create(record));
        }

        UpdateState();
    }

    private void OnFilterChanged(object sender, SelectionChangedEventArgs args)
    {
        if (isApplyingFilterOptions || !IsLoaded)
        {
            return;
        }

        ApplyFilter();
    }

    private void OnSelectionChanged(object sender, SelectionChangedEventArgs args) =>
        CopyEventButton.IsEnabled = ActivityList.SelectedItem is not null;

    private void OnCopyEventClick(object sender, RoutedEventArgs args)
    {
        if (ActivityList.SelectedItem is not ActivityRecordDisplay selected)
        {
            return;
        }

        DataPackage package = new()
        {
            RequestedOperation = DataPackageOperation.Copy,
        };
        package.SetText(selected.CopyText);
        Clipboard.SetContent(package);
        ReportStatus(
            InfoBarSeverity.Success,
            "Event copied",
            "The selected event was copied to the clipboard.");
    }

    private void OnClearActivityClick(object sender, RoutedEventArgs args)
    {
        // Only the application's own journal and rolling log files are removed.
        diagnostics?.ClearLocalActivity();
        records.Clear();
        visible.Clear();
        RefreshFilterOptions(force: true);
        UpdateState();
        ReportStatus(
            InfoBarSeverity.Success,
            "Local activity cleared",
            "The recorded activity and DesktopShift's own log files were removed.");
    }

    private void OnOpenLogLocationClick(object sender, RoutedEventArgs args)
    {
        if (diagnostics is null)
        {
            return;
        }

        try
        {
            string path = diagnostics.LogLocation.DirectoryPath;
            _ = Directory.CreateDirectory(path);
            using Process? shell = Process.Start(
                new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception exception)
        {
            ReportStatus(
                InfoBarSeverity.Error,
                "The log location could not be opened",
                exception.Message);
        }
    }

    private async void OnExportBundleClick(object sender, RoutedEventArgs args)
    {
        if (diagnostics is null)
        {
            return;
        }

        ExportBundleButton.IsEnabled = false;
        try
        {
            // The bundle is written and nothing more. It is not opened, sent,
            // or shared; what happens to it next is the user's decision.
            DiagnosticBundleSummary summary = await diagnostics
                .ExportBundleToDefaultLocationAsync(lifetimeToken);
            ReportStatus(
                InfoBarSeverity.Success,
                "Diagnostic bundle exported",
                $"{summary.ActivityRecordCount} events and {summary.LogFileCount} log files were written to {summary.DisplayPath}.");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ReportStatus(
                InfoBarSeverity.Error,
                "The diagnostic bundle could not be exported",
                exception.Message);
        }
        finally
        {
            ExportBundleButton.IsEnabled = diagnostics is not null;
        }
    }

    private void ReportStatus(
        InfoBarSeverity severity,
        string title,
        string message)
    {
        StatusBar.Severity = severity;
        StatusBar.Title = title;
        StatusBar.Message = message;
        StatusBar.IsOpen = true;
    }

    private void UpdateState()
    {
        bool hasVisible = visible.Count > 0;
        EmptyState.Visibility = hasVisible ? Visibility.Collapsed : Visibility.Visible;
        ActivityList.Visibility = hasVisible ? Visibility.Visible : Visibility.Collapsed;
        EmptyStateMessage.Text = records.Count > 0
            ? "No events match the current filters."
            : "Decisions, moves, switches, and assignment results will appear here with privacy-safe window identity.";
        ActivityCount.Text = visible.Count == 1
            ? $"1 of {records.Count} events"
            : $"{visible.Count} of {records.Count} events";
        CopyEventButton.IsEnabled = ActivityList.SelectedItem is not null;
        OpenLogLocationButton.IsEnabled = diagnostics is not null;
        ExportBundleButton.IsEnabled = diagnostics is not null;
    }
}

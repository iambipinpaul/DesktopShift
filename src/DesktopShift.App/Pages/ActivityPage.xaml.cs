using System.Collections.ObjectModel;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class ActivityPage : Page
{
    private const int MaximumDisplayedActivities = 500;
    private readonly ObservableCollection<ObservationActivityPresentation> activities = [];
    private readonly ObservableCollection<AssignmentActivityPresentation> assignments = [];
    private IWindowObservationActivityProjection? projection;
    private IWindowAssignmentActivityProjection? assignmentProjection;

    public ActivityPage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = activities;
        AssignmentList.ItemsSource = assignments;
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Update(IWindowObservationActivityProjection activityProjection)
    {
        ArgumentNullException.ThrowIfNull(activityProjection);

        if (!ReferenceEquals(projection, activityProjection))
        {
            Unsubscribe();
            projection = activityProjection;

            if (IsLoaded)
            {
                Subscribe();
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

        RefreshAssignmentSnapshot();
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Subscribe();
        SubscribeAssignments();
        RefreshSnapshot();
        RefreshAssignmentSnapshot();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Unsubscribe();
        UnsubscribeAssignments();
    }

    private void Subscribe()
    {
        if (projection is not null)
        {
            projection.ActivityRecorded -= OnActivityRecorded;
            projection.ActivityRecorded += OnActivityRecorded;
        }
    }

    private void Unsubscribe()
    {
        if (projection is not null)
        {
            projection.ActivityRecorded -= OnActivityRecorded;
        }
    }

    private void SubscribeAssignments()
    {
        if (assignmentProjection is not null)
        {
            assignmentProjection.ActivityRecorded -= OnAssignmentActivityRecorded;
            assignmentProjection.ActivityRecorded += OnAssignmentActivityRecorded;
        }
    }

    private void UnsubscribeAssignments()
    {
        if (assignmentProjection is not null)
        {
            assignmentProjection.ActivityRecorded -= OnAssignmentActivityRecorded;
        }
    }

    private void OnActivityRecorded(
        object? sender,
        WindowObservationActivityRecordedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            AddActivity(args.Activity);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                AddActivity(args.Activity);
            }
        });
    }

    private void OnAssignmentActivityRecorded(
        object? sender,
        WindowAssignmentActivityRecordedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            AddAssignment(args.Activity);
            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                AddAssignment(args.Activity);
            }
        });
    }

    private void RefreshSnapshot()
    {
        IWindowObservationActivityProjection? currentProjection = projection;
        if (currentProjection is null)
        {
            activities.Clear();
            UpdateState();
            return;
        }

        IReadOnlyList<WindowObservationActivity> snapshot =
            currentProjection.Snapshot;
        activities.Clear();

        foreach (WindowObservationActivity activity in snapshot
            .TakeLast(MaximumDisplayedActivities)
            .Reverse())
        {
            activities.Add(ObservationActivityPresentation.Create(activity));
        }

        UpdateState();
    }

    private void RefreshAssignmentSnapshot()
    {
        IWindowAssignmentActivityProjection? currentProjection =
            assignmentProjection;
        if (currentProjection is null)
        {
            assignments.Clear();
            UpdateState();
            return;
        }

        IReadOnlyList<WindowAssignmentActivity> snapshot =
            currentProjection.Snapshot;
        assignments.Clear();

        foreach (WindowAssignmentActivity activity in snapshot
            .TakeLast(MaximumDisplayedActivities)
            .Reverse())
        {
            assignments.Add(AssignmentActivityPresentation.Create(activity));
        }

        UpdateState();
    }

    private void AddActivity(WindowObservationActivity activity)
    {
        int existingIndex = FindActivityIndex(activity.EventSequence);
        if (existingIndex >= 0)
        {
            activities.RemoveAt(existingIndex);
        }

        activities.Insert(0, ObservationActivityPresentation.Create(activity));
        while (activities.Count > MaximumDisplayedActivities)
        {
            activities.RemoveAt(activities.Count - 1);
        }

        UpdateState();
    }

    private void AddAssignment(WindowAssignmentActivity activity)
    {
        int existingIndex = FindAssignmentIndex(
            activity.CorrelationId,
            activity.WindowHandle);
        if (existingIndex >= 0)
        {
            assignments.RemoveAt(existingIndex);
        }

        assignments.Insert(0, AssignmentActivityPresentation.Create(activity));
        while (assignments.Count > MaximumDisplayedActivities)
        {
            assignments.RemoveAt(assignments.Count - 1);
        }

        UpdateState();
    }

    private int FindActivityIndex(long eventSequence)
    {
        for (int index = 0; index < activities.Count; index++)
        {
            if (activities[index].EventSequence == eventSequence)
            {
                return index;
            }
        }

        return -1;
    }

    private int FindAssignmentIndex(Guid correlationId, nint windowHandle)
    {
        for (int index = 0; index < assignments.Count; index++)
        {
            AssignmentActivityPresentation candidate = assignments[index];
            if (candidate.CorrelationId == correlationId &&
                candidate.WindowHandle == windowHandle)
            {
                return index;
            }
        }

        return -1;
    }

    private void UpdateState()
    {
        bool hasActivity = activities.Count > 0 || assignments.Count > 0;
        EmptyState.Visibility = hasActivity ? Visibility.Collapsed : Visibility.Visible;
        ActivityPanel.Visibility =
            activities.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        AssignmentPanel.Visibility =
            assignments.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ActivityCount.Text = activities.Count == 1
            ? "1 observation"
            : $"{activities.Count} observations";
        AssignmentCount.Text = assignments.Count == 1
            ? "1 assignment"
            : $"{assignments.Count} assignments";
    }
}

using System.Collections.ObjectModel;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Observation;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class ActivityPage : Page
{
    private const int MaximumDisplayedActivities = 500;
    private readonly ObservableCollection<ObservationActivityPresentation> activities = [];
    private IWindowObservationActivityProjection? projection;

    public ActivityPage()
    {
        InitializeComponent();
        ActivityList.ItemsSource = activities;
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

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Subscribe();
        RefreshSnapshot();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Unsubscribe();
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

    private void UpdateState()
    {
        bool hasActivity = activities.Count > 0;
        EmptyState.Visibility = hasActivity ? Visibility.Collapsed : Visibility.Visible;
        ActivityPanel.Visibility = hasActivity ? Visibility.Visible : Visibility.Collapsed;
        ActivityCount.Text = activities.Count == 1
            ? "1 observation"
            : $"{activities.Count} observations";
    }
}

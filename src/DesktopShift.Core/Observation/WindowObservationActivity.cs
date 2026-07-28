namespace DesktopShift.Core.Observation;

public enum WindowObservationOutcome
{
    Matched,
    Skipped,
}

public sealed record WindowObservationActivity(
    DateTimeOffset OccurredAt,
    long EventSequence,
    WindowEventKind Trigger,
    nint WindowHandle,
    WindowObservationOutcome Outcome,
    WindowSkipReason SkipReason,
    string? RuleId,
    string? TargetDesktopKey,
    WindowSafeIdentity? Identity,
    int? NativeErrorCode = null);

public interface IWindowObservationActivitySink
{
    ValueTask RecordAsync(
        WindowObservationActivity activity,
        CancellationToken cancellationToken = default);
}

public sealed class WindowObservationActivityRecordedEventArgs : EventArgs
{
    public WindowObservationActivityRecordedEventArgs(
        WindowObservationActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Activity = activity;
    }

    public WindowObservationActivity Activity { get; }
}

public interface IWindowObservationActivityProjection
{
    IReadOnlyList<WindowObservationActivity> Snapshot { get; }

    event EventHandler<WindowObservationActivityRecordedEventArgs>?
        ActivityRecorded;
}

public sealed class BoundedWindowObservationActivityStore :
    IWindowObservationActivitySink,
    IWindowObservationActivityProjection
{
    public const int DefaultCapacity = 500;

    private readonly object syncRoot = new();
    private readonly Queue<WindowObservationActivity> activities;
    private readonly int capacity;

    public BoundedWindowObservationActivityStore(
        int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        activities = new Queue<WindowObservationActivity>(capacity);
    }

    public IReadOnlyList<WindowObservationActivity> Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return activities.ToArray();
            }
        }
    }

    public event EventHandler<WindowObservationActivityRecordedEventArgs>?
        ActivityRecorded;

    public ValueTask RecordAsync(
        WindowObservationActivity activity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (activities.Count == capacity)
            {
                activities.Dequeue();
            }

            activities.Enqueue(activity);
        }

        ActivityRecorded?.Invoke(
            this,
            new WindowObservationActivityRecordedEventArgs(activity));
        return ValueTask.CompletedTask;
    }
}

public sealed class NullWindowObservationActivitySink :
    IWindowObservationActivitySink
{
    public ValueTask RecordAsync(
        WindowObservationActivity activity,
        CancellationToken cancellationToken = default) =>
        ValueTask.CompletedTask;
}

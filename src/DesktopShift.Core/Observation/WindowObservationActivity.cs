using DesktopShift.Core.Assignments;

namespace DesktopShift.Core.Observation;

public enum WindowObservationOutcome
{
    Matched,
    Skipped,
}

/// <summary>
/// The provenance of a single observation decision: what was observed, what was
/// decided, and which privacy-safe signal drove the decision.
/// </summary>
/// <param name="OccurredAt">When the originating window event was observed.</param>
/// <param name="EventSequence">
/// The monotonic sequence number of the originating window event.
/// </param>
/// <param name="Trigger">The window event kind that started the observation.</param>
/// <param name="WindowHandle">The root window handle the decision applies to.</param>
/// <param name="Outcome">Whether the window matched a rule or was skipped.</param>
/// <param name="SkipReason">
/// Why the window was skipped, or None when the window matched.
/// </param>
/// <param name="RuleId">
/// The identifier of the matched rule, null for skipped windows.
/// </param>
/// <param name="TargetDesktopKey">
/// The managed desktop key the matched rule targets, null for skipped windows.
/// </param>
/// <param name="Identity">
/// The privacy-safe window identity, null when identity could not be resolved.
/// </param>
/// <param name="NativeErrorCode">
/// The Windows error code reported while qualifying or resolving the window.
/// </param>
/// <param name="AssignmentCorrelationId">
/// The correlation identifier of the assignment attempt, when one ran.
/// </param>
/// <param name="AssignmentOutcome">The assignment result, when one ran.</param>
/// <param name="AssignmentDuration">
/// How long the assignment attempt took, when one ran.
/// </param>
/// <param name="TargetRuntimeDesktopId">
/// The runtime desktop identifier the assignment resolved, when one ran.
/// </param>
/// <param name="AssignmentError">The assignment failure, when one occurred.</param>
/// <param name="Assignment">The full assignment activity, when one ran.</param>
/// <param name="MatchedOn">
/// The window identity signal that selected the rule, so the Activity view can
/// tell a strong packaged identity apart from a weak process name. This is null
/// for skipped windows, which never selected a rule, and for matches recorded
/// before this field existed.
/// </param>
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
    int? NativeErrorCode = null,
    Guid? AssignmentCorrelationId = null,
    WindowAssignmentOutcome? AssignmentOutcome = null,
    TimeSpan? AssignmentDuration = null,
    Guid? TargetRuntimeDesktopId = null,
    WindowAssignmentError? AssignmentError = null,
    WindowAssignmentActivity? Assignment = null,
    WindowMatchStrength? MatchedOn = null);

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

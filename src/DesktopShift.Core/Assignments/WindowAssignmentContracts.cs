using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Assignments;

public enum WindowAssignmentOutcome
{
    Succeeded,
    Skipped,
    Failed,
}

public enum WindowAssignmentSkipReason
{
    None,
    AlreadyOnTargetDesktop,
    TargetDesktopUnresolved,
    SelfGeneratedForegroundSuppressed,
}

public enum WindowMoveOutcome
{
    NotAttempted,
    Succeeded,
    AlreadyCorrect,
    Failed,
}

public enum DesktopSwitchOutcome
{
    NotRequested,
    Succeeded,
    Suppressed,
    Limited,
    Failed,
}

public enum DesktopSwitchDecisionReason
{
    BackgroundEventMoveOnly,
    PolicyNever,
    PolicyOnNewWindowNotEligible,
    CurrentDesktopAlreadyTarget,
    SelfGeneratedForegroundEvent,
    CapabilityUnavailable,
    PolicyApproved,
    SwitchFailed,
}

public sealed record WindowAssignmentError(
    string Code,
    string Message,
    int? HResult = null,
    int? NativeErrorCode = null);

public sealed record WindowAssignmentActivity(
    Guid CorrelationId,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    WindowEventKind Trigger,
    nint WindowHandle,
    WindowAssignmentOutcome Outcome,
    WindowAssignmentSkipReason SkipReason,
    string RuleId,
    string TargetDesktopKey,
    Guid? TargetDesktopId,
    Guid? PreviousDesktopId,
    WindowSafeIdentity Identity,
    WindowAssignmentError? Error,
    WindowMoveOutcome MoveOutcome = WindowMoveOutcome.NotAttempted,
    DesktopSwitchPolicy SwitchPolicy = DesktopSwitchPolicy.Never,
    DesktopSwitchOutcome SwitchOutcome = DesktopSwitchOutcome.NotRequested,
    DesktopSwitchDecisionReason SwitchDecisionReason =
        DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
    TimeSpan SwitchDuration = default,
    Guid? RelatedCorrelationId = null);

public sealed class WindowAssignmentActivityRecordedEventArgs : EventArgs
{
    public WindowAssignmentActivityRecordedEventArgs(
        WindowAssignmentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Activity = activity;
    }

    public WindowAssignmentActivity Activity { get; }
}

public interface IWindowAssignmentActivitySink
{
    ValueTask RecordAsync(
        WindowAssignmentActivity activity,
        CancellationToken cancellationToken = default);
}

public interface IWindowAssignmentActivityProjection
{
    IReadOnlyList<WindowAssignmentActivity> Snapshot { get; }

    event EventHandler<WindowAssignmentActivityRecordedEventArgs>?
        ActivityRecorded;
}

/// <param name="Trigger">The window event kind that started the assignment.</param>
/// <param name="WindowHandle">The root window handle to assign.</param>
/// <param name="Rule">The rule that selected the target desktop.</param>
/// <param name="Identity">The privacy-safe identity of the window.</param>
/// <param name="CorrelationId">
/// The identifier the observation already minted for this window event. It is
/// carried forward so the decision, the move, the switch, and the result all
/// report the same correlation. A default value means the caller had no
/// correlation to hand and the assignment mints its own.
/// </param>
public sealed record WindowAssignmentRequest(
    WindowEventKind Trigger,
    nint WindowHandle,
    WindowObservationRule Rule,
    WindowSafeIdentity Identity,
    Guid CorrelationId = default);

public interface IWindowAssignmentService
{
    ValueTask<WindowAssignmentActivity> AssignAsync(
        WindowAssignmentRequest request,
        CancellationToken cancellationToken = default);
}

public sealed record WindowReassignmentBatchResult(
    Guid CorrelationId,
    DateTimeOffset StartedAtUtc,
    TimeSpan Duration,
    WindowEventKind Trigger,
    int EnumeratedWindowCount,
    ImmutableArray<WindowAssignmentActivity> Assignments);

public interface IWindowReassignmentService
{
    Task<WindowReassignmentBatchResult> ReconcileStartupAsync(
        CancellationToken cancellationToken = default);

    Task<WindowReassignmentBatchResult> ReassignAllAsync(
        CancellationToken cancellationToken = default);
}

public sealed class BoundedWindowAssignmentActivityStore :
    IWindowAssignmentActivitySink,
    IWindowAssignmentActivityProjection
{
    public const int DefaultCapacity = 500;

    private readonly object syncRoot = new();
    private readonly Queue<WindowAssignmentActivity> activities;
    private readonly int capacity;

    public BoundedWindowAssignmentActivityStore(
        int capacity = DefaultCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);
        this.capacity = capacity;
        activities = new Queue<WindowAssignmentActivity>(capacity);
    }

    public IReadOnlyList<WindowAssignmentActivity> Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return activities.ToArray();
            }
        }
    }

    public event EventHandler<WindowAssignmentActivityRecordedEventArgs>?
        ActivityRecorded;

    public ValueTask RecordAsync(
        WindowAssignmentActivity activity,
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

        Publish(activity);
        return ValueTask.CompletedTask;
    }

    private void Publish(WindowAssignmentActivity activity)
    {
        EventHandler<WindowAssignmentActivityRecordedEventArgs>? handlers =
            ActivityRecorded;
        if (handlers is null)
        {
            return;
        }

        WindowAssignmentActivityRecordedEventArgs args = new(activity);
        foreach (EventHandler<WindowAssignmentActivityRecordedEventArgs> handler
            in handlers.GetInvocationList()
                .Cast<EventHandler<WindowAssignmentActivityRecordedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A projection subscriber cannot invalidate an assignment.
            }
        }
    }
}

using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Diagnostics;

/// <summary>
/// The stage of the assignment pipeline that emitted an activity event.
/// </summary>
/// <remarks>
/// Every stage of one assignment carries the same correlation identifier, so a
/// reader can follow a single window from the decision that selected a rule to
/// the move, the desktop switch, and the final result.
/// </remarks>
public enum ActivityEventSource
{
    /// <summary>The rule decision taken for an observed window event.</summary>
    Observation,

    /// <summary>The attempt to move the window to its managed desktop.</summary>
    Move,

    /// <summary>The attempt to switch the current desktop.</summary>
    Switch,

    /// <summary>The overall outcome of the assignment.</summary>
    Assignment,
}

/// <summary>
/// The three outcomes every activity event reduces to, so that one result
/// filter can span observations, moves, switches, and assignments.
/// </summary>
public enum ActivityResult
{
    Succeeded,
    Skipped,
    Failed,
}

/// <summary>
/// The failure detail attached to an activity event, including the Windows
/// error data the Activity view and diagnostic bundles are required to carry.
/// </summary>
public sealed record ActivityErrorDetail(
    string Code,
    string Message,
    int? HResult = null,
    int? NativeErrorCode = null)
{
    /// <summary>
    /// The <c>HRESULT</c> in the conventional hexadecimal form, or null when the
    /// failure did not come from a COM or Win32 call.
    /// </summary>
    public string? HResultText => HResult is int value
        ? $"0x{value:X8}"
        : null;
}

/// <summary>
/// One privacy-safe activity event.
/// </summary>
/// <remarks>
/// This is the only shape that reaches the Activity list, the rolling log, and
/// exported diagnostic bundles. It deliberately carries
/// <see cref="WindowSafeIdentity"/> rather than
/// <see cref="WindowIdentity"/>: window titles, browser URLs, executable paths,
/// and command lines have no member to travel in.
/// </remarks>
public sealed record ActivityRecord(
    Guid CorrelationId,
    Guid SessionId,
    DateTimeOffset OccurredAt,
    ActivityEventSource Source,
    WindowEventKind Trigger,
    ActivityResult Result,
    string ResultCode,
    string Summary,
    string? Application,
    WindowSafeIdentity? Identity,
    string? RuleId,
    string? TargetDesktopKey,
    TimeSpan? Duration = null,
    long? EventSequence = null,
    ActivityErrorDetail? Error = null);

/// <summary>
/// Projects the pipeline's own activity records onto the privacy-safe
/// <see cref="ActivityRecord"/> stream.
/// </summary>
public static class ActivityRecordFactory
{
    /// <summary>
    /// Projects one observation decision.
    /// </summary>
    /// <remarks>
    /// The embedded assignment is deliberately not expanded here. The
    /// assignment sink publishes its own activity, so expanding it twice would
    /// duplicate every move, switch, and result in the journal.
    /// </remarks>
    /// <param name="activity">The observation decision to project.</param>
    /// <param name="sessionId">The application run the event belongs to.</param>
    /// <returns>The decision event for the observed window.</returns>
    public static ActivityRecord FromObservation(
        WindowObservationActivity activity,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(activity);

        bool isMatched = activity.Outcome == WindowObservationOutcome.Matched;
        string resultCode = isMatched
            ? "observation.matched"
            : $"observation.skipped.{ToCode(activity.SkipReason)}";
        string summary = isMatched
            ? BuildMatchSummary(activity)
            : $"Skipped: {Humanize(activity.SkipReason)}.";
        ActivityErrorDetail? error = activity.NativeErrorCode is int nativeErrorCode
            ? new ActivityErrorDetail(
                "observation.native_error",
                $"Windows reported an error while inspecting the window: {Humanize(activity.SkipReason)}.",
                NativeErrorCode: nativeErrorCode)
            : null;

        return new ActivityRecord(
            activity.CorrelationId,
            sessionId,
            activity.OccurredAt,
            ActivityEventSource.Observation,
            activity.Trigger,
            isMatched ? ActivityResult.Succeeded : ActivityResult.Skipped,
            resultCode,
            summary,
            activity.Identity?.ProcessName,
            activity.Identity,
            activity.RuleId,
            activity.TargetDesktopKey,
            EventSequence: activity.EventSequence,
            Error: error);
    }

    /// <summary>
    /// Projects one assignment into its move, switch, and result events.
    /// </summary>
    /// <param name="activity">The assignment to project.</param>
    /// <param name="sessionId">The application run the events belong to.</param>
    /// <returns>
    /// The events the assignment produced, in pipeline order, all sharing the
    /// assignment's correlation identifier.
    /// </returns>
    public static ImmutableArray<ActivityRecord> FromAssignment(
        WindowAssignmentActivity activity,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(activity);

        ImmutableArray<ActivityRecord>.Builder records =
            ImmutableArray.CreateBuilder<ActivityRecord>(3);

        if (activity.MoveOutcome != WindowMoveOutcome.NotAttempted)
        {
            records.Add(
                CreateAssignmentStage(
                    activity,
                    sessionId,
                    ActivityEventSource.Move,
                    activity.MoveOutcome switch
                    {
                        WindowMoveOutcome.Succeeded => ActivityResult.Succeeded,
                        WindowMoveOutcome.Failed => ActivityResult.Failed,
                        _ => ActivityResult.Skipped,
                    },
                    $"move.{ToCode(activity.MoveOutcome)}",
                    BuildMoveSummary(activity),
                    activity.StartedAtUtc,
                    duration: null,
                    error: activity.MoveOutcome == WindowMoveOutcome.Failed
                        ? ToErrorDetail(activity.Error)
                        : null));
        }

        if (activity.SwitchOutcome != DesktopSwitchOutcome.NotRequested)
        {
            records.Add(
                CreateAssignmentStage(
                    activity,
                    sessionId,
                    ActivityEventSource.Switch,
                    activity.SwitchOutcome switch
                    {
                        DesktopSwitchOutcome.Succeeded => ActivityResult.Succeeded,
                        DesktopSwitchOutcome.Failed => ActivityResult.Failed,
                        _ => ActivityResult.Skipped,
                    },
                    $"switch.{ToCode(activity.SwitchOutcome)}",
                    BuildSwitchSummary(activity),
                    activity.StartedAtUtc,
                    activity.SwitchDuration,
                    activity.SwitchOutcome is
                        DesktopSwitchOutcome.Succeeded or
                        DesktopSwitchOutcome.Suppressed
                        ? null
                        : ToErrorDetail(activity.Error)));
        }

        records.Add(
            CreateAssignmentStage(
                activity,
                sessionId,
                ActivityEventSource.Assignment,
                activity.Outcome switch
                {
                    WindowAssignmentOutcome.Succeeded => ActivityResult.Succeeded,
                    WindowAssignmentOutcome.Failed => ActivityResult.Failed,
                    _ => ActivityResult.Skipped,
                },
                activity.Outcome == WindowAssignmentOutcome.Skipped
                    ? $"assignment.skipped.{ToCode(activity.SkipReason)}"
                    : $"assignment.{ToCode(activity.Outcome)}",
                BuildAssignmentSummary(activity),
                activity.StartedAtUtc + activity.Duration,
                activity.Duration,
                ToErrorDetail(activity.Error)));

        return records.ToImmutable();
    }

    private static ActivityRecord CreateAssignmentStage(
        WindowAssignmentActivity activity,
        Guid sessionId,
        ActivityEventSource source,
        ActivityResult result,
        string resultCode,
        string summary,
        DateTimeOffset occurredAt,
        TimeSpan? duration,
        ActivityErrorDetail? error) =>
        new(
            activity.CorrelationId,
            sessionId,
            occurredAt,
            source,
            activity.Trigger,
            result,
            resultCode,
            summary,
            activity.Identity.ProcessName,
            activity.Identity,
            activity.RuleId,
            activity.TargetDesktopKey,
            duration,
            EventSequence: null,
            Error: error);

    private static ActivityErrorDetail? ToErrorDetail(
        WindowAssignmentError? error) =>
        error is null
            ? null
            : new ActivityErrorDetail(
                error.Code,
                error.Message,
                error.HResult,
                error.NativeErrorCode);

    private static string BuildMatchSummary(WindowObservationActivity activity)
    {
        string rule = activity.RuleId ?? "a rule";
        string target = activity.TargetDesktopKey ?? "its managed desktop";
        return activity.MatchedOn is WindowMatchStrength strength
            ? $"Matched {rule} to {target} by {Humanize(strength).ToLower(CultureInfo.InvariantCulture)}."
            : $"Matched {rule} to {target}.";
    }

    private static string BuildMoveSummary(WindowAssignmentActivity activity) =>
        activity.MoveOutcome switch
        {
            WindowMoveOutcome.Succeeded =>
                $"Moved the window to {activity.TargetDesktopKey}.",
            WindowMoveOutcome.AlreadyCorrect =>
                $"The window was already on {activity.TargetDesktopKey}.",
            WindowMoveOutcome.Failed =>
                $"The window could not be moved to {activity.TargetDesktopKey}.",
            _ => "No move was attempted.",
        };

    private static string BuildSwitchSummary(WindowAssignmentActivity activity) =>
        activity.SwitchOutcome switch
        {
            DesktopSwitchOutcome.Succeeded =>
                $"Switched to {activity.TargetDesktopKey}.",
            DesktopSwitchOutcome.Failed =>
                $"The switch to {activity.TargetDesktopKey} failed: {Humanize(activity.SwitchDecisionReason)}.",
            DesktopSwitchOutcome.Limited =>
                "Desktop switching is unavailable in Limited Mode.",
            DesktopSwitchOutcome.Suppressed =>
                $"The switch was suppressed: {Humanize(activity.SwitchDecisionReason)}.",
            _ => $"No switch was requested: {Humanize(activity.SwitchDecisionReason)}.",
        };

    private static string BuildAssignmentSummary(
        WindowAssignmentActivity activity) =>
        activity.Outcome switch
        {
            WindowAssignmentOutcome.Succeeded =>
                $"Assigned {activity.Identity.ProcessName} to {activity.TargetDesktopKey}.",
            WindowAssignmentOutcome.Failed =>
                $"Could not assign {activity.Identity.ProcessName} to {activity.TargetDesktopKey}.",
            _ =>
                $"Skipped {activity.Identity.ProcessName}: {Humanize(activity.SkipReason)}.",
        };

    /// <summary>
    /// Renders an enumeration name as a stable lower snake_case code, so log
    /// readers and bundle consumers get an identifier that never depends on the
    /// user's display language.
    /// </summary>
    internal static string ToCode<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string source = value.ToString();
        StringBuilder result = new(source.Length + 8);

        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (index > 0 &&
                char.IsUpper(current) &&
                !char.IsUpper(source[index - 1]))
            {
                _ = result.Append('_');
            }

            _ = result.Append(char.ToLowerInvariant(current));
        }

        return result.ToString();
    }

    /// <summary>
    /// Renders an enumeration name as spaced words for a human reader.
    /// </summary>
    internal static string Humanize<TEnum>(TEnum value)
        where TEnum : struct, Enum
    {
        string source = value.ToString();
        StringBuilder result = new(source.Length + 8);

        for (int index = 0; index < source.Length; index++)
        {
            char current = source[index];
            if (index > 0 &&
                char.IsUpper(current) &&
                !char.IsUpper(source[index - 1]))
            {
                _ = result.Append(' ');
                _ = result.Append(char.ToLowerInvariant(current));
                continue;
            }

            _ = result.Append(current);
        }

        return result.ToString();
    }
}

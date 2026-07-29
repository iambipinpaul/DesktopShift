using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;

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

    /// <summary>
    /// A decision taken because the Windows virtual-desktop topology changed:
    /// the semantic reconciliation itself, a recreation that was suppressed, a
    /// desktop that only moved position, and whether a window pass followed.
    /// </summary>
    /// <remarks>
    /// These events are not about one window, so they carry no window identity.
    /// They exist so a user who deleted a desktop can read why DesktopShift did
    /// — or deliberately did not — put it back.
    /// </remarks>
    Topology,

    /// <summary>
    /// A decision taken because the shell or the machine disrupted DesktopShift:
    /// Explorer restarting, the machine resuming from sleep, or the display
    /// topology changing.
    /// </summary>
    /// <remarks>
    /// These carry no window identity either. They exist so that "assignment
    /// stopped working after I restarted Explorer" is a question the activity
    /// log already answers, including when the answer is that recovery was tried
    /// and failed.
    /// </remarks>
    Recovery,
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
/// <param name="TopologyReason">
/// Why Windows said the virtual-desktop topology changed — <c>Destroyed</c>,
/// <c>Moved</c>, and so on — on the events
/// <see cref="ActivityEventSource.Topology"/> produces, and null on every other
/// event. It is a structured member rather than prose in the summary because a
/// reader chasing a recreation loop filters on it.
/// </param>
/// <param name="RecoverySignal">
/// Which shell or machine disruption drove the event — <c>ExplorerRestarted</c>,
/// <c>SessionResumed</c>, or <c>DisplayChanged</c> — on the events
/// <see cref="ActivityEventSource.Recovery"/> produces, and null on every other
/// event. Structured for the same reason: the three recovery paths have to be
/// distinguishable in an exported log without reading prose.
/// </param>
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
    ActivityErrorDetail? Error = null,
    string? TopologyReason = null,
    string? RecoverySignal = null);

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

    /// <summary>
    /// Projects one topology-driven recovery pass into its reconciliation,
    /// suppression, reorder, and window-pass events.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every event shares the pass's correlation identifier, so a reader who
    /// finds a suppressed recreation can see the reconciliation that decided it
    /// and whether a window pass followed, without correlating by timestamp.
    /// </para>
    /// <para>
    /// The events carry no <see cref="WindowSafeIdentity"/> because a topology
    /// decision is about desktops, not windows. The only names in them are
    /// semantic keys and configured display names, both of which the user typed.
    /// </para>
    /// </remarks>
    /// <param name="result">The completed pass to project.</param>
    /// <param name="sessionId">The application run the events belong to.</param>
    /// <returns>The events the pass produced, in the order it decided them.</returns>
    public static ImmutableArray<ActivityRecord> FromTopologyRecovery(
        ManagedDesktopTopologyRecoveryResult result,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(result);

        ImmutableArray<ActivityRecord>.Builder records =
            ImmutableArray.CreateBuilder<ActivityRecord>();

        records.Add(
            CreateTopologyRecord(
                result,
                sessionId,
                result.Outcome switch
                {
                    ManagedDesktopTopologyRecoveryOutcome.Failed =>
                        ActivityResult.Failed,
                    ManagedDesktopTopologyRecoveryOutcome.Reconciled =>
                        ActivityResult.Succeeded,
                    _ => ActivityResult.Skipped,
                },
                $"topology.{ToCode(result.Outcome)}",
                result.Summary,
                targetDesktopKey: null));

        foreach (ManagedDesktopRecreationSuppression suppression in result.Suppressions)
        {
            records.Add(
                CreateTopologyRecord(
                    result,
                    sessionId,
                    ActivityResult.Skipped,
                    "topology.recreation_suppressed",
                    suppression.Reason,
                    suppression.SemanticKey,
                    new ActivityErrorDetail(
                        suppression.Code,
                        suppression.Reason)));
        }

        foreach (ManagedDesktopRuntimeRemap remap in result.Remaps)
        {
            records.Add(
                CreateTopologyRecord(
                    result,
                    sessionId,
                    ActivityResult.Succeeded,
                    "topology.runtime_position_changed",
                    $"'{remap.SemanticKey}' still maps to the same Windows desktop, now at position " +
                    $"{Describe(remap.Position)} instead of {Describe(remap.PreviousPosition)}. " +
                    "No rule and no window changed.",
                    remap.SemanticKey));
        }

        records.Add(
            CreateTopologyRecord(
                result,
                sessionId,
                result.WindowReconciliationRan
                    ? ActivityResult.Succeeded
                    : ActivityResult.Skipped,
                result.WindowReconciliationRan
                    ? "topology.window_reconciliation_ran"
                    : "topology.window_reconciliation_skipped",
                result.WindowReconciliationRan
                    ? $"A managed destination changed, so one window reconciliation inspected {result.WindowsInspected} windows."
                    : "No managed destination changed, so no window was inspected or moved.",
                targetDesktopKey: null));

        return records.ToImmutable();
    }

    /// <summary>
    /// Projects one shell or machine recovery pass into the steps it took.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Every event carries <see cref="ActivityRecord.RecoverySignal"/>, so the
    /// three recovery paths stay distinguishable in an exported log without a
    /// reader having to parse the summary. The result code carries the signal
    /// too, which is what makes "every Explorer recovery that failed" a search
    /// rather than an investigation.
    /// </para>
    /// <para>
    /// A pass that found everything intact still produces its outcome event. A
    /// recovery that decided nothing needed doing is exactly the evidence a user
    /// wants after a disruption they expected to break something.
    /// </para>
    /// </remarks>
    /// <param name="result">The completed pass to project.</param>
    /// <param name="sessionId">The application run the events belong to.</param>
    /// <returns>The events the pass produced, in the order it decided them.</returns>
    public static ImmutableArray<ActivityRecord> FromShellRecovery(
        ShellRecoveryResult result,
        Guid sessionId)
    {
        ArgumentNullException.ThrowIfNull(result);

        string signal = ToCode(result.Signal);
        ImmutableArray<ActivityRecord>.Builder records =
            ImmutableArray.CreateBuilder<ActivityRecord>();

        records.Add(
            CreateRecoveryRecord(
                result,
                sessionId,
                result.Outcome switch
                {
                    ShellRecoveryOutcome.Failed => ActivityResult.Failed,
                    ShellRecoveryOutcome.NoActionNeeded => ActivityResult.Skipped,
                    _ => ActivityResult.Succeeded,
                },
                $"recovery.{signal}.{ToCode(result.Outcome)}",
                result.Summary,
                result.Outcome == ShellRecoveryOutcome.Failed
                    ? new ActivityErrorDetail(
                        result.Code,
                        result.Summary,
                        result.HResult)
                    : null));

        if (result.RegistrationsInvalidated)
        {
            records.Add(
                CreateRecoveryRecord(
                    result,
                    sessionId,
                    ActivityResult.Succeeded,
                    $"recovery.{signal}.registrations_invalidated",
                    "Registrations taken against the previous shell were dropped before anything was validated or taken again."));
        }

        if (result.CapabilityValidationRan)
        {
            records.Add(
                CreateRecoveryRecord(
                    result,
                    sessionId,
                    result.FailedStage ==
                        ShellRecoveryStage.CapabilityValidation
                        ? ActivityResult.Failed
                        : ActivityResult.Succeeded,
                    $"recovery.{signal}.capability_validated",
                    result.FailedStage switch
                    {
                        ShellRecoveryStage.CapabilityValidation =>
                            "Capability validation did not succeed, so no hook and no topology registration was taken again.",
                        _ =>
                            result.State == ShellRecoveryState.Limited
                                ? "Capability validation reached Limited Mode. Window placement is available; desktop creation, switching, and notifications are not."
                                : "Capability validation succeeded for this Windows build, so fresh registrations were allowed.",
                    }));
        }

        if (result.RegistrationsRestored)
        {
            records.Add(
                CreateRecoveryRecord(
                    result,
                    sessionId,
                    ActivityResult.Succeeded,
                    $"recovery.{signal}.registrations_restored",
                    "Fresh hooks and topology registrations were taken after validation succeeded."));
        }

        records.Add(
            CreateRecoveryRecord(
                result,
                sessionId,
                result.FailedStage == ShellRecoveryStage.Reconciliation
                    ? ActivityResult.Failed
                    : result.ReconciliationRan
                        ? ActivityResult.Succeeded
                        : ActivityResult.Skipped,
                result.FailedStage == ShellRecoveryStage.Reconciliation
                    ? $"recovery.{signal}.reconciliation_failed"
                    : result.ReconciliationRan
                        ? $"recovery.{signal}.reconciliation_ran"
                        : $"recovery.{signal}.reconciliation_skipped",
                result.FailedStage == ShellRecoveryStage.Reconciliation
                    ? $"The desktop reconciliation answering {DescribeSignals(result.CoalescedSignalCount)} did not complete."
                    : result.ReconciliationRan
                        ? $"One desktop reconciliation answered {DescribeSignals(result.CoalescedSignalCount)}."
                        : $"Nothing needed reconciling, so no desktop and no window was touched for {DescribeSignals(result.CoalescedSignalCount)}."));

        return records.ToImmutable();
    }

    private static string DescribeSignals(int count) =>
        count == 1
            ? "this notification"
            : $"all {count.ToString(CultureInfo.InvariantCulture)} notifications in the burst";

    private static ActivityRecord CreateRecoveryRecord(
        ShellRecoveryResult result,
        Guid sessionId,
        ActivityResult activityResult,
        string resultCode,
        string summary,
        ActivityErrorDetail? error = null) =>
        new(
            result.CorrelationId,
            sessionId,
            result.OccurredAtUtc,
            ActivityEventSource.Recovery,

            // A shell disruption is not a window event. StartupReconciliation is
            // the closest WindowEventKind offers: it is the only member naming a
            // sweep that rebuilds state rather than one window's lifecycle.
            WindowEventKind.StartupReconciliation,
            activityResult,
            resultCode,
            summary,
            Application: null,
            Identity: null,
            RuleId: null,
            TargetDesktopKey: null,
            Duration: null,
            EventSequence: null,
            error,
            TopologyReason: null,
            ToCode(result.Signal));

    private static ActivityRecord CreateTopologyRecord(
        ManagedDesktopTopologyRecoveryResult result,
        Guid sessionId,
        ActivityResult activityResult,
        string resultCode,
        string summary,
        string? targetDesktopKey,
        ActivityErrorDetail? error = null) =>
        new(
            result.CorrelationId,
            sessionId,
            result.OccurredAtUtc,
            ActivityEventSource.Topology,

            // A topology notification is not a window event, and
            // WindowEventKind has no member that says so. StartupReconciliation
            // is the closest available: it is the only value that names a
            // reconciliation sweep rather than one window's lifecycle.
            WindowEventKind.StartupReconciliation,
            activityResult,
            resultCode,
            summary,
            Application: null,
            Identity: null,
            RuleId: null,
            targetDesktopKey,
            Duration: null,
            EventSequence: null,
            error,
            result.Reason);

    private static string Describe(int? position) =>
        position is int value
            ? value.ToString(CultureInfo.InvariantCulture)
            : "unknown";

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
            WindowMoveOutcome.WindowUnavailable =>
                $"The window became unavailable before it could be moved to {activity.TargetDesktopKey}.",
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
            WindowAssignmentOutcome.Skipped
                when activity.SkipReason == WindowAssignmentSkipReason.WindowNotTracked =>
                $"Skipped an {activity.Identity.ProcessName} window that Windows was not tracking on a virtual desktop.",
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

using System.Globalization;
using System.Text;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.ViewModels;

public sealed record AssignmentActivityPresentation(
    Guid CorrelationId,
    nint WindowHandle,
    string Outcome,
    string Decision,
    string OccurredAt,
    string Duration,
    string Trigger,
    string ProcessName,
    string Rule,
    string TargetDesktop,
    string Movement,
    string DesktopNavigation,
    string SwitchPolicy,
    string SwitchDuration,
    string Correlation,
    string RelatedCorrelation,
    string IdentityDetails,
    string Diagnostic,
    string AutomationName)
{
    public static AssignmentActivityPresentation Create(
        WindowAssignmentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        string processName = ValueOrFallback(
            activity.Identity.ProcessName,
            "Process identity unavailable");
        // A pin never moves the window: its rule keeps a desktop key that
        // nothing reads, so repeating it, or calling it unresolved, would
        // describe a move that was never going to happen.
        string target = IsPin(activity)
            ? "Every desktop"
            : ValueOrFallback(activity.TargetDesktopKey, "Unresolved desktop");
        string rule = ValueOrFallback(activity.RuleId, "No matching rule");
        string outcome = FormatOutcome(activity);
        string decision = FormatDecision(activity, processName, target);
        string occurredAt = activity.StartedAtUtc
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        string duration = FormatDuration(activity.Duration);
        string trigger = FormatEnum(activity.Trigger);
        string movement = FormatMovement(activity, target);
        string desktopNavigation = FormatDesktopNavigation(activity, target);
        string switchPolicy = FormatSwitchPolicy(activity.SwitchPolicy);
        string switchDuration = FormatSwitchDuration(activity);
        string correlation = $"Correlation ID: {activity.CorrelationId:D}";
        string relatedCorrelation = activity.RelatedCorrelationId is Guid relatedId
            ? $"Related correlation ID: {relatedId:D}"
            : string.Empty;
        string identityDetails = FormatSafeIdentity(activity.Identity);
        string diagnostic = FormatDiagnostic(activity);
        string automationName =
            $"{outcome}. {decision}. Process: {processName}. Target desktop: {target}. " +
            $"Rule: {rule}. Trigger: {trigger}. Duration: {duration}. {correlation}. " +
            $"Window movement: {movement}. Desktop navigation: {desktopNavigation}. " +
            $"Switch policy: {switchPolicy}. Switch duration: {switchDuration}. " +
            $"{relatedCorrelation} {identityDetails} {diagnostic}";

        return new AssignmentActivityPresentation(
            activity.CorrelationId,
            activity.WindowHandle,
            outcome,
            decision,
            occurredAt,
            duration,
            trigger,
            processName,
            rule,
            target,
            movement,
            desktopNavigation,
            switchPolicy,
            switchDuration,
            correlation,
            relatedCorrelation,
            identityDetails,
            diagnostic,
            automationName.Trim());
    }

    internal static string FormatDuration(TimeSpan duration)
    {
        if (duration < TimeSpan.FromMilliseconds(1))
        {
            return "<1 ms";
        }

        if (duration < TimeSpan.FromSeconds(1))
        {
            return $"{duration.TotalMilliseconds.ToString("0.#", CultureInfo.CurrentCulture)} ms";
        }

        return $"{duration.TotalSeconds.ToString("0.##", CultureInfo.CurrentCulture)} s";
    }

    private static string FormatOutcome(WindowAssignmentActivity activity)
    {
        if (activity.MoveOutcome == WindowMoveOutcome.Failed)
        {
            return "Move failed";
        }

        if (IsPin(activity) && activity.Outcome == WindowAssignmentOutcome.Failed)
        {
            return "Pin failed";
        }

        if (activity.SwitchOutcome == DesktopSwitchOutcome.Failed)
        {
            return activity.MoveOutcome == WindowMoveOutcome.Succeeded
                ? "Moved • switch failed"
                : "Switch failed";
        }

        if (activity.SwitchOutcome == DesktopSwitchOutcome.Succeeded)
        {
            return activity.MoveOutcome == WindowMoveOutcome.Succeeded
                ? "Moved + switched"
                : "Switched";
        }

        if (activity.SwitchOutcome == DesktopSwitchOutcome.Limited)
        {
            return activity.MoveOutcome == WindowMoveOutcome.Succeeded
                ? "Moved • Limited Mode"
                : "Limited Mode";
        }

        if (activity.SwitchOutcome == DesktopSwitchOutcome.Suppressed)
        {
            return activity.SkipReason ==
                WindowAssignmentSkipReason.SelfGeneratedForegroundSuppressed
                ? "Suppressed"
                : activity.MoveOutcome == WindowMoveOutcome.Succeeded
                    ? "Moved • switch suppressed"
                    : "Switch suppressed";
        }

        return activity.MoveOutcome switch
        {
            WindowMoveOutcome.Succeeded => "Moved",
            WindowMoveOutcome.AlreadyCorrect => "Already correct",
            WindowMoveOutcome.PinnedToAllDesktops => "Pinned",
            WindowMoveOutcome.AlreadyPinnedToAllDesktops => "Already pinned",
            _ => activity.Outcome switch
            {
                WindowAssignmentOutcome.Succeeded => "Assigned",
                WindowAssignmentOutcome.Skipped
                    when activity.SkipReason ==
                        WindowAssignmentSkipReason.AlreadyOnTargetDesktop =>
                    "Already correct",
                WindowAssignmentOutcome.Skipped
                    when activity.SkipReason ==
                        WindowAssignmentSkipReason.PinUnavailable =>
                    "Pin unavailable",
                WindowAssignmentOutcome.Skipped
                    when activity.SkipReason ==
                        WindowAssignmentSkipReason.PinHeldUntilRepairEvent =>
                    "Still pinned",
                WindowAssignmentOutcome.Skipped => "Skipped",
                WindowAssignmentOutcome.Failed => "Failed",
                _ => activity.Outcome.ToString(),
            },
        };
    }

    private static string FormatMovement(
        WindowAssignmentActivity activity,
        string target) =>
        IsPin(activity)
            ? FormatPinMovement(activity)
            : activity.MoveOutcome switch
            {
                WindowMoveOutcome.Succeeded => $"Moved to {target}",
                WindowMoveOutcome.AlreadyCorrect => $"Already on {target}",
                WindowMoveOutcome.Failed => $"Move to {target} failed",
                WindowMoveOutcome.WindowUnavailable =>
                    $"Window became unavailable before move to {target}",
                _ => "Move not attempted",
            };

    /// <summary>
    /// What a pin did to the window, which is never a move to a desktop.
    /// </summary>
    /// <remarks>
    /// A pin this host could not make is reported as a pin that did not happen
    /// rather than as a failure: the host has said it cannot pin, and saying
    /// something failed would send the user looking for a fault.
    /// </remarks>
    private static string FormatPinMovement(WindowAssignmentActivity activity) =>
        activity.MoveOutcome switch
        {
            WindowMoveOutcome.PinnedToAllDesktops => "Pinned to all desktops",
            WindowMoveOutcome.AlreadyPinnedToAllDesktops =>
                "Already pinned to all desktops",
            _ when activity.SkipReason ==
                WindowAssignmentSkipReason.PinHeldUntilRepairEvent =>
                "Kept pinned",
            _ => activity.Outcome == WindowAssignmentOutcome.Failed
                ? "Pin failed"
                : "Pin not attempted",
        };

    private static string FormatDecision(
        WindowAssignmentActivity activity,
        string processName,
        string target)
    {
        if (activity.SkipReason ==
            WindowAssignmentSkipReason.SelfGeneratedForegroundSuppressed)
        {
            return $"Suppressed a self-generated foreground event for {processName}";
        }

        if (activity.SkipReason == WindowAssignmentSkipReason.WindowNotTracked)
        {
            return $"Ignored a {processName} window that Windows was not tracking on a virtual desktop";
        }

        string placement = activity.MoveOutcome switch
        {
            WindowMoveOutcome.Succeeded => $"Moved {processName} to {target}",
            WindowMoveOutcome.AlreadyCorrect => $"{processName} is already on {target}",
            WindowMoveOutcome.Failed => $"Couldn’t move {processName} to {target}",
            WindowMoveOutcome.PinnedToAllDesktops =>
                $"Pinned {processName} to all desktops",
            WindowMoveOutcome.AlreadyPinnedToAllDesktops =>
                $"{processName} is already pinned to all desktops",
            _ when activity.SkipReason ==
                WindowAssignmentSkipReason.TargetDesktopUnresolved =>
                $"Skipped {processName}: target desktop unresolved",
            _ when activity.SkipReason ==
                WindowAssignmentSkipReason.PinUnavailable =>
                $"Skipped {processName}: this host cannot pin windows yet",
            _ when activity.SkipReason ==
                WindowAssignmentSkipReason.PinHeldUntilRepairEvent =>
                $"Kept {processName} pinned; the move waits for its next placement event",
            _ when IsPin(activity) &&
                activity.Outcome == WindowAssignmentOutcome.Failed =>
                $"Couldn’t pin {processName} to all desktops",
            _ => $"Processed {processName} for {target}",
        };

        return activity.SwitchOutcome switch
        {
            DesktopSwitchOutcome.Succeeded => $"{placement} and switched desktops",
            DesktopSwitchOutcome.Failed => $"{placement}; desktop switch failed",
            DesktopSwitchOutcome.Limited => $"{placement}; switch unavailable in Limited Mode",
            DesktopSwitchOutcome.Suppressed => $"{placement}; desktop switch suppressed",
            _ => placement,
        };
    }

    private static string FormatDesktopNavigation(
        WindowAssignmentActivity activity,
        string target) =>
        activity.SwitchOutcome switch
        {
            DesktopSwitchOutcome.Succeeded => $"Switched to {target}",
            DesktopSwitchOutcome.Failed => $"Switch to {target} failed",
            DesktopSwitchOutcome.Limited =>
                "Move only — desktop switching is unavailable in Limited Mode",
            DesktopSwitchOutcome.Suppressed =>
                $"Suppressed — {FormatSwitchReason(activity.SwitchDecisionReason)}",
            _ when IsPin(activity) =>
                $"No switch — {FormatSwitchReason(activity.SwitchDecisionReason)}",
            _ => $"Move only — {FormatSwitchReason(activity.SwitchDecisionReason)}",
        };

    private static string FormatSwitchPolicy(DesktopSwitchPolicy policy) =>
        FormatEnum(policy);

    private static string FormatSwitchDuration(WindowAssignmentActivity activity) =>
        activity.SwitchOutcome is
            DesktopSwitchOutcome.Succeeded or DesktopSwitchOutcome.Failed
            ? FormatDuration(activity.SwitchDuration)
            : "Not applicable";

    private static string FormatSwitchReason(DesktopSwitchDecisionReason reason) =>
        reason switch
        {
            DesktopSwitchDecisionReason.BackgroundEventMoveOnly =>
                "background events never switch desktops",
            DesktopSwitchDecisionReason.PolicyNever =>
                "the rule’s switch policy is Never",
            DesktopSwitchDecisionReason.PolicyOnNewWindowNotEligible =>
                "this was not the first eligible activation of a new window",
            DesktopSwitchDecisionReason.CurrentDesktopAlreadyTarget =>
                "the target desktop is already current",
            DesktopSwitchDecisionReason.SelfGeneratedForegroundEvent =>
                "the foreground event was generated by DesktopShift’s own switch",
            DesktopSwitchDecisionReason.CapabilityUnavailable =>
                "the provider cannot switch desktops",
            DesktopSwitchDecisionReason.PinnedToAllDesktops =>
                "the rule pins the window rather than moving it",
            DesktopSwitchDecisionReason.PolicyApproved =>
                "the rule policy approved switching",
            DesktopSwitchDecisionReason.SwitchFailed =>
                "Windows could not complete the switch",
            DesktopSwitchDecisionReason.SwitchCancelled =>
                "DesktopShift was shutting down",
            _ => FormatEnum(reason),
        };

    private static string FormatDiagnostic(WindowAssignmentActivity activity)
    {
        if (activity.Error is WindowAssignmentError error)
        {
            List<string> details = [$"{error.Message} ({error.Code})"];
            if (error.HResult is int hResult)
            {
                details.Add($"HRESULT 0x{hResult:X8}");
            }

            if (error.NativeErrorCode is int nativeErrorCode)
            {
                details.Add(
                    $"Windows error {nativeErrorCode.ToString(CultureInfo.InvariantCulture)}");
            }

            return string.Join(" • ", details);
        }

        return $"{FormatMovement(activity, activity.TargetDesktopKey)}. " +
            $"{FormatDesktopNavigation(activity, activity.TargetDesktopKey)}.";
    }

    private static string FormatSafeIdentity(WindowSafeIdentity identity)
    {
        List<string> parts = [];
        AddIdentityPart(parts, "Package family", identity.PackageFamilyName);
        AddIdentityPart(parts, "App ID", identity.AppUserModelId);
        AddIdentityPart(parts, "Window class", identity.WindowClass);
        return parts.Count == 0
            ? "No additional privacy-safe identity details were available."
            : string.Join(" • ", parts);
    }

    private static void AddIdentityPart(
        ICollection<string> parts,
        string label,
        string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            parts.Add($"{label}: {value.Trim()}");
        }
    }

    /// <summary>
    /// Whether the assignment pinned the window rather than moving it.
    /// </summary>
    /// <remarks>
    /// A pin that this host could not make records no pin outcome of its own,
    /// so the switch reason is what identifies it: every pin records that no
    /// switch was considered because the rule pins.
    /// </remarks>
    private static bool IsPin(WindowAssignmentActivity activity) =>
        activity.MoveOutcome is
            WindowMoveOutcome.PinnedToAllDesktops or
            WindowMoveOutcome.AlreadyPinnedToAllDesktops ||
        activity.SwitchDecisionReason ==
            DesktopSwitchDecisionReason.PinnedToAllDesktops;

    private static string ValueOrFallback(string? value, string fallback) =>
        string.IsNullOrWhiteSpace(value) ? fallback : value.Trim();

    private static string FormatEnum<TEnum>(TEnum value)
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
            }

            _ = result.Append(current);
        }

        return result.ToString();
    }
}

public enum ReassignmentResultTone
{
    Information,
    Success,
    Warning,
    Error,
}

public sealed record ReassignmentBatchPresentation(
    string Title,
    string Message,
    ReassignmentResultTone Tone)
{
    public static ReassignmentBatchPresentation Create(
        WindowReassignmentBatchResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        int succeeded = result.Assignments.Count(
            assignment => assignment.Outcome == WindowAssignmentOutcome.Succeeded);
        int skipped = result.Assignments.Count(
            assignment => assignment.Outcome == WindowAssignmentOutcome.Skipped);
        int failed = result.Assignments.Count(
            assignment => assignment.Outcome == WindowAssignmentOutcome.Failed);
        string duration = AssignmentActivityPresentation.FormatDuration(result.Duration);
        string title = failed switch
        {
            0 when succeeded > 0 => "Reassignment completed",
            0 => "Reassignment finished",
            _ when succeeded > 0 || skipped > 0 => "Reassignment completed with issues",
            _ => "Reassignment failed",
        };
        ReassignmentResultTone tone = failed switch
        {
            0 when succeeded > 0 => ReassignmentResultTone.Success,
            0 => ReassignmentResultTone.Information,
            _ when succeeded > 0 || skipped > 0 => ReassignmentResultTone.Warning,
            _ => ReassignmentResultTone.Error,
        };
        string message =
            $"Enumerated {FormatCount(result.EnumeratedWindowCount, "window")}; " +
            $"{succeeded} assigned, {skipped} skipped, {failed} failed in {duration}. " +
            $"Correlation ID: {result.CorrelationId:D}.";

        return new ReassignmentBatchPresentation(title, message, tone);
    }

    private static string FormatCount(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

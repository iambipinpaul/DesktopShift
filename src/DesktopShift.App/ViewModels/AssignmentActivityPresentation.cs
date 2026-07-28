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
        string target = ValueOrFallback(
            activity.TargetDesktopKey,
            "Unresolved desktop");
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
            _ => activity.Outcome switch
            {
                WindowAssignmentOutcome.Succeeded => "Assigned",
                WindowAssignmentOutcome.Skipped
                    when activity.SkipReason ==
                        WindowAssignmentSkipReason.AlreadyOnTargetDesktop =>
                    "Already correct",
                WindowAssignmentOutcome.Skipped => "Skipped",
                WindowAssignmentOutcome.Failed => "Failed",
                _ => activity.Outcome.ToString(),
            },
        };
    }

    private static string FormatMovement(
        WindowAssignmentActivity activity,
        string target) =>
        activity.MoveOutcome switch
        {
            WindowMoveOutcome.Succeeded => $"Moved to {target}",
            WindowMoveOutcome.AlreadyCorrect => $"Already on {target}",
            WindowMoveOutcome.Failed => $"Move to {target} failed",
            _ => "Move not attempted",
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

        string placement = activity.MoveOutcome switch
        {
            WindowMoveOutcome.Succeeded => $"Moved {processName} to {target}",
            WindowMoveOutcome.AlreadyCorrect => $"{processName} is already on {target}",
            WindowMoveOutcome.Failed => $"Couldn’t move {processName} to {target}",
            _ when activity.SkipReason ==
                WindowAssignmentSkipReason.TargetDesktopUnresolved =>
                $"Skipped {processName}: target desktop unresolved",
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
            DesktopSwitchDecisionReason.PolicyApproved =>
                "the rule policy approved switching",
            DesktopSwitchDecisionReason.SwitchFailed =>
                "Windows could not complete the switch",
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

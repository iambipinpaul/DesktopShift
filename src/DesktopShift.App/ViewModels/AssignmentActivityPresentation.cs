using System.Globalization;
using System.Text;
using DesktopShift.Core.Assignments;
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
    string Correlation,
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
        string correlation = $"Correlation ID: {activity.CorrelationId:D}";
        string identityDetails = FormatSafeIdentity(activity.Identity);
        string diagnostic = FormatDiagnostic(activity);
        string automationName =
            $"{outcome}. {decision}. Process: {processName}. Target desktop: {target}. " +
            $"Rule: {rule}. Trigger: {trigger}. Duration: {duration}. {correlation}. " +
            $"{identityDetails} {diagnostic}";

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
            correlation,
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

    private static string FormatOutcome(WindowAssignmentActivity activity) =>
        activity.Outcome switch
        {
            WindowAssignmentOutcome.Succeeded => "Assigned",
            WindowAssignmentOutcome.Skipped
                when activity.SkipReason ==
                    WindowAssignmentSkipReason.AlreadyOnTargetDesktop =>
                "Already correct",
            WindowAssignmentOutcome.Skipped => "Skipped",
            WindowAssignmentOutcome.Failed => "Failed",
            _ => activity.Outcome.ToString(),
        };

    private static string FormatDecision(
        WindowAssignmentActivity activity,
        string processName,
        string target) =>
        activity.Outcome switch
        {
            WindowAssignmentOutcome.Succeeded =>
                $"Assigned {processName} to {target}",
            WindowAssignmentOutcome.Skipped
                when activity.SkipReason ==
                    WindowAssignmentSkipReason.AlreadyOnTargetDesktop =>
                $"{processName} is already on {target}",
            WindowAssignmentOutcome.Skipped
                when activity.SkipReason ==
                    WindowAssignmentSkipReason.TargetDesktopUnresolved =>
                $"Skipped {processName}: target desktop unresolved",
            WindowAssignmentOutcome.Skipped =>
                $"Skipped {processName}",
            WindowAssignmentOutcome.Failed =>
                $"Couldn’t assign {processName} to {target}",
            _ => $"{activity.Outcome}: {processName}",
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

        return activity.Outcome switch
        {
            WindowAssignmentOutcome.Succeeded =>
                "The window moved to the resolved managed desktop.",
            WindowAssignmentOutcome.Skipped
                when activity.SkipReason ==
                    WindowAssignmentSkipReason.AlreadyOnTargetDesktop =>
                "No move was needed.",
            WindowAssignmentOutcome.Skipped
                when activity.SkipReason ==
                    WindowAssignmentSkipReason.TargetDesktopUnresolved =>
                "The managed desktop could not be resolved, so the window was not moved.",
            WindowAssignmentOutcome.Skipped =>
                "The window was intentionally not moved.",
            _ => string.Empty,
        };
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

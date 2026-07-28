using System.Globalization;
using System.Text;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.ViewModels;

public sealed record ObservationActivityPresentation(
    long EventSequence,
    string Outcome,
    string Decision,
    string OccurredAt,
    string Trigger,
    string Rule,
    string TargetDesktop,
    string ProcessName,
    string IdentityDetails,
    string Diagnostic,
    string AutomationName)
{
    public static ObservationActivityPresentation Create(
        WindowObservationActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);

        bool isMatched = activity.Outcome is WindowObservationOutcome.Matched;
        string outcome = isMatched ? "Matched" : "Skipped";
        string rule = ValueOrFallback(activity.RuleId, "No matching rule");
        string targetDesktop = ValueOrFallback(
            activity.TargetDesktopKey,
            "No desktop assigned");
        string skipReason = FormatEnum(activity.SkipReason);
        string decision = isMatched
            ? $"Matched {rule} to {targetDesktop}"
            : $"Skipped: {skipReason}";
        string trigger = FormatEnum(activity.Trigger);
        string occurredAt = activity.OccurredAt
            .ToLocalTime()
            .ToString("g", CultureInfo.CurrentCulture);
        string processName = ValueOrFallback(
            activity.Identity?.ProcessName,
            "Process identity unavailable");
        string identityDetails = FormatSafeIdentity(activity.Identity);
        string diagnostic = activity.NativeErrorCode is int nativeErrorCode
            ? $"Windows error code: {nativeErrorCode.ToString(CultureInfo.InvariantCulture)}"
            : string.Empty;
        string automationName =
            $"{outcome}. {decision}. Trigger: {trigger}. Process: {processName}. " +
            $"Rule: {rule}. Target desktop: {targetDesktop}. {identityDetails} {diagnostic}";

        return new ObservationActivityPresentation(
            activity.EventSequence,
            outcome,
            decision,
            occurredAt,
            trigger,
            rule,
            targetDesktop,
            processName,
            identityDetails,
            diagnostic,
            automationName.Trim());
    }

    private static string FormatSafeIdentity(WindowSafeIdentity? identity)
    {
        if (identity is null)
        {
            return "No privacy-safe identity details were available.";
        }

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

using System.Globalization;
using System.Text;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Diagnostics;

/// <summary>
/// One row of the Activity list, already reduced to display strings.
/// </summary>
/// <remarks>
/// The projection lives here rather than in the shell so it can be proved
/// without a XAML host. It is also the single place the copy-one-event text is
/// produced, which keeps what a user copies identical to what they can see.
/// </remarks>
/// <param name="CapturedIdentity">
/// The privacy-safe identity the row was recorded with, so a row that offers to
/// write a rule can pre-fill it from what was actually observed rather than from
/// what the user retypes.
/// </param>
/// <param name="OffersRuleCreation">
/// Whether this row is the decision that swept a window no rule named. Only the
/// decision offers it: the move and the result that follow carry the same
/// identity, and three buttons for one window would be three ways to do the same
/// thing.
/// </param>
public sealed record ActivityRecordDisplay(
    Guid CorrelationId,
    Guid SessionId,
    ActivityResult Result,
    string Timestamp,
    string Application,
    string Identity,
    string Trigger,
    string Source,
    string Target,
    string Rule,
    string ResultLabel,
    string Summary,
    string Duration,
    string ErrorDetails,
    string Correlation,
    string AutomationName,
    string CopyText,
    WindowSafeIdentity? CapturedIdentity = null,
    bool OffersRuleCreation = false)
{
    private const string NoIdentity =
        "No additional privacy-safe identity details were available.";

    public static ActivityRecordDisplay Create(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        bool isSwept = UnmanagedWindowSweep.IsSweptRuleId(record.RuleId);
        string timestamp = record.OccurredAt
            .ToLocalTime()
            .ToString("G", CultureInfo.CurrentCulture);
        string application = ValueOrFallback(
            record.Application,
            "Process identity unavailable");
        string identity = FormatIdentity(record.Identity);
        string trigger = ActivityRecordFactory.Humanize(record.Trigger);
        string source = FormatSource(record.Source, record.RecoverySignal);

        // The sweep's rule and destination are internal names, not things a
        // user wrote, so they are said rather than shown.
        string target = isSwept
            ? "First desktop"
            : ValueOrFallback(record.TargetDesktopKey, "No desktop assigned");
        string rule = isSwept
            ? "No rule names this app"
            : ValueOrFallback(record.RuleId, "No matching rule");
        string resultLabel = ActivityRecordFactory.Humanize(record.Result);
        string summary = record.Summary;
        string duration = record.Duration is TimeSpan value
            ? FormatDuration(value)
            : "Not measured";
        string errorDetails = FormatError(record.Error);
        string correlation = $"Correlation ID: {record.CorrelationId:D}";
        string automationName =
            $"{source}. {resultLabel}. {summary} Application: {application}. " +
            $"Trigger: {trigger}. Target desktop: {target}. Rule: {rule}. " +
            $"Duration: {duration}. {identity} {errorDetails}";

        return new ActivityRecordDisplay(
            record.CorrelationId,
            record.SessionId,
            record.Result,
            timestamp,
            application,
            identity,
            trigger,
            source,
            target,
            rule,
            resultLabel,
            summary,
            duration,
            errorDetails,
            correlation,
            automationName.Trim(),
            BuildCopyText(
                record,
                timestamp,
                application,
                identity,
                trigger,
                source,
                target,
                rule,
                resultLabel,
                duration,
                errorDetails),
            record.Identity,
            isSwept &&
                record.Source == ActivityEventSource.Observation &&
                record.Identity is not null);
    }

    public static string FormatDuration(TimeSpan duration)
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

    private static string FormatSource(
        ActivityEventSource source,
        string? recoverySignal) =>
        source switch
        {
            ActivityEventSource.Observation => "Decision",
            ActivityEventSource.Move => "Window move",
            ActivityEventSource.Switch => "Desktop switch",
            ActivityEventSource.Assignment => "Assignment result",
            ActivityEventSource.Recovery
                when !string.IsNullOrWhiteSpace(recoverySignal) =>
                    $"Shell recovery ({recoverySignal.Trim()})",
            ActivityEventSource.Recovery => "Shell recovery",
            _ => ActivityRecordFactory.Humanize(source),
        };

    private static string FormatIdentity(WindowSafeIdentity? identity)
    {
        if (identity is null)
        {
            return "No privacy-safe identity details were available.";
        }

        List<string> parts = [];
        AddPart(parts, "Package family", identity.PackageFamilyName);
        AddPart(parts, "App ID", identity.AppUserModelId);
        AddPart(parts, "Window class", identity.WindowClass);
        return parts.Count == 0 ? NoIdentity : string.Join(" • ", parts);
    }

    private static string FormatError(ActivityErrorDetail? error)
    {
        if (error is null)
        {
            return string.Empty;
        }

        List<string> details =
        [
            $"{DiagnosticRedaction.Redact(error.Message)} ({error.Code})",
        ];

        if (error.HResultText is string hResult)
        {
            details.Add($"HRESULT {hResult}");
        }

        if (error.NativeErrorCode is int nativeErrorCode)
        {
            details.Add(
                $"Windows error {nativeErrorCode.ToString(CultureInfo.InvariantCulture)}");
        }

        return string.Join(" • ", details);
    }

    private static string BuildCopyText(
        ActivityRecord record,
        string timestamp,
        string application,
        string identity,
        string trigger,
        string source,
        string target,
        string rule,
        string resultLabel,
        string duration,
        string errorDetails)
    {
        StringBuilder text = new();
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Timestamp: {timestamp}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Source: {source}");
        if (!string.IsNullOrWhiteSpace(record.RecoverySignal))
        {
            _ = text.AppendLine(
                CultureInfo.CurrentCulture,
                $"Recovery signal: {record.RecoverySignal.Trim()}");
        }

        _ = text.AppendLine(
            CultureInfo.CurrentCulture,
            $"Result: {resultLabel} ({record.ResultCode})");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Summary: {record.Summary}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Application: {application}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Identity: {identity}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Trigger: {trigger}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Rule: {rule}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Target desktop: {target}");
        _ = text.AppendLine(CultureInfo.CurrentCulture, $"Duration: {duration}");
        _ = text.AppendLine(
            CultureInfo.CurrentCulture,
            $"Correlation ID: {record.CorrelationId:D}");
        _ = text.AppendLine(
            CultureInfo.CurrentCulture,
            $"Session ID: {record.SessionId:D}");

        if (!string.IsNullOrEmpty(errorDetails))
        {
            _ = text.AppendLine(
                CultureInfo.CurrentCulture,
                $"Error: {errorDetails}");
        }

        return text.ToString().TrimEnd();
    }

    private static void AddPart(
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
}

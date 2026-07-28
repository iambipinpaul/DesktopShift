using System.Collections.Immutable;

namespace DesktopShift.Core.Compatibility;

public enum CompatibilityDiagnosticSeverity
{
    Information,
    Warning,
    Error,
}

public sealed record CompatibilityDiagnostic(
    string EventName,
    CompatibilityDiagnosticSeverity Severity,
    string Message,
    ImmutableDictionary<string, string> Properties,
    int? HResult = null,
    int? NativeErrorCode = null)
{
    public static CompatibilityDiagnostic Create(
        string eventName,
        CompatibilityDiagnosticSeverity severity,
        string message,
        params (string Key, string Value)[] properties)
    {
        ImmutableDictionary<string, string>.Builder values =
            ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);

        foreach ((string key, string value) in properties)
        {
            values[key] = value;
        }

        return new(eventName, severity, message, values.ToImmutable());
    }
}

public enum CompatibilityTestOutcome
{
    NotRun,
    PassedLimitedMode,
    PassedFullMode,
    Failed,
}

public sealed record CompatibilityTestResult(
    CompatibilityTestOutcome Outcome,
    DateTimeOffset? TestedAtUtc,
    string Summary,
    ImmutableArray<CompatibilityDiagnostic> Diagnostics)
{
    public static CompatibilityTestResult NotRun { get; } = new(
        CompatibilityTestOutcome.NotRun,
        null,
        "Compatibility test has not been run.",
        []);

    public bool IsSuccessful =>
        Outcome is CompatibilityTestOutcome.PassedLimitedMode or CompatibilityTestOutcome.PassedFullMode;
}

public sealed record CompatibilityStatus(
    WindowsBuildInfo Build,
    WindowsBuildAssessment BuildAssessment,
    DesktopTopologyProviderState Provider,
    CompatibilityTestResult LastTest)
{
    public bool IsLimitedMode => Provider.Identity.Mode == DesktopTopologyProviderMode.Limited;
}

public sealed class CompatibilityStatusChangedEventArgs : EventArgs
{
    public CompatibilityStatusChangedEventArgs(CompatibilityStatus status)
    {
        ArgumentNullException.ThrowIfNull(status);
        Status = status;
    }

    public CompatibilityStatus Status { get; }
}

public interface ICompatibilityCoordinator
{
    CompatibilityStatus Current { get; }

    event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged;

    ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
        CancellationToken cancellationToken = default);
}

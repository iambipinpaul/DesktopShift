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

/// <summary>
/// What DesktopShift's own privileges let it reach, and what it will not do to
/// widen them.
/// </summary>
/// <remarks>
/// <para>
/// DesktopShift runs per-user with the signed-in user's own token and asks
/// Windows for nothing more. A window owned by a process running at a higher
/// integrity level is therefore closed to it: Windows answers access denied
/// (<c>0x80070005</c>) and the window is left exactly where it is. That is a
/// stated boundary, not a defect, and this type exists so a diagnostic can say
/// so in words a user can act on.
/// </para>
/// <para>
/// Reaching those windows would need a separate companion holding different
/// rights. Naming that boundary is deliberate and is as far as it goes: nothing
/// in DesktopShift implements, installs, launches, or depends on such a
/// companion, and every feature works without one.
/// </para>
/// </remarks>
/// <param name="IsProcessElevated">
/// Whether DesktopShift's own process is running with administrator rights.
/// Routine operation never needs this, so it is reported rather than required.
/// </param>
/// <param name="Explanation">What the current privileges mean for the user.</param>
/// <param name="ElevatedCompanionBoundary">
/// The windows that stay out of reach, and the explicit statement that no
/// elevated companion exists or is required to run DesktopShift.
/// </param>
public sealed record PrivilegeBoundary(
    bool IsProcessElevated,
    string Explanation,
    string ElevatedCompanionBoundary)
{
    /// <summary>
    /// The sentence every access-denied window failure ends with, so Activity,
    /// the compatibility surface, and exported bundles all give the same reason.
    /// </summary>
    public const string DeniedWindowExplanation =
        "Windows denied the cross-process window operation with E_ACCESSDENIED " +
        "(0x80070005). The target may have higher privileges, or another Shell " +
        "security or ownership boundary may have refused the request. DesktopShift " +
        "runs with the signed-in user's own privileges and does not request elevation.";

    /// <summary>The rights DesktopShift holds when it runs as intended.</summary>
    public const string LeastPrivilegeExplanation =
        "DesktopShift runs with the signed-in user's own privileges and never " +
        "asks Windows for more. It opens another process only for the limited " +
        "query right that reading an application's file name and package " +
        "identity needs, and it moves only windows Windows itself agrees to move.";

    /// <summary>What it means when DesktopShift is itself running elevated.</summary>
    public const string ElevatedProcessExplanation =
        "DesktopShift is running with administrator rights, which it neither " +
        "requests nor needs. Nothing changes in how it works, and running it " +
        "this way only widens what its rules can reach beyond the user's own " +
        "applications.";

    /// <summary>
    /// The boundary statement carried into diagnostics, naming the companion
    /// that does not exist so nobody has to guess whether one is missing.
    /// </summary>
    public const string ElevatedCompanionNotice =
        DeniedWindowExplanation +
        " Windows that are inaccessible specifically because they run with higher " +
        "privileges would require a separate companion running with different " +
        "rights. No such companion is implemented, installed, or started; " +
        "DesktopShift works without one, with those windows remaining out of reach.";

    /// <summary>The boundary DesktopShift reports when it runs as intended.</summary>
    public static PrivilegeBoundary LeastPrivilege { get; } = new(
        IsProcessElevated: false,
        LeastPrivilegeExplanation,
        ElevatedCompanionNotice);

    /// <summary>
    /// Describes the boundary for a process whose elevation state is known.
    /// </summary>
    /// <param name="isProcessElevated">Whether the process is elevated.</param>
    /// <returns>The boundary to report.</returns>
    public static PrivilegeBoundary ForProcess(bool isProcessElevated) =>
        isProcessElevated
            ? new(true, ElevatedProcessExplanation, ElevatedCompanionNotice)
            : LeastPrivilege;
}

/// <summary>
/// Reports the privileges DesktopShift's own process holds.
/// </summary>
/// <remarks>
/// The only subject is this process. Nothing here opens, inspects, or adjusts
/// another process's token, and nothing here can raise the privileges it reads.
/// </remarks>
public interface IProcessPrivilegeProvider
{
    /// <summary>
    /// Reads whether the current process runs with administrator rights.
    /// </summary>
    /// <returns>True when the process is elevated.</returns>
    bool IsCurrentProcessElevated();
}

/// <param name="Privileges">
/// The privilege boundary the coordinator observed. Null only on a status built
/// by hand; the coordinator always supplies one.
/// </param>
public sealed record CompatibilityStatus(
    WindowsBuildInfo Build,
    WindowsBuildAssessment BuildAssessment,
    DesktopTopologyProviderState Provider,
    CompatibilityTestResult LastTest,
    PrivilegeBoundary? Privileges = null)
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

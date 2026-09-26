using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Diagnostics;

/// <summary>
/// Builders for the pipeline records the diagnostics layer projects.
/// </summary>
internal static class DiagnosticTestData
{
    public const string SecretTitle = "Quarterly-Salaries.xlsx — Private";
    public const string SecretCommandLine =
        "--profile-directory=\"Person 3\" --token=hunter2";
    public const string SecretExecutablePath =
        @"C:\Users\marguerite\AppData\Local\Programs\Code\Code.exe";

    public static readonly Guid Correlation =
        Guid.Parse("11111111-2222-3333-4444-555555555555");

    public static readonly Guid Session =
        Guid.Parse("99999999-8888-7777-6666-555555555555");

    public static readonly DateTimeOffset Occurred =
        new(2026, 7, 28, 9, 30, 0, TimeSpan.Zero);

    public static readonly Guid TargetDesktopId =
        Guid.Parse("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee");

    public static readonly Guid PreviousDesktopId =
        Guid.Parse("ffffffff-eeee-dddd-cccc-bbbbbbbbbbbb");

    /// <summary>
    /// A resolved identity carrying every field the projections must never
    /// reveal, so a leak in any layer shows up as a literal string match.
    /// </summary>
    public static WindowIdentity SensitiveIdentity() =>
        new(
            4242,
            "Code.exe",
            SecretExecutablePath,
            PackageFamilyName: null,
            AppUserModelId: null,
            "Chrome_WidgetWin_1",
            SecretTitle,
            SecretCommandLine);

    public static WindowSafeIdentity SafeIdentity() =>
        SensitiveIdentity().ToSafeIdentity();

    public static WindowObservationActivity MatchedObservation(
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null,
        string ruleId = "vscode",
        string targetDesktopKey = "code") =>
        new(
            occurredAt ?? Occurred,
            17,
            WindowEventKind.Created,
            (nint)101,
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            ruleId,
            targetDesktopKey,
            SafeIdentity(),
            MatchedOn: WindowMatchStrength.ProcessName,
            CorrelationId: correlationId ?? Correlation);

    public static WindowObservationActivity SkippedObservation(
        WindowSkipReason reason = WindowSkipReason.NoMatchingRule,
        Guid? correlationId = null,
        DateTimeOffset? occurredAt = null,
        int? nativeErrorCode = null) =>
        new(
            occurredAt ?? Occurred,
            18,
            WindowEventKind.ForegroundActivated,
            (nint)102,
            WindowObservationOutcome.Skipped,
            reason,
            null,
            null,
            SafeIdentity(),
            nativeErrorCode,
            CorrelationId: correlationId ?? Correlation);

    public static WindowAssignmentActivity Assignment(
        Guid? correlationId = null,
        DateTimeOffset? startedAtUtc = null,
        WindowAssignmentOutcome outcome = WindowAssignmentOutcome.Succeeded,
        WindowAssignmentSkipReason skipReason = WindowAssignmentSkipReason.None,
        WindowMoveOutcome moveOutcome = WindowMoveOutcome.Succeeded,
        DesktopSwitchOutcome switchOutcome = DesktopSwitchOutcome.Succeeded,
        WindowAssignmentError? error = null,
        string ruleId = "vscode",
        string targetDesktopKey = "code",
        WindowSafeIdentity? identity = null,
        DesktopSwitchDecisionReason switchReason =
            DesktopSwitchDecisionReason.PolicyApproved) =>
        new(
            correlationId ?? Correlation,
            startedAtUtc ?? Occurred,
            TimeSpan.FromMilliseconds(42),
            WindowEventKind.Created,
            (nint)101,
            outcome,
            skipReason,
            ruleId,
            targetDesktopKey,
            TargetDesktopId,
            PreviousDesktopId,
            identity ?? SafeIdentity(),
            error,
            moveOutcome,
            DesktopSwitchPolicy.OnNewWindowActivation,
            switchOutcome,
            switchReason,
            TimeSpan.FromMilliseconds(7));
}

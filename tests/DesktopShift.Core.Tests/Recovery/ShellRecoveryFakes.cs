using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Recovery;

namespace DesktopShift.Core.Tests.Recovery;

/// <summary>
/// The names a recovery pass appends as it takes each step, so ordering can be
/// asserted rather than inferred from call counts.
/// </summary>
internal static class RecoveryStep
{
    public const string Check = "check";
    public const string Invalidate = "invalidate";
    public const string Validate = "validate";
    public const string Reregister = "reregister";
    public const string Reconcile = "reconcile";
}

/// <summary>
/// Everything one recovery test needs, wired to fakes only.
/// </summary>
/// <remarks>
/// No test built on this harness can restart Explorer, suspend the machine,
/// change a display setting, register a real hook, or touch a real virtual
/// desktop. Every fault is injected into a fake and every clock reading comes
/// from <see cref="TimeProvider"/>, so nothing here waits on wall-clock time.
/// </remarks>
internal sealed class RecoveryHarness
{
    public static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 29, 9, 15, 0, TimeSpan.Zero);

    private RecoveryHarness(bool withDiagnostics)
    {
        Time = new FixedTimeProvider(ObservedAt);
        Registrations = new FakeNativeRegistrationSet(Steps);
        Compatibility = new FakeCompatibilityCoordinator(Steps);
        Topology = new FakeTopologyRecoveryService(Steps);

        // The journal always exists so a test can assert what a host without
        // diagnostics did not publish; only the wiring is left out.
        Journal = new BoundedActivityJournal(Time);
        Service = new ShellRecoveryService(
            Registrations,
            Compatibility,
            Topology,
            Time,
            withDiagnostics ? Journal : null);
    }

    /// <summary>The steps every fake appended, in the order they ran.</summary>
    public List<string> Steps { get; } = [];

    public FixedTimeProvider Time { get; }

    public FakeNativeRegistrationSet Registrations { get; }

    public FakeCompatibilityCoordinator Compatibility { get; }

    public FakeTopologyRecoveryService Topology { get; }

    public BoundedActivityJournal Journal { get; }

    public ShellRecoveryService Service { get; }

    public static RecoveryHarness Create() => new(withDiagnostics: true);

    /// <summary>A host that registered recovery without diagnostics.</summary>
    public static RecoveryHarness WithoutDiagnostics() => new(withDiagnostics: false);
}

internal sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

/// <summary>
/// A registration set whose answers and failures are scripted per test.
/// </summary>
internal sealed class FakeNativeRegistrationSet : INativeRegistrationSet
{
    private readonly List<string> steps;

    public FakeNativeRegistrationSet(List<string> steps) => this.steps = steps;

    /// <summary>Everything the shell gave DesktopShift is still installed.</summary>
    public static NativeRegistrationValidity Intact { get; } = new(
        WindowHooksLive: true,
        TopologyNotificationsLive: true,
        IsObservable: true,
        "The window hooks and the topology registration are still live.");

    /// <summary>The hooks are gone even though the process is still running.</summary>
    public static NativeRegistrationValidity Broken { get; } = new(
        WindowHooksLive: false,
        TopologyNotificationsLive: false,
        IsObservable: true,
        "The window hooks are no longer installed.");

    public NativeRegistrationValidity Validity { get; set; } = Intact;

    public NativeRegistrationResult ReregisterResult { get; set; } =
        NativeRegistrationResult.Succeeded;

    public Exception? CheckFailure { get; set; }

    public Exception? InvalidateFailure { get; set; }

    public Exception? ReregisterFailure { get; set; }

    public int CheckCount { get; private set; }

    public int InvalidateCount { get; private set; }

    public int ReregisterCount { get; private set; }

    public ValueTask<NativeRegistrationValidity> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CheckCount++;
        steps.Add(RecoveryStep.Check);

        if (CheckFailure is not null)
        {
            throw CheckFailure;
        }

        return ValueTask.FromResult(Validity);
    }

    public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        InvalidateCount++;
        steps.Add(RecoveryStep.Invalidate);

        if (InvalidateFailure is not null)
        {
            throw InvalidateFailure;
        }

        return ValueTask.CompletedTask;
    }

    public ValueTask<NativeRegistrationResult> ReregisterAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ReregisterCount++;
        steps.Add(RecoveryStep.Reregister);

        if (ReregisterFailure is not null)
        {
            throw ReregisterFailure;
        }

        return ValueTask.FromResult(ReregisterResult);
    }
}

/// <summary>
/// A capability validation whose outcome and failures are scripted per test.
/// </summary>
internal sealed class FakeCompatibilityCoordinator : ICompatibilityCoordinator
{
    private static readonly WindowsBuildInfo BuildInfo =
        new(true, 10, 0, 26200, 1, Architecture.X64);

    private static readonly CompatibilityStatus ReadyStatus = new(
        BuildInfo,
        WindowsBuildClassifier.Classify(BuildInfo),
        new DesktopTopologyProviderState(
            new DesktopTopologyProviderIdentity(
                "test.full",
                "Test Full",
                "1",
                DesktopTopologyProviderMode.Full,
                UsesPrivateApis: false),
            new VirtualDesktopCapabilities(true, true, true, true, true, false, true),
            DesktopTopologyProviderAvailability.Ready,
            "Ready"),
        CompatibilityTestResult.NotRun);

    private readonly List<string> steps;

    public FakeCompatibilityCoordinator(List<string> steps) => this.steps = steps;

    public CompatibilityTestOutcome Outcome { get; set; } =
        CompatibilityTestOutcome.PassedFullMode;

    public Exception? Failure { get; set; }

    /// <summary>
    /// Runs while the validation is in flight, so a test can cancel a pass from
    /// the middle of it without a timer.
    /// </summary>
    public Action? OnRun { get; set; }

    /// <summary>
    /// Holds validation in flight so another signal can attempt to enter the
    /// registration sequence at its most dangerous boundary.
    /// </summary>
    public TaskCompletionSource? Gate { get; set; }

    public int RunCount { get; private set; }

    public CompatibilityStatus Current =>
        Outcome == CompatibilityTestOutcome.PassedLimitedMode
            ? ReadyStatus with
            {
                Provider = ReadyStatus.Provider with
                {
                    Identity = ReadyStatus.Provider.Identity with
                    {
                        Mode = DesktopTopologyProviderMode.Limited,
                    },
                },
            }
            : ReadyStatus;

    public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged
    {
        add { }
        remove { }
    }

    public async ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        RunCount++;
        steps.Add(RecoveryStep.Validate);
        OnRun?.Invoke();

        if (Gate is not null)
        {
            await Gate.Task.WaitAsync(cancellationToken).ConfigureAwait(false);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return new CompatibilityTestResult(
            Outcome,
            RecoveryHarness.ObservedAt,
            "Capability validation ran against a fake provider.",
            []);
    }
}

/// <summary>
/// The one reconciliation a recovery pass is allowed, counted and gateable.
/// </summary>
/// <remarks>
/// <see cref="Gate"/> is a <see cref="TaskCompletionSource"/> rather than a
/// delay so that a burst of notifications can be delivered while a pass is
/// genuinely in flight, with no wall-clock time involved and no chance of a
/// flaky ordering.
/// </remarks>
internal sealed class FakeTopologyRecoveryService :
    IManagedDesktopTopologyRecoveryService
{
    private readonly List<string> steps;

    public FakeTopologyRecoveryService(List<string> steps) => this.steps = steps;

    public ManagedDesktopTopologyRecoveryOutcome Outcome { get; set; } =
        ManagedDesktopTopologyRecoveryOutcome.NoChange;

    public Exception? Failure { get; set; }

    public TaskCompletionSource? Gate { get; set; }

    public int CallCount { get; private set; }

    public List<string> Reasons { get; } = [];

    public async Task<ManagedDesktopTopologyRecoveryResult> HandleTopologyChangedAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CallCount++;
        Reasons.Add(reason);
        steps.Add(RecoveryStep.Reconcile);

        if (Gate is not null)
        {
            await Gate.Task.ConfigureAwait(false);
        }

        if (Failure is not null)
        {
            throw Failure;
        }

        return new ManagedDesktopTopologyRecoveryResult(
            Guid.NewGuid(),
            RecoveryHarness.ObservedAt,
            reason,
            Outcome,
            ManagedDesktopReconciliationSnapshot.NotRun,
            ManagedDesktopReconciliationReport.Empty,
            WindowReconciliationRan: false,
            WindowsInspected: 0,
            [],
            []);
    }
}

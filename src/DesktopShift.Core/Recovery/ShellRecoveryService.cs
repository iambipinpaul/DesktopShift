using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Recovery;

/// <summary>
/// Turns one shell or machine disruption into the smallest sequence of steps
/// that puts automatic assignment back to work.
/// </summary>
/// <remarks>
/// <para>
/// The three signals get two shapes, not one. An Explorer restart destroyed the
/// shell that owned every hook and the virtual-desktop COM object, so nothing it
/// registered can be trusted and no probe is asked: registrations are dropped,
/// capability validation is re-run, and fresh registrations are taken only if
/// that validation succeeded. A resume or a display change asks a targeted
/// question first — "is what I am holding still real?" — and does the expensive
/// rebuild only when the answer is no. Nothing here runs on a timer, so a
/// machine nobody is touching produces no signals and therefore does no work.
/// </para>
/// <para>
/// The order inside a rebuild is the point of the type. Dropping first is what
/// stops a second hook being installed alongside a dead one. Validating second
/// is what stops registrations being taken against a shell that cannot support
/// them, and it is expressed as an early return rather than as a condition
/// around the re-registration so that "hooks are taken only after validation
/// succeeded" is structurally true.
/// </para>
/// <para>
/// One pass runs at most one reconciliation, and it delegates that to
/// <see cref="IManagedDesktopTopologyRecoveryService"/>, which already bounds
/// itself to one desktop pass and at most one window pass. Nothing here restarts
/// Explorer, suspends the machine, changes a display, or moves a window.
/// </para>
/// </remarks>
public sealed class ShellRecoveryService : IShellRecoveryService
{
    /// <summary>The pass that was folded into one already running.</summary>
    public const string DuplicateNotificationCode =
        "recovery.duplicate_notification_folded";

    /// <summary>The targeted check found nothing to rebuild.</summary>
    public const string IntactCode = "recovery.intact";

    /// <summary>Fresh registrations were taken after validation succeeded.</summary>
    public const string RegistrationsRestoredCode =
        "recovery.registrations_restored";

    /// <summary>Registrations were restored, but only into Limited Mode.</summary>
    public const string RegistrationsRestoredLimitedCode =
        "recovery.registrations_restored_limited";

    /// <summary>The single reconciliation changed the managed desktop layout.</summary>
    public const string ReconciledCode = "recovery.desktop_layout_reconciled";

    /// <summary>Validation failed, so no registration was taken again.</summary>
    public const string CapabilityValidationFailedCode =
        "recovery.capability_validation_failed";

    /// <summary>Validation succeeded but fresh registrations could not be taken.</summary>
    public const string ReregistrationFailedCode = "recovery.reregistration_failed";

    /// <summary>The single reconciliation could not complete.</summary>
    public const string ReconciliationFailedCode = "recovery.reconciliation_failed";

    /// <summary>The provider could not observe topology during reconciliation.</summary>
    public const string ReconciliationLimitedCode =
        "recovery.reconciliation_limited";

    /// <summary>A step failed in a way this service did not anticipate.</summary>
    public const string UnexpectedFailureCode = "recovery.unexpected_failure";

    private readonly ShellRecoverySignalCoalescer coalescer = new();
    private readonly SemaphoreSlim recoveryGate = new(1, 1);
    private readonly object syncRoot = new();
    private readonly INativeRegistrationSet registrations;
    private readonly ICompatibilityCoordinator compatibilityCoordinator;
    private readonly IManagedDesktopTopologyRecoveryService topologyRecoveryService;
    private readonly TimeProvider timeProvider;
    private readonly IActivityJournal? activityJournal;

    private ShellRecoveryState state = ShellRecoveryState.Healthy;

    /// <param name="registrations">
    /// The native registrations to check, drop, and take again.
    /// </param>
    /// <param name="compatibilityCoordinator">
    /// The capability validation that has to succeed before anything is
    /// registered again. Re-running it is also what rebuilds provider state.
    /// </param>
    /// <param name="topologyRecoveryService">
    /// The one reconciliation a pass is allowed to run. It owns the bound of one
    /// desktop pass and at most one window pass, so nothing here counts windows.
    /// </param>
    /// <param name="timeProvider">The clock every recorded time comes from.</param>
    /// <param name="activityJournal">
    /// Where recovery decisions are published, or null in a host without
    /// diagnostics. A missing journal degrades to recovering silently rather
    /// than to not recovering.
    /// </param>
    public ShellRecoveryService(
        INativeRegistrationSet registrations,
        ICompatibilityCoordinator compatibilityCoordinator,
        IManagedDesktopTopologyRecoveryService topologyRecoveryService,
        TimeProvider timeProvider,
        IActivityJournal? activityJournal = null)
    {
        this.registrations = registrations ??
            throw new ArgumentNullException(nameof(registrations));
        this.compatibilityCoordinator = compatibilityCoordinator ??
            throw new ArgumentNullException(nameof(compatibilityCoordinator));
        this.topologyRecoveryService = topologyRecoveryService ??
            throw new ArgumentNullException(nameof(topologyRecoveryService));
        this.timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        this.activityJournal = activityJournal;
    }

    public ShellRecoveryState State
    {
        get
        {
            lock (syncRoot)
            {
                return state;
            }
        }
    }

    public async Task<ShellRecoveryResult> RecoverAsync(
        ShellLifecycleSignal signal,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!coalescer.TryBeginPass(signal))
        {
            // A folded notification decided nothing, so it changes no state and
            // publishes no activity. Recording it would turn one physical event
            // into a burst of events in the log the burst was folded to avoid.
            return CreateFoldedResult(signal);
        }

        DateTimeOffset occurredAtUtc = timeProvider.GetUtcNow();
        RecoveryPass? pass = null;
        bool gateEntered = false;
        bool coalescerCompleted = false;

        try
        {
            // Distinct signals are not duplicates, but they still touch the same
            // registrations, provider state, and topology service. Serialize the
            // actual passes so one cannot invalidate registrations between
            // another pass's validation and re-registration. The per-signal
            // coalescer claim remains held while waiting, so overlapping
            // duplicates still fold into their queued pass.
            await recoveryGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            gateEntered = true;
            pass = new RecoveryPass(signal, occurredAtUtc, State);

            try
            {
                await RunPassAsync(pass, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                // The caller asked for the pass to stop. That is the one exception
                // this service is allowed to let out.
                throw;
            }
            catch (Exception exception)
            {
                // Every other failure becomes a reported result. Recovery failing
                // is not a reason for the application to stop running.
                pass.FailUnexpectedly(exception);
            }

            pass.CoalescedSignalCount = coalescer.CompletePass(signal);
            coalescerCompleted = true;

            ShellRecoveryResult result = pass.ToResult();
            SetState(result.State);
            Publish(result);
            return result;
        }
        finally
        {
            // Cancellation can happen while this pass is waiting for another
            // signal to finish. Always release its coalescing claim even when it
            // never entered the gate, or every later notification of this kind
            // would be folded forever.
            if (!coalescerCompleted)
            {
                _ = coalescer.CompletePass(signal);
            }

            if (gateEntered)
            {
                recoveryGate.Release();
            }
        }
    }

    private async Task RunPassAsync(
        RecoveryPass pass,
        CancellationToken cancellationToken)
    {
        if (pass.Signal == ShellLifecycleSignal.ExplorerRestarted)
        {
            // No probe. The shell that answered probes is the thing that died,
            // so an answer of "still installed" would describe hooks owned by a
            // process that no longer exists.
            await RebuildAsync(pass, cancellationToken).ConfigureAwait(false);
            return;
        }

        NativeRegistrationValidity validity =
            await CheckAsync(pass, cancellationToken).ConfigureAwait(false);

        if (!validity.IsIntact)
        {
            await RebuildAsync(pass, cancellationToken).ConfigureAwait(false);
            return;
        }

        if (pass.Signal == ShellLifecycleSignal.DisplayChanged)
        {
            // Virtual desktops are not per-monitor. A monitor arriving cannot
            // move a window between desktops, so an intact check ends the pass:
            // nothing is validated, nothing is registered again, and no
            // reconciliation runs.
            pass.Outcome = ShellRecoveryOutcome.NoActionNeeded;
            pass.Code = IntactCode;
            pass.Summary =
                "The display topology changed and DesktopShift's own hooks and " +
                "topology registration are still live. Virtual desktops are not " +
                "tied to a monitor, so nothing was validated, registered again, " +
                "or reconciled.";
            return;
        }

        // A resume is the case where the registrations can survive but the
        // desktop layout underneath them cannot, so the mapping is reconciled
        // even though nothing needed rebuilding.
        await ReconcileAsync(pass, cancellationToken).ConfigureAwait(false);

        if (pass.Outcome is
            ShellRecoveryOutcome.Failed or ShellRecoveryOutcome.Limited)
        {
            return;
        }

        if (pass.ReconciliationChangedSomething)
        {
            pass.Outcome = ShellRecoveryOutcome.Recovered;
            pass.Code = ReconciledCode;
            pass.Summary =
                "The machine resumed with DesktopShift's hooks and topology " +
                "registration still live, and the one reconciliation that " +
                "followed brought the managed desktop layout back into line.";
            return;
        }

        pass.Outcome = ShellRecoveryOutcome.NoActionNeeded;
        pass.Code = IntactCode;
        pass.Summary =
            "The machine resumed with DesktopShift's hooks and topology " +
            "registration still live, and the managed desktop layout was already " +
            "correct, so no desktop and no window was touched.";
    }

    /// <summary>
    /// Asks the targeted question, treating a probe that threw as an answer of
    /// "cannot tell" rather than as a failure of the pass.
    /// </summary>
    /// <remarks>
    /// A probe that cannot answer is not evidence that everything is fine, so an
    /// unknown result reads as broken and drives the rebuild. The stage is
    /// recorded so that a pass which goes on to fail without reaching a later
    /// step still says where it lost its footing.
    /// </remarks>
    private async ValueTask<NativeRegistrationValidity> CheckAsync(
        RecoveryPass pass,
        CancellationToken cancellationToken)
    {
        pass.Stage = ShellRecoveryStage.ValidityCheck;

        try
        {
            return await registrations.CheckAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            pass.HResult = exception.HResult;
            return NativeRegistrationValidity.Unknown(
                "DesktopShift could not read whether its own hooks and topology " +
                "registration are still live, so it rebuilt them rather than " +
                "assume they survived.");
        }
    }

    /// <summary>
    /// Drops what is held, validates the capability, and takes fresh
    /// registrations only if the validation succeeded.
    /// </summary>
    private async Task RebuildAsync(
        RecoveryPass pass,
        CancellationToken cancellationToken)
    {
        // Dropping comes first so that nothing is ever validated around, or
        // installed beside, a registration the previous shell already lost.
        pass.Stage = ShellRecoveryStage.Invalidation;
        await registrations.InvalidateAsync(cancellationToken).ConfigureAwait(false);
        pass.RegistrationsInvalidated = true;

        pass.Stage = ShellRecoveryStage.CapabilityValidation;
        CompatibilityTestResult validation = await compatibilityCoordinator
            .RunCompatibilityTestAsync(cancellationToken)
            .ConfigureAwait(false);
        pass.CapabilityValidationRan = true;

        if (!validation.IsSuccessful)
        {
            // Returning here, rather than guarding the re-registration below, is
            // what makes "hooks are re-registered only after capability
            // validation succeeds" a property of the shape of this method.
            pass.Fail(
                ShellRecoveryStage.CapabilityValidation,
                CapabilityValidationFailedCode,
                $"{Preamble(pass.Signal)} Capability validation did not succeed " +
                "on this Windows build, so no hook and no topology registration " +
                "was taken again and automatic assignment is not working.");
            return;
        }

        bool limited =
            validation.Outcome == CompatibilityTestOutcome.PassedLimitedMode;

        pass.Stage = ShellRecoveryStage.Reregistration;
        NativeRegistrationResult reregistration = await registrations
            .ReregisterAsync(cancellationToken)
            .ConfigureAwait(false);

        if (!reregistration.IsSuccess)
        {
            pass.Fail(
                ShellRecoveryStage.Reregistration,
                string.IsNullOrWhiteSpace(reregistration.Code)
                    ? ReregistrationFailedCode
                    : reregistration.Code,
                $"{Preamble(pass.Signal)} Capability validation succeeded, but " +
                $"fresh registrations could not be taken: {reregistration.Message}",
                reregistration.HResult);
            return;
        }

        pass.RegistrationsRestored = true;
        pass.Outcome = limited
            ? ShellRecoveryOutcome.Limited
            : ShellRecoveryOutcome.Recovered;
        pass.State = limited
            ? ShellRecoveryState.Limited
            : ShellRecoveryState.Healthy;
        pass.Code = limited
            ? RegistrationsRestoredLimitedCode
            : RegistrationsRestoredCode;
        pass.Summary = limited
            ? $"{Preamble(pass.Signal)} Capability validation reached Limited " +
                "Mode, so fresh registrations were taken for window placement. " +
                "Desktop creation, switching, and topology notifications are not " +
                "available on this build."
            : $"{Preamble(pass.Signal)} Capability validation succeeded and " +
                "fresh hooks and topology registrations were taken.";

        await ReconcileAsync(pass, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Runs the one reconciliation a pass is allowed, and survives it failing.
    /// </summary>
    /// <remarks>
    /// The single call is the whole of the bound. The service behind it already
    /// guarantees one desktop reconciliation and at most one window pass, so
    /// this never calls it twice and never reaches for a reassignment service of
    /// its own.
    /// </remarks>
    private async Task ReconcileAsync(
        RecoveryPass pass,
        CancellationToken cancellationToken)
    {
        pass.Stage = ShellRecoveryStage.Reconciliation;
        pass.ReconciliationRan = true;

        ManagedDesktopTopologyRecoveryResult reconciliation;

        try
        {
            reconciliation = await topologyRecoveryService
                .HandleTopologyChangedAsync(
                    pass.Signal.ToString(),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            pass.Fail(
                ShellRecoveryStage.Reconciliation,
                ReconciliationFailedCode,
                ReconciliationFailureSummary(pass.Signal),
                exception.HResult);
            return;
        }

        if (reconciliation.Outcome == ManagedDesktopTopologyRecoveryOutcome.Failed)
        {
            pass.Fail(
                ShellRecoveryStage.Reconciliation,
                ReconciliationFailedCode,
                ReconciliationFailureSummary(pass.Signal));
            return;
        }

        if (reconciliation.Outcome == ManagedDesktopTopologyRecoveryOutcome.Limited)
        {
            // The provider cannot read the topology, so the mapping was not
            // resolved. Window placement still works, which is what Limited
            // Mode means, so this is reported rather than treated as a failure.
            pass.State = ShellRecoveryState.Limited;
            pass.Outcome = ShellRecoveryOutcome.Limited;

            // A rebuild whose capability validation already reached Limited Mode
            // has a more specific summary and code. Preserve those; otherwise
            // make the reconciliation's reduced capability explicit instead of
            // allowing the resume path to report that nothing needed doing.
            if (pass.Code != RegistrationsRestoredLimitedCode)
            {
                pass.Code = ReconciliationLimitedCode;
                pass.Summary =
                    $"DesktopShift handled {Describe(pass.Signal)}, but the " +
                    "selected provider could not read the desktop topology " +
                    "during reconciliation. Window placement is still available, " +
                    "but desktop creation, switching, and topology notifications " +
                    "are not.";
            }

            return;
        }

        // A successful reconciliation proves the current provider is usable.
        // The coordinator's selected provider mode is the authority for whether
        // that means Healthy or Limited, so a later successful disruption can
        // clear a transient Error without guessing at the capability level.
        pass.State = compatibilityCoordinator.Current.IsLimitedMode
            ? ShellRecoveryState.Limited
            : ShellRecoveryState.Healthy;
        pass.ReconciliationChangedSomething =
            reconciliation.Outcome ==
            ManagedDesktopTopologyRecoveryOutcome.Reconciled;
    }

    private ShellRecoveryResult CreateFoldedResult(ShellLifecycleSignal signal) =>
        new(
            Guid.NewGuid(),
            timeProvider.GetUtcNow(),
            signal,
            ShellRecoveryOutcome.NoActionNeeded,
            State,
            DuplicateNotificationCode,
            $"A recovery pass for {Describe(signal)} was already running, so " +
            "this duplicate notification was dropped rather than queued.",
            CoalescedSignalCount: 0);

    private void SetState(ShellRecoveryState next)
    {
        lock (syncRoot)
        {
            state = next;
        }
    }

    private void Publish(ShellRecoveryResult result)
    {
        if (activityJournal is null)
        {
            return;
        }

        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromShellRecovery(result, activityJournal.SessionId);
        activityJournal.Record(records);
    }

    private static string ReconciliationFailureSummary(ShellLifecycleSignal signal) =>
        $"DesktopShift recovered from {Describe(signal)}, but the single desktop " +
        "reconciliation that follows could not complete, so the managed desktop " +
        "layout may be out of date.";

    /// <summary>
    /// The opening sentence of a rebuild summary, which differs because the two
    /// paths reached the rebuild for genuinely different reasons.
    /// </summary>
    private static string Preamble(ShellLifecycleSignal signal) =>
        signal == ShellLifecycleSignal.ExplorerRestarted
            ? "Explorer restarted, so every hook and topology registration taken " +
                "against the previous shell was dropped without being trusted."
            : "The targeted check found DesktopShift's own registrations were no " +
                $"longer live after {Describe(signal)}, so they were dropped.";

    private static string Describe(ShellLifecycleSignal signal) => signal switch
    {
        ShellLifecycleSignal.ExplorerRestarted => "the Explorer restart",
        ShellLifecycleSignal.SessionResumed => "the resume from sleep",
        ShellLifecycleSignal.DisplayChanged => "the display change",
        _ => "the disruption",
    };

    /// <summary>
    /// The mutable state of one pass, so that the immutable result is built once
    /// from what the pass actually decided.
    /// </summary>
    private sealed class RecoveryPass
    {
        public RecoveryPass(
            ShellLifecycleSignal signal,
            DateTimeOffset occurredAtUtc,
            ShellRecoveryState state)
        {
            Signal = signal;
            OccurredAtUtc = occurredAtUtc;
            State = state;
            CorrelationId = Guid.NewGuid();
            Code = IntactCode;
            Summary = $"Nothing needed doing for {Describe(signal)}.";
        }

        public Guid CorrelationId { get; }

        public DateTimeOffset OccurredAtUtc { get; }

        public ShellLifecycleSignal Signal { get; }

        /// <summary>
        /// The step currently running, so a failure this service did not
        /// anticipate is still reported against the place it happened.
        /// </summary>
        public ShellRecoveryStage? Stage { get; set; }

        public ShellRecoveryOutcome Outcome { get; set; } =
            ShellRecoveryOutcome.NoActionNeeded;

        /// <summary>
        /// The health to report. It starts at the health of the previous pass,
        /// because a targeted check that found everything intact has validated
        /// no capability and must not claim one.
        /// </summary>
        public ShellRecoveryState State { get; set; }

        public string Code { get; set; }

        public string Summary { get; set; }

        public bool RegistrationsInvalidated { get; set; }

        public bool CapabilityValidationRan { get; set; }

        public bool RegistrationsRestored { get; set; }

        public bool ReconciliationRan { get; set; }

        public bool ReconciliationChangedSomething { get; set; }

        public int CoalescedSignalCount { get; set; } = 1;

        public ShellRecoveryStage? FailedStage { get; set; }

        public int? HResult { get; set; }

        public void Fail(
            ShellRecoveryStage stage,
            string code,
            string summary,
            int? hResult = null)
        {
            Stage = stage;
            FailedStage = stage;
            Outcome = ShellRecoveryOutcome.Failed;
            State = ShellRecoveryState.Error;
            Code = code;
            Summary = summary;
            HResult = hResult;
        }

        public void FailUnexpectedly(Exception exception) => Fail(
            Stage ?? ShellRecoveryStage.ValidityCheck,
            UnexpectedFailureCode,
            $"Recovery from {Describe(Signal)} could not be completed: " +
            $"{exception.Message} DesktopShift is still running and will try " +
            "again the next time Windows reports a disruption.",
            exception.HResult);

        public ShellRecoveryResult ToResult() => new(
            CorrelationId,
            OccurredAtUtc,
            Signal,
            Outcome,
            State,
            Code,
            Summary,
            RegistrationsInvalidated,
            CapabilityValidationRan,
            RegistrationsRestored,
            ReconciliationRan,

            // The coalescer reports zero when it was not holding the signal,
            // which its own contract says callers read as one.
            CoalescedSignalCount == 0 ? 1 : CoalescedSignalCount,
            Outcome == ShellRecoveryOutcome.Failed ? FailedStage : null,
            Outcome == ShellRecoveryOutcome.Failed ? HResult : null);
    }
}

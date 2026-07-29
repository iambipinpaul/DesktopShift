namespace DesktopShift.Core.Recovery;

/// <summary>
/// A disruption that can leave DesktopShift holding native registrations
/// Windows has already thrown away.
/// </summary>
/// <remarks>
/// <para>
/// The three members are deliberately distinct rather than folded into one
/// "something happened" signal. They differ in what is actually at risk:
/// restarting Explorer destroys the shell that owns the virtual-desktop COM
/// objects and every WinEvent hook registered against it; resuming from sleep
/// leaves registrations alive but can leave the desktop layout behind them
/// stale; and a display change usually breaks nothing at all. A recovery that
/// treated the third like the first would rebuild the world every time a user
/// unplugged a monitor.
/// </para>
/// <para>
/// Nothing here restarts Explorer, suspends the machine, or changes a display.
/// DesktopShift only ever observes these happening to it.
/// </para>
/// </remarks>
public enum ShellLifecycleSignal
{
    /// <summary>
    /// Explorer restarted. Every hook and topology registration taken against
    /// the previous shell is stale, whether or not Windows says so.
    /// </summary>
    ExplorerRestarted,

    /// <summary>
    /// The machine resumed from sleep or hibernation. Registrations usually
    /// survive, but the desktop layout underneath them may not have.
    /// </summary>
    SessionResumed,

    /// <summary>
    /// The display topology changed — a monitor arrived, left, or changed
    /// resolution. Virtual desktops are not per-monitor, so this is the signal
    /// least likely to require anything at all.
    /// </summary>
    DisplayChanged,
}

/// <summary>
/// What one recovery pass did.
/// </summary>
public enum ShellRecoveryOutcome
{
    /// <summary>
    /// The targeted check found everything intact and nothing needed doing.
    /// This is the expected result for most display changes.
    /// </summary>
    NoActionNeeded,

    /// <summary>Registrations, provider state, or mappings were restored.</summary>
    Recovered,

    /// <summary>
    /// Recovery completed, but capability validation only reached Limited Mode.
    /// Window placement still works; desktop creation and switching do not.
    /// </summary>
    Limited,

    /// <summary>
    /// Recovery could not restore a usable state. The app keeps running and
    /// says so; it does not crash and it does not touch Explorer.
    /// </summary>
    Failed,
}

/// <summary>
/// The step of a recovery pass that a failure came from.
/// </summary>
/// <remarks>
/// The stage is reported separately from the message because the three failures
/// have genuinely different meanings to a user: a validity check that threw is a
/// probe problem, a validation that failed is a Windows-build problem, and a
/// re-registration that failed is a shell problem.
/// </remarks>
public enum ShellRecoveryStage
{
    /// <summary>The targeted check of the registrations DesktopShift holds.</summary>
    ValidityCheck,

    /// <summary>Dropping registrations taken against the previous shell.</summary>
    Invalidation,

    /// <summary>Re-running the compatibility test that rebuilds provider state.</summary>
    CapabilityValidation,

    /// <summary>Taking fresh hooks once validation has succeeded.</summary>
    Reregistration,

    /// <summary>The single desktop and window reconciliation pass.</summary>
    Reconciliation,
}

/// <summary>
/// The health DesktopShift reports after its most recent recovery pass.
/// </summary>
public enum ShellRecoveryState
{
    /// <summary>Registrations are live and the provider is in Full Mode.</summary>
    Healthy,

    /// <summary>
    /// Recovery succeeded into Limited Mode. Reduced capability, stated plainly,
    /// is a supported way to run.
    /// </summary>
    Limited,

    /// <summary>
    /// Recovery failed. Automatic assignment is not working and the app says so
    /// rather than pretending otherwise.
    /// </summary>
    Error,
}

/// <summary>
/// What a targeted check found, without changing anything.
/// </summary>
/// <remarks>
/// This is the answer to "is what I am holding still real?", and it is asked
/// only when a signal arrives. Nothing in DesktopShift asks it on a timer: a
/// machine nobody is touching produces no signals and therefore runs no checks.
/// </remarks>
/// <param name="WindowHooksLive">
/// Whether the WinEvent hooks that feed window observation are still installed.
/// </param>
/// <param name="TopologyNotificationsLive">
/// Whether the virtual-desktop topology notification registration is still
/// held. False in Limited Mode, where the capability does not exist to begin
/// with — see <paramref name="IsObservable"/>.
/// </param>
/// <param name="IsObservable">
/// Whether the check could be answered at all. A provider that cannot observe
/// topology changes reports <c>false</c> here, which is why an absent
/// notification registration is not by itself treated as breakage.
/// </param>
/// <param name="Explanation">What the check found, in the user's terms.</param>
public sealed record NativeRegistrationValidity(
    bool WindowHooksLive,
    bool TopologyNotificationsLive,
    bool IsObservable,
    string Explanation)
{
    /// <summary>
    /// Whether nothing needs rebuilding. Topology notifications only count when
    /// the provider claims to support them.
    /// </summary>
    public bool IsIntact =>
        WindowHooksLive && (!IsObservable || TopologyNotificationsLive);

    /// <summary>The result reported when a probe itself threw.</summary>
    /// <param name="explanation">What went wrong.</param>
    /// <returns>A validity that will never read as intact.</returns>
    public static NativeRegistrationValidity Unknown(string explanation) => new(
        WindowHooksLive: false,
        TopologyNotificationsLive: false,
        IsObservable: false,
        explanation);
}

/// <summary>
/// The result of dropping or retaking native registrations.
/// </summary>
public sealed record NativeRegistrationResult(
    bool IsSuccess,
    string Code,
    string Message,
    int? HResult = null)
{
    /// <summary>The result of a re-registration that worked.</summary>
    public static NativeRegistrationResult Succeeded { get; } = new(
        IsSuccess: true,
        "recovery.registrations_restored",
        "Native registrations were taken again.");

    /// <summary>Reports a re-registration that did not work.</summary>
    /// <param name="code">The stable diagnostic code.</param>
    /// <param name="message">What failed, in the user's terms.</param>
    /// <param name="hResult">The Windows error, when there was one.</param>
    /// <returns>The failed result.</returns>
    public static NativeRegistrationResult Failed(
        string code,
        string message,
        int? hResult = null) =>
        new(IsSuccess: false, code, message, hResult);
}

/// <summary>
/// The native registrations a recovery pass can check, drop, and take again.
/// </summary>
/// <remarks>
/// <para>
/// This seam exists so the recovery decision itself is platform-neutral and can
/// be driven by injected faults. The Windows implementation owns the WinEvent
/// hooks and the topology notification registration; a test implementation owns
/// counters and a script of failures.
/// </para>
/// <para>
/// Nothing in this interface can create, delete, rename, reorder, or switch a
/// desktop, and nothing in it can move a window. Recovery restores the ability
/// to observe; the existing assignment pipeline remains the only thing that
/// places anything.
/// </para>
/// </remarks>
public interface INativeRegistrationSet
{
    /// <summary>Reads whether the held registrations are still real.</summary>
    ValueTask<NativeRegistrationValidity> CheckAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Drops every registration taken against the previous shell.
    /// </summary>
    /// <remarks>
    /// Unhooking a hook whose shell is gone is not an error, so this cannot
    /// fail. Leaving a stale hook installed while taking a second one is the
    /// failure worth preventing, and doing this first is what prevents it.
    /// </remarks>
    ValueTask InvalidateAsync(CancellationToken cancellationToken = default);

    /// <summary>
    /// Takes fresh registrations. Called only after capability validation has
    /// succeeded.
    /// </summary>
    ValueTask<NativeRegistrationResult> ReregisterAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// A window-event source whose hooks can be dropped and taken again without the
/// source itself being disposed.
/// </summary>
/// <remarks>
/// <see cref="Observation.IWindowEventSource"/> deliberately offers only
/// <c>Start</c> and <c>Dispose</c>, because for the whole lifetime of a normal
/// run that is all anyone needs. Recovery is the exception: an Explorer restart
/// requires unhooking and hooking again while the queue, the processor, and the
/// hosted service all stay exactly as they are. Keeping that on a separate
/// interface means a source that cannot be restarted simply does not implement
/// it, and no existing implementation has to change.
/// </remarks>
public interface IRestartableWindowEventSource
{
    /// <summary>
    /// Drops every hook, leaving the source able to <c>Start</c> again.
    /// </summary>
    void Stop();
}

/// <summary>
/// Everything one recovery pass decided, in the order it decided it.
/// </summary>
/// <param name="CorrelationId">
/// Ties every event this pass produced into one readable thread, the same way an
/// assignment correlation ties a decision to its move and switch.
/// </param>
/// <param name="Signal">
/// Which disruption drove the pass. This is the member that makes Explorer,
/// resume, and display recovery distinguishable in structured logs, and it is a
/// structured field rather than prose because a reader chasing a recovery loop
/// filters on it.
/// </param>
/// <param name="FailedStage">
/// The step that failed, or null when the pass succeeded.
/// </param>
/// <param name="CoalescedSignalCount">
/// How many raw notifications this single pass answered. Windows raises several
/// messages for one physical event — a wake produces two power notifications, a
/// monitor change several display notifications — so answering a burst with a
/// burst of reconciliations would be the defect. A pass that was itself folded
/// into one already running reports zero: it answered nothing because something
/// else already had.
/// </param>
/// <param name="ReconciliationRan">
/// Whether the pass ran its one desktop reconciliation. At most one is run per
/// pass, and the reconciliation itself runs at most one window pass, so no
/// recovery path can produce two of either.
/// </param>
public sealed record ShellRecoveryResult(
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    ShellLifecycleSignal Signal,
    ShellRecoveryOutcome Outcome,
    ShellRecoveryState State,
    string Code,
    string Summary,
    bool RegistrationsInvalidated = false,
    bool CapabilityValidationRan = false,
    bool RegistrationsRestored = false,
    bool ReconciliationRan = false,
    int CoalescedSignalCount = 1,
    ShellRecoveryStage? FailedStage = null,
    int? HResult = null);

/// <summary>
/// Restores automatic assignment after the shell or the machine disrupts it.
/// </summary>
/// <remarks>
/// <para>
/// One signal produces at most one desktop and window reconciliation pass. The
/// order within a pass is fixed and is the whole point of the type: stale
/// registrations are dropped first, capability validation runs second, and fresh
/// registrations are taken only if that validation succeeded. Taking hooks
/// before validating would install them against a shell that may not support
/// them; validating before dropping would validate around registrations that are
/// already dead.
/// </para>
/// <para>
/// Coalescing lives here rather than in the caller, so that the bound holds for
/// every caller. Windows raises a burst of notifications for one physical event,
/// and a notification that arrives while a pass for the same signal is already
/// running is a duplicate of the event that pass is handling. Such a call
/// returns immediately, having decided nothing, which is what keeps one wake or
/// one monitor change to a single reconciliation.
/// </para>
/// <para>
/// A failure at any step leaves the app running. It reports
/// <see cref="ShellRecoveryState.Limited"/> or
/// <see cref="ShellRecoveryState.Error"/>, and it never retries Explorer, never
/// restarts it, and never escalates.
/// </para>
/// </remarks>
public interface IShellRecoveryService
{
    /// <summary>The health of the most recent pass.</summary>
    ShellRecoveryState State { get; }

    /// <summary>Handles one raw lifecycle notification.</summary>
    /// <param name="signal">The disruption to recover from.</param>
    /// <param name="cancellationToken">Cancels the pass.</param>
    /// <returns>
    /// What the pass decided, or a folded result carrying
    /// <see cref="ShellRecoveryResult.CoalescedSignalCount"/> of zero when a pass
    /// for the same signal was already running.
    /// </returns>
    Task<ShellRecoveryResult> RecoverAsync(
        ShellLifecycleSignal signal,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Carries one raw lifecycle signal from the platform to the coalescer.
/// </summary>
public sealed class ShellLifecycleSignalEventArgs : EventArgs
{
    /// <param name="signal">The disruption Windows reported.</param>
    public ShellLifecycleSignalEventArgs(ShellLifecycleSignal signal) =>
        Signal = signal;

    /// <summary>The disruption Windows reported.</summary>
    public ShellLifecycleSignal Signal { get; }
}

/// <summary>
/// Reports shell and machine lifecycle disruptions as they happen.
/// </summary>
/// <remarks>
/// Implementations observe. None of them may restart Explorer, suspend or resume
/// the machine, or change a display setting, and none of them may raise a signal
/// that Windows did not actually deliver.
/// </remarks>
public interface IShellLifecycleSignalSource : IDisposable
{
    /// <summary>Raised once for every disruption Windows reports.</summary>
    event EventHandler<ShellLifecycleSignalEventArgs>? SignalRaised;

    /// <summary>Begins listening. Calling it twice is harmless.</summary>
    void Start();
}

/// <summary>
/// Folds the burst of notifications Windows raises for one physical event into
/// the single recovery pass that answers it.
/// </summary>
/// <remarks>
/// <para>
/// The rule is deliberately not a timed settle window. A delay would need a
/// timer, and a timer is the thing this ticket exists to avoid: recovery is
/// event-driven, so an idle machine must schedule nothing and wake for nothing.
/// The rule here needs no clock at all — a notification that arrives while a
/// pass for the same signal is running is a duplicate of the event that pass is
/// already handling, and is dropped rather than queued.
/// </para>
/// <para>
/// Signals are folded per kind. A display change arriving during Explorer
/// recovery is a genuinely different event and gets its own pass.
/// </para>
/// </remarks>
public sealed class ShellRecoverySignalCoalescer
{
    private readonly object syncRoot = new();
    private readonly Dictionary<ShellLifecycleSignal, int> inFlight = [];

    /// <summary>Claims the right to run a pass for one notification.</summary>
    /// <param name="signal">The notification that arrived.</param>
    /// <returns>
    /// <c>true</c> when the caller must run the pass; <c>false</c> when the
    /// notification was folded into a pass already running for that signal.
    /// </returns>
    public bool TryBeginPass(ShellLifecycleSignal signal)
    {
        lock (syncRoot)
        {
            if (inFlight.TryGetValue(signal, out int folded))
            {
                inFlight[signal] = folded + 1;
                return false;
            }

            inFlight[signal] = 1;
            return true;
        }
    }

    /// <summary>Ends the pass and reports what it answered.</summary>
    /// <param name="signal">The signal whose pass finished.</param>
    /// <returns>
    /// How many raw notifications the pass answered, including the one that
    /// started it. Zero when no pass was running, which callers treat as one.
    /// </returns>
    public int CompletePass(ShellLifecycleSignal signal)
    {
        lock (syncRoot)
        {
            if (!inFlight.Remove(signal, out int folded))
            {
                return 0;
            }

            return folded;
        }
    }
}

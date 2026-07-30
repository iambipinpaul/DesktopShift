using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Assignments;

public sealed record DesktopSwitchRequest(
    Guid CorrelationId,
    WindowEventKind Trigger,
    nint WindowHandle,
    Guid TargetDesktopId,
    DesktopSwitchPolicy Policy,
    bool IsFirstForegroundActivation);

public sealed record DesktopSwitchResult(
    DesktopSwitchOutcome Outcome,
    DesktopSwitchDecisionReason DecisionReason,
    TimeSpan Duration,
    WindowAssignmentError? Error = null,
    Guid? RelatedCorrelationId = null);

public interface IDesktopSwitchCoordinator
{
    ValueTask<DesktopSwitchResult> ApplyAsync(
        DesktopSwitchRequest request,
        CancellationToken cancellationToken = default);
}

public interface IForegroundSwitchSuppression
{
    bool HasPending(nint windowHandle);

    bool TryConsume(nint windowHandle, out Guid relatedCorrelationId);

    void Register(nint windowHandle, Guid correlationId);

    void Clear(nint windowHandle);
}

public interface INewWindowActivationTracker
{
    void MarkMatchedWindowNew(nint windowHandle);

    bool ConsumeFirstForegroundActivation(nint windowHandle);

    void Clear(nint windowHandle);
}

/// <summary>
/// Remembers a window that took the foreground before it was ready to be placed.
/// </summary>
/// <remarks>
/// <para>
/// Applications do not agree on the order of showing a window and focusing it.
/// One shows first and focuses after, and its activation arrives on a window
/// that qualifies. Another focuses first and shows after, and its activation
/// arrives on a window that is still invisible — which the classifier rejects,
/// rightly, because an invisible window is not something to place.
/// </para>
/// <para>
/// Rejecting the window also throws away the only signal that says the user
/// asked for it. By the time the window is visible the sole remaining event is
/// an open event, which never follows, so a launch the user made deliberately
/// leaves them behind. The activation is therefore remembered against the
/// handle, and the open event arriving a moment later inherits it.
/// </para>
/// <para>
/// The memory is deliberately short. It answers "this window was focused as it
/// came up", not "this window was focused at some point": a window that takes
/// seconds to appear after being focused is no longer the launch the user is
/// waiting on, and following it would move them for something they have
/// forgotten about.
/// </para>
/// </remarks>
public interface IEarlyForegroundActivationMemory
{
    /// <summary>Records that an unqualified window took the foreground.</summary>
    void Remember(nint windowHandle);

    /// <summary>
    /// Takes the remembered activation, if one is still live.
    /// </summary>
    /// <remarks>
    /// Consuming rather than peeking, so one early activation answers for one
    /// open event. A window that is created and then shown produces two, and the
    /// second must not inherit an activation the first already spent.
    /// </remarks>
    bool TryConsume(nint windowHandle);

    void Clear(nint windowHandle);
}

public sealed class BoundedForegroundSwitchSuppression(
    TimeProvider timeProvider) : IForegroundSwitchSuppression
{
    public const int DefaultCapacity = 128;
    public static readonly TimeSpan DefaultLifetime =
        TimeSpan.FromMilliseconds(750);

    private readonly object syncRoot = new();
    private readonly Dictionary<nint, SuppressionEntry> entries = [];
    private long generation;

    public bool HasPending(nint windowHandle)
    {
        lock (syncRoot)
        {
            PruneExpired();
            return entries.ContainsKey(windowHandle);
        }
    }

    public bool TryConsume(nint windowHandle, out Guid relatedCorrelationId)
    {
        lock (syncRoot)
        {
            PruneExpired();
            if (!entries.Remove(windowHandle, out SuppressionEntry? entry))
            {
                relatedCorrelationId = Guid.Empty;
                return false;
            }

            relatedCorrelationId = entry.CorrelationId;
            return true;
        }
    }

    public void Register(nint windowHandle, Guid correlationId)
    {
        lock (syncRoot)
        {
            PruneExpired();
            if (entries.Count >= DefaultCapacity &&
                !entries.ContainsKey(windowHandle))
            {
                nint oldest = entries.MinBy(
                    static pair => pair.Value.Generation).Key;
                entries.Remove(oldest);
            }

            entries[windowHandle] = new SuppressionEntry(
                correlationId,
                timeProvider.GetUtcNow() + DefaultLifetime,
                ++generation);
        }
    }

    public void Clear(nint windowHandle)
    {
        lock (syncRoot)
        {
            entries.Remove(windowHandle);
        }
    }

    private void PruneExpired()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (nint handle in entries
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            entries.Remove(handle);
        }
    }

    private sealed record SuppressionEntry(
        Guid CorrelationId,
        DateTimeOffset ExpiresAtUtc,
        long Generation);
}

public sealed class BoundedNewWindowActivationTracker :
    INewWindowActivationTracker
{
    public const int DefaultCapacity = 1024;

    private readonly object syncRoot = new();
    private readonly Dictionary<nint, EligibilityEntry> entries = [];
    private long generation;

    public void MarkMatchedWindowNew(nint windowHandle)
    {
        lock (syncRoot)
        {
            if (entries.ContainsKey(windowHandle))
            {
                return;
            }

            if (entries.Count >= DefaultCapacity)
            {
                nint oldest = entries.MinBy(
                    static pair => pair.Value.Generation).Key;
                entries.Remove(oldest);
            }

            entries[windowHandle] = new EligibilityEntry(
                IsEligible: true,
                ++generation);
        }
    }

    public bool ConsumeFirstForegroundActivation(nint windowHandle)
    {
        lock (syncRoot)
        {
            if (!entries.TryGetValue(
                windowHandle,
                out EligibilityEntry? entry) ||
                !entry.IsEligible)
            {
                return false;
            }

            entries[windowHandle] = entry with { IsEligible = false };
            return true;
        }
    }

    public void Clear(nint windowHandle)
    {
        lock (syncRoot)
        {
            entries.Remove(windowHandle);
        }
    }

    private sealed record EligibilityEntry(
        bool IsEligible,
        long Generation);
}

public sealed class BoundedEarlyForegroundActivationMemory(
    TimeProvider timeProvider) : IEarlyForegroundActivationMemory
{
    public const int DefaultCapacity = 128;

    /// <summary>
    /// How long an activation on an unqualified window stays available to the
    /// open event that follows it.
    /// </summary>
    /// <remarks>
    /// The observed gap between Windows Terminal taking the foreground and its
    /// window becoming visible is around 665ms, so this is a wide margin rather
    /// than a tuned value. It is bounded because the memory decides whether the
    /// user is taken to another desktop, and an activation old enough to have
    /// been forgotten must not be able to do that.
    /// </remarks>
    public static readonly TimeSpan DefaultLifetime =
        TimeSpan.FromSeconds(2);

    private readonly object syncRoot = new();
    private readonly Dictionary<nint, MemoryEntry> entries = [];
    private long generation;

    public void Remember(nint windowHandle)
    {
        lock (syncRoot)
        {
            PruneExpired();
            if (entries.Count >= DefaultCapacity &&
                !entries.ContainsKey(windowHandle))
            {
                nint oldest = entries.MinBy(
                    static pair => pair.Value.Generation).Key;
                entries.Remove(oldest);
            }

            entries[windowHandle] = new MemoryEntry(
                timeProvider.GetUtcNow() + DefaultLifetime,
                ++generation);
        }
    }

    public bool TryConsume(nint windowHandle)
    {
        lock (syncRoot)
        {
            PruneExpired();
            return entries.Remove(windowHandle);
        }
    }

    public void Clear(nint windowHandle)
    {
        lock (syncRoot)
        {
            entries.Remove(windowHandle);
        }
    }

    private void PruneExpired()
    {
        DateTimeOffset now = timeProvider.GetUtcNow();
        foreach (nint handle in entries
            .Where(pair => pair.Value.ExpiresAtUtc <= now)
            .Select(static pair => pair.Key)
            .ToArray())
        {
            entries.Remove(handle);
        }
    }

    private sealed record MemoryEntry(
        DateTimeOffset ExpiresAtUtc,
        long Generation);
}

/// <summary>
/// Decides whether a placed window should take the user with it, and performs
/// at most one desktop switch at a time.
/// </summary>
/// <remarks>
/// <para>
/// The gate covers reading the current desktop, comparing it with the target,
/// and switching. Those three have to be one step: two switches interleaved
/// would each read the desktop the other is about to leave, so the second would
/// decide against a desktop that no longer exists by the time it acts, and the
/// desktop the user ends up on would be whichever call happened to finish last.
/// </para>
/// <para>
/// The wait is asynchronous and nothing here blocks a thread. That is what keeps
/// the switch free of the deadlocks its callers could otherwise create, because
/// a switch is reached from four places at once: the UI thread through the
/// Reassign All command and the tray, a hosted worker draining window events, a
/// ThreadPool timer releasing a held open-window assignment, and — through the
/// suppression registered on the way out — the WinEvent callback that the switch
/// itself provokes. A blocking wait on any of those would stall a message pump
/// or a callback Windows is waiting on, which is a hung desktop rather than a
/// slow one.
/// </para>
/// <para>
/// It is also the innermost of the two gates an assignment holds. An assignment
/// takes <see cref="PerWindowAssignmentGate"/> first and this one second, always
/// in that order and never the reverse, so the two cannot form a cycle. Nothing
/// under this gate calls back into an assignment.
/// </para>
/// <para>
/// A caller that cancels while waiting never enters, so an abandoned switch
/// releases nothing and leaves the gate for the next one.
/// </para>
/// </remarks>
/// <param name="performanceRecorder">
/// Where a completed switch reports how long it took, or null to leave switching
/// unmeasured. Only switches that actually moved the user are recorded: a query
/// that found the desktop already correct, and a switch that failed, are not
/// durations of switching.
/// </param>
public sealed class DesktopSwitchCoordinator(
    IDesktopTopologyProvider topologyProvider,
    IForegroundSwitchSuppression suppression,
    TimeProvider timeProvider,
    IPerformanceRecorder? performanceRecorder = null) : IDesktopSwitchCoordinator
{
    private readonly SemaphoreSlim switchGate = new(1, 1);

    public async ValueTask<DesktopSwitchResult> ApplyAsync(
        DesktopSwitchRequest request,
        CancellationToken cancellationToken = default)
    {
        DesktopSwitchDecisionReason? moveOnlyReason =
            GetMoveOnlyReason(request);
        if (moveOnlyReason is not null)
        {
            return new DesktopSwitchResult(
                DesktopSwitchOutcome.NotRequested,
                moveOnlyReason.Value,
                TimeSpan.Zero);
        }

        if (!topologyProvider.Capabilities.CanSwitchDesktop ||
            !topologyProvider.Capabilities.CanGetCurrentDesktop)
        {
            return new DesktopSwitchResult(
                DesktopSwitchOutcome.Limited,
                DesktopSwitchDecisionReason.CapabilityUnavailable,
                TimeSpan.Zero,
                new WindowAssignmentError(
                    "assignment.switch_capability_unavailable",
                    "The selected provider cannot query and switch the current desktop."));
        }

        await switchGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            long startedTimestamp = timeProvider.GetTimestamp();
            DesktopTopologyProviderResult<Guid> current =
                await topologyProvider.GetCurrentDesktopIdAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (!current.IsSuccess || current.Value == Guid.Empty)
            {
                return new DesktopSwitchResult(
                    current.Outcome == DesktopTopologyResultOutcome.Unsupported
                        ? DesktopSwitchOutcome.Limited
                        : DesktopSwitchOutcome.Failed,
                    current.Outcome == DesktopTopologyResultOutcome.Unsupported
                        ? DesktopSwitchDecisionReason.CapabilityUnavailable
                        : DesktopSwitchDecisionReason.SwitchFailed,
                    timeProvider.GetElapsedTime(startedTimestamp),
                    ToError(
                        current.Error,
                        "assignment.current_desktop_for_switch_failed",
                        "The current desktop could not be queried before switching."));
            }

            if (current.Value == request.TargetDesktopId)
            {
                return new DesktopSwitchResult(
                    DesktopSwitchOutcome.NotRequested,
                    DesktopSwitchDecisionReason.CurrentDesktopAlreadyTarget,
                    timeProvider.GetElapsedTime(startedTimestamp));
            }

            DesktopTopologyProviderResult switched =
                await topologyProvider.SwitchDesktopAsync(
                    request.TargetDesktopId,
                    cancellationToken).ConfigureAwait(false);
            if (!switched.IsSuccess)
            {
                return new DesktopSwitchResult(
                    switched.Outcome == DesktopTopologyResultOutcome.Unsupported
                        ? DesktopSwitchOutcome.Limited
                        : DesktopSwitchOutcome.Failed,
                    switched.Outcome == DesktopTopologyResultOutcome.Unsupported
                        ? DesktopSwitchDecisionReason.CapabilityUnavailable
                        : DesktopSwitchDecisionReason.SwitchFailed,
                    timeProvider.GetElapsedTime(startedTimestamp),
                    ToError(
                        switched.Error,
                        "assignment.switch_failed",
                        "The current desktop could not be switched."));
            }

            suppression.Register(request.WindowHandle, request.CorrelationId);
            TimeSpan duration = timeProvider.GetElapsedTime(startedTimestamp);
            performanceRecorder?.RecordDesktopSwitch(duration);
            return new DesktopSwitchResult(
                DesktopSwitchOutcome.Succeeded,
                DesktopSwitchDecisionReason.PolicyApproved,
                duration);
        }
        finally
        {
            switchGate.Release();
        }
    }

    private static DesktopSwitchDecisionReason? GetMoveOnlyReason(
        DesktopSwitchRequest request)
    {
        if (request.Trigger != WindowEventKind.ForegroundActivated)
        {
            // One rule, with no exceptions: the desktop follows a window only
            // when Windows says the user activated it. Ruled windows and swept
            // windows alike.
            //
            // An open-time event reaching here has already waited out its grace
            // period in the observer without being activated, so what is being
            // moved is a window that opened without anyone asking for it — an
            // updater, a helper window, an application restoring itself.
            // Following one of those would take the user away from whatever they
            // were doing, which is a far worse thing to get wrong than leaving
            // them where they are.
            return DesktopSwitchDecisionReason.BackgroundEventMoveOnly;
        }

        return request.Policy switch
        {
            DesktopSwitchPolicy.Never =>
                DesktopSwitchDecisionReason.PolicyNever,
            DesktopSwitchPolicy.OnNewWindowActivation
                when !request.IsFirstForegroundActivation =>
                DesktopSwitchDecisionReason.PolicyOnNewWindowNotEligible,
            _ => null,
        };
    }

    private static WindowAssignmentError ToError(
        DesktopTopologyProviderError? error,
        string fallbackCode,
        string fallbackMessage) =>
        error is null
            ? new WindowAssignmentError(fallbackCode, fallbackMessage)
            : new WindowAssignmentError(
                error.Code,
                error.Message,
                error.HResult,
                error.NativeErrorCode);
}

using DesktopShift.Core.Assignments;
using DesktopShift.Core.Performance;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Observation;

public interface IWindowCoalescingScheduler
{
    IDisposable Schedule(TimeSpan dueTime, Action callback);
}

public sealed class TimerWindowCoalescingScheduler :
    IWindowCoalescingScheduler
{
    public IDisposable Schedule(TimeSpan dueTime, Action callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new Timer(
            static state =>
            {
                try
                {
                    ((Action)state!).Invoke();
                }
                catch (Exception)
                {
                    // Cleanup is best effort and must not terminate the process from
                    // a ThreadPool timer callback.
                }
            },
            callback,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }
}

/// <summary>
/// How much redundant work coalescing has removed since the process started.
/// </summary>
/// <remarks>
/// Counted over coalescing-eligible events only — the window lifecycle events a
/// duplicate is possible for. Destroyed events, startup reconciliation, and a
/// manual reassignment are exempt by design and are deliberately left out of the
/// denominator, because counting events that were never candidates would only
/// dilute the rate towards zero and hide the thing it exists to show.
/// </remarks>
/// <param name="Evaluated">
/// How many coalescing-eligible events were considered.
/// </param>
/// <param name="Coalesced">
/// How many of those were recognised as a repeat inside the window and skipped.
/// </param>
public sealed record WindowCoalescingSnapshot(long Evaluated, long Coalesced)
{
    /// <summary>The reading for a host that has no coalescer at all.</summary>
    public static WindowCoalescingSnapshot None { get; } = new(0, 0);

    /// <summary>
    /// The share of eligible events that were skipped, between 0 and 1.
    /// </summary>
    public double CoalescingRate =>
        Evaluated == 0 ? 0d : (double)Coalesced / Evaluated;
}

/// <summary>
/// Reads what coalescing has done, without being able to change it.
/// </summary>
/// <remarks>
/// A separate interface so a performance report can be built in a host that has
/// no observation pipeline, and so a test can hand the reporter a fixed reading
/// rather than having to provoke a real burst.
/// </remarks>
public interface IWindowCoalescingMetrics
{
    /// <inheritdoc cref="WindowCoalescingSnapshot"/>
    WindowCoalescingSnapshot Snapshot { get; }
}

public sealed class WindowEventCoalescer : IDisposable, IWindowCoalescingMetrics
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(75);

    private readonly TimeSpan coalescingWindow;
    private readonly IWindowCoalescingScheduler scheduler;
    private readonly object syncRoot = new();
    private readonly Dictionary<(nint Handle, WindowEventKind Kind), ScheduledEntry>
        lastObserved = [];
    private long generation;
    private long evaluated;
    private long coalesced;
    private bool disposed;

    public WindowEventCoalescer(
        TimeSpan? coalescingWindow = null,
        IWindowCoalescingScheduler? scheduler = null)
    {
        this.coalescingWindow = coalescingWindow ?? DefaultWindow;
        this.scheduler = scheduler ?? new TimerWindowCoalescingScheduler();
    }

    public int TrackedEntryCount
    {
        get
        {
            lock (syncRoot)
            {
                return lastObserved.Count;
            }
        }
    }

    /// <inheritdoc cref="WindowCoalescingSnapshot"/>
    /// <remarks>
    /// Read under the same lock the counters are written under, so the two
    /// figures always describe the same instant and a rate can never come back
    /// above one.
    /// </remarks>
    public WindowCoalescingSnapshot Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return new WindowCoalescingSnapshot(evaluated, coalesced);
            }
        }
    }

    public bool ShouldProcess(WindowEvent windowEvent)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (windowEvent.Kind == WindowEventKind.Destroyed)
            {
                Remove(windowEvent.WindowHandle);
                return true;
            }

            if (windowEvent.Kind is
                WindowEventKind.StartupReconciliation or
                WindowEventKind.ManualReassignment)
            {
                return true;
            }

            (nint, WindowEventKind) key = (
                windowEvent.WindowHandle,
                windowEvent.Kind);
            bool isDuplicate =
                lastObserved.TryGetValue(key, out ScheduledEntry? previous) &&
                windowEvent.ObservedAt - previous.ObservedAt < coalescingWindow;
            previous?.Dispose();

            evaluated++;
            if (isDuplicate)
            {
                coalesced++;
            }

            long nextGeneration = ++generation;
            ScheduledEntry entry = new(nextGeneration, windowEvent.ObservedAt);
            lastObserved[key] = entry;
            IDisposable lease = scheduler.Schedule(
                coalescingWindow,
                () => Expire(key, nextGeneration));
            entry.Lease = lease;

            if (!lastObserved.TryGetValue(key, out ScheduledEntry? current) ||
                !ReferenceEquals(entry, current))
            {
                lease.Dispose();
            }

            return !isDuplicate;
        }
    }

    private void Remove(nint windowHandle)
    {
        Remove((windowHandle, WindowEventKind.Created));
        Remove((windowHandle, WindowEventKind.Shown));
        Remove((windowHandle, WindowEventKind.ForegroundActivated));
    }

    private void Remove((nint Handle, WindowEventKind Kind) key)
    {
        if (lastObserved.Remove(key, out ScheduledEntry? entry))
        {
            entry.Dispose();
        }
    }

    private void Expire(
        (nint Handle, WindowEventKind Kind) key,
        long expectedGeneration)
    {
        lock (syncRoot)
        {
            if (lastObserved.TryGetValue(key, out ScheduledEntry? entry) &&
                entry.Generation == expectedGeneration)
            {
                lastObserved.Remove(key);
                entry.Dispose();
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (ScheduledEntry entry in lastObserved.Values)
            {
                entry.Dispose();
            }

            lastObserved.Clear();
        }
    }

    private sealed class ScheduledEntry : IDisposable
    {
        public ScheduledEntry(long generation, DateTimeOffset observedAt)
        {
            Generation = generation;
            ObservedAt = observedAt;
        }

        public long Generation { get; }

        public DateTimeOffset ObservedAt { get; }

        public IDisposable? Lease { get; set; }

        public void Dispose() => Lease?.Dispose();
    }
}

/// <summary>
/// Schedules a held assignment to run once its grace period has elapsed.
/// </summary>
/// <remarks>
/// Distinct from <see cref="IWindowCoalescingScheduler"/>, whose callback only
/// expires a bookkeeping entry. This one runs the assignment itself, so its
/// callback is asynchronous and the scheduler owns the decision to let it run
/// unobserved.
/// </remarks>
public interface IOpenWindowFollowScheduler
{
    IDisposable Schedule(TimeSpan dueTime, Func<Task> callback);
}

public sealed class TimerOpenWindowFollowScheduler : IOpenWindowFollowScheduler
{
    public IDisposable Schedule(TimeSpan dueTime, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);
        return new Timer(
            static state => Run((Func<Task>)state!),
            callback,
            dueTime,
            Timeout.InfiniteTimeSpan);
    }

    private static void Run(Func<Task> callback)
    {
        try
        {
            // Nothing awaits a held assignment, and it records its own failures.
            // Both arms here exist only to keep a failure from reaching a
            // ThreadPool timer callback or the finalizer as an unobserved
            // exception, either of which would terminate the process.
            _ = callback().ContinueWith(
                static faulted => _ = faulted.Exception,
                CancellationToken.None,
                TaskContinuationOptions.OnlyOnFaulted |
                    TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        catch (Exception)
        {
        }
    }
}

/// <summary>
/// Holds a newly opened window's assignment briefly, so a foreground activation
/// can overtake it.
/// </summary>
/// <remarks>
/// <para>
/// The desktop follows a window only when Windows says the user activated it.
/// Moving at open time defeats that test rather than passing it: once a window
/// is on another desktop it can never take the foreground on this one, so the
/// activation the policy is waiting for never arrives, and a launch the user
/// asked for leaves them behind. Which of the two happened came down to whether
/// the Shell had registered an application view yet — a few milliseconds
/// deciding whether the desktop followed.
/// </para>
/// <para>
/// So the open-time assignment waits. If the window takes the foreground inside
/// the grace period, that event does the work and the desktop follows it; if it
/// does not, the held assignment runs and the window is moved without the user.
/// A window that opens on its own — an updater, a helper window, a restored
/// background app — never takes the foreground, and so is never followed.
/// </para>
/// <para>
/// A <see cref="TimeSpan.Zero"/> grace period disables the hold and assigns
/// inline, which is what a caller that drives events by hand wants.
/// </para>
/// </remarks>
public sealed class OpenWindowFollowGrace : IDisposable
{
    /// <summary>
    /// How long an open-time assignment waits for a foreground activation.
    /// </summary>
    /// <remarks>
    /// The observed gap between a window being shown and taking the foreground
    /// is around a millisecond, so this is a wide margin rather than a tuned
    /// value. It stays under the threshold where a delay reads as lag, which
    /// matters because it is also how long an unwanted window stays visible
    /// before it is moved away.
    /// </remarks>
    public static readonly TimeSpan DefaultGracePeriod =
        TimeSpan.FromMilliseconds(150);

    private readonly TimeSpan gracePeriod;
    private readonly IOpenWindowFollowScheduler scheduler;
    private readonly object syncRoot = new();
    private readonly Dictionary<nint, HeldAssignment> held = [];
    private long generation;
    private bool disposed;

    public OpenWindowFollowGrace(
        TimeSpan? gracePeriod = null,
        IOpenWindowFollowScheduler? scheduler = null)
    {
        TimeSpan period = gracePeriod ?? DefaultGracePeriod;
        ArgumentOutOfRangeException.ThrowIfLessThan(period, TimeSpan.Zero);

        this.gracePeriod = period;
        this.scheduler = scheduler ?? new TimerOpenWindowFollowScheduler();
    }

    /// <summary>Whether an open-time assignment is held rather than run inline.</summary>
    public bool IsEnabled => gracePeriod > TimeSpan.Zero;

    /// <summary>How many windows are currently waiting on an activation.</summary>
    public int HeldCount
    {
        get
        {
            lock (syncRoot)
            {
                return held.Count;
            }
        }
    }

    /// <summary>
    /// Whether a window is still inside the grace period that began when it
    /// opened.
    /// </summary>
    /// <remarks>
    /// This is how the observer tells a window that has just opened apart from
    /// one that has been sitting there — the two are otherwise indistinguishable
    /// by the time a foreground activation arrives.
    /// </remarks>
    /// <param name="windowHandle">The window to ask about.</param>
    /// <returns>Whether an assignment is being held for it.</returns>
    public bool IsHeld(nint windowHandle)
    {
        lock (syncRoot)
        {
            return held.ContainsKey(windowHandle);
        }
    }

    /// <summary>
    /// Holds an assignment for a window, replacing any already held for it.
    /// </summary>
    /// <remarks>
    /// A new window produces a created event and a shown event a moment apart.
    /// Replacing rather than stacking means the later of the two is the one that
    /// runs, so a window is assigned once whichever pair of events Windows sends.
    /// </remarks>
    /// <param name="windowHandle">The window whose assignment is held.</param>
    /// <param name="release">The assignment to run when the grace period ends.</param>
    public void Hold(nint windowHandle, Func<Task> release)
    {
        ArgumentNullException.ThrowIfNull(release);

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);

            if (held.Remove(windowHandle, out HeldAssignment? previous))
            {
                previous.Dispose();
            }

            long nextGeneration = ++generation;
            HeldAssignment entry = new(nextGeneration, release);
            held[windowHandle] = entry;
            entry.Lease = scheduler.Schedule(
                gracePeriod,
                () => ReleaseAsync(windowHandle, nextGeneration));
        }
    }

    /// <summary>
    /// Drops a held assignment, because something else is answering for the
    /// window: the activation the hold was waiting for, or the window closing.
    /// </summary>
    /// <param name="windowHandle">The window to stop holding.</param>
    /// <returns>Whether an assignment was being held.</returns>
    public bool Cancel(nint windowHandle)
    {
        lock (syncRoot)
        {
            if (!held.Remove(windowHandle, out HeldAssignment? entry))
            {
                return false;
            }

            entry.Dispose();
            return true;
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            foreach (HeldAssignment entry in held.Values)
            {
                entry.Dispose();
            }

            held.Clear();
        }
    }

    private Task ReleaseAsync(nint windowHandle, long expectedGeneration)
    {
        Func<Task>? release = null;
        lock (syncRoot)
        {
            if (held.TryGetValue(windowHandle, out HeldAssignment? entry) &&
                entry.Generation == expectedGeneration)
            {
                held.Remove(windowHandle);
                release = entry.Release;
                entry.Dispose();
            }
        }

        // Invoked outside the lock. The assignment moves a window and can switch
        // the desktop, neither of which should run while a foreground event is
        // waiting to cancel a different window's hold.
        return release?.Invoke() ?? Task.CompletedTask;
    }

    private sealed class HeldAssignment(long generation, Func<Task> release)
        : IDisposable
    {
        public long Generation { get; } = generation;

        public Func<Task> Release { get; } = release;

        public IDisposable? Lease { get; set; }

        public void Dispose() => Lease?.Dispose();
    }
}

public sealed class WindowObservationProcessor : IDisposable
{
    private readonly IWindowClassifier classifier;
    private readonly IWindowIdentityResolver identityResolver;
    private readonly IWindowRuleSource ruleSource;
    private readonly IWindowObservationActivitySink activitySink;
    private readonly WindowRuleMatcher matcher;
    private readonly WindowEventCoalescer coalescer;
    private readonly OpenWindowFollowGrace followGrace;
    private readonly IWindowAssignmentService? assignmentService;
    private readonly INewWindowActivationTracker? activationTracker;
    private readonly IForegroundSwitchSuppression? switchSuppression;
    private readonly IEarlyForegroundActivationMemory? earlyForeground;
    private readonly IPerformanceRecorder? performanceRecorder;
    private readonly ITilingTrigger? tilingTrigger;
    private readonly TimeProvider timeProvider;

    public WindowObservationProcessor(
        IWindowClassifier classifier,
        IWindowIdentityResolver identityResolver,
        IWindowRuleSource ruleSource,
        IWindowObservationActivitySink activitySink,
        WindowRuleMatcher? matcher = null,
        WindowEventCoalescer? coalescer = null,
        IWindowAssignmentService? assignmentService = null,
        INewWindowActivationTracker? activationTracker = null,
        IForegroundSwitchSuppression? switchSuppression = null,
        OpenWindowFollowGrace? followGrace = null,
        IPerformanceRecorder? performanceRecorder = null,
        TimeProvider? timeProvider = null,
        IEarlyForegroundActivationMemory? earlyForeground = null,
        ITilingTrigger? tilingTrigger = null)
    {
        ArgumentNullException.ThrowIfNull(classifier);
        ArgumentNullException.ThrowIfNull(identityResolver);
        ArgumentNullException.ThrowIfNull(ruleSource);
        ArgumentNullException.ThrowIfNull(activitySink);

        this.classifier = classifier;
        this.identityResolver = identityResolver;
        this.ruleSource = ruleSource;
        this.activitySink = activitySink;
        this.matcher = matcher ?? new WindowRuleMatcher();
        this.coalescer = coalescer ?? new WindowEventCoalescer();
        this.followGrace = followGrace ?? new OpenWindowFollowGrace();
        this.assignmentService = assignmentService;
        this.activationTracker = activationTracker;
        this.switchSuppression = switchSuppression;
        this.earlyForeground = earlyForeground;
        this.performanceRecorder = performanceRecorder;
        this.tilingTrigger = tilingTrigger;
        this.timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<WindowObservationActivity> ProcessAsync(
        WindowEvent windowEvent,
        CancellationToken cancellationToken = default)
    {
        // Minted once, here, and carried through every downstream event this
        // window event produces.
        Guid correlationId = Guid.NewGuid();

        // The monotonic start of event-to-move latency. An event the source
        // stamped carries the moment Windows handed it over, queue wait
        // included; one it did not is measured from here, which is the earliest
        // honest answer available.
        long receivedTimestamp = windowEvent.ReceivedTimestamp != 0
            ? windowEvent.ReceivedTimestamp
            : timeProvider.GetTimestamp();

        if (windowEvent.Kind == WindowEventKind.Destroyed)
        {
            activationTracker?.Clear(windowEvent.WindowHandle);
            switchSuppression?.Clear(windowEvent.WindowHandle);
            earlyForeground?.Clear(windowEvent.WindowHandle);
            tilingTrigger?.NotifyWindowDestroyed(windowEvent.WindowHandle);

            // A window that closed inside its grace period is not moved. There
            // is nothing left to place, and the handle may already have been
            // handed to a different window.
            followGrace.Cancel(windowEvent.WindowHandle);
            coalescer.ShouldProcess(windowEvent);
            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                WindowSkipReason.CleanupEvent,
                correlationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // A move/size drag ended. This is the user's hand finishing, not an
        // event the assignment rules answer: tiling re-reads the window's
        // monitor and state once and the pipeline records that it happened.
        // It deliberately bypasses coalescing — one drag end is already the
        // burst boundary — and rule matching below.
        if (windowEvent.Kind == WindowEventKind.MoveSizeEnded)
        {
            tilingTrigger?.NotifyMoveSizeEnded(windowEvent.WindowHandle);
            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                WindowSkipReason.TilingMoveSizeEndObserved,
                correlationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (windowEvent.Kind is
            WindowEventKind.Cloaked or
            WindowEventKind.Uncloaked or
            WindowEventKind.Hidden or
            WindowEventKind.Minimized or
            WindowEventKind.Restored or
            WindowEventKind.StateChanged)
        {
            tilingTrigger?.NotifyWindowStateChanged(windowEvent.WindowHandle);
        }

        bool hasPendingSwitchSuppression =
            windowEvent.Kind == WindowEventKind.ForegroundActivated &&
            switchSuppression?.HasPending(windowEvent.WindowHandle) == true;
        if (!hasPendingSwitchSuppression &&
            !coalescer.ShouldProcess(windowEvent))
        {
            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                WindowSkipReason.Coalesced,
                correlationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowQualification qualification =
            classifier.Qualify(windowEvent.WindowHandle);
        if (qualification.Window is null)
        {
            // The window is not placeable — usually because it is not visible
            // yet — but it just took the foreground, and that is the one signal
            // saying the user asked for it. Remembered rather than dropped, so
            // the open event that follows can inherit it.
            if (windowEvent.Kind == WindowEventKind.ForegroundActivated)
            {
                earlyForeground?.Remember(windowEvent.WindowHandle);
            }

            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                qualification.SkipReason,
                correlationId,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        QualifiedWindow window = qualification.Window;
        WindowIdentityResolution resolution = await identityResolver
            .ResolveAsync(window, cancellationToken)
            .ConfigureAwait(false);

        if (resolution.Identity is null)
        {
            WindowSkipReason reason = resolution.Failure switch
            {
                WindowIdentityResolutionFailure.StaleWindow or
                WindowIdentityResolutionFailure.ProcessExited =>
                    WindowSkipReason.StaleWindow,
                WindowIdentityResolutionFailure.AccessDenied =>
                    WindowSkipReason.IdentityAccessDenied,
                _ => WindowSkipReason.IdentityUnavailable,
            };

            return await RecordSkipAsync(
                windowEvent,
                window.RootWindowHandle,
                reason,
                correlationId,
                nativeErrorCode: resolution.NativeErrorCode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowIdentity identity = resolution.Identity;

        // Cloak transitions are instrumentation for the external-relocation
        // spike. Resolve a privacy-safe identity when Windows leaves enough of
        // the window available to do so, then stop: neither a user rule nor the
        // unmanaged sweep is allowed to turn this measurement into placement
        // behaviour before the signal has been validated on a real capture.
        if (windowEvent.Kind is
            WindowEventKind.Cloaked or WindowEventKind.Uncloaked)
        {
            return await RecordSkipAsync(
                windowEvent,
                window.RootWindowHandle,
                WindowSkipReason.CloakStateChangeObserved,
                correlationId,
                identity.ToSafeIdentity(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowSkipReason identitySkip =
            classifier.ClassifyIdentity(window, identity);
        if (identitySkip != WindowSkipReason.None)
        {
            return await RecordSkipAsync(
                windowEvent,
                window.RootWindowHandle,
                identitySkip,
                correlationId,
                identity.ToSafeIdentity(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        // Every window gets one of three answers. A rule that names it and
        // targets a Managed Desktop moves it; a rule that names it and says
        // Anywhere leaves it alone; and a window no enabled rule names at all is
        // swept to the first desktop, so an application nobody has thought about
        // cannot pile up on whichever desktop happened to be in front.
        IReadOnlyList<WindowObservationRule> rules = ruleSource.GetRules();
        WindowRuleMatch? match = matcher.Match(identity, windowEvent.Kind, rules);

        if (match is { Rule.Destination: WindowRuleDestination.Anywhere })
        {
            tilingTrigger?.NotifyAssignmentCompleted(
                new TilingAssignmentNotification(
                    window.RootWindowHandle,
                    match.Rule.TargetDesktopKey,
                    TilingAssignmentDisposition.AlreadyInPlace));
            return await RecordSkipAsync(
                windowEvent,
                window.RootWindowHandle,
                WindowSkipReason.AllowedAnywhere,
                correlationId,
                identity.ToSafeIdentity(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowObservationRule rule;
        WindowMatchStrength? matchedOn;
        if (match is not null)
        {
            rule = match.Rule;
            matchedOn = match.Strength;
        }
        else
        {
            // The sweep does not answer foreground activations, so that clicking
            // a stray window never moves it out from under the click. A window
            // still inside its opening grace period is the exception: there the
            // activation is the window being launched, which is the one thing
            // the sweep does answer — it just arrived as a different event.
            bool answersEvent =
                UnmanagedWindowSweep.AnswersEvent(windowEvent.Kind) ||
                (windowEvent.Kind == WindowEventKind.ForegroundActivated &&
                    followGrace.IsHeld(window.RootWindowHandle));

            // The sweep test asks whether any enabled rule names the
            // application, never whether one matched this event. Reusing the
            // match result would turn narrowing a rule's triggers into
            // banishing its windows.
            bool hasEnabledRules =
                rules.Any(static candidate => candidate.IsEnabled);
            bool isNamedByAnyRule =
                hasEnabledRules && matcher.IsNamedByAnyRule(identity, rules);
            if (!answersEvent || isNamedByAnyRule)
            {
                WindowSkipReason reason = !hasEnabledRules
                    ? WindowSkipReason.NoEnabledRules
                    : !answersEvent
                        ? WindowSkipReason.ActivationNotSwept
                        : WindowSkipReason.NoMatchingRule;

                if (windowEvent.Kind is
                    WindowEventKind.Created or
                    WindowEventKind.Shown or
                    WindowEventKind.StartupReconciliation or
                    WindowEventKind.ManualReassignment)
                {
                    tilingTrigger?.NotifyAssignmentCompleted(
                        new TilingAssignmentNotification(
                            window.RootWindowHandle,
                            string.Empty,
                            TilingAssignmentDisposition.AlreadyInPlace));
                }

                return await RecordSkipAsync(
                    windowEvent,
                    window.RootWindowHandle,
                    reason,
                    correlationId,
                    identity.ToSafeIdentity(),
                    cancellationToken: cancellationToken).ConfigureAwait(false);
            }

            rule = UnmanagedWindowSweep.Rule;
            matchedOn = null;
        }

        WindowSafeIdentity safeIdentity = identity.ToSafeIdentity();

        // An activation that arrived before this window could be placed. The
        // window is being opened by a user who focused it on the way up, so this
        // open event carries the same weight as an activation would have.
        bool focusedBeforeVisible =
            windowEvent.Kind is WindowEventKind.Created or WindowEventKind.Shown &&
            earlyForeground?.TryConsume(window.RootWindowHandle) == true;

        if (windowEvent.Kind is WindowEventKind.Created or WindowEventKind.Shown)
        {
            activationTracker?.MarkMatchedWindowNew(window.RootWindowHandle);

            // Wait rather than move now. Moving takes the window off this
            // desktop, and a window that is not here can never take the
            // foreground here, so moving first destroys the very signal that
            // decides whether the desktop should follow it.
            //
            // Unless that signal already arrived. A window focused before it
            // became visible has nothing left to wait for, and holding it would
            // only delay a move the user is watching for.
            if (assignmentService is not null && followGrace.IsEnabled &&
                !focusedBeforeVisible)
            {
                // Where the deliberate wait starts. Whatever the hold costs is
                // measured from here and reported as policy deferral rather than
                // as pipeline latency, so following windows can never be the
                // reason a latency budget fails.
                long heldFromTimestamp = timeProvider.GetTimestamp();
                followGrace.Hold(
                    window.RootWindowHandle,
                    () => CompleteMatchAsync(
                        windowEvent,
                        window.RootWindowHandle,
                        safeIdentity,
                        rule,
                        matchedOn,
                        correlationId,
                        receivedTimestamp,
                        heldFromTimestamp,
                        focusedBeforeVisible: false,
                        CancellationToken.None).AsTask());

                // Deliberately not recorded. Whichever way the wait ends records
                // the observation — the held assignment when it runs, or the
                // activation that overtakes it — so opening a window leaves one
                // row behind rather than two.
                return new WindowObservationActivity(
                    windowEvent.ObservedAt,
                    windowEvent.Sequence,
                    windowEvent.Kind,
                    window.RootWindowHandle,
                    WindowObservationOutcome.Matched,
                    WindowSkipReason.None,
                    rule.Id,
                    rule.TargetDesktopKey,
                    safeIdentity,
                    MatchedOn: matchedOn,
                    CorrelationId: correlationId);
            }
        }
        else if (windowEvent.Kind == WindowEventKind.ForegroundActivated)
        {
            // The activation the hold was waiting for. Dropped here, once this
            // event is known to be assigning, rather than when it arrived: an
            // activation that goes on to be skipped has to leave the hold alone,
            // or a window that fails to qualify on activation is never placed.
            followGrace.Cancel(window.RootWindowHandle);
        }

        return await CompleteMatchAsync(
            windowEvent,
            window.RootWindowHandle,
            safeIdentity,
            rule,
            matchedOn,
            correlationId,
            receivedTimestamp,
            heldFromTimestamp: null,
            focusedBeforeVisible,
            cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Assigns a matched window and records the observation it belongs to.
    /// </summary>
    /// <remarks>
    /// Reached either directly, or from
    /// <see cref="OpenWindowFollowGrace"/> once an opened window has waited out
    /// its grace period without being activated. The window event it is given is
    /// the original one, so a held assignment is recorded against the moment the
    /// window appeared rather than the moment the wait expired.
    /// </remarks>
    /// <param name="receivedTimestamp">
    /// The monotonic reading taken when the window event was received, which is
    /// where event-to-move latency is measured from.
    /// </param>
    /// <param name="heldFromTimestamp">
    /// When the open-window grace period began, or null for an assignment that
    /// never waited. Everything between it and now is reported as deliberate
    /// deferral rather than as latency DesktopShift is answerable for.
    /// </param>
    private async ValueTask<WindowObservationActivity> CompleteMatchAsync(
        WindowEvent windowEvent,
        nint windowHandle,
        WindowSafeIdentity identity,
        WindowObservationRule rule,
        WindowMatchStrength? matchedOn,
        Guid correlationId,
        long receivedTimestamp,
        long? heldFromTimestamp,
        bool focusedBeforeVisible,
        CancellationToken cancellationToken)
    {
        // Read before the assignment runs, so the wait is measured up to the
        // moment work resumed and not up to the moment it finished.
        TimeSpan deferred = heldFromTimestamp is long heldFrom
            ? timeProvider.GetElapsedTime(heldFrom)
            : TimeSpan.Zero;

        // The assignment is told what the event meant rather than which event it
        // was. A window focused before it became visible was activated by the
        // user just as surely as one focused after, and the switch policy asks
        // only whether the user asked for this window.
        WindowEventKind assignmentTrigger = focusedBeforeVisible
            ? WindowEventKind.ForegroundActivated
            : windowEvent.Kind;

        WindowAssignmentActivity? assignment = null;
        if (assignmentService is not null)
        {
            assignment = await assignmentService.AssignAsync(
                new WindowAssignmentRequest(
                    assignmentTrigger,
                    windowHandle,
                    rule,
                    identity,
                    correlationId),
                cancellationToken).ConfigureAwait(false);

            // Recorded here rather than inside the assignment because this is
            // the only frame that knows when the event arrived and how long the
            // follow policy chose to wait.
            performanceRecorder?.RecordAssignment(
                new AssignmentLatencySample(
                    windowEvent.Kind,
                    assignment.MoveOutcome,
                    timeProvider.GetElapsedTime(receivedTimestamp),
                    deferred,
                    assignment.Duration));

            // The moment a window is known to live on its managed desktop is
            // the moment it can be considered for a tile. Tiling decides for
            // itself whether the desktop is visible; this only vouches.
            tilingTrigger?.NotifyAssignmentCompleted(new TilingAssignmentNotification(
                windowHandle,
                rule.TargetDesktopKey,
                DispositionFrom(assignment)));
        }

        WindowObservationActivity activity = new(
            windowEvent.ObservedAt,
            windowEvent.Sequence,
            windowEvent.Kind,
            windowHandle,
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            rule.Id,
            rule.TargetDesktopKey,
            identity,
            AssignmentCorrelationId: assignment?.CorrelationId,
            AssignmentOutcome: assignment?.Outcome,
            AssignmentDuration: assignment?.Duration,
            TargetRuntimeDesktopId: assignment?.TargetDesktopId,
            AssignmentError: assignment?.Error,
            Assignment: assignment,
            MatchedOn: matchedOn,
            CorrelationId: correlationId);
        await activitySink
            .RecordAsync(activity, cancellationToken)
            .ConfigureAwait(false);
        return activity;
    }

    private static TilingAssignmentDisposition DispositionFrom(
        WindowAssignmentActivity assignment) =>
        assignment.Outcome switch
        {
            WindowAssignmentOutcome.Succeeded =>
                assignment.SkipReason ==
                    WindowAssignmentSkipReason.AlreadyOnTargetDesktop ||
                assignment.MoveOutcome == WindowMoveOutcome.AlreadyCorrect
                    ? TilingAssignmentDisposition.AlreadyInPlace
                    : TilingAssignmentDisposition.PlacedOnTargetDesktop,
            _ => TilingAssignmentDisposition.NotPlaced,
        };

    public async Task RunAsync(
        IWindowEventQueue queue,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(queue);

        await foreach (WindowEvent windowEvent in
            queue.ReadAllAsync(cancellationToken).ConfigureAwait(false))
        {
            await ProcessAsync(windowEvent, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    private async ValueTask<WindowObservationActivity> RecordSkipAsync(
        WindowEvent windowEvent,
        nint windowHandle,
        WindowSkipReason reason,
        Guid correlationId,
        WindowSafeIdentity? identity = null,
        int? nativeErrorCode = null,
        CancellationToken cancellationToken = default)
    {
        WindowObservationActivity activity = new(
            windowEvent.ObservedAt,
            windowEvent.Sequence,
            windowEvent.Kind,
            windowHandle,
            WindowObservationOutcome.Skipped,
            reason,
            null,
            null,
            identity,
            nativeErrorCode,
            CorrelationId: correlationId);
        await activitySink
            .RecordAsync(activity, cancellationToken)
            .ConfigureAwait(false);
        return activity;
    }

    public void Dispose()
    {
        coalescer.Dispose();

        // Held assignments are dropped rather than run. Shutdown is not the
        // moment to start moving the user's windows around.
        followGrace.Dispose();
    }
}

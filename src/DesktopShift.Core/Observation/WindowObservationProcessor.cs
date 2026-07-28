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

public sealed class WindowEventCoalescer : IDisposable
{
    public static readonly TimeSpan DefaultWindow = TimeSpan.FromMilliseconds(75);

    private readonly TimeSpan coalescingWindow;
    private readonly IWindowCoalescingScheduler scheduler;
    private readonly object syncRoot = new();
    private readonly Dictionary<(nint Handle, WindowEventKind Kind), ScheduledEntry>
        lastObserved = [];
    private long generation;
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

            (nint, WindowEventKind) key = (
                windowEvent.WindowHandle,
                windowEvent.Kind);
            bool isDuplicate =
                lastObserved.TryGetValue(key, out ScheduledEntry? previous) &&
                windowEvent.ObservedAt - previous.ObservedAt < coalescingWindow;
            previous?.Dispose();

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

public sealed class WindowObservationProcessor : IDisposable
{
    private readonly IWindowClassifier classifier;
    private readonly IWindowIdentityResolver identityResolver;
    private readonly IWindowRuleSource ruleSource;
    private readonly IWindowObservationActivitySink activitySink;
    private readonly WindowRuleMatcher matcher;
    private readonly WindowEventCoalescer coalescer;

    public WindowObservationProcessor(
        IWindowClassifier classifier,
        IWindowIdentityResolver identityResolver,
        IWindowRuleSource ruleSource,
        IWindowObservationActivitySink activitySink,
        WindowRuleMatcher? matcher = null,
        WindowEventCoalescer? coalescer = null)
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
    }

    public async ValueTask<WindowObservationActivity> ProcessAsync(
        WindowEvent windowEvent,
        CancellationToken cancellationToken = default)
    {
        if (windowEvent.Kind == WindowEventKind.Destroyed)
        {
            coalescer.ShouldProcess(windowEvent);
            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                WindowSkipReason.CleanupEvent,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        if (!coalescer.ShouldProcess(windowEvent))
        {
            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                WindowSkipReason.Coalesced,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowQualification qualification =
            classifier.Qualify(windowEvent.WindowHandle);
        if (qualification.Window is null)
        {
            return await RecordSkipAsync(
                windowEvent,
                windowEvent.WindowHandle,
                qualification.SkipReason,
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
                nativeErrorCode: resolution.NativeErrorCode,
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowIdentity identity = resolution.Identity;
        WindowSkipReason identitySkip =
            classifier.ClassifyIdentity(window, identity);
        if (identitySkip != WindowSkipReason.None)
        {
            return await RecordSkipAsync(
                windowEvent,
                window.RootWindowHandle,
                identitySkip,
                identity.ToSafeIdentity(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        IReadOnlyList<WindowObservationRule> rules = ruleSource.GetRules();
        WindowRuleMatch? match = matcher.Match(identity, windowEvent.Kind, rules);
        if (match is null)
        {
            return await RecordSkipAsync(
                windowEvent,
                window.RootWindowHandle,
                rules.Any(static rule => rule.IsEnabled)
                    ? WindowSkipReason.NoMatchingRule
                    : WindowSkipReason.NoEnabledRules,
                identity.ToSafeIdentity(),
                cancellationToken: cancellationToken).ConfigureAwait(false);
        }

        WindowObservationActivity activity = new(
            windowEvent.ObservedAt,
            windowEvent.Sequence,
            windowEvent.Kind,
            window.RootWindowHandle,
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            match.Rule.Id,
            match.Rule.TargetDesktopKey,
            identity.ToSafeIdentity());
        await activitySink
            .RecordAsync(activity, cancellationToken)
            .ConfigureAwait(false);
        return activity;
    }

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
            nativeErrorCode);
        await activitySink
            .RecordAsync(activity, cancellationToken)
            .ConfigureAwait(false);
        return activity;
    }

    public void Dispose() => coalescer.Dispose();
}

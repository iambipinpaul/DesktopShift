using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Observation;

/// <summary>
/// What the observation pipeline does when events arrive faster than it can
/// answer them, and what it does when shutdown lands in the middle of that.
/// </summary>
/// <remarks>
/// A session restore, an Explorer restart, or a machine waking up all produce
/// hundreds of window events at once. These drive the real host wiring — the
/// bounded queue, the hosted pump, and the processor — rather than the pieces
/// in isolation, because it is the handoffs between them that a burst strains.
/// </remarks>
[TestClass]
public sealed class WindowEventBurstTests
{
    /// <summary>
    /// A burst larger than the queue loses only what the queue refused, and
    /// says how much that was.
    /// </summary>
    /// <remarks>
    /// The processor is held for the length of the burst rather than raced
    /// against it. Saturation is the subject here, so it has to be certain: a
    /// test that let the pump keep up would sometimes assert against a queue
    /// that never filled.
    /// </remarks>
    [TestMethod]
    public async Task Burst_LargerThanTheQueue_ProcessesEveryEventItAccepted()
    {
        const int capacity = 16;
        const int burst = 400;

        using IHost host = CreateHost(capacity, holdIdentityResolution: true);
        BoundedWindowEventQueue queue =
            host.Services.GetRequiredService<BoundedWindowEventQueue>();
        CountingActivitySink sink =
            host.Services.GetRequiredService<CountingActivitySink>();
        SelectiveWindowIdentityResolver resolver =
            host.Services.GetRequiredService<SelectiveWindowIdentityResolver>();

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();

        int accepted = source.Publish(WindowEventKind.Shown, (nint)1) ? 1 : 0;
        Assert.AreEqual(1, accepted);
        await resolver.WaitUntilEnteredAsync(TimeSpan.FromSeconds(5));

        for (int index = 1; index < burst; index++)
        {
            if (source.Publish(WindowEventKind.Shown, (nint)(index + 1)))
            {
                accepted++;
            }
        }

        resolver.Release();
        await sink.WaitForAtLeastAsync(accepted, TimeSpan.FromSeconds(10));
        await host.StopAsync();

        WindowEventQueueSnapshot snapshot = queue.Snapshot;
        Assert.AreEqual(capacity, snapshot.Capacity);
        Assert.AreEqual(accepted, (int)snapshot.Accepted);
        Assert.AreEqual(snapshot.Accepted, snapshot.Read);
        Assert.AreEqual(burst - accepted, (int)snapshot.Dropped);
        Assert.AreEqual(0, snapshot.CurrentDepth);
        Assert.IsTrue(accepted <= capacity + 1);

        // One storm reads as one episode, not as one per lost event. That is
        // the whole point of counting them separately.
        Assert.AreEqual(1L, snapshot.SaturationEpisodes);
        Assert.AreEqual(accepted, sink.Count);
    }

    /// <summary>
    /// Events published concurrently from several threads are all accounted
    /// for, and none of them reaches the processor twice.
    /// </summary>
    [TestMethod]
    public async Task ConcurrentPublishers_AreAccountedForExactlyOnce()
    {
        const int publishers = 6;
        const int perPublisher = 250;

        using IHost host = CreateHost(capacity: 64);
        BoundedWindowEventQueue queue =
            host.Services.GetRequiredService<BoundedWindowEventQueue>();
        CountingActivitySink sink =
            host.Services.GetRequiredService<CountingActivitySink>();

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();

        int accepted = 0;
        await Task.WhenAll(
            Enumerable.Range(0, publishers).Select(
                publisher => Task.Run(
                    () =>
                    {
                        for (int index = 0; index < perPublisher; index++)
                        {
                            if (source.Publish(
                                WindowEventKind.Shown,
                                (nint)((publisher * perPublisher) + index + 1)))
                            {
                                Interlocked.Increment(ref accepted);
                            }
                        }
                    })));

        await sink.WaitForAtLeastAsync(accepted, TimeSpan.FromSeconds(15));
        await host.StopAsync();

        WindowEventQueueSnapshot snapshot = queue.Snapshot;
        Assert.AreEqual(
            publishers * perPublisher,
            (int)(snapshot.Accepted + snapshot.Dropped));
        Assert.AreEqual(snapshot.Accepted, snapshot.Read);
        Assert.AreEqual(0, snapshot.CurrentDepth);
        Assert.HasCount((int)snapshot.Read, sink.Handles.Distinct().ToArray());
    }

    /// <summary>
    /// Shutdown arriving mid-burst stops rather than draining, and leaves
    /// nothing hooked or running.
    /// </summary>
    /// <remarks>
    /// Waiting for the backlog would mean an application that will not close
    /// while windows are opening. Dropping it is the deliberate answer, so what
    /// is asserted here is that stopping is clean and complete, not that every
    /// queued event was answered first.
    /// </remarks>
    [TestMethod]
    public async Task ShutdownDuringABurst_StopsCleanlyAndUnhooksEverything()
    {
        using IHost host = CreateHost(capacity: 128);
        BoundedWindowEventQueue queue =
            host.Services.GetRequiredService<BoundedWindowEventQueue>();

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        for (int index = 0; index < 500; index++)
        {
            _ = source.Publish(WindowEventKind.Shown, (nint)(index + 1));
        }

        await host.StopAsync();

        Assert.IsFalse(source.IsRunning);
        Assert.AreEqual(1, source.DisposeCount);
        Assert.IsFalse(source.Publish(WindowEventKind.Shown, (nint)9_001));
        Assert.IsFalse(
            queue.TryPublish(
                new WindowEvent(
                    9_002,
                    WindowEventKind.Shown,
                    (nint)9_002,
                    DateTimeOffset.UtcNow)));
    }

    /// <summary>
    /// A window whose identity cannot be resolved is skipped, and the burst
    /// carries on around it.
    /// </summary>
    /// <remarks>
    /// This is the shape a burst really has: handles go stale while the backlog
    /// is being drained, because the windows they named closed while they
    /// waited. One stale handle must not cost the events behind it.
    /// </remarks>
    [TestMethod]
    public async Task StaleHandlesInABurst_AreSkippedWithoutStoppingTheRest()
    {
        using IHost host = CreateHost(
            capacity: 128,
            staleWindowHandles: [(nint)2, (nint)4, (nint)6]);
        CountingActivitySink sink =
            host.Services.GetRequiredService<CountingActivitySink>();

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        for (int index = 1; index <= 8; index++)
        {
            Assert.IsTrue(source.Publish(WindowEventKind.Shown, (nint)index));
        }

        await sink.WaitForAtLeastAsync(8, TimeSpan.FromSeconds(10));
        await host.StopAsync();

        IReadOnlyList<WindowObservationActivity> recorded = sink.Activities;
        Assert.HasCount(
            3,
            recorded
                .Where(static activity =>
                    activity.SkipReason == WindowSkipReason.StaleWindow)
                .ToArray());
        Assert.HasCount(
            5,
            recorded
                .Where(static activity =>
                    activity.Outcome == WindowObservationOutcome.Matched)
                .ToArray());
    }

    private static IHost CreateHost(
        int capacity,
        IReadOnlyCollection<nint>? staleWindowHandles = null,
        bool holdIdentityResolution = false)
    {
        return DesktopShiftHost.Create(services =>
        {
            services.AssignOpenedWindowsInline();
            services.AddSingleton<IConfigurationService>(
                new StubConfigurationService(
                    new ConfigurationState(
                        ConfigurationDefaults.Create(),
                        ConfigurationDefaults.Create(),
                        [],
                        DateTimeOffset.UtcNow)));
            services.AddSingleton(new BoundedWindowEventQueue(capacity));
            services.AddSingleton<FakeWindowEventSource>();
            services.AddSingleton<IWindowEventSource>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<FakeWindowEventSource>());
            services.AddSingleton<
                IWindowClassifier,
                PassthroughWindowClassifier>();
            services.AddSingleton(
                new SelectiveWindowIdentityResolver(
                    staleWindowHandles ?? [],
                    holdIdentityResolution));
            services.AddSingleton<IWindowIdentityResolver>(
                static serviceProvider => serviceProvider
                    .GetRequiredService<SelectiveWindowIdentityResolver>());
            services.AddSingleton<CountingActivitySink>();
            services.AddSingleton<IWindowObservationActivitySink>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<CountingActivitySink>());

            services.AddDesktopShiftObservation();
        });
    }

    private sealed class FakeWindowEventSource(
        IWindowEventQueue queue,
        TimeProvider timeProvider) : IWindowEventSource
    {
        private long sequence;

        public bool IsRunning { get; private set; }

        public int DisposeCount { get; private set; }

        public void Start() => IsRunning = true;

        public bool Publish(WindowEventKind kind, nint windowHandle) =>
            IsRunning &&
            queue.TryPublish(
                new WindowEvent(
                    Interlocked.Increment(ref sequence),
                    kind,
                    windowHandle,
                    timeProvider.GetUtcNow()));

        public void Dispose()
        {
            if (!IsRunning)
            {
                return;
            }

            IsRunning = false;
            DisposeCount++;
        }
    }

    private sealed class PassthroughWindowClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    100,
                    "TestWindow"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    /// <summary>
    /// Resolves every window except the handles the test declared stale, which
    /// report the failure a window that closed under the backlog produces.
    /// </summary>
    private sealed class SelectiveWindowIdentityResolver :
        IWindowIdentityResolver
    {
        private readonly IReadOnlyCollection<nint> staleWindowHandles;
        private readonly TaskCompletionSource released =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public SelectiveWindowIdentityResolver(
            IReadOnlyCollection<nint> staleWindowHandles,
            bool hold = false)
        {
            this.staleWindowHandles = staleWindowHandles;
            if (!hold)
            {
                released.TrySetResult();
            }
        }

        /// <summary>Lets the pump start draining the backlog.</summary>
        public void Release() => released.TrySetResult();

        public async Task WaitUntilEnteredAsync(TimeSpan timeout)
        {
            await entered.Task.WaitAsync(timeout).ConfigureAwait(false);
        }

        public async ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            entered.TrySetResult();
            await released.Task.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            return staleWindowHandles.Contains(window.RootWindowHandle)
                ? WindowIdentityResolution.Failed(
                    WindowIdentityResolutionFailure.StaleWindow,
                    1400)
                : WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        "Code.exe",
                        @"C:\Program Files\Microsoft VS Code\Code.exe",
                        null,
                        null,
                        window.WindowClass,
                        "Visual Studio Code",
                        null));
        }
    }

    private sealed class CountingActivitySink : IWindowObservationActivitySink
    {
        private readonly object syncRoot = new();
        private readonly List<WindowObservationActivity> activities = [];

        public int Count
        {
            get
            {
                lock (syncRoot)
                {
                    return activities.Count;
                }
            }
        }

        public IReadOnlyList<WindowObservationActivity> Activities
        {
            get
            {
                lock (syncRoot)
                {
                    return [.. activities];
                }
            }
        }

        public IReadOnlyList<nint> Handles
        {
            get
            {
                lock (syncRoot)
                {
                    return [.. activities.Select(
                        static activity => activity.WindowHandle)];
                }
            }
        }

        public ValueTask RecordAsync(
            WindowObservationActivity activity,
            CancellationToken cancellationToken = default)
        {
            lock (syncRoot)
            {
                activities.Add(activity);
            }

            return ValueTask.CompletedTask;
        }

        public async Task WaitForAtLeastAsync(int expected, TimeSpan timeout)
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow + timeout;
            while (DateTimeOffset.UtcNow < deadline)
            {
                if (Count >= expected)
                {
                    return;
                }

                await Task.Delay(10);
            }

            Assert.Fail(
                $"Only {Count} of {expected} events were processed in time.");
        }
    }

    private sealed class StubConfigurationService(
        ConfigurationState state) : IConfigurationService
    {
        public ConfigurationState CurrentState { get; private set; } = state;

        public Task<ConfigurationState> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentState);
        }

        public Task<ConfigurationSaveResult> SaveCandidateAsync(
            ConfigurationDocument candidate,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CurrentState = CurrentState with { Candidate = candidate };
            return Task.FromResult(
                new ConfigurationSaveResult(false, CurrentState));
        }
    }
}

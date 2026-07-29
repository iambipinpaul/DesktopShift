using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Recovery;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

/// <summary>
/// Proves the hosting seam around shell recovery: one lifecycle signal drives
/// one recovery pass, a stopped host answers none, a failed pass leaves the host
/// running, and a host with no platform signal source starts and does nothing.
/// </summary>
/// <remarks>
/// <para>
/// Every fake here is a Core type. Nothing in this file restarts Explorer,
/// suspends the machine, changes a display setting, touches a real Windows
/// Virtual Desktop, or moves a real window: a "signal" is a method call on a
/// test double, which is the only honest way to test the answer to a disruption
/// without causing the disruption.
/// </para>
/// <para>
/// Every active pass is awaited through <c>StopAsync</c>, which is specified to
/// await pending work before it returns. That makes every assertion here
/// deterministic without a single delay.
/// </para>
/// </remarks>
[TestClass]
public sealed class ShellRecoveryHostingTests
{
    [TestMethod]
    public async Task HostStart_StartsTheSignalSourceExactlyOnce()
    {
        FakeShellLifecycleSignalSource source = new();
        FakeShellRecoveryService recovery = new();
        using IHost host = CreateHost(source, recovery);

        await host.StartAsync();

        Assert.AreEqual(1, source.StartCount);
        Assert.IsTrue(
            source.HasSubscribers,
            "Starting must subscribe before it starts listening.");

        await host.StopAsync();
    }

    [TestMethod]
    public async Task SignalSourceStartFailure_RemovesTheHostedServiceSubscription()
    {
        FakeShellLifecycleSignalSource source = new()
        {
            StartFailure = new InvalidOperationException(
                "The platform listener could not start."),
        };
        FakeShellRecoveryService recovery = new();
        using IHost host = CreateHost(source, recovery);

        _ = await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => host.StartAsync());

        Assert.IsFalse(
            source.HasSubscribers,
            "A failed hosted-service start must not leave a live callback.");
        source.Raise(ShellLifecycleSignal.ExplorerRestarted);
        Assert.IsEmpty(recovery.Observed);
    }

    [TestMethod]
    public async Task RaisedSignal_DrivesOneRecoveryPassForThatSignal()
    {
        FakeShellLifecycleSignalSource source = new();
        FakeShellRecoveryService recovery = new();
        using IHost host = CreateHost(source, recovery);
        await host.StartAsync();

        source.Raise(ShellLifecycleSignal.ExplorerRestarted);

        await host.StopAsync();

        CollectionAssert.AreEqual(
            new[] { ShellLifecycleSignal.ExplorerRestarted },
            recovery.Observed);
    }

    [TestMethod]
    public async Task EveryLifecycleSignal_DrivesItsOwnRecoveryPass()
    {
        FakeShellLifecycleSignalSource source = new();
        FakeShellRecoveryService recovery = new();
        using IHost host = CreateHost(source, recovery);
        await host.StartAsync();

        source.Raise(ShellLifecycleSignal.ExplorerRestarted);
        source.Raise(ShellLifecycleSignal.SessionResumed);
        source.Raise(ShellLifecycleSignal.DisplayChanged);

        await host.StopAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                ShellLifecycleSignal.ExplorerRestarted,
                ShellLifecycleSignal.SessionResumed,
                ShellLifecycleSignal.DisplayChanged,
            },
            recovery.Observed,
            "Three distinct disruptions are three events, not one.");
    }

    [TestMethod]
    public async Task EveryLifecycleSignal_ReachesTheStructuredLog()
    {
        FakeShellLifecycleSignalSource source = new();
        FakeShellRecoveryService recovery = new();
        RecordingDiagnosticLogWriter log = new();
        using IHost host = CreateHost(
            source,
            recovery,
            services =>
            {
                services.AddSingleton<IActivityJournal>(
                    new BoundedActivityJournal(TimeProvider.System));
                services.AddSingleton<IDiagnosticLogWriter>(log);
            });
        await host.StartAsync();

        source.Raise(ShellLifecycleSignal.ExplorerRestarted);
        source.Raise(ShellLifecycleSignal.SessionResumed);
        source.Raise(ShellLifecycleSignal.DisplayChanged);

        await host.StopAsync();

        CollectionAssert.AreEquivalent(
            new[]
            {
                "explorer_restarted",
                "session_resumed",
                "display_changed",
            },
            log.Records
                .Select(static record => record.RecoverySignal)
                .OfType<string>()
                .Distinct(StringComparer.Ordinal)
                .ToArray());
    }

    [TestMethod]
    public async Task DuplicateSignal_ReachesRecoveryWhileTheFirstPassIsRunning()
    {
        FakeShellLifecycleSignalSource source = new();
        OverlapObservingRecoveryService recovery = new();
        using IHost host = CreateHost(source, recovery);
        await host.StartAsync();

        source.Raise(ShellLifecycleSignal.SessionResumed);
        await recovery.FirstCallStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        source.Raise(ShellLifecycleSignal.SessionResumed);
        await recovery.SecondCallStarted.Task.WaitAsync(
            TimeSpan.FromSeconds(5));

        Assert.AreEqual(
            2,
            recovery.ActiveCallCount,
            "The duplicate must reach the recovery service while its first " +
            "per-signal claim is active so the service can fold it.");

        recovery.ReleaseFirstCall.SetResult();
        await host.StopAsync();
    }

    [TestMethod]
    public async Task SignalRaisedAfterStop_DrivesNoFurtherPass()
    {
        FakeShellLifecycleSignalSource source = new();
        FakeShellRecoveryService recovery = new();
        using IHost host = CreateHost(source, recovery);
        await host.StartAsync();
        source.Raise(ShellLifecycleSignal.SessionResumed);

        await host.StopAsync();
        source.Raise(ShellLifecycleSignal.SessionResumed);

        Assert.HasCount(1, recovery.Observed);
        Assert.IsFalse(
            source.HasSubscribers,
            "Stopping must unsubscribe, not merely ignore what arrives.");
        Assert.AreEqual(
            0,
            source.DisposeCount,
            "The container owns the signal source, so the hosted service must not dispose it.");
    }

    [TestMethod]
    public async Task FailedRecoveryPass_LeavesTheHostRunningAndTheChainAlive()
    {
        FakeShellLifecycleSignalSource source = new();
        FakeShellRecoveryService recovery = new();
        _ = recovery.FailingSignals.Add(ShellLifecycleSignal.ExplorerRestarted);
        using IHost host = CreateHost(source, recovery);
        await host.StartAsync();

        source.Raise(ShellLifecycleSignal.ExplorerRestarted);
        source.Raise(ShellLifecycleSignal.DisplayChanged);

        // A fault left unobserved on the chain would be re-raised here and fail
        // the stop, so a clean stop is the proof that it was observed.
        await host.StopAsync();

        CollectionAssert.AreEqual(
            new[]
            {
                ShellLifecycleSignal.ExplorerRestarted,
                ShellLifecycleSignal.DisplayChanged,
            },
            recovery.Observed,
            "A pass that threw must not stop the next one from running.");
    }

    [TestMethod]
    public async Task HostWithoutASignalSource_StartsAndStopsWithoutRecovering()
    {
        FakeShellRecoveryService recovery = new();
        using IHost host = CreateHost(signalSource: null, recovery);

        await host.StartAsync();
        await host.StopAsync();

        Assert.IsEmpty(recovery.Observed);

        // The hosted service must still be registered and still have started;
        // it simply has nothing to listen to.
        Assert.HasCount(
            1,
            host.Services
                .GetServices<IHostedService>()
                .Where(static service =>
                    service.GetType().Name == "ShellRecoveryHostedService")
                .ToArray());
    }

    [TestMethod]
    public void AddDesktopShiftShellRecovery_ResolvesTheRecoveryServiceFromItsDependencies()
    {
        ServiceCollection services = new();
        services.AddSingleton(TimeProvider.System);
        services.AddSingleton<INativeRegistrationSet>(
            new FakeNativeRegistrationSet());
        services.AddSingleton<ICompatibilityCoordinator>(
            new UnusedCompatibilityCoordinator());
        services.AddSingleton<IManagedDesktopTopologyRecoveryService>(
            new UnusedTopologyRecoveryService());

        services.AddDesktopShiftShellRecovery();

        using ServiceProvider provider = services.BuildServiceProvider();
        IShellRecoveryService recoveryService =
            provider.GetRequiredService<IShellRecoveryService>();

        Assert.IsInstanceOfType<ShellRecoveryService>(recoveryService);
        Assert.AreSame(
            recoveryService,
            provider.GetRequiredService<IShellRecoveryService>(),
            "Recovery holds per-pass state, so it must be a singleton.");
    }

    private static IHost CreateHost(
        FakeShellLifecycleSignalSource? signalSource,
        IShellRecoveryService recoveryService,
        Action<IServiceCollection>? configureServices = null)
    {
        return DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<INativeRegistrationSet>(
                new FakeNativeRegistrationSet());
            services.AddSingleton<IShellRecoveryService>(recoveryService);
            if (signalSource is not null)
            {
                services.AddSingleton<IShellLifecycleSignalSource>(signalSource);
            }

            configureServices?.Invoke(services);
            services.AddDesktopShiftShellRecovery();
        });
    }

    /// <summary>
    /// Holds the first pass open and reports whether a duplicate can enter the
    /// service concurrently. The real service uses that overlap to fold the
    /// duplicate through its per-signal coalescer.
    /// </summary>
    private sealed class OverlapObservingRecoveryService : IShellRecoveryService
    {
        private int activeCallCount;
        private int callCount;

        public TaskCompletionSource FirstCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource SecondCallStarted { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public TaskCompletionSource ReleaseFirstCall { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int ActiveCallCount => Volatile.Read(ref activeCallCount);

        public ShellRecoveryState State => ShellRecoveryState.Healthy;

        public async Task<ShellRecoveryResult> RecoverAsync(
            ShellLifecycleSignal signal,
            CancellationToken cancellationToken = default)
        {
            int currentCall = Interlocked.Increment(ref callCount);
            _ = Interlocked.Increment(ref activeCallCount);
            try
            {
                if (currentCall == 1)
                {
                    FirstCallStarted.SetResult();
                    await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
                }
                else
                {
                    SecondCallStarted.SetResult();
                    await ReleaseFirstCall.Task.WaitAsync(cancellationToken);
                }

                return new ShellRecoveryResult(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    signal,
                    ShellRecoveryOutcome.NoActionNeeded,
                    ShellRecoveryState.Healthy,
                    "recovery.no_action_needed",
                    "Everything the check looked at was still intact.");
            }
            finally
            {
                _ = Interlocked.Decrement(ref activeCallCount);
            }
        }
    }

    private sealed class RecordingDiagnosticLogWriter : IDiagnosticLogWriter
    {
        private readonly object syncRoot = new();
        private readonly List<ActivityRecord> records = [];

        public ActivityRecord[] Records
        {
            get
            {
                lock (syncRoot)
                {
                    return [.. records];
                }
            }
        }

        public void Write(ActivityRecord record)
        {
            lock (syncRoot)
            {
                records.Add(record);
            }
        }

        public void Clear()
        {
            lock (syncRoot)
            {
                records.Clear();
            }
        }
    }

    /// <summary>
    /// Stands in for the platform listener. <c>Raise</c> is a method call, not a
    /// real Explorer restart, wake, or display change.
    /// </summary>
    private sealed class FakeShellLifecycleSignalSource : IShellLifecycleSignalSource
    {
        public event EventHandler<ShellLifecycleSignalEventArgs>? SignalRaised;

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public bool HasSubscribers => SignalRaised is not null;

        public Exception? StartFailure { get; init; }

        public void Start()
        {
            StartCount++;
            if (StartFailure is not null)
            {
                throw StartFailure;
            }
        }

        public void Raise(ShellLifecycleSignal signal) =>
            SignalRaised?.Invoke(this, new ShellLifecycleSignalEventArgs(signal));

        public void Dispose() => DisposeCount++;
    }

    private sealed class FakeShellRecoveryService : IShellRecoveryService
    {
        private readonly object syncRoot = new();
        private readonly List<ShellLifecycleSignal> observed = [];

        /// <summary>
        /// The signals whose pass throws. Keyed by signal rather than by a flag
        /// flipped between raises, so the failure lands on the intended pass no
        /// matter when the chain gets around to running it.
        /// </summary>
        public HashSet<ShellLifecycleSignal> FailingSignals { get; } = [];

        public ShellRecoveryState State { get; private set; } =
            ShellRecoveryState.Healthy;

        public ShellLifecycleSignal[] Observed
        {
            get
            {
                lock (syncRoot)
                {
                    return [.. observed];
                }
            }
        }

        public Task<ShellRecoveryResult> RecoverAsync(
            ShellLifecycleSignal signal,
            CancellationToken cancellationToken = default)
        {
            lock (syncRoot)
            {
                observed.Add(signal);
            }

            if (FailingSignals.Contains(signal))
            {
                State = ShellRecoveryState.Error;
                throw new InvalidOperationException(
                    "Recovery threw, which it is not supposed to do.");
            }

            State = ShellRecoveryState.Healthy;
            return Task.FromResult(
                new ShellRecoveryResult(
                    Guid.NewGuid(),
                    DateTimeOffset.UtcNow,
                    signal,
                    ShellRecoveryOutcome.NoActionNeeded,
                    ShellRecoveryState.Healthy,
                    "recovery.no_action_needed",
                    "Everything the check looked at was still intact."));
        }
    }

    /// <summary>
    /// Reports intact registrations and takes none. Nothing here installs,
    /// drops, or retakes a real hook.
    /// </summary>
    private sealed class FakeNativeRegistrationSet : INativeRegistrationSet
    {
        public ValueTask<NativeRegistrationValidity> CheckAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new NativeRegistrationValidity(
                    WindowHooksLive: true,
                    TopologyNotificationsLive: true,
                    IsObservable: true,
                    "The test registrations are intact."));
        }

        public ValueTask InvalidateAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.CompletedTask;
        }

        public ValueTask<NativeRegistrationResult> ReregisterAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(NativeRegistrationResult.Succeeded);
        }
    }

    /// <summary>
    /// Satisfies the container without being usable. These tests resolve the
    /// recovery service; they do not run a pass through it.
    /// </summary>
    private sealed class UnusedCompatibilityCoordinator : ICompatibilityCoordinator
    {
        public CompatibilityStatus Current => throw new NotSupportedException();

        public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    /// <inheritdoc cref="UnusedCompatibilityCoordinator"/>
    private sealed class UnusedTopologyRecoveryService :
        IManagedDesktopTopologyRecoveryService
    {
        public Task<ManagedDesktopTopologyRecoveryResult> HandleTopologyChangedAsync(
            string reason,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }
}

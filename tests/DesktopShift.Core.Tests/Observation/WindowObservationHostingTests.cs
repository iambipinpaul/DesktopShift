using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Observation;

[TestClass]
public sealed class WindowObservationHostingTests
{
    [TestMethod]
    public async Task HostStart_StartsEventSource()
    {
        using IHost host = CreateHost();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();

        await host.StartAsync();

        Assert.IsTrue(source.IsRunning);
        Assert.AreEqual(1, source.StartCount);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task PublishedEvent_IsProcessedBeforeHostBecomesQuiescent()
    {
        using IHost host = CreateHost();
        IWindowObservationActivityProjection projection =
            host.Services.GetRequiredService<
                IWindowObservationActivityProjection>();
        TaskCompletionSource<WindowObservationActivity> recorded =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        projection.ActivityRecorded += (_, args) =>
            recorded.TrySetResult(args.Activity);

        await host.StartAsync();

        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        Assert.IsTrue(source.Publish(
            WindowEventKind.Created,
            (nint)42));

        WindowObservationActivity activity = await recorded.Task.WaitAsync(
            TimeSpan.FromSeconds(3));

        Assert.AreEqual(WindowObservationOutcome.Matched, activity.Outcome);
        Assert.AreEqual("ide-development", activity.RuleId);
        Assert.AreEqual("ide-development", activity.TargetDesktopKey);
        Assert.AreEqual(0, host.Services
            .GetRequiredService<IWindowEventQueue>()
            .Snapshot
            .CurrentDepth);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task HostStop_DisposesHooksBeforeCompletingQueue()
    {
        using IHost host = CreateHost();
        await host.StartAsync();

        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        IWindowEventQueue queue =
            host.Services.GetRequiredService<IWindowEventQueue>();

        await host.StopAsync();

        Assert.IsFalse(source.IsRunning);
        Assert.AreEqual(1, source.DisposeCount);
        Assert.IsFalse(source.Publish(
            WindowEventKind.Shown,
            (nint)43));
        Assert.IsFalse(queue.TryPublish(
            new WindowEvent(
                99,
                WindowEventKind.Shown,
                (nint)43,
                DateTimeOffset.UtcNow)));
    }

    [TestMethod]
    public async Task CancelledStop_CancelsWorkerAndReturnsCleanly()
    {
        using IHost host = CreateHost(blockIdentityResolution: true);
        BlockingWindowIdentityResolver resolver =
            host.Services.GetRequiredService<
                BlockingWindowIdentityResolver>();

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        Assert.IsTrue(source.Publish(
            WindowEventKind.Created,
            (nint)44));
        await resolver.Entered.WaitAsync(TimeSpan.FromSeconds(3));

        IHostedService observationService = host.Services
            .GetServices<IHostedService>()
            .Single(service =>
                service.GetType().Name == "WindowObservationHostedService");
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await observationService.StopAsync(cancellation.Token);

        Assert.IsTrue(resolver.WasCancelled);
        Assert.IsFalse(source.IsRunning);
        await host.StopAsync();
    }

    [TestMethod]
    public async Task RepeatedCleanStop_DoesNotRaceDisposedSource()
    {
        using IHost host = CreateHost();
        await host.StartAsync();

        Task firstStop = host.StopAsync();
        Task secondStop = host.StopAsync();
        await Task.WhenAll(firstStop, secondStop);

        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        Assert.AreEqual(1, source.DisposeCount);
    }

    [TestMethod]
    public async Task WorkerFault_RequestsHostShutdownAndPropagatesOnStop()
    {
        using IHost host = CreateHost(throwDuringClassification: true);
        IHostApplicationLifetime lifetime =
            host.Services.GetRequiredService<IHostApplicationLifetime>();
        TaskCompletionSource stopping =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration =
            lifetime.ApplicationStopping.Register(
                static state =>
                    ((TaskCompletionSource)state!).TrySetResult(),
                stopping);

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();
        Assert.IsTrue(source.Publish(
            WindowEventKind.Created,
            (nint)45));

        await stopping.Task.WaitAsync(TimeSpan.FromSeconds(3));
        InvalidOperationException exception =
            await Assert.ThrowsExactlyAsync<InvalidOperationException>(
                () => host.StopAsync());

        Assert.AreEqual("Classifier failure.", exception.Message);
        Assert.IsFalse(source.IsRunning);
    }

    [TestMethod]
    public void FirstRunConfiguration_ExposesNoObservationRules()
    {
        ConfigurationDocument candidate = ConfigurationDefaults.Create();
        using IHost host = CreateHost(
            configurationState: new ConfigurationState(
                candidate,
                Active: null,
                [],
                DateTimeOffset.UtcNow));

        IReadOnlyList<WindowObservationRule> rules =
            host.Services.GetRequiredService<IWindowRuleSource>().GetRules();

        Assert.IsEmpty(rules);
    }

    [TestMethod]
    public void UnacceptedCandidate_DoesNotReplaceActiveRules()
    {
        ConfigurationDocument active = ConfigurationDefaults.Create();
        ApplicationRule candidateOnlyRule = active.ApplicationRules[0] with
        {
            Id = "candidate-only",
            ProcessNames = ["Unaccepted.exe"],
        };
        ConfigurationDocument candidate = active with
        {
            ApplicationRules = [candidateOnlyRule],
        };
        using IHost host = CreateHost(
            configurationState: new ConfigurationState(
                candidate,
                active,
                [
                    new ConfigurationValidationIssue(
                        ConfigurationValidationCode.RequiredValue,
                        "Candidate was not accepted.",
                        "$.applicationRules[0]",
                        ConfigurationEntryKind.ApplicationRule,
                        candidateOnlyRule.Id),
                ],
                DateTimeOffset.UtcNow));

        IReadOnlyList<WindowObservationRule> rules =
            host.Services.GetRequiredService<IWindowRuleSource>().GetRules();

        Assert.HasCount(7, rules);
        Assert.IsTrue(rules.Any(rule => rule.Id == "ide-development"));
        Assert.IsFalse(rules.Any(rule => rule.Id == "candidate-only"));
    }

    private static IHost CreateHost(
        bool blockIdentityResolution = false,
        bool throwDuringClassification = false,
        ConfigurationState? configurationState = null)
    {
        return DesktopShiftHost.Create(services =>
        {
            services.AssignOpenedWindowsInline();
            services.AddSingleton<IConfigurationService>(
                new StubConfigurationService(
                    configurationState ??
                    new ConfigurationState(
                        ConfigurationDefaults.Create(),
                        ConfigurationDefaults.Create(),
                        [],
                        DateTimeOffset.UtcNow)));
            services.AddSingleton<FakeWindowEventSource>();
            services.AddSingleton<IWindowEventSource>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<
                        FakeWindowEventSource>());
            if (throwDuringClassification)
            {
                services.AddSingleton<
                    IWindowClassifier,
                    ThrowingWindowClassifier>();
            }
            else
            {
                services.AddSingleton<
                    IWindowClassifier,
                    FakeWindowClassifier>();
            }

            if (blockIdentityResolution)
            {
                services.AddSingleton<BlockingWindowIdentityResolver>();
                services.AddSingleton<IWindowIdentityResolver>(
                    static serviceProvider =>
                        serviceProvider.GetRequiredService<
                            BlockingWindowIdentityResolver>());
            }
            else
            {
                services.AddSingleton<
                    IWindowIdentityResolver,
                    FakeWindowIdentityResolver>();
            }

            services.AddDesktopShiftObservation();
        });
    }

    private sealed class FakeWindowEventSource(
        IWindowEventQueue queue,
        TimeProvider timeProvider) : IWindowEventSource
    {
        private long sequence;

        public bool IsRunning { get; private set; }

        public int StartCount { get; private set; }

        public int DisposeCount { get; private set; }

        public void Start()
        {
            IsRunning = true;
            StartCount++;
        }

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

    private sealed class FakeWindowClassifier : IWindowClassifier
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

    private sealed class ThrowingWindowClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            throw new InvalidOperationException("Classifier failure.");

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class FakeWindowIdentityResolver :
        IWindowIdentityResolver
    {
        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    CreateIdentity(window)));
        }
    }

    private sealed class BlockingWindowIdentityResolver :
        IWindowIdentityResolver
    {
        private readonly TaskCompletionSource entered =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Entered => entered.Task;

        public bool WasCancelled { get; private set; }

        public async ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            entered.TrySetResult();
            try
            {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                WasCancelled = true;
                throw;
            }

            return WindowIdentityResolution.Succeeded(
                CreateIdentity(window));
        }
    }

    private static WindowIdentity CreateIdentity(QualifiedWindow window) =>
        new(
            window.ProcessId,
            "Code.exe",
            @"C:\Program Files\Microsoft VS Code\Code.exe",
            null,
            null,
            window.WindowClass,
            "Visual Studio Code",
            null);

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

using System.Collections.Concurrent;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

/// <summary>
/// Pausing has to stop DesktopShift acting on its own without stopping it doing
/// what it is explicitly told.
/// </summary>
[TestClass]
public sealed class AutomaticAssignmentPauseTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public async Task Pause_StopsAutomaticAssignmentAndResumeRestartsIt()
    {
        using IHost host = CreateHost();
        RecordedActivities recorded = new(
            host.Services.GetRequiredService<IWindowObservationActivityProjection>());
        IAutomaticAssignmentPauseController pause =
            host.Services.GetRequiredService<IAutomaticAssignmentPauseController>();
        IWindowEventQueue queue = host.Services.GetRequiredService<IWindowEventQueue>();

        await host.StartAsync();
        FakeWindowEventSource source =
            host.Services.GetRequiredService<FakeWindowEventSource>();

        Assert.IsFalse(pause.IsPaused);
        Assert.IsTrue(source.Publish(WindowEventKind.Created, (nint)10));
        await recorded.WaitForCountAsync(1);

        Assert.IsTrue(pause.TogglePause());
        Assert.IsTrue(pause.IsPaused);
        Assert.IsTrue(source.Publish(WindowEventKind.Created, (nint)11));

        // The pump increments the read counter as it takes the event off the
        // queue, so once the count reaches two the paused decision has been
        // made and the assertion is not a race against a slow worker.
        await WaitForReadCountAsync(queue, 2);
        Assert.HasCount(1, recorded.Snapshot);
        Assert.AreEqual((nint)10, recorded.Snapshot[0].WindowHandle);

        pause.Resume();
        Assert.IsFalse(pause.IsPaused);
        Assert.IsTrue(source.Publish(WindowEventKind.Created, (nint)12));
        await recorded.WaitForCountAsync(2);
        Assert.AreEqual((nint)12, recorded.Snapshot[1].WindowHandle);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task Pause_LeavesExplicitReassignmentWorking()
    {
        using IHost host = CreateHost();
        RecordedActivities recorded = new(
            host.Services.GetRequiredService<IWindowObservationActivityProjection>());
        IAutomaticAssignmentPauseController pause =
            host.Services.GetRequiredService<IAutomaticAssignmentPauseController>();
        IWindowReassignmentService reassignment =
            host.Services.GetRequiredService<IWindowReassignmentService>();

        await host.StartAsync();
        pause.Pause();

        WindowReassignmentBatchResult result = await reassignment.ReassignAllAsync();

        Assert.AreEqual(2, result.EnumeratedWindowCount);
        Assert.AreEqual(WindowEventKind.ManualReassignment, result.Trigger);
        Assert.HasCount(2, recorded.Snapshot);
        Assert.IsTrue(
            recorded.Snapshot.All(
                activity => activity.Outcome == WindowObservationOutcome.Matched));
        Assert.IsTrue(pause.IsPaused, "Reassigning must not silently resume.");

        await host.StopAsync();
    }

    [TestMethod]
    public void PauseStateChanged_IsRaisedOnlyOnAnActualChange()
    {
        using IHost host = CreateHost();
        IAutomaticAssignmentPauseController pause =
            host.Services.GetRequiredService<IAutomaticAssignmentPauseController>();
        List<bool> observed = [];
        pause.PauseStateChanged += (_, args) => observed.Add(args.IsPaused);

        pause.Resume();
        pause.Pause();
        pause.Pause();
        pause.Resume();

        CollectionAssert.AreEqual(new[] { true, false }, observed);
    }

    private static async Task WaitForReadCountAsync(
        IWindowEventQueue queue,
        long expected)
    {
        DateTime deadline = DateTime.UtcNow + Timeout;
        while (queue.Snapshot.Read < expected)
        {
            if (DateTime.UtcNow > deadline)
            {
                Assert.Fail(
                    $"The observation pump read {queue.Snapshot.Read} events, expected {expected}.");
            }

            await Task.Delay(10);
        }
    }

    private static IHost CreateHost()
    {
        return DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<IConfigurationService>(
                new StubConfigurationService(
                    new ConfigurationState(
                        ConfigurationDefaults.Create(),
                        ConfigurationDefaults.Create(),
                        [],
                        DateTimeOffset.UtcNow)));
            services.AddSingleton<FakeWindowEventSource>();
            services.AddSingleton<IWindowEventSource>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<FakeWindowEventSource>());
            services.AddSingleton<IWindowClassifier, FakeWindowClassifier>();
            services.AddSingleton<IWindowIdentityResolver, FakeWindowIdentityResolver>();
            services.AddSingleton<
                ITopLevelWindowEnumerator,
                FakeTopLevelWindowEnumerator>();
            services.AddDesktopShiftObservation();
            services.AddSingleton<WindowReassignmentService>();
            services.AddSingleton<IWindowReassignmentService>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<WindowReassignmentService>());
        });
    }

    private sealed class RecordedActivities
    {
        private readonly ConcurrentQueue<WindowObservationActivity> activities = new();

        public RecordedActivities(IWindowObservationActivityProjection projection)
        {
            projection.ActivityRecorded += (_, args) => activities.Enqueue(args.Activity);
        }

        public IReadOnlyList<WindowObservationActivity> Snapshot => [.. activities];

        public async Task WaitForCountAsync(int expected)
        {
            DateTime deadline = DateTime.UtcNow + Timeout;
            while (activities.Count < expected)
            {
                if (DateTime.UtcNow > deadline)
                {
                    Assert.Fail(
                        $"Recorded {activities.Count} observations, expected {expected}.");
                }

                await Task.Delay(10);
            }
        }
    }

    private sealed class FakeWindowEventSource(
        IWindowEventQueue queue,
        TimeProvider timeProvider) : IWindowEventSource
    {
        private long sequence;

        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public bool Publish(WindowEventKind kind, nint windowHandle) =>
            IsRunning &&
            queue.TryPublish(
                new WindowEvent(
                    Interlocked.Increment(ref sequence),
                    kind,
                    windowHandle,
                    timeProvider.GetUtcNow()));

        public void Dispose() => IsRunning = false;
    }

    private sealed class FakeTopLevelWindowEnumerator : ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() => [(nint)20, (nint)21];
    }

    private sealed class FakeWindowClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(windowHandle, windowHandle, 100, "TestWindow"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class FakeWindowIdentityResolver : IWindowIdentityResolver
    {
        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        "Code.exe",
                        @"C:\Program Files\Microsoft VS Code\Code.exe",
                        null,
                        null,
                        window.WindowClass,
                        "Visual Studio Code",
                        null)));
        }
    }

    private sealed class StubConfigurationService(ConfigurationState state) :
        IConfigurationService
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
            return Task.FromResult(new ConfigurationSaveResult(false, CurrentState));
        }
    }
}

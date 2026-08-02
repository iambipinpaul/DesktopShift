using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Assignments;

[TestClass]
public sealed class WindowAssignmentHostingAcceptanceTests
{
    private static readonly Guid CodeDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();

    [TestMethod]
    public async Task Host_ExecutesCreateShowStartupAndManualCodeAssignments()
    {
        AssignmentHarness harness = new([301]);
        harness.Placement.SetCurrent(301, OtherDesktopId);
        using IHost host = CreateHost(harness);
        IWindowAssignmentActivityProjection projection =
            host.Services.GetRequiredService<IWindowAssignmentActivityProjection>();
        TaskCompletionSource<WindowAssignmentActivity> startupRecorded =
            WaitForActivity(
                projection,
                static activity =>
                    activity.Trigger == WindowEventKind.StartupReconciliation);

        await host.StartAsync();
        WindowAssignmentActivity startup =
            await startupRecorded.Task.WaitAsync(TimeSpan.FromSeconds(5));

        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();
        harness.Placement.SetCurrent(101, OtherDesktopId);
        WindowObservationActivity created = await processor.ProcessAsync(
            new WindowEvent(
                10,
                WindowEventKind.Created,
                (nint)101,
                DateTimeOffset.UtcNow));
        harness.Placement.SetCurrent(102, OtherDesktopId);
        WindowObservationActivity shown = await processor.ProcessAsync(
            new WindowEvent(
                11,
                WindowEventKind.Shown,
                (nint)102,
                DateTimeOffset.UtcNow));

        harness.Placement.SetCurrent(301, OtherDesktopId);
        WindowReassignmentBatchResult manual = await host.Services
            .GetRequiredService<IWindowReassignmentService>()
            .ReassignAllAsync();

        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, startup.Outcome);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, created.AssignmentOutcome);
        Assert.AreEqual(WindowEventKind.Created, created.Assignment!.Trigger);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, shown.AssignmentOutcome);
        Assert.AreEqual(WindowEventKind.Shown, shown.Assignment!.Trigger);
        Assert.HasCount(1, manual.Assignments);
        Assert.AreEqual(
            WindowEventKind.ManualReassignment,
            manual.Assignments[0].Trigger);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, manual.Assignments[0].Outcome);
        Assert.AreEqual("ide-development", manual.Assignments[0].RuleId);
        Assert.AreEqual("ide-development", manual.Assignments[0].TargetDesktopKey);
        Assert.AreEqual(CodeDesktopId, manual.Assignments[0].TargetDesktopId);
        Assert.AreEqual(4, harness.Placement.Moves.Count);
        Assert.IsTrue(projection.Snapshot.All(
            static activity =>
                activity.CorrelationId != Guid.Empty &&
                activity.Duration >= TimeSpan.Zero &&
                activity.Identity.ProcessName == "Code.exe" &&
                activity.Identity.WindowClass == "CodeWindow"));

        await host.StopAsync();
    }

    [TestMethod]
    public async Task CorrectDesktop_IsSkippedWithoutMove()
    {
        AssignmentHarness harness = new([]);
        harness.Placement.SetCurrent(401, CodeDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    20,
                    WindowEventKind.Shown,
                    (nint)401,
                    DateTimeOffset.UtcNow));

        Assert.AreEqual(WindowAssignmentOutcome.Skipped, observation.AssignmentOutcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            observation.Assignment!.SkipReason);
        Assert.AreEqual(CodeDesktopId, observation.Assignment.PreviousDesktopId);
        Assert.IsNull(observation.Assignment.Error);
        Assert.IsEmpty(harness.Placement.Moves);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task MoveFailure_IsStructuredAndDoesNotReportSuccess()
    {
        AssignmentHarness harness = new([]);
        harness.Placement.SetCurrent(501, OtherDesktopId);
        harness.Placement.FailMoves.Add((nint)501);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    30,
                    WindowEventKind.Created,
                    (nint)501,
                    DateTimeOffset.UtcNow));

        WindowAssignmentActivity assignment = observation.Assignment!;
        Assert.AreEqual(WindowAssignmentOutcome.Failed, assignment.Outcome);
        Assert.AreEqual("test.move_denied", assignment.Error!.Code);
        Assert.AreEqual(unchecked((int)0x80070005), assignment.Error.HResult);
        Assert.AreEqual(5, assignment.Error.NativeErrorCode);
        Assert.AreEqual(OtherDesktopId, assignment.PreviousDesktopId);
        Assert.AreEqual(CodeDesktopId, assignment.TargetDesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task ManualBatch_ProcessesMultipleWindowsIndependently()
    {
        AssignmentHarness harness = new([601, 602, 603]);
        harness.Placement.SetCurrent(601, OtherDesktopId);
        harness.Placement.SetCurrent(602, OtherDesktopId);
        harness.Placement.SetCurrent(603, CodeDesktopId);
        harness.Placement.FailMoves.Add((nint)602);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        // Startup is one-shot. Reset the fake runtime positions so the manual
        // pass exercises success, failure, and idempotent skip independently.
        harness.Placement.SetCurrent(601, OtherDesktopId);
        harness.Placement.SetCurrent(602, OtherDesktopId);
        harness.Placement.SetCurrent(603, CodeDesktopId);
        harness.Placement.Moves.Clear();

        WindowReassignmentBatchResult result = await host.Services
            .GetRequiredService<IWindowReassignmentService>()
            .ReassignAllAsync();

        Assert.AreEqual(3, result.EnumeratedWindowCount);
        Assert.HasCount(3, result.Assignments);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            result.Assignments.Single(
                static item => item.WindowHandle == (nint)601).Outcome);
        Assert.AreEqual(
            WindowAssignmentOutcome.Failed,
            result.Assignments.Single(
                static item => item.WindowHandle == (nint)602).Outcome);
        Assert.AreEqual(
            WindowAssignmentOutcome.Skipped,
            result.Assignments.Single(
                static item => item.WindowHandle == (nint)603).Outcome);
        Assert.AreEqual(2, harness.Placement.Moves.Count);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task SuccessfulMove_IsRecordedWhenCancellationArrivesAfterCommit()
    {
        FakePlacementService placement = new();
        placement.SetCurrent(701, OtherDesktopId);
        using CancellationTokenSource cancellation = new();
        placement.AfterSuccessfulMove = cancellation.Cancel;
        BoundedWindowAssignmentActivityStore store = new();
        WindowAssignmentService service = new(
            placement,
            new ReadyReconciliationService(),
            store,
            TimeProvider.System);
        WindowObservationRule rule =
            new ConfigurationWindowRuleSource(ConfigurationDefaults.Create)
                .GetRules()
                .Single(static candidate => candidate.Id == "ide-development");

        WindowAssignmentActivity activity = await service.AssignAsync(
            new WindowAssignmentRequest(
                WindowEventKind.Created,
                (nint)701,
                rule,
                new WindowSafeIdentity(
                    "Code.exe",
                    PackageFamilyName: null,
                    AppUserModelId: null,
                    "CodeWindow")),
            cancellation.Token);

        Assert.IsTrue(cancellation.IsCancellationRequested);
        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, activity.Outcome);
        Assert.AreSame(activity, store.Snapshot.Single());
    }

    [TestMethod]
    public async Task StartupBatchFault_RequestsHostShutdownAndPropagatesOnStop()
    {
        AssignmentHarness harness = new(
            [],
            new ThrowingWindowEnumerator());
        using IHost host = CreateHost(harness);
        IHostApplicationLifetime lifetime =
            host.Services.GetRequiredService<IHostApplicationLifetime>();
        TaskCompletionSource shutdownRequested =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        using CancellationTokenRegistration registration =
            lifetime.ApplicationStopping.Register(shutdownRequested.SetResult);

        await host.StartAsync();
        await shutdownRequested.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => host.StopAsync());
    }

    private static IHost CreateHost(AssignmentHarness harness)
    {
        ConfigurationDocument configuration = ConfigurationDefaults.Create();

        return DesktopShiftHost.Create(services =>
        {
            services.AssignOpenedWindowsInline();
            services.AddSingleton<IConfigurationService>(
                new ActiveConfigurationService(configuration));
            services.AddSingleton<ICompatibilityCoordinator>(
                new ReadyCompatibilityCoordinator());
            services.AddSingleton<IManagedDesktopReconciliationService>(
                new ReadyReconciliationService());
            services.AddSingleton<IWindowClassifier>(harness.Classifier);
            services.AddSingleton<IWindowIdentityResolver>(harness.IdentityResolver);
            services.AddSingleton<IWindowEventSource>(new NoopWindowEventSource());
            services.AddSingleton<IWindowDesktopPlacementService>(harness.Placement);
            services.AddSingleton<ITopLevelWindowEnumerator>(harness.Enumerator);
            services.AddDesktopShiftObservation();
            services.AddDesktopShiftAssignments();
        });
    }

    private static TaskCompletionSource<WindowAssignmentActivity> WaitForActivity(
        IWindowAssignmentActivityProjection projection,
        Func<WindowAssignmentActivity, bool> predicate)
    {
        TaskCompletionSource<WindowAssignmentActivity> completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        projection.ActivityRecorded += (_, args) =>
        {
            if (predicate(args.Activity))
            {
                completion.TrySetResult(args.Activity);
            }
        };
        return completion;
    }

    private sealed class AssignmentHarness(
        IReadOnlyList<nint> windows,
        ITopLevelWindowEnumerator? windowEnumerator = null)
    {
        public FakePlacementService Placement { get; } = new();

        public ITopLevelWindowEnumerator Enumerator { get; } =
            windowEnumerator ?? new FixedWindowEnumerator(windows);

        public AcceptAllClassifier Classifier { get; } = new();

        public CodeIdentityResolver IdentityResolver { get; } = new();
    }

    private sealed class ActiveConfigurationService(
        ConfigurationDocument configuration) : IConfigurationService
    {
        public ConfigurationState CurrentState { get; } = new(
            configuration,
            configuration,
            [],
            DateTimeOffset.UtcNow);

        public Task<ConfigurationState> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentState);
        }

        public Task<ConfigurationSaveResult> SaveCandidateAsync(
            ConfigurationDocument candidate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ReadyCompatibilityCoordinator : ICompatibilityCoordinator
    {
        private static readonly WindowsBuildInfo Build =
            new(true, 10, 0, 26200, 1, Architecture.X64);
        private static readonly DesktopTopologyProviderIdentity Identity =
            new(
                "test.full",
                "Test Full",
                "1",
                DesktopTopologyProviderMode.Full,
                UsesPrivateApis: false);
        private static readonly VirtualDesktopCapabilities Capabilities =
            new(true, true, true, true, true, false, true);

        public CompatibilityStatus Current { get; } = new(
            Build,
            WindowsBuildClassifier.Classify(Build),
            new DesktopTopologyProviderState(
                Identity,
                Capabilities,
                DesktopTopologyProviderAvailability.Ready,
                "Ready"),
            new CompatibilityTestResult(
                CompatibilityTestOutcome.PassedFullMode,
                DateTimeOffset.UtcNow,
                "Ready",
                []));

        public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current.LastTest);
    }

    private sealed class ReadyReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "ide-development",
                "IDE Development",
                1,
                true,
                CodeDesktopId,
                "Code",
                0,
                ManagedDesktopMappingStatus.ReusedPersistedBinding,
                [],
                "test.bound",
                "Bound")],
            []);

        public event EventHandler<ManagedDesktopReconciliationChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
            ManagedDesktopReconciliationTrigger trigger,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }

    private sealed class AcceptAllClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    checked((uint)(long)windowHandle),
                    "CodeWindow"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class CodeIdentityResolver : IWindowIdentityResolver
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
                        ExecutablePath: @"C:\private\Code.exe",
                        PackageFamilyName: null,
                        AppUserModelId: null,
                        "CodeWindow",
                        WindowTitle: "private project",
                        CommandLine: "--private")));
        }
    }

    private sealed class FixedWindowEnumerator(IReadOnlyList<nint> windows) :
        ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() => windows;
    }

    private sealed class ThrowingWindowEnumerator : ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() =>
            throw new InvalidOperationException("EnumWindows failed unexpectedly.");
    }

    private sealed class FakePlacementService : IWindowDesktopPlacementService
    {
        private readonly Dictionary<nint, Guid> currentDesktopIds = [];

        public List<(nint WindowHandle, Guid DesktopId)> Moves { get; } = [];

        public HashSet<nint> FailMoves { get; } = [];

        public Action? AfterSuccessfulMove { get; set; }

        public void SetCurrent(nint windowHandle, Guid desktopId) =>
            currentDesktopIds[windowHandle] = desktopId;

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid current = currentDesktopIds.TryGetValue(
                windowHandle,
                out Guid desktopId)
                ? desktopId
                : OtherDesktopId;
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(current));
        }

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Moves.Add((windowHandle, desktopId));
            if (FailMoves.Contains(windowHandle))
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Failed(
                        "test.move_denied",
                        "Move denied.",
                        unchecked((int)0x80070005),
                        nativeErrorCode: 5));
            }

            currentDesktopIds[windowHandle] = desktopId;
            AfterSuccessfulMove?.Invoke();
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    private sealed class NoopWindowEventSource : IWindowEventSource
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Dispose() => IsRunning = false;
    }
}

using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;
using DesktopShift.Core.Tests.ManagedDesktops;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// Configured Managed Desktops exist before any window is assigned.
/// </summary>
/// <remarks>
/// Creating a virtual desktop is by far the most expensive thing in the pipeline.
/// If one ever happened while a window was waiting, that window's latency would be
/// dominated by it and no amount of tuning elsewhere would show up. So the promise
/// is not that creation is fast — it is that creation is not on the path at all,
/// and the evidence for it is a count of zero rather than a duration.
/// </remarks>
[TestClass]
public sealed class StartupDesktopPrecreationTests
{
    [TestMethod]
    public async Task StartingTheHost_CreatesEveryConfiguredDesktopBeforeAnyWindowMoves()
    {
        using IHost host = CreateHost("work", "comms", "media");
        RecordingTopologyProvider topology =
            host.Services.GetRequiredService<RecordingTopologyProvider>();
        PerformanceMonitor monitor =
            host.Services.GetRequiredService<PerformanceMonitor>();

        await host.StartAsync();

        IManagedDesktopReconciliationService reconciliation =
            host.Services.GetRequiredService<IManagedDesktopReconciliationService>();
        ManagedDesktopReconciliationSnapshot snapshot = reconciliation.Current;

        Assert.AreEqual(
            ManagedDesktopReconciliationTrigger.Startup,
            snapshot.Trigger);
        Assert.AreEqual(
            ManagedDesktopReconciliationOutcome.Succeeded,
            snapshot.Outcome);
        Assert.HasCount(3, snapshot.Mappings);
        Assert.IsTrue(snapshot.Mappings.All(static mapping => mapping.IsBound));
        Assert.AreEqual(3, topology.CreateCallCount);

        // Every creation is attributed to the startup pass, and the report says so
        // in the only way that matters for latency: the durations exist, and they
        // exist here rather than later.
        PerformanceReport atStartup = monitor.CreateReport();
        Assert.AreEqual(3L, atStartup.DesktopsCreated);
        Assert.AreEqual(0L, atStartup.AssignmentsMeasured);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task AfterStartup_AssigningWindowsCreatesNoDesktopAtAll()
    {
        using IHost host = CreateHost("work");
        RecordingTopologyProvider topology =
            host.Services.GetRequiredService<RecordingTopologyProvider>();
        PerformanceMonitor monitor =
            host.Services.GetRequiredService<PerformanceMonitor>();

        await host.StartAsync();
        Assert.AreEqual(1, topology.CreateCallCount);

        // The interval every latency figure is judged over starts here: after
        // startup, with the desktops already in place. That is exactly the
        // documented normal-local workload.
        monitor.Reset();

        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();
        for (int index = 1; index <= 25; index++)
        {
            _ = await processor.ProcessAsync(
                new WindowEvent(
                    index,
                    WindowEventKind.StartupReconciliation,
                    index,
                    DateTimeOffset.UnixEpoch));
        }

        PerformanceReport report = monitor.CreateReport();

        Assert.AreEqual(1, topology.CreateCallCount);
        Assert.AreEqual(
            0L,
            report.DesktopsCreated,
            "A desktop was created while windows were being assigned, so at " +
            "least one window's latency includes making one.");
        Assert.AreEqual(25L, report.AssignmentsMeasured);
        Assert.AreEqual(25L, report.AssignmentsMoved);

        await host.StopAsync();
    }

    /// <summary>
    /// Builds a host with the real reconciliation and assignment wiring over a
    /// topology provider that records what it was asked to do.
    /// </summary>
    /// <remarks>
    /// The provider is a fake, but the startup ordering is not: the hosted service
    /// that reconciles is the shipping one, and it is what decides that creation
    /// happens during <c>StartAsync</c> rather than on a window's path.
    /// </remarks>
    private static IHost CreateHost(params string[] semanticKeys) =>
        DesktopShiftHost.Create(services =>
        {
            _ = services.AddSingleton<RecordingTopologyProvider>();
            _ = services.AddSingleton<IDesktopTopologyProvider>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<RecordingTopologyProvider>());
            _ = services.AddSingleton<
                ICompatibilityCoordinator,
                InertCompatibilityCoordinator>();
            _ = services.AddSingleton<IConfigurationService>(
                new MaintenanceConfigurationService(
                    new ConfigurationDocument(
                        ConfigurationDefaults.CurrentSchemaVersion,
                        PerformanceDefinitions.Create(semanticKeys),
                        [],
                        ConfigurationDefaults.Create().Behavior)));
            _ = services.AddSingleton<IManagedDesktopBindingStore>(
                new MaintenanceBindingStore());
            _ = services.AddSingleton<IWindowClassifier, AnyWindowClassifier>();
            _ = services.AddSingleton<
                IWindowIdentityResolver,
                OneApplicationIdentityResolver>();
            _ = services.AddSingleton<
                IWindowDesktopPlacementService,
                InstantPlacementService>();
            _ = services.AddSingleton<IWindowRuleSource>(
                new KeyedRuleSource(semanticKeys[0]));

            // No native hook. The host is started for its startup ordering, and a
            // test that hooked the machine it runs on would move the user's real
            // windows.
            _ = services.AddSingleton<IWindowEventSource, IdleWindowEventSource>();

            // Nothing here enumerates the machine's real windows either. The
            // reassignment service needs an enumerator to construct; this one
            // reports an empty desktop, so a startup pass touches no window that
            // the test did not name.
            _ = services.AddSingleton<
                ITopLevelWindowEnumerator,
                EmptyWindowEnumerator>();
            _ = services.AssignOpenedWindowsInline();
            _ = services
                .AddDesktopShiftObservation()
                .AddDesktopShiftManagedDesktopReconciliation()
                .AddDesktopShiftAssignments();
        });

    private sealed class RecordingTopologyProvider : IDesktopTopologyProvider
    {
        private int created;

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.full",
            "Test",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: true);

        public VirtualDesktopCapabilities Capabilities { get; } = new(
            CanGetWindowDesktopId: true,
            CanMoveWindowToDesktop: true,
            CanEnumerateDesktops: true,
            CanGetCurrentDesktop: true,
            CanCreateDesktop: true,
            CanSwitchDesktop: true,
            CanObserveTopologyChanges: true);

        public List<VirtualDesktopDescriptor> Desktops { get; } = [];

        public int CreateCallCount { get; private set; }

        public Guid CurrentDesktopId { get; private set; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<
            IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<
                    IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                        Desktops.ToArray()));

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetCurrentDesktopIdAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    CurrentDesktopId == Guid.Empty
                        ? Desktops.Count > 0 ? Desktops[0].Id : Guid.Empty
                        : CurrentDesktopId));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default)
        {
            CreateCallCount++;
            created++;
            Guid id = new($"55555555-5555-5555-5555-{created:D12}");
            Desktops.Add(
                new VirtualDesktopDescriptor(
                    id,
                    DisplayName: null,
                    Desktops.Count,
                    IsCurrent: false));
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(id));
        }

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            CurrentDesktopId = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult>
            StartTopologyNotificationsAsync(
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    /// <summary>
    /// An event source that raises nothing and hooks nothing.
    /// </summary>
    /// <remarks>
    /// The hosted pump needs one to start. Registering the real WinEvent source
    /// here would hook the machine running the test and start moving its windows,
    /// so the events these tests care about are driven through the processor
    /// directly instead.
    /// </remarks>
    private sealed class IdleWindowEventSource : IWindowEventSource
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Dispose() => IsRunning = false;
    }

    private sealed class EmptyWindowEnumerator : ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() => [];
    }

    /// <summary>
    /// A coordinator that has never run a compatibility test.
    /// </summary>
    /// <remarks>
    /// Deliberately never successful, which keeps the startup window pass from
    /// being queued. These tests are about when desktops are created, and a window
    /// pass running alongside would make the assignment counts describe two things
    /// at once.
    /// </remarks>
    private sealed class InertCompatibilityCoordinator : ICompatibilityCoordinator
    {
        public CompatibilityStatus Current { get; } = new(
            new WindowsBuildInfo(
                isWindows: true,
                10,
                0,
                26100,
                0,
                System.Runtime.InteropServices.Architecture.X64),
            new WindowsBuildAssessment(
                WindowsBuildSupport.Supported,
                "Windows 11",
                "Recognised for the test host."),
            new DesktopTopologyProviderState(
                new DesktopTopologyProviderIdentity(
                    "test.full",
                    "Test",
                    "1",
                    DesktopTopologyProviderMode.Full,
                    UsesPrivateApis: true),
                new VirtualDesktopCapabilities(
                    CanGetWindowDesktopId: true,
                    CanMoveWindowToDesktop: true,
                    CanEnumerateDesktops: true,
                    CanGetCurrentDesktop: true,
                    CanCreateDesktop: true,
                    CanSwitchDesktop: true,
                    CanObserveTopologyChanges: true),
                DesktopTopologyProviderAvailability.NotTested,
                "Not tested for the test host."),
            CompatibilityTestResult.NotRun);

        public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current.LastTest);
    }

    private sealed class AnyWindowClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    checked((uint)(long)windowHandle),
                    "Chrome_WidgetWin_1"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class OneApplicationIdentityResolver : IWindowIdentityResolver
    {
        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        "Measured.exe",
                        ExecutablePath: null,
                        PackageFamilyName: null,
                        AppUserModelId: null,
                        "Chrome_WidgetWin_1",
                        WindowTitle: null,
                        CommandLine: null)));
    }

    private sealed class InstantPlacementService : IWindowDesktopPlacementService
    {
        private readonly Dictionary<nint, Guid> placed = [];

        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    placed.TryGetValue(windowHandle, out Guid desktopId)
                        ? desktopId
                        : Guid.Parse("99999999-9999-9999-9999-999999999999")));

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            placed[windowHandle] = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    private sealed class KeyedRuleSource(string semanticKey) : IWindowRuleSource
    {
        private readonly WindowObservationRule[] rules =
        [
            new WindowObservationRule(
                "measured-app",
                "Measured App",
                IsEnabled: true,
                semanticKey,
                [
                    ApplicationRuleTrigger.WindowCreated,
                    ApplicationRuleTrigger.WindowShown,
                    ApplicationRuleTrigger.StartupReconciliation,
                    ApplicationRuleTrigger.ManualReassignment,
                ],
                DesktopSwitchPolicy.Never,
                WindowMatchCriteria.ForProcessNames(["Measured.exe"]),
                Order: 1),
        ];

        public IReadOnlyList<WindowObservationRule> GetRules() => rules;
    }
}

using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.ManagedDesktops;

[TestClass]
public sealed class ManagedDesktopReconciliationHostingTests
{
    [TestMethod]
    public async Task HostStart_ReconcilesTheActiveConfiguration()
    {
        ConfigurationDocument configuration = new(
            SchemaVersion: 1,
            [new ManagedDesktopDefinition("code", "Code", 1, true)],
            [],
            new BehaviorSettings(false, false, false));
        ActiveConfigurationService configurationService = new(configuration);
        FullTopologyProvider topologyProvider = new();
        InMemoryBindingStore bindingStore = new();

        using IHost host = DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<IConfigurationService>(configurationService);
            services.AddSingleton<IDesktopTopologyProvider>(topologyProvider);
            services.AddSingleton<ICompatibilityCoordinator, CompatibilityCoordinatorStub>();
            services.AddSingleton<IManagedDesktopBindingStore>(bindingStore);
            services.AddDesktopShiftManagedDesktopReconciliation();
        });

        await host.StartAsync();

        IManagedDesktopReconciliationService reconciliation =
            host.Services.GetRequiredService<IManagedDesktopReconciliationService>();
        Assert.AreEqual(
            ManagedDesktopReconciliationOutcome.Succeeded,
            reconciliation.Current.Outcome);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Created,
            reconciliation.Current.Mappings.Single().Status);
        Assert.AreEqual(1, topologyProvider.CreateCallCount);
        Assert.AreEqual(
            reconciliation.Current.Mappings.Single().RuntimeDesktopId,
            bindingStore.Bindings.Single().RuntimeDesktopId);

        await host.StopAsync();
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

    private sealed class InMemoryBindingStore : IManagedDesktopBindingStore
    {
        public IReadOnlyList<ManagedDesktopBinding> Bindings { get; private set; } = [];

        public Task<IReadOnlyList<ManagedDesktopBinding>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(Bindings);
        }

        public Task SaveAsync(
            IReadOnlyCollection<ManagedDesktopBinding> bindings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Bindings = bindings.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class CompatibilityCoordinatorStub : ICompatibilityCoordinator
    {
        public CompatibilityStatus Current =>
            throw new NotSupportedException();

        public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class FullTopologyProvider : IDesktopTopologyProvider
    {
        private readonly List<VirtualDesktopDescriptor> desktops = [];

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.full",
            "Test Full",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: false);

        public VirtualDesktopCapabilities Capabilities { get; } = new(
            CanGetWindowDesktopId: true,
            CanMoveWindowToDesktop: true,
            CanEnumerateDesktops: true,
            CanGetCurrentDesktop: true,
            CanCreateDesktop: true,
            CanSwitchDesktop: false,
            CanObserveTopologyChanges: true);

        public int CreateCallCount { get; private set; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                    desktops.ToArray()));
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(Guid.Empty));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCallCount++;
            Guid id = Guid.NewGuid();
            desktops.Add(new VirtualDesktopDescriptor(
                id,
                DisplayName: null,
                desktops.Count,
                IsCurrent: desktops.Count == 0));
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(id));
        }

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }
}

using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

[TestClass]
public sealed class ManagedDesktopReconciliationAcceptanceTests
{
    private static readonly DateTimeOffset ExpectedTime =
        new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task PersistedSemanticBindings_SurviveRuntimeReorderingAndConfigRenaming()
    {
        Guid codeId = Guid.NewGuid();
        Guid webId = Guid.NewGuid();
        Guid unrelatedId = Guid.NewGuid();
        FakeTopologyProvider provider = FakeTopologyProvider.Full(
            new VirtualDesktopDescriptor(unrelatedId, "Personal", 0, true),
            new VirtualDesktopDescriptor(webId, "Old Web", 1, false),
            new VirtualDesktopDescriptor(codeId, "Old Code", 2, false));
        InMemoryBindingStore store = new(
            new ManagedDesktopBinding("code", codeId),
            new ManagedDesktopBinding("web", webId));
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("web", "Browsing", 2, true),
            new ManagedDesktopDefinition("code", "Development", 1, true));
        using ManagedDesktopReconciliationService service =
            CreateService(configuration, provider, store);

        ManagedDesktopReconciliationSnapshot snapshot =
            await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Startup);

        Assert.AreEqual(ManagedDesktopReconciliationOutcome.Succeeded, snapshot.Outcome);
        CollectionAssert.AreEqual(
            new[] { "code", "web" },
            snapshot.Mappings.Select(static item => item.SemanticKey).ToArray());
        Assert.AreEqual(codeId, snapshot.Mappings[0].RuntimeDesktopId);
        Assert.AreEqual(2, snapshot.Mappings[0].RuntimePosition);
        Assert.AreEqual(webId, snapshot.Mappings[1].RuntimeDesktopId);
        Assert.IsTrue(snapshot.Mappings.All(
            static item =>
                item.Status == ManagedDesktopMappingStatus.ReusedPersistedBinding));
        Assert.AreEqual(0, provider.CreateCallCount);
        Assert.IsTrue(provider.Desktops.Any(item => item.Id == unrelatedId));
    }

    [TestMethod]
    public async Task Reconciliation_UsesUniqueNamesReportsAmbiguityAndCreatesMissingInPreferredOrder()
    {
        Guid reusedId = Guid.NewGuid();
        Guid duplicateOne = Guid.NewGuid();
        Guid duplicateTwo = Guid.NewGuid();
        FakeTopologyProvider provider = FakeTopologyProvider.Full(
            new VirtualDesktopDescriptor(reusedId, "Code", 0, true),
            new VirtualDesktopDescriptor(duplicateOne, "Web", 1, false),
            new VirtualDesktopDescriptor(duplicateTwo, "web", 2, false));
        InMemoryBindingStore store = new();
        ConfigurationDocument configuration = CreateConfiguration(
            new ManagedDesktopDefinition("remote", "Remote", 4, true),
            new ManagedDesktopDefinition("terminal", "Terminal", 3, true),
            new ManagedDesktopDefinition("web", "Web", 2, true),
            new ManagedDesktopDefinition("code", "Code", 1, true));
        using ManagedDesktopReconciliationService service =
            CreateService(configuration, provider, store);

        ManagedDesktopReconciliationSnapshot snapshot =
            await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Manual);

        Assert.AreEqual(ManagedDesktopReconciliationOutcome.Partial, snapshot.Outcome);
        CollectionAssert.AreEqual(
            new[] { "code", "web", "terminal", "remote" },
            snapshot.Mappings.Select(static item => item.SemanticKey).ToArray());
        Assert.AreEqual(
            ManagedDesktopMappingStatus.ReusedCompatibleDesktop,
            snapshot.Mappings[0].Status);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Ambiguous,
            snapshot.Mappings[1].Status);
        CollectionAssert.AreEquivalent(
            new[] { duplicateOne, duplicateTwo },
            snapshot.Mappings[1].CandidateDesktopIds.ToArray());
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Created,
            snapshot.Mappings[2].Status);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Created,
            snapshot.Mappings[3].Status);
        CollectionAssert.AreEqual(
            provider.CreatedIds.ToArray(),
            new[]
            {
                snapshot.Mappings[2].RuntimeDesktopId!.Value,
                snapshot.Mappings[3].RuntimeDesktopId!.Value,
            });
        Assert.AreEqual(2, provider.CreateCallCount);
        Assert.IsTrue(provider.Desktops.Any(item => item.Id == duplicateOne));
        Assert.IsTrue(provider.Desktops.Any(item => item.Id == duplicateTwo));
    }

    [TestMethod]
    public async Task LimitedMode_PublishesStructuredMappingsWithoutMutation()
    {
        FakeTopologyProvider provider = FakeTopologyProvider.Limited();
        InMemoryBindingStore store = new();
        using ManagedDesktopReconciliationService service = CreateService(
            CreateConfiguration(
                new ManagedDesktopDefinition("code", "Code", 1, true)),
            provider,
            store);

        ManagedDesktopReconciliationSnapshot snapshot =
            await service.ReconcileAsync(
                ManagedDesktopReconciliationTrigger.CompatibilityChanged);

        Assert.AreEqual(ManagedDesktopReconciliationOutcome.Limited, snapshot.Outcome);
        Assert.AreEqual(ManagedDesktopMappingStatus.Limited, snapshot.Mappings[0].Status);
        Assert.AreEqual(
            "managed_desktops.enumeration_unavailable",
            snapshot.Mappings[0].Code);
        Assert.HasCount(1, snapshot.Issues);
        Assert.AreEqual(0, provider.CreateCallCount);
        Assert.AreEqual(0, store.SaveCallCount);
    }

    [TestMethod]
    public async Task MetadataPreflightFailure_PreventsDesktopCreation()
    {
        FakeTopologyProvider provider = FakeTopologyProvider.Full();
        InMemoryBindingStore store = new()
        {
            FailSavesStartingAt = 1,
        };
        using ManagedDesktopReconciliationService service = CreateService(
            CreateConfiguration(
                new ManagedDesktopDefinition("code", "Code", 1, true)),
            provider,
            store);

        ManagedDesktopReconciliationSnapshot snapshot =
            await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Startup);

        Assert.AreEqual(ManagedDesktopReconciliationOutcome.Failed, snapshot.Outcome);
        Assert.AreEqual(ManagedDesktopMappingStatus.Failed, snapshot.Mappings[0].Status);
        Assert.AreEqual("managed_desktops.metadata_not_writable", snapshot.Mappings[0].Code);
        Assert.AreEqual(0, provider.CreateCallCount);
    }

    [TestMethod]
    public async Task FailedPostCreationCheckpoint_DoesNotDuplicateDesktopInSameSession()
    {
        FakeTopologyProvider provider = FakeTopologyProvider.Full();
        InMemoryBindingStore store = new()
        {
            FailSavesStartingAt = 2,
        };
        using ManagedDesktopReconciliationService service = CreateService(
            CreateConfiguration(
                new ManagedDesktopDefinition("code", "Code", 1, true)),
            provider,
            store);

        ManagedDesktopReconciliationSnapshot first =
            await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Startup);
        ManagedDesktopReconciliationSnapshot second =
            await service.ReconcileAsync(
                ManagedDesktopReconciliationTrigger.TopologyChanged);

        Assert.AreEqual(ManagedDesktopMappingStatus.Created, first.Mappings[0].Status);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.ReusedPersistedBinding,
            second.Mappings[0].Status);
        Assert.AreEqual(first.Mappings[0].RuntimeDesktopId, second.Mappings[0].RuntimeDesktopId);
        Assert.AreEqual(1, provider.CreateCallCount);
    }

    [TestMethod]
    public async Task CreationCheckpoint_PreservesBindingsThatHaveNotBeenProcessedYet()
    {
        Guid webId = Guid.NewGuid();
        FakeTopologyProvider provider = FakeTopologyProvider.Full(
            new VirtualDesktopDescriptor(webId, DisplayName: null, 0, true));
        InMemoryBindingStore store = new(
            new ManagedDesktopBinding("web", webId))
        {
            FailSavesStartingAt = 3,
        };
        using ManagedDesktopReconciliationService service = CreateService(
            CreateConfiguration(
                new ManagedDesktopDefinition("code", "Code", 1, true),
                new ManagedDesktopDefinition("web", "Web", 2, true)),
            provider,
            store);

        ManagedDesktopReconciliationSnapshot snapshot =
            await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Startup);

        Assert.AreEqual(ManagedDesktopReconciliationOutcome.Partial, snapshot.Outcome);
        Assert.AreEqual(1, provider.CreateCallCount);
        CollectionAssert.AreEquivalent(
            new[] { "code", "web" },
            store.Current.Select(static binding => binding.SemanticKey).ToArray());
    }

    [TestMethod]
    public async Task Publication_IsAtomicSubscriberSafeAndCancellationPreservesPreviousSnapshot()
    {
        Guid desktopId = Guid.NewGuid();
        FakeTopologyProvider provider = FakeTopologyProvider.Full(
            new VirtualDesktopDescriptor(desktopId, "Code", 0, true));
        using ManagedDesktopReconciliationService service = CreateService(
            CreateConfiguration(
                new ManagedDesktopDefinition("code", "Code", 1, true)),
            provider,
            new InMemoryBindingStore());
        int successfulNotifications = 0;
        service.Changed += static (_, _) => throw new InvalidOperationException("UI failure");
        service.Changed += (_, _) => successfulNotifications++;

        ManagedDesktopReconciliationSnapshot published =
            await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Startup);
        using CancellationTokenSource cancellation = new();
        cancellation.Cancel();

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => service.ReconcileAsync(
                ManagedDesktopReconciliationTrigger.Manual,
                cancellation.Token));

        Assert.AreSame(published, service.Current);
        Assert.AreEqual(1, successfulNotifications);
    }

    private static ManagedDesktopReconciliationService CreateService(
        ConfigurationDocument configuration,
        FakeTopologyProvider provider,
        InMemoryBindingStore store) =>
        new(
            new ActiveConfigurationService(configuration),
            provider,
            store,
            new FixedTimeProvider(ExpectedTime));

    private static ConfigurationDocument CreateConfiguration(
        params ManagedDesktopDefinition[] definitions) =>
        new(
            SchemaVersion: 1,
            definitions.ToImmutableArray(),
            [],
            new BehaviorSettings(false, false, false));

    private sealed class ActiveConfigurationService(
        ConfigurationDocument configuration) : IConfigurationService
    {
        public ConfigurationState CurrentState { get; } = new(
            configuration,
            configuration,
            [],
            ExpectedTime);

        public Task<ConfigurationState> LoadAsync(
            CancellationToken cancellationToken = default) =>
            Task.FromResult(CurrentState);

        public Task<ConfigurationSaveResult> SaveCandidateAsync(
            ConfigurationDocument candidate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class InMemoryBindingStore(
        params ManagedDesktopBinding[] bindings) : IManagedDesktopBindingStore
    {
        private IReadOnlyList<ManagedDesktopBinding> current = bindings;

        public int SaveCallCount { get; private set; }

        public int? FailSavesStartingAt { get; init; }

        public IReadOnlyList<ManagedDesktopBinding> Current => current;

        public Task<IReadOnlyList<ManagedDesktopBinding>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(current);
        }

        public Task SaveAsync(
            IReadOnlyCollection<ManagedDesktopBinding> newBindings,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SaveCallCount++;
            if (FailSavesStartingAt is int failureStart &&
                SaveCallCount >= failureStart)
            {
                throw new IOException("The metadata volume is read-only.");
            }

            current = newBindings.ToArray();
            return Task.CompletedTask;
        }
    }

    private sealed class FakeTopologyProvider : IDesktopTopologyProvider
    {
        private readonly List<VirtualDesktopDescriptor> desktops;

        private FakeTopologyProvider(
            DesktopTopologyProviderIdentity identity,
            VirtualDesktopCapabilities capabilities,
            IEnumerable<VirtualDesktopDescriptor> desktops)
        {
            Identity = identity;
            Capabilities = capabilities;
            this.desktops = desktops.ToList();
        }

        public DesktopTopologyProviderIdentity Identity { get; }

        public VirtualDesktopCapabilities Capabilities { get; }

        public IReadOnlyList<VirtualDesktopDescriptor> Desktops => desktops;

        public int CreateCallCount { get; private set; }

        public List<Guid> CreatedIds { get; } = [];

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public static FakeTopologyProvider Full(
            params VirtualDesktopDescriptor[] desktops) =>
            new(
                new DesktopTopologyProviderIdentity(
                    "test.full",
                    "Test Full",
                    "1",
                    DesktopTopologyProviderMode.Full,
                    UsesPrivateApis: false),
                new VirtualDesktopCapabilities(
                    true,
                    true,
                    true,
                    true,
                    true,
                    false,
                    true),
                desktops);

        public static FakeTopologyProvider Limited() =>
            new(
                new DesktopTopologyProviderIdentity(
                    "test.limited",
                    "Test Limited",
                    "1",
                    DesktopTopologyProviderMode.Limited,
                    UsesPrivateApis: false),
                VirtualDesktopCapabilities.DocumentedLimited,
                []);

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
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    desktops.FirstOrDefault(static item => item.IsCurrent)?.Id ??
                    Guid.Empty));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CreateCallCount++;
            Guid id = Guid.NewGuid();
            CreatedIds.Add(id);
            desktops.Add(new VirtualDesktopDescriptor(
                id,
                DisplayName: null,
                desktops.Count,
                IsCurrent: false));
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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

/// <summary>
/// The naming pass, proved against a fake without touching a real desktop.
/// </summary>
/// <remarks>
/// Nothing here loads the native bridge, and nothing here can name a Windows
/// desktop. The behavior that genuinely needs one — that the shell stores the
/// name, and that the vtable slot is the one this build believes it is — is in
/// <c>docs/manual-tests/desktop-topology-recovery.md</c>, because there is no
/// way to prove it in an automated test without changing the machine the test
/// runs on.
/// </remarks>
[TestClass]
public sealed class ManagedDesktopNamingTests
{
    private static readonly Guid CodeDesktop =
        Guid.Parse("11111111-1111-1111-1111-111111111111");

    [TestMethod]
    public async Task AnUnnamedBoundDesktop_IsNamedAndConfirmed()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        ManagedDesktopNamingService service = CreateService(provider);

        ManagedDesktopNamingPass pass = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null)));

        ManagedDesktopNamingResult result = pass.Results.Single();
        Assert.AreEqual(ManagedDesktopNamingOutcome.Applied, result.Outcome);
        Assert.AreEqual("Code", provider.NameOf(CodeDesktop));
        Assert.AreEqual(1, provider.RenameCallCount);
    }

    [TestMethod]
    public async Task ADesktopThatAlreadyMatches_IsLeftAlone()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, "Code", 0, IsCurrent: true));
        ManagedDesktopNamingService service = CreateService(provider);

        ManagedDesktopNamingPass pass = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, "Code")));

        Assert.AreEqual(
            ManagedDesktopNamingOutcome.AlreadyNamed,
            pass.Results.Single().Outcome);
        Assert.AreEqual(0, provider.RenameCallCount);
    }

    /// <summary>
    /// The convergence that makes a rename fight terminate on its own.
    /// </summary>
    /// <remarks>
    /// Writing a name raises a Windows name-changed notification, which
    /// reconciles, which runs another naming pass. If that second pass rewrote
    /// the name, the two would never settle. It must see its own work and stop.
    /// </remarks>
    [TestMethod]
    public async Task ASecondPassAfterNaming_WritesNothingFurther()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        ManagedDesktopNamingService service = CreateService(provider);

        _ = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null)));
        ManagedDesktopNamingPass second = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, provider.NameOf(CodeDesktop))));

        Assert.AreEqual(
            ManagedDesktopNamingOutcome.AlreadyNamed,
            second.Results.Single().Outcome);
        Assert.AreEqual(1, provider.RenameCallCount);
    }

    [TestMethod]
    public async Task AUserRenamingItBack_IsAnsweredAndThenConcededTo()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        ManagedDesktopNamingService service = CreateService(provider);

        _ = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null)));

        List<ManagedDesktopNamingOutcome> outcomes = [];
        for (int attempt = 0; attempt < 4; attempt++)
        {
            // The user renames it in Task View, and the next topology
            // notification brings the pass back around.
            provider.SimulateUserRenamed(CodeDesktop, "Work");
            ManagedDesktopNamingPass pass = await service.ApplyAsync(
                Snapshot(Bound("code", "Code", CodeDesktop, "Work")));
            outcomes.Add(pass.Results.Single().Outcome);
        }

        CollectionAssert.AreEqual(
            new[]
            {
                ManagedDesktopNamingOutcome.Applied,
                ManagedDesktopNamingOutcome.Applied,
                ManagedDesktopNamingOutcome.Applied,
                ManagedDesktopNamingOutcome.Conceded,
            },
            outcomes);

        // Conceding means the user's name survives, not that it is overwritten
        // one last time.
        Assert.AreEqual("Work", provider.NameOf(CodeDesktop));
    }

    /// <summary>
    /// The guard against the one loop that feeds itself.
    /// </summary>
    /// <remarks>
    /// A shell that accepts a name and stores a different one would otherwise be
    /// rewritten on every topology notification for as long as the application
    /// runs, with no user involved at all.
    /// </remarks>
    [TestMethod]
    public async Task ANameWindowsDoesNotStore_StopsTheKeyInsteadOfRetrying()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        provider.TruncateNamesTo = 2;
        ManagedDesktopNamingService service = CreateService(provider);

        ManagedDesktopNamingPass first = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null)));
        ManagedDesktopNamingPass second = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, provider.NameOf(CodeDesktop))));
        ManagedDesktopNamingPass third = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, provider.NameOf(CodeDesktop))));

        Assert.AreEqual(
            ManagedDesktopNamingOutcome.NotStored,
            first.Results.Single().Outcome);
        Assert.AreEqual(
            ManagedDesktopNamingOutcome.NotStored,
            second.Results.Single().Outcome);
        Assert.AreEqual(
            ManagedDesktopNamingOutcome.NotStored,
            third.Results.Single().Outcome);

        // One attempt, ever. The point of the guard is that it does not keep
        // trying something that cannot converge.
        Assert.AreEqual(1, provider.RenameCallCount);
    }

    [TestMethod]
    public async Task AProviderThatCannotName_TouchesNothingAndSaysSo()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CannotName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        ManagedDesktopNamingService service = CreateService(provider);

        ManagedDesktopNamingPass pass = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null)));

        Assert.AreEqual(
            ManagedDesktopNamingOutcome.Unavailable,
            pass.Results.Single().Outcome);
        Assert.AreEqual(0, provider.RenameCallCount);
    }

    [TestMethod]
    public async Task NamingSwitchedOff_TouchesNothingAndSaysSo()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        ManagedDesktopNamingService service = CreateService(
            provider,
            nameWindowsDesktops: false);

        ManagedDesktopNamingPass pass = await service.ApplyAsync(
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null)));

        Assert.AreEqual(
            ManagedDesktopNamingOutcome.Unavailable,
            pass.Results.Single().Outcome);
        Assert.AreEqual(0, provider.RenameCallCount);
    }

    /// <summary>
    /// An unbound definition is not a desktop, so there is nothing to name.
    /// </summary>
    [TestMethod]
    public async Task AnUnboundDefinition_IsNotNamed()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName();
        ManagedDesktopNamingService service = CreateService(provider);

        ManagedDesktopNamingPass pass = await service.ApplyAsync(
            Snapshot(Unbound("code", "Code")));

        Assert.IsEmpty(pass.Results);
        Assert.AreEqual(0, provider.RenameCallCount);
    }

    /// <summary>
    /// Naming stays out of the way of the thing that actually matters.
    /// </summary>
    [TestMethod]
    public async Task ARefusedName_LeavesTheBindingAlone()
    {
        NamingTopologyProvider provider = NamingTopologyProvider.CanName(
            new VirtualDesktopDescriptor(CodeDesktop, null, 0, IsCurrent: true));
        provider.RenameOverride = DesktopTopologyProviderResult.Failed(
            "native.desktop_rename_failed",
            "The Windows Shell rejected the virtual-desktop name.");
        ManagedDesktopNamingService service = CreateService(provider);
        ManagedDesktopReconciliationSnapshot snapshot =
            Snapshot(Bound("code", "Code", CodeDesktop, runtimeName: null));

        ManagedDesktopNamingPass pass = await service.ApplyAsync(snapshot);

        Assert.AreEqual(
            ManagedDesktopNamingOutcome.Failed,
            pass.Results.Single().Outcome);
        Assert.IsTrue(snapshot.Mappings.Single().IsBound);
        Assert.AreEqual(CodeDesktop, snapshot.Mappings.Single().RuntimeDesktopId);
    }

    private static ManagedDesktopNamingService CreateService(
        NamingTopologyProvider provider,
        bool nameWindowsDesktops = true) =>
        new(
            provider,
            new NamingConfigurationService(nameWindowsDesktops),
            TimeProvider.System);

    private static ManagedDesktopReconciliationSnapshot Snapshot(
        params ManagedDesktopRuntimeMapping[] mappings) =>
        new(
            DateTimeOffset.UnixEpoch,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [.. mappings],
            []);

    private static ManagedDesktopRuntimeMapping Bound(
        string semanticKey,
        string displayName,
        Guid runtimeDesktopId,
        string? runtimeName) =>
        new(
            semanticKey,
            displayName,
            PreferredOrder: 1,
            RecreateWhenMissing: true,
            runtimeDesktopId,
            runtimeName,
            RuntimePosition: 0,
            ManagedDesktopMappingStatus.ReusedPersistedBinding,
            [],
            "managed_desktops.persisted_binding_reused",
            "Bound for the purposes of this test.");

    private static ManagedDesktopRuntimeMapping Unbound(
        string semanticKey,
        string displayName) =>
        new(
            semanticKey,
            displayName,
            PreferredOrder: 1,
            RecreateWhenMissing: true,
            RuntimeDesktopId: null,
            RuntimeDisplayName: null,
            RuntimePosition: null,
            ManagedDesktopMappingStatus.Missing,
            [],
            "managed_desktops.missing_recreation_disabled",
            "Unbound for the purposes of this test.");

    /// <summary>
    /// A topology provider that can name desktops in memory.
    /// </summary>
    /// <remarks>
    /// <see cref="TruncateNamesTo"/> is how a shell that stores a name
    /// differently from how it was given is reproduced. No documented limit
    /// exists for the private naming call, which is exactly why the production
    /// code reads the name back rather than trusting the success code.
    /// </remarks>
    private sealed class NamingTopologyProvider : IDesktopTopologyProvider
    {
        private readonly List<VirtualDesktopDescriptor> desktops;

        private NamingTopologyProvider(
            VirtualDesktopCapabilities capabilities,
            IEnumerable<VirtualDesktopDescriptor> desktops)
        {
            Capabilities = capabilities;
            this.desktops = [.. desktops];
        }

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.full",
            "Test Full",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: false);

        public VirtualDesktopCapabilities Capabilities { get; }

        public int RenameCallCount { get; private set; }

        public int? TruncateNamesTo { get; set; }

        public DesktopTopologyProviderResult? RenameOverride { get; set; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public static NamingTopologyProvider CanName(
            params VirtualDesktopDescriptor[] desktops) =>
            new(FullCapabilities(canRename: true), desktops);

        public static NamingTopologyProvider CannotName(
            params VirtualDesktopDescriptor[] desktops) =>
            new(FullCapabilities(canRename: false), desktops);

        public string? NameOf(Guid desktopId) =>
            desktops.SingleOrDefault(desktop => desktop.Id == desktopId)?.DisplayName;

        /// <summary>
        /// Renames a desktop the way a user typing in Task View would.
        /// </summary>
        public void SimulateUserRenamed(Guid desktopId, string name) =>
            Replace(desktopId, name);

        public ValueTask<DesktopTopologyProviderResult> RenameDesktopAsync(
            Guid desktopId,
            string displayName,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RenameCallCount++;

            if (RenameOverride is DesktopTopologyProviderResult failure)
            {
                return ValueTask.FromResult(failure);
            }

            string stored = TruncateNamesTo is int limit && displayName.Length > limit
                ? displayName[..limit]
                : displayName;
            Replace(desktopId, stored);
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>
                    .Succeeded([.. desktops]));
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    desktops.FirstOrDefault(static desktop => desktop.IsCurrent)?.Id ??
                    Guid.Empty));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "A naming pass must never create a desktop.");

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "A naming pass must never switch desktops.");

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        private static VirtualDesktopCapabilities FullCapabilities(bool canRename) =>
            new(
                CanGetWindowDesktopId: true,
                CanMoveWindowToDesktop: true,
                CanEnumerateDesktops: true,
                CanGetCurrentDesktop: true,
                CanCreateDesktop: true,
                CanSwitchDesktop: true,
                CanObserveTopologyChanges: true,
                CanRenameDesktop: canRename);

        private void Replace(Guid desktopId, string name)
        {
            int index = desktops.FindIndex(desktop => desktop.Id == desktopId);
            if (index >= 0)
            {
                desktops[index] = desktops[index] with { DisplayName = name };
            }
        }
    }

    private sealed class NamingConfigurationService : IConfigurationService
    {
        public NamingConfigurationService(bool nameWindowsDesktops)
        {
            ConfigurationDocument document = ConfigurationDefaults.Create();
            document = document with
            {
                Behavior = document.Behavior with
                {
                    NameWindowsDesktops = nameWindowsDesktops,
                },
            };
            CurrentState = new ConfigurationState(
                document,
                document,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch);
        }

        public ConfigurationState CurrentState { get; }

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
            return Task.FromResult(new ConfigurationSaveResult(true, CurrentState));
        }
    }
}

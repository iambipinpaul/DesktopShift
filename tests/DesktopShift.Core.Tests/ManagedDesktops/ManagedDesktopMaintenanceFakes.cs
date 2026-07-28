using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Tests.ManagedDesktops;

/// <summary>
/// A topology provider that records every mutating call it receives.
/// </summary>
/// <remarks>
/// <see cref="MutatingCallCount"/> exists so a test can assert that an
/// operation touched no real desktop at all. The interface has no
/// desktop-removal operation, so the strongest available proof that removing a
/// definition leaves a Windows desktop alone is that the inventory is unchanged
/// and no mutating call was made.
/// </remarks>
internal sealed class MaintenanceTopologyProvider : IDesktopTopologyProvider
{
    private readonly List<VirtualDesktopDescriptor> desktops;

    private MaintenanceTopologyProvider(
        DesktopTopologyProviderIdentity identity,
        VirtualDesktopCapabilities capabilities,
        IEnumerable<VirtualDesktopDescriptor> desktops)
    {
        Identity = identity;
        Capabilities = capabilities;
        this.desktops = [.. desktops];
    }

    public DesktopTopologyProviderIdentity Identity { get; }

    public VirtualDesktopCapabilities Capabilities { get; }

    public IReadOnlyList<VirtualDesktopDescriptor> Desktops => desktops;

    public int CreateCallCount { get; private set; }

    public int SwitchCallCount { get; private set; }

    public int MutatingCallCount => CreateCallCount + SwitchCallCount;

    public List<Guid> CreatedIds { get; } = [];

    public DesktopTopologyProviderResult<Guid>? CreateOverride { get; set; }

    public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
    {
        add { }
        remove { }
    }

    public static MaintenanceTopologyProvider Full(
        params VirtualDesktopDescriptor[] desktops) =>
        new(
            new DesktopTopologyProviderIdentity(
                "test.full",
                "Test Full",
                "1",
                DesktopTopologyProviderMode.Full,
                UsesPrivateApis: false),
            new VirtualDesktopCapabilities(
                CanGetWindowDesktopId: true,
                CanMoveWindowToDesktop: true,
                CanEnumerateDesktops: true,
                CanGetCurrentDesktop: true,
                CanCreateDesktop: true,
                CanSwitchDesktop: true,
                CanObserveTopologyChanges: true),
            desktops);

    public static MaintenanceTopologyProvider Limited() =>
        new(
            new DesktopTopologyProviderIdentity(
                "windows.documented.limited",
                "Windows documented API",
                "1",
                DesktopTopologyProviderMode.Limited,
                UsesPrivateApis: false),
            VirtualDesktopCapabilities.DocumentedLimited,
            []);

    public DesktopTopologyProviderState CreateState(
        DesktopTopologyProviderAvailability availability =
            DesktopTopologyProviderAvailability.Ready,
        string explanation = "Ready.") =>
        new(Identity, Capabilities, availability, explanation);

    /// <summary>
    /// Removes a desktop the way a user deleting one in Task View would.
    /// </summary>
    /// <remarks>
    /// This is a test affordance on the fake only. Nothing in the production
    /// provider interface can delete a desktop.
    /// </remarks>
    public void SimulateUserDeleted(Guid desktopId)
    {
        _ = desktops.RemoveAll(desktop => desktop.Id == desktopId);
    }

    public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
        WindowsBuildInfo build,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

    public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!Capabilities.CanEnumerateDesktops)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Unsupported(
                    "desktop_topology.unsupported_in_limited_mode",
                    "The documented Windows virtual-desktop API does not expose this topology operation."));
        }

        return ValueTask.FromResult(
            DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                [.. desktops]));
    }

    public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            DesktopTopologyProviderResult<Guid>.Succeeded(
                desktops.FirstOrDefault(static desktop => desktop.IsCurrent)?.Id ??
                Guid.Empty));

    public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        CreateCallCount++;

        if (CreateOverride is DesktopTopologyProviderResult<Guid> configured)
        {
            return ValueTask.FromResult(configured);
        }

        Guid id = Guid.NewGuid();
        CreatedIds.Add(id);
        desktops.Add(new VirtualDesktopDescriptor(
            id,
            DisplayName: null,
            desktops.Count,
            IsCurrent: false));
        return ValueTask.FromResult(DesktopTopologyProviderResult<Guid>.Succeeded(id));
    }

    public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
        Guid desktopId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        SwitchCallCount++;
        return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
}

internal sealed class MaintenanceBindingStore(
    params ManagedDesktopBinding[] bindings) : IManagedDesktopBindingStore
{
    private IReadOnlyList<ManagedDesktopBinding> current = bindings;

    public IReadOnlyList<ManagedDesktopBinding> Current => current;

    public int SaveCallCount { get; private set; }

    public bool FailSaves { get; set; }

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

        if (FailSaves)
        {
            throw new IOException("The metadata volume is read-only.");
        }

        current = [.. newBindings];
        return Task.CompletedTask;
    }
}

/// <summary>
/// A configuration service with the same candidate-and-active semantics as the
/// real one: every candidate is stored, and only a candidate that passes
/// <see cref="Validate"/> is promoted to active.
/// </summary>
internal sealed class MaintenanceConfigurationService : IConfigurationService
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 28, 12, 30, 0, TimeSpan.Zero);

    private ConfigurationState state;

    public MaintenanceConfigurationService(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);
        state = new ConfigurationState(document, document, [], ObservedAt);
    }

    public Func<ConfigurationDocument, ImmutableArray<ConfigurationValidationIssue>>? Validate
    {
        get;
        set;
    }

    public int SaveCallCount { get; private set; }

    public ConfigurationDocument? LastSavedCandidate { get; private set; }

    public ConfigurationState CurrentState => state;

    public Task<ConfigurationState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(state);
    }

    public Task<ConfigurationSaveResult> SaveCandidateAsync(
        ConfigurationDocument candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);
        cancellationToken.ThrowIfCancellationRequested();

        SaveCallCount++;
        LastSavedCandidate = candidate;

        ImmutableArray<ConfigurationValidationIssue> issues =
            Validate?.Invoke(candidate) ?? [];
        bool accepted = issues.IsEmpty;
        state = new ConfigurationState(
            candidate,
            accepted ? candidate : state.Active,
            issues,
            ObservedAt);

        return Task.FromResult(new ConfigurationSaveResult(accepted, state));
    }

    public void ReplaceIssues(ImmutableArray<ConfigurationValidationIssue> issues)
    {
        state = state with { Issues = issues };
    }
}

internal sealed class MaintenanceTimeProvider(DateTimeOffset utcNow) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => utcNow;
}

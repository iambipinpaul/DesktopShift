using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Performance;

namespace DesktopShift.Core.ManagedDesktops;

public sealed class ManagedDesktopReconciliationService :
    IManagedDesktopReconciliationService,
    IDisposable
{
    private readonly IConfigurationService configurationService;
    private readonly IDesktopTopologyProvider topologyProvider;
    private readonly IManagedDesktopBindingStore bindingStore;
    private readonly TimeProvider timeProvider;
    private readonly IManagedDesktopRecreationGate? recreationGate;
    private readonly IPerformanceRecorder? performanceRecorder;
    private readonly SemaphoreSlim gate = new(1, 1);
    private readonly Dictionary<string, ManagedDesktopBinding> sessionBindings =
        new(StringComparer.OrdinalIgnoreCase);
    private ManagedDesktopReconciliationSnapshot current =
        ManagedDesktopReconciliationSnapshot.NotRun;
    private bool disposed;

    /// <param name="recreationGate">
    /// The bound on recreations nobody asked for, or null to leave recreation
    /// ungated. The gate is consulted only for
    /// <see cref="ManagedDesktopReconciliationTrigger.TopologyChanged"/> passes:
    /// startup, a saved configuration, and an explicit user action are all
    /// intent, and intent is never rate limited.
    /// </param>
    /// <param name="performanceRecorder">
    /// Where a created desktop reports how long Windows took to make it, or null
    /// to leave creation unmeasured. Creation is timed separately from everything
    /// else because it is the one operation the startup pass is meant to have
    /// already paid for — a creation counted while windows are being assigned
    /// means one landed on a window's critical path.
    /// </param>
    public ManagedDesktopReconciliationService(
        IConfigurationService configurationService,
        IDesktopTopologyProvider topologyProvider,
        IManagedDesktopBindingStore bindingStore,
        TimeProvider timeProvider,
        IManagedDesktopRecreationGate? recreationGate = null,
        IPerformanceRecorder? performanceRecorder = null)
    {
        this.configurationService =
            configurationService ?? throw new ArgumentNullException(nameof(configurationService));
        this.topologyProvider =
            topologyProvider ?? throw new ArgumentNullException(nameof(topologyProvider));
        this.bindingStore =
            bindingStore ?? throw new ArgumentNullException(nameof(bindingStore));
        this.timeProvider =
            timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        this.recreationGate = recreationGate;
        this.performanceRecorder = performanceRecorder;
    }

    public ManagedDesktopReconciliationSnapshot Current =>
        Volatile.Read(ref current);

    public event EventHandler<ManagedDesktopReconciliationChangedEventArgs>? Changed;

    public async Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
        ManagedDesktopReconciliationTrigger trigger,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);

        try
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            ConfigurationDocument? configuration =
                configurationService.CurrentState.Active;
            ManagedDesktopReconciliationSnapshot snapshot =
                configuration is null
                    ? CreateNoActiveConfigurationSnapshot(trigger)
                    : await ReconcileDefinitionsAsync(
                        configuration.ManagedDesktops,
                        trigger,
                        cancellationToken).ConfigureAwait(false);

            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref current, snapshot);
            PublishChanged(snapshot);
            return snapshot;
        }
        finally
        {
            gate.Release();
        }
    }

    public void Dispose()
    {
        disposed = true;
        gate.Dispose();
    }

    private async Task<ManagedDesktopReconciliationSnapshot> ReconcileDefinitionsAsync(
        ImmutableArray<ManagedDesktopDefinition> definitions,
        ManagedDesktopReconciliationTrigger trigger,
        CancellationToken cancellationToken)
    {
        DesktopTopologyProviderIdentity providerIdentity = topologyProvider.Identity;
        if (!topologyProvider.Capabilities.CanEnumerateDesktops)
        {
            return CreateAllLimitedSnapshot(
                definitions,
                trigger,
                providerIdentity,
                "managed_desktops.enumeration_unavailable",
                "The selected provider cannot enumerate virtual desktops, so semantic bindings were not changed.");
        }

        IReadOnlyList<ManagedDesktopBinding> storedBindings;
        try
        {
            storedBindings = await bindingStore.LoadAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            return CreateAllFailedSnapshot(
                definitions,
                trigger,
                providerIdentity,
                "managed_desktops.metadata_unreadable",
                $"Local managed-desktop binding metadata could not be read: {exception.Message}");
        }

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>
            inventoryResult = await topologyProvider
                .EnumerateDesktopsAsync(cancellationToken)
                .ConfigureAwait(false);
        if (!inventoryResult.IsSuccess || inventoryResult.Value is null)
        {
            DesktopTopologyProviderError error =
                inventoryResult.Error ??
                new DesktopTopologyProviderError(
                    "managed_desktops.inventory_unavailable",
                    "The virtual desktop inventory is unavailable.");
            return inventoryResult.Outcome == DesktopTopologyResultOutcome.Unsupported
                ? CreateAllLimitedSnapshot(
                    definitions,
                    trigger,
                    providerIdentity,
                    error.Code,
                    error.Message)
                : CreateAllFailedSnapshot(
                    definitions,
                    trigger,
                    providerIdentity,
                    error.Code,
                    error.Message);
        }

        List<VirtualDesktopDescriptor> inventory =
            inventoryResult.Value.OrderBy(static item => item.Position).ToList();
        Dictionary<Guid, VirtualDesktopDescriptor> inventoryById =
            inventory.ToDictionary(static item => item.Id);
        Dictionary<string, ManagedDesktopBinding> bindingByKey;

        try
        {
            bindingByKey = storedBindings.ToDictionary(
                static item => item.SemanticKey,
                StringComparer.OrdinalIgnoreCase);
        }
        catch (ArgumentException)
        {
            return CreateAllFailedSnapshot(
                definitions,
                trigger,
                providerIdentity,
                "managed_desktops.metadata_duplicate_key",
                "Local managed-desktop binding metadata contains duplicate semantic keys.");
        }

        // A session binding outranks the persisted one, but only while the
        // desktop it names still exists. Once the user deletes that desktop the
        // binding is dead, and keeping it would let it outrank a fresher
        // persisted binding written since — an explicit recreation, for
        // instance — and send this pass off to create a second desktop for a
        // key that already has one.
        List<string> deadSessionKeys = [];
        foreach ((string semanticKey, ManagedDesktopBinding binding) in sessionBindings)
        {
            if (inventoryById.ContainsKey(binding.RuntimeDesktopId))
            {
                bindingByKey[semanticKey] = binding;
                continue;
            }

            deadSessionKeys.Add(semanticKey);
        }

        foreach (string semanticKey in deadSessionKeys)
        {
            sessionBindings.Remove(semanticKey);
        }

        Dictionary<string, ManagedDesktopBinding> checkpointBindings =
            new(bindingByKey, StringComparer.OrdinalIgnoreCase);
        bool metadataWritable = true;
        string? metadataWriteFailure = null;
        try
        {
            await bindingStore.SaveAsync(
                checkpointBindings.Values.ToArray(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            metadataWritable = false;
            metadataWriteFailure = exception.Message;
        }

        HashSet<Guid> conflictingBindingIds = bindingByKey
            .Where(pair => definitions.Any(
                definition => KeysEqual(definition.SemanticKey, pair.Key)))
            .GroupBy(static pair => pair.Value.RuntimeDesktopId)
            .Where(static group => group.Count() > 1)
            .Select(static group => group.Key)
            .ToHashSet();

        HashSet<Guid> claimedIds = [];
        ImmutableArray<ManagedDesktopRuntimeMapping>.Builder mappings =
            ImmutableArray.CreateBuilder<ManagedDesktopRuntimeMapping>(
                definitions.Length);
        ImmutableArray<ManagedDesktopBinding>.Builder resolvedBindings =
            ImmutableArray.CreateBuilder<ManagedDesktopBinding>(
                definitions.Length);

        foreach (ManagedDesktopDefinition definition in definitions
            .OrderBy(static item => item.PreferredOrder)
            .ThenBy(static item => item.SemanticKey, StringComparer.OrdinalIgnoreCase))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (bindingByKey.TryGetValue(definition.SemanticKey, out ManagedDesktopBinding? binding) &&
                conflictingBindingIds.Contains(binding.RuntimeDesktopId))
            {
                mappings.Add(CreateMapping(
                    definition,
                    ManagedDesktopMappingStatus.Ambiguous,
                    "managed_desktops.metadata_runtime_collision",
                    "Multiple semantic keys claim the same persisted runtime desktop.",
                    candidateDesktopIds: [binding.RuntimeDesktopId]));
                continue;
            }

            if (binding is not null &&
                inventoryById.TryGetValue(binding.RuntimeDesktopId, out VirtualDesktopDescriptor? persistedDesktop) &&
                claimedIds.Add(persistedDesktop.Id))
            {
                mappings.Add(CreateBoundMapping(
                    definition,
                    persistedDesktop,
                    ManagedDesktopMappingStatus.ReusedPersistedBinding,
                    "managed_desktops.persisted_binding_reused",
                    "The durable semantic key was resolved through local app-owned binding metadata."));
                resolvedBindings.Add(binding);
                sessionBindings[definition.SemanticKey] = binding;
                checkpointBindings[definition.SemanticKey] = binding;
                continue;
            }

            VirtualDesktopDescriptor[] compatible = inventory
                .Where(item =>
                    !claimedIds.Contains(item.Id) &&
                    NamesEqual(item.DisplayName, definition.DisplayName))
                .ToArray();

            if (compatible.Length > 1)
            {
                mappings.Add(CreateMapping(
                    definition,
                    ManagedDesktopMappingStatus.Ambiguous,
                    "managed_desktops.compatible_match_ambiguous",
                    "More than one unclaimed runtime desktop has the configured display name; no desktop was selected or created.",
                    candidateDesktopIds: compatible.Select(static item => item.Id)));
                continue;
            }

            if (compatible.Length == 1)
            {
                VirtualDesktopDescriptor desktop = compatible[0];
                claimedIds.Add(desktop.Id);
                mappings.Add(CreateBoundMapping(
                    definition,
                    desktop,
                    ManagedDesktopMappingStatus.ReusedCompatibleDesktop,
                    "managed_desktops.compatible_desktop_reused",
                    "A unique unclaimed runtime desktop with the configured display name was reused."));
                ManagedDesktopBinding compatibleBinding =
                    new(definition.SemanticKey, desktop.Id);
                resolvedBindings.Add(compatibleBinding);
                sessionBindings[definition.SemanticKey] = compatibleBinding;
                checkpointBindings[definition.SemanticKey] = compatibleBinding;
                continue;
            }

            if (!definition.RecreateWhenMissing)
            {
                mappings.Add(CreateMapping(
                    definition,
                    ManagedDesktopMappingStatus.Missing,
                    "managed_desktops.missing_recreation_disabled",
                    "No compatible runtime desktop exists and recreation is disabled."));
                continue;
            }

            if (!topologyProvider.Capabilities.CanCreateDesktop)
            {
                mappings.Add(CreateMapping(
                    definition,
                    ManagedDesktopMappingStatus.Limited,
                    "managed_desktops.creation_unavailable",
                    "No compatible runtime desktop exists and the selected provider cannot create one."));
                continue;
            }

            // Policy and validated capability both said yes. The last question
            // is whether this key is in the middle of a fight with the user,
            // and only a topology-driven pass can be.
            if (trigger == ManagedDesktopReconciliationTrigger.TopologyChanged &&
                recreationGate is not null)
            {
                ManagedDesktopRecreationDecision decision =
                    recreationGate.Evaluate(definition.SemanticKey);
                if (!decision.IsAllowed)
                {
                    mappings.Add(CreateMapping(
                        definition,
                        ManagedDesktopMappingStatus.Missing,
                        decision.Code,
                        decision.Reason));
                    continue;
                }
            }

            if (!metadataWritable)
            {
                mappings.Add(CreateMapping(
                    definition,
                    ManagedDesktopMappingStatus.Failed,
                    "managed_desktops.metadata_not_writable",
                    $"A missing desktop was not created because its local binding could not be durably recorded: {metadataWriteFailure}"));
                continue;
            }

            long creationStartedTimestamp = timeProvider.GetTimestamp();
            DesktopTopologyProviderResult<Guid> creation =
                await topologyProvider.CreateDesktopAsync(cancellationToken)
                    .ConfigureAwait(false);
            TimeSpan creationDuration =
                timeProvider.GetElapsedTime(creationStartedTimestamp);
            if (!creation.IsSuccess || creation.Value == Guid.Empty)
            {
                DesktopTopologyProviderError error =
                    creation.Error ??
                    new DesktopTopologyProviderError(
                        "managed_desktops.creation_failed",
                        "The provider did not return a valid runtime desktop ID.");
                ManagedDesktopMappingStatus status =
                    creation.Outcome == DesktopTopologyResultOutcome.Unsupported
                        ? ManagedDesktopMappingStatus.Limited
                        : ManagedDesktopMappingStatus.Failed;
                mappings.Add(CreateMapping(
                    definition,
                    status,
                    error.Code,
                    error.Message));
                continue;
            }

            // Recorded once the provider has answered with a usable desktop, so
            // the series describes how long making one takes rather than how
            // long a refusal takes.
            performanceRecorder?.RecordDesktopCreation(creationDuration);

            Guid createdId = creation.Value;
            if (!claimedIds.Add(createdId) || inventoryById.ContainsKey(createdId))
            {
                mappings.Add(CreateMapping(
                    definition,
                    ManagedDesktopMappingStatus.Failed,
                    "managed_desktops.creation_returned_existing_id",
                    "The provider returned a runtime desktop ID that was already present or claimed.",
                    candidateDesktopIds: [createdId]));
                continue;
            }

            int createdPosition =
                inventory.Count == 0
                    ? 0
                    : inventory.Max(static item => item.Position) + 1;
            VirtualDesktopDescriptor createdDesktop =
                new(createdId, DisplayName: null, createdPosition, IsCurrent: false);
            inventory.Add(createdDesktop);
            inventoryById.Add(createdId, createdDesktop);
            mappings.Add(CreateBoundMapping(
                definition,
                createdDesktop,
                ManagedDesktopMappingStatus.Created,
                "managed_desktops.desktop_created",
                "A missing managed destination was created in preferred configuration order."));
            if (trigger == ManagedDesktopReconciliationTrigger.TopologyChanged)
            {
                recreationGate?.NoteRecreated(definition.SemanticKey);
            }

            ManagedDesktopBinding createdBinding =
                new(definition.SemanticKey, createdId);
            resolvedBindings.Add(createdBinding);
            sessionBindings[definition.SemanticKey] = createdBinding;
            checkpointBindings[definition.SemanticKey] = createdBinding;

            try
            {
                await bindingStore.SaveAsync(
                    checkpointBindings.Values.ToArray(),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !cancellationToken.IsCancellationRequested)
            {
                metadataWritable = false;
                metadataWriteFailure = exception.Message;
            }
        }

        ImmutableArray<ManagedDesktopReconciliationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ManagedDesktopReconciliationIssue>();
        try
        {
            await bindingStore.SaveAsync(
                resolvedBindings.ToImmutable(),
                cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            issues.Add(
                new ManagedDesktopReconciliationIssue(
                    "managed_desktops.metadata_write_failed",
                    $"Runtime mappings were resolved, but local binding metadata could not be saved: {exception.Message}"));
        }

        ImmutableArray<ManagedDesktopRuntimeMapping> resultMappings =
            mappings.ToImmutable();
        return new ManagedDesktopReconciliationSnapshot(
            timeProvider.GetUtcNow(),
            trigger,
            providerIdentity.Id,
            providerIdentity.Mode,
            DetermineOutcome(resultMappings, issues.Count > 0),
            resultMappings,
            issues.ToImmutable());
    }

    private void PublishChanged(ManagedDesktopReconciliationSnapshot snapshot)
    {
        EventHandler<ManagedDesktopReconciliationChangedEventArgs>? handlers = Changed;
        if (handlers is null)
        {
            return;
        }

        ManagedDesktopReconciliationChangedEventArgs args = new(snapshot);
        foreach (EventHandler<ManagedDesktopReconciliationChangedEventArgs> handler
            in handlers.GetInvocationList()
                .Cast<EventHandler<ManagedDesktopReconciliationChangedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A projection subscriber must not invalidate a completed,
                // already-published reconciliation.
            }
        }
    }

    private ManagedDesktopReconciliationSnapshot CreateNoActiveConfigurationSnapshot(
        ManagedDesktopReconciliationTrigger trigger)
    {
        DesktopTopologyProviderIdentity provider = topologyProvider.Identity;
        return new ManagedDesktopReconciliationSnapshot(
            timeProvider.GetUtcNow(),
            trigger,
            provider.Id,
            provider.Mode,
            ManagedDesktopReconciliationOutcome.Limited,
            [],
            [new ManagedDesktopReconciliationIssue(
                "managed_desktops.no_active_configuration",
                "No accepted active configuration is available, so no managed desktops were reconciled.")]);
    }

    private ManagedDesktopReconciliationSnapshot CreateAllLimitedSnapshot(
        IEnumerable<ManagedDesktopDefinition> definitions,
        ManagedDesktopReconciliationTrigger trigger,
        DesktopTopologyProviderIdentity provider,
        string code,
        string explanation) =>
        CreateUniformSnapshot(
            definitions,
            trigger,
            provider,
            ManagedDesktopReconciliationOutcome.Limited,
            ManagedDesktopMappingStatus.Limited,
            code,
            explanation);

    private ManagedDesktopReconciliationSnapshot CreateAllFailedSnapshot(
        IEnumerable<ManagedDesktopDefinition> definitions,
        ManagedDesktopReconciliationTrigger trigger,
        DesktopTopologyProviderIdentity provider,
        string code,
        string explanation) =>
        CreateUniformSnapshot(
            definitions,
            trigger,
            provider,
            ManagedDesktopReconciliationOutcome.Failed,
            ManagedDesktopMappingStatus.Failed,
            code,
            explanation);

    private ManagedDesktopReconciliationSnapshot CreateUniformSnapshot(
        IEnumerable<ManagedDesktopDefinition> definitions,
        ManagedDesktopReconciliationTrigger trigger,
        DesktopTopologyProviderIdentity provider,
        ManagedDesktopReconciliationOutcome outcome,
        ManagedDesktopMappingStatus status,
        string code,
        string explanation) =>
        new(
            timeProvider.GetUtcNow(),
            trigger,
            provider.Id,
            provider.Mode,
            outcome,
            definitions
                .OrderBy(static item => item.PreferredOrder)
                .ThenBy(static item => item.SemanticKey, StringComparer.OrdinalIgnoreCase)
                .Select(item => CreateMapping(item, status, code, explanation))
                .ToImmutableArray(),
            [new ManagedDesktopReconciliationIssue(code, explanation)]);

    private static ManagedDesktopRuntimeMapping CreateBoundMapping(
        ManagedDesktopDefinition definition,
        VirtualDesktopDescriptor desktop,
        ManagedDesktopMappingStatus status,
        string code,
        string explanation) =>
        new(
            definition.SemanticKey,
            definition.DisplayName,
            definition.PreferredOrder,
            definition.RecreateWhenMissing,
            desktop.Id,
            desktop.DisplayName,
            desktop.Position,
            status,
            [],
            code,
            explanation);

    private static ManagedDesktopRuntimeMapping CreateMapping(
        ManagedDesktopDefinition definition,
        ManagedDesktopMappingStatus status,
        string code,
        string explanation,
        IEnumerable<Guid>? candidateDesktopIds = null) =>
        new(
            definition.SemanticKey,
            definition.DisplayName,
            definition.PreferredOrder,
            definition.RecreateWhenMissing,
            RuntimeDesktopId: null,
            RuntimeDisplayName: null,
            RuntimePosition: null,
            status,
            candidateDesktopIds?.ToImmutableArray() ?? [],
            code,
            explanation);

    private static ManagedDesktopReconciliationOutcome DetermineOutcome(
        ImmutableArray<ManagedDesktopRuntimeMapping> mappings,
        bool metadataWriteFailed)
    {
        bool anyBound = mappings.Any(static item => item.IsBound);
        bool anyFailed = mappings.Any(
            static item => item.Status == ManagedDesktopMappingStatus.Failed);
        bool anyLimited = mappings.Any(
            static item => item.Status == ManagedDesktopMappingStatus.Limited);
        bool anyUnresolved = mappings.Any(static item => !item.IsBound);

        if (anyFailed && !anyBound)
        {
            return ManagedDesktopReconciliationOutcome.Failed;
        }

        if (metadataWriteFailed)
        {
            return ManagedDesktopReconciliationOutcome.Partial;
        }

        if (anyLimited && !anyBound && !anyFailed)
        {
            return ManagedDesktopReconciliationOutcome.Limited;
        }

        return anyUnresolved
            ? ManagedDesktopReconciliationOutcome.Partial
            : ManagedDesktopReconciliationOutcome.Succeeded;
    }

    private static bool KeysEqual(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static bool NamesEqual(string? left, string right) =>
        !string.IsNullOrWhiteSpace(left) &&
        string.Equals(
            left.Trim(),
            right.Trim(),
            StringComparison.OrdinalIgnoreCase);
}

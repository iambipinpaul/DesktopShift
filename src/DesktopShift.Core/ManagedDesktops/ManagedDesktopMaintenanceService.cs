using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// Applies the Managed Desktop maintenance operations the Desktops page offers.
/// </summary>
/// <remarks>
/// <para>
/// Editing a definition is a configuration change and nothing more. Every edit
/// is applied to the accepted document, saved, and — only if the save was
/// accepted — followed by a reconciliation pass so the runtime mapping catches
/// up with what the user just changed. A rejected edit writes nothing.
/// </para>
/// <para>
/// The one operation that touches the live topology is
/// <see cref="RecreateAsync"/>, and it only ever creates. There is no code path
/// here, and no operation on <see cref="IDesktopTopologyProvider"/>, that
/// removes a real Windows desktop.
/// </para>
/// </remarks>
public sealed class ManagedDesktopMaintenanceService : IManagedDesktopMaintenanceService
{
    private readonly IConfigurationService configurationService;
    private readonly IManagedDesktopReconciliationService reconciliationService;
    private readonly IDesktopTopologyProvider topologyProvider;
    private readonly IManagedDesktopBindingStore bindingStore;

    public ManagedDesktopMaintenanceService(
        IConfigurationService configurationService,
        IManagedDesktopReconciliationService reconciliationService,
        IDesktopTopologyProvider topologyProvider,
        IManagedDesktopBindingStore bindingStore)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(reconciliationService);
        ArgumentNullException.ThrowIfNull(topologyProvider);
        ArgumentNullException.ThrowIfNull(bindingStore);

        this.configurationService = configurationService;
        this.reconciliationService = reconciliationService;
        this.topologyProvider = topologyProvider;
        this.bindingStore = bindingStore;
    }

    public ManagedDesktopCatalog GetCatalog(
        DesktopTopologyProviderState? providerState = null)
    {
        ConfigurationState state = configurationService.CurrentState;
        return ManagedDesktopCatalogProjection.Project(
            ResolveDocument(state),
            reconciliationService.Current,
            state.Issues,
            providerState);
    }

    public Task<ManagedDesktopMaintenanceResult> AddAsync(
        ManagedDesktopDraft draft,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(draft);

        return ApplyEditAsync(
            ManagedDesktopMaintenanceOperation.Add,
            draft.SemanticKey,
            document => ManagedDesktopDefinitionEditor.Add(document, draft),
            cancellationToken);
    }

    public Task<ManagedDesktopMaintenanceResult> RenameAsync(
        string semanticKey,
        string displayName,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        return ApplyEditAsync(
            ManagedDesktopMaintenanceOperation.Rename,
            semanticKey,
            document => ManagedDesktopDefinitionEditor.Rename(
                document,
                semanticKey,
                displayName),
            cancellationToken);
    }

    public Task<ManagedDesktopMaintenanceResult> MoveAsync(
        string semanticKey,
        ManagedDesktopMoveDirection direction,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        return ApplyEditAsync(
            ManagedDesktopMaintenanceOperation.Reorder,
            semanticKey,
            document => ManagedDesktopDefinitionEditor.Move(
                document,
                semanticKey,
                direction),
            cancellationToken);
    }

    public Task<ManagedDesktopMaintenanceResult> SetRecreationPolicyAsync(
        string semanticKey,
        bool recreateWhenMissing,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        return ApplyEditAsync(
            ManagedDesktopMaintenanceOperation.SetRecreationPolicy,
            semanticKey,
            document => ManagedDesktopDefinitionEditor.SetRecreationPolicy(
                document,
                semanticKey,
                recreateWhenMissing),
            cancellationToken);
    }

    public Task<ManagedDesktopMaintenanceResult> RemoveFromManagementAsync(
        string semanticKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        return ApplyEditAsync(
            ManagedDesktopMaintenanceOperation.RemoveFromManagement,
            semanticKey,
            document => ManagedDesktopDefinitionEditor.Remove(document, semanticKey),
            cancellationToken);
    }

    public async Task<ManagedDesktopMaintenanceResult> RecreateAsync(
        string semanticKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        ManagedDesktopDefinition? definition = FindDefinition(semanticKey);
        if (definition is null)
        {
            return RejectUnknownKey(
                ManagedDesktopMaintenanceOperation.Recreate,
                semanticKey);
        }

        VirtualDesktopCapabilities capabilities = topologyProvider.Capabilities;
        if (!capabilities.CanEnumerateDesktops || !capabilities.CanCreateDesktop)
        {
            return Unavailable(
                ManagedDesktopMaintenanceOperation.Recreate,
                definition,
                "managed_desktops.recreate_unavailable",
                "This Windows desktop cannot be recreated",
                $"'{definition.DisplayName}' was not recreated because the selected desktop provider cannot create Windows desktops.",
                "Run the compatibility test on the Settings page. Recreation needs a provider that can enumerate and create desktops.");
        }

        // Reconcile first so the decision is made against the live topology
        // rather than a snapshot that may predate the user deleting or
        // recreating desktops in Task View.
        ManagedDesktopReconciliationSnapshot before = await reconciliationService
            .ReconcileAsync(
                ManagedDesktopReconciliationTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);
        ManagedDesktopRuntimeMapping? mapping = FindMapping(before, definition.SemanticKey);

        if (mapping is null)
        {
            return Unavailable(
                ManagedDesktopMaintenanceOperation.Recreate,
                definition,
                "managed_desktops.recreate_not_reconciled",
                "This Windows desktop cannot be recreated yet",
                $"The last reconciliation did not cover '{definition.DisplayName}', so DesktopShift does not know whether its Windows desktop is missing.",
                "Reconcile all, then try again.");
        }

        if (mapping.Status == ManagedDesktopMappingStatus.Created)
        {
            return new ManagedDesktopMaintenanceResult(
                ManagedDesktopMaintenanceOperation.Recreate,
                ManagedDesktopMaintenanceOutcome.Applied,
                $"'{definition.DisplayName}' was recreated and is mapped to a new Windows desktop.",
                ManagedDesktopValidationProjection.FromRuntime(before),
                ManagedDesktopReconciliationReport.FromSnapshot(before),
                definition.SemanticKey);
        }

        if (mapping.IsBound)
        {
            return new ManagedDesktopMaintenanceResult(
                ManagedDesktopMaintenanceOperation.Recreate,
                ManagedDesktopMaintenanceOutcome.NoChange,
                $"'{definition.DisplayName}' is already mapped to a Windows desktop, so nothing was created.",
                [],
                ManagedDesktopReconciliationReport.FromSnapshot(before),
                definition.SemanticKey);
        }

        if (mapping.Status == ManagedDesktopMappingStatus.Ambiguous)
        {
            return new ManagedDesktopMaintenanceResult(
                ManagedDesktopMaintenanceOperation.Recreate,
                ManagedDesktopMaintenanceOutcome.Rejected,
                $"'{definition.DisplayName}' was not recreated because more than one Windows desktop already matches it.",
                ManagedDesktopValidationProjection.FromRuntime(before),
                ManagedDesktopReconciliationReport.FromSnapshot(before),
                definition.SemanticKey);
        }

        DesktopTopologyProviderResult<Guid> creation = await topologyProvider
            .CreateDesktopAsync(cancellationToken)
            .ConfigureAwait(false);
        if (!creation.IsSuccess || creation.Value == Guid.Empty)
        {
            DesktopTopologyProviderError error =
                creation.Error ??
                new DesktopTopologyProviderError(
                    "managed_desktops.creation_failed",
                    "The provider did not return a valid runtime desktop ID.");
            return Unavailable(
                ManagedDesktopMaintenanceOperation.Recreate,
                definition,
                error.Code,
                "This Windows desktop could not be created",
                error.Message,
                "Run the compatibility test on the Settings page to confirm the provider is still working.",
                creation.Outcome == DesktopTopologyResultOutcome.Unsupported
                    ? ManagedDesktopMaintenanceOutcome.Limited
                    : ManagedDesktopMaintenanceOutcome.Failed);
        }

        string? bindingFailure = await TryRecordBindingAsync(
            definition.SemanticKey,
            creation.Value,
            cancellationToken).ConfigureAwait(false);

        ManagedDesktopReconciliationSnapshot after = await reconciliationService
            .ReconcileAsync(
                ManagedDesktopReconciliationTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);
        ManagedDesktopRuntimeMapping? bound = FindMapping(after, definition.SemanticKey);
        ManagedDesktopReconciliationReport report =
            ManagedDesktopReconciliationReport.FromSnapshot(after);

        // The pass that runs after the desktop exists sees it as an
        // already-bound desktop and would report it as left alone. This
        // operation created it, and that is what the report has to say.
        if (bound?.IsBound == true &&
            bound.Status != ManagedDesktopMappingStatus.Created)
        {
            report = report with
            {
                CreatedCount = report.CreatedCount + 1,
                LeftAloneCount = Math.Max(0, report.LeftAloneCount - 1),
            };
        }

        if (bound?.IsBound == true && bindingFailure is null)
        {
            return new ManagedDesktopMaintenanceResult(
                ManagedDesktopMaintenanceOperation.Recreate,
                ManagedDesktopMaintenanceOutcome.Applied,
                $"A Windows desktop was created for '{definition.DisplayName}'.",
                ManagedDesktopValidationProjection.FromRuntime(after),
                report,
                definition.SemanticKey);
        }

        string explanation = bindingFailure is null
            ? $"A Windows desktop was created for '{definition.DisplayName}', but reconciliation did not bind it."
            : $"A Windows desktop was created for '{definition.DisplayName}', but its binding could not be recorded: {bindingFailure}";

        return new ManagedDesktopMaintenanceResult(
            ManagedDesktopMaintenanceOperation.Recreate,
            ManagedDesktopMaintenanceOutcome.Partial,
            explanation,
            [
                new ManagedDesktopValidationMessage(
                    ManagedDesktopValidationScope.Runtime,
                    ManagedDesktopValidationSeverity.Warning,
                    "managed_desktops.recreate_not_bound",
                    "A Windows desktop was created but not bound",
                    explanation,
                    "Reconcile all to bind it. The new desktop was left in place, so nothing needs to be cleaned up first.",
                    definition.SemanticKey),
            ],
            report,
            definition.SemanticKey);
    }

    public async Task<ManagedDesktopMaintenanceResult> ReconcileAllAsync(
        CancellationToken cancellationToken = default)
    {
        ManagedDesktopReconciliationSnapshot snapshot = await reconciliationService
            .ReconcileAsync(
                ManagedDesktopReconciliationTrigger.Manual,
                cancellationToken)
            .ConfigureAwait(false);
        ManagedDesktopReconciliationReport report =
            ManagedDesktopReconciliationReport.FromSnapshot(snapshot);

        ManagedDesktopMaintenanceOutcome outcome = snapshot.Outcome switch
        {
            ManagedDesktopReconciliationOutcome.Succeeded => report.ChangedAnything
                ? ManagedDesktopMaintenanceOutcome.Applied
                : ManagedDesktopMaintenanceOutcome.NoChange,
            ManagedDesktopReconciliationOutcome.Limited =>
                ManagedDesktopMaintenanceOutcome.Limited,
            ManagedDesktopReconciliationOutcome.Partial =>
                ManagedDesktopMaintenanceOutcome.Partial,
            _ => ManagedDesktopMaintenanceOutcome.Failed,
        };

        return new ManagedDesktopMaintenanceResult(
            ManagedDesktopMaintenanceOperation.Reconcile,
            outcome,
            report.Summary,
            ManagedDesktopValidationProjection.FromRuntime(snapshot),
            report);
    }

    private async Task<ManagedDesktopMaintenanceResult> ApplyEditAsync(
        ManagedDesktopMaintenanceOperation operation,
        string? semanticKey,
        Func<ConfigurationDocument, ManagedDesktopEditResult> edit,
        CancellationToken cancellationToken)
    {
        ConfigurationDocument source = ResolveDocument(configurationService.CurrentState);
        ManagedDesktopEditResult editResult = edit(source);

        if (!editResult.IsAccepted)
        {
            return new ManagedDesktopMaintenanceResult(
                operation,
                ManagedDesktopMaintenanceOutcome.Rejected,
                editResult.Summary,
                editResult.Messages,
                Report: null,
                semanticKey);
        }

        if (ReferenceEquals(editResult.Document, source))
        {
            return new ManagedDesktopMaintenanceResult(
                operation,
                ManagedDesktopMaintenanceOutcome.NoChange,
                editResult.Summary,
                [],
                Report: null,
                semanticKey);
        }

        ConfigurationSaveResult save = await configurationService
            .SaveCandidateAsync(editResult.Document, cancellationToken)
            .ConfigureAwait(false);
        if (!save.Accepted)
        {
            return new ManagedDesktopMaintenanceResult(
                operation,
                ManagedDesktopMaintenanceOutcome.Rejected,
                "The change was saved as a draft but not accepted, so it is not in effect yet.",
                ManagedDesktopValidationProjection.FromConfiguration(save.State.Issues),
                Report: null,
                semanticKey);
        }

        ManagedDesktopReconciliationSnapshot snapshot = await reconciliationService
            .ReconcileAsync(
                ManagedDesktopReconciliationTrigger.ConfigurationAccepted,
                cancellationToken)
            .ConfigureAwait(false);

        return new ManagedDesktopMaintenanceResult(
            operation,
            ManagedDesktopMaintenanceOutcome.Applied,
            editResult.Summary,
            ManagedDesktopValidationProjection.FromRuntime(snapshot),
            ManagedDesktopReconciliationReport.FromSnapshot(snapshot),
            semanticKey);
    }

    /// <summary>
    /// Merges one semantic binding into the persisted set.
    /// </summary>
    /// <returns>The failure message, or null when the binding was recorded.</returns>
    private async Task<string?> TryRecordBindingAsync(
        string semanticKey,
        Guid runtimeDesktopId,
        CancellationToken cancellationToken)
    {
        try
        {
            IReadOnlyList<ManagedDesktopBinding> stored = await bindingStore
                .LoadAsync(cancellationToken)
                .ConfigureAwait(false);
            ManagedDesktopBinding[] merged =
            [
                .. stored.Where(binding => !KeysEqual(binding.SemanticKey, semanticKey)),
                new ManagedDesktopBinding(semanticKey, runtimeDesktopId),
            ];
            await bindingStore.SaveAsync(merged, cancellationToken).ConfigureAwait(false);
            return null;
        }
        catch (Exception exception) when (
            exception is not OperationCanceledException ||
            !cancellationToken.IsCancellationRequested)
        {
            return exception.Message;
        }
    }

    /// <summary>
    /// The document an edit is applied to.
    /// </summary>
    /// <remarks>
    /// The accepted document wins. Editing an unaccepted draft would build the
    /// next change on top of one that never passed validation.
    /// </remarks>
    private static ConfigurationDocument ResolveDocument(ConfigurationState state) =>
        state.Active ?? state.Candidate;

    private ManagedDesktopDefinition? FindDefinition(string semanticKey) =>
        ResolveDocument(configurationService.CurrentState).ManagedDesktops
            .FirstOrDefault(definition => KeysEqual(definition.SemanticKey, semanticKey));

    private static ManagedDesktopRuntimeMapping? FindMapping(
        ManagedDesktopReconciliationSnapshot snapshot,
        string semanticKey) =>
        snapshot.Mappings.FirstOrDefault(
            mapping => KeysEqual(mapping.SemanticKey, semanticKey));

    private static ManagedDesktopMaintenanceResult RejectUnknownKey(
        ManagedDesktopMaintenanceOperation operation,
        string semanticKey) =>
        new(
            operation,
            ManagedDesktopMaintenanceOutcome.Rejected,
            $"No Managed Desktop is defined with the semantic key '{semanticKey}'.",
            [
                new ManagedDesktopValidationMessage(
                    ManagedDesktopValidationScope.Configuration,
                    ManagedDesktopValidationSeverity.Error,
                    "managed_desktops.edit.unknown_key",
                    "That Managed Desktop no longer exists",
                    $"No Managed Desktop is defined with the semantic key '{semanticKey}'.",
                    "Refresh the page. Another change may have removed it since this view was drawn.",
                    semanticKey),
            ],
            Report: null,
            semanticKey);

    private static ManagedDesktopMaintenanceResult Unavailable(
        ManagedDesktopMaintenanceOperation operation,
        ManagedDesktopDefinition definition,
        string code,
        string title,
        string message,
        string remedy,
        ManagedDesktopMaintenanceOutcome outcome =
            ManagedDesktopMaintenanceOutcome.Limited) =>
        new(
            operation,
            outcome,
            message,
            [
                new ManagedDesktopValidationMessage(
                    ManagedDesktopValidationScope.Runtime,
                    ManagedDesktopValidationSeverity.Warning,
                    code,
                    title,
                    message,
                    remedy,
                    definition.SemanticKey),
            ],
            Report: null,
            definition.SemanticKey);

    private static bool KeysEqual(string? left, string? right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);
}

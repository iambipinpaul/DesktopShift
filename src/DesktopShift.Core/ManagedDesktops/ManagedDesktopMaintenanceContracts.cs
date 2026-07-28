using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// The maintenance operation a <see cref="ManagedDesktopMaintenanceResult"/>
/// describes.
/// </summary>
public enum ManagedDesktopMaintenanceOperation
{
    Add,
    Rename,
    Reorder,
    SetRecreationPolicy,
    RemoveFromManagement,
    Recreate,
    Reconcile,
}

/// <summary>
/// What a maintenance operation actually did.
/// </summary>
/// <remarks>
/// <see cref="NoChange"/> is deliberately distinct from <see cref="Applied"/>.
/// Every operation here is idempotent, so a caller that repeats one — a retry,
/// or a future topology-driven trigger running on a cooldown — needs to tell
/// "there was nothing left to do" apart from "something was changed".
/// </remarks>
public enum ManagedDesktopMaintenanceOutcome
{
    Applied,
    NoChange,
    Rejected,
    Limited,
    Partial,
    Failed,
}

public enum ManagedDesktopMoveDirection
{
    Earlier,
    Later,
}

/// <summary>
/// Where a validation message comes from.
/// </summary>
/// <remarks>
/// A configuration message is about the saved document: two definitions sharing
/// a semantic key, a rule naming a desktop that does not exist. A runtime
/// message is about resolving a definition against the live Windows desktops:
/// two real desktops that both plausibly match one definition. The two are
/// different problems with different fixes, so they are never merged.
/// </remarks>
public enum ManagedDesktopValidationScope
{
    Configuration,
    Runtime,
}

public enum ManagedDesktopValidationSeverity
{
    Information,
    Warning,
    Error,
}

/// <summary>
/// A Managed Desktop the user asked to add.
/// </summary>
public sealed record ManagedDesktopDraft(
    string SemanticKey,
    string DisplayName,
    bool RecreateWhenMissing);

/// <summary>
/// One actionable problem shown next to the Managed Desktop list.
/// </summary>
/// <param name="Remedy">
/// What the user can do about it. A validation message without a remedy is a
/// complaint, so this is required rather than optional.
/// </param>
public sealed record ManagedDesktopValidationMessage(
    ManagedDesktopValidationScope Scope,
    ManagedDesktopValidationSeverity Severity,
    string Code,
    string Title,
    string Message,
    string Remedy,
    string? SemanticKey = null)
{
    public string FullText => $"{Message} {Remedy}";
}

/// <summary>
/// Which runtime desktop operations the selected provider can actually perform.
/// </summary>
/// <remarks>
/// Editing definitions is always available: a definition is configuration and
/// needs no virtual-desktop capability at all. Recreating and reconciling both
/// touch the live topology, so in Limited Mode they are reported unavailable
/// with the reason, never offered and then silently failed.
/// </remarks>
public sealed record ManagedDesktopMaintenanceAvailability(
    bool CanRecreate,
    bool CanReconcile,
    string RecreateUnavailableReason,
    string ReconcileUnavailableReason)
{
    private const string PendingReason =
        "DesktopShift has not finished the Windows compatibility check yet, so runtime desktop operations are unavailable.";

    public static ManagedDesktopMaintenanceAvailability Available { get; } =
        new(CanRecreate: true, CanReconcile: true, string.Empty, string.Empty);

    public static ManagedDesktopMaintenanceAvailability Evaluate(
        DesktopTopologyProviderState? state)
    {
        if (state is null ||
            state.Availability == DesktopTopologyProviderAvailability.NotTested)
        {
            return new(false, false, PendingReason, PendingReason);
        }

        if (state.Availability == DesktopTopologyProviderAvailability.Failed)
        {
            string failure = string.IsNullOrWhiteSpace(state.Explanation)
                ? "The Windows desktop provider is unavailable."
                : state.Explanation;
            return new(false, false, failure, failure);
        }

        string provider = $"{state.Identity.DisplayName} runs in {state.Identity.Mode} Mode";
        bool canReconcile = state.Capabilities.CanEnumerateDesktops;
        string reconcileReason = canReconcile
            ? string.Empty
            : $"{provider} and cannot enumerate Windows desktops, so runtime mappings cannot be reconciled.";

        bool canRecreate = canReconcile && state.Capabilities.CanCreateDesktop;
        string recreateReason = canRecreate
            ? string.Empty
            : state.Capabilities.CanCreateDesktop
                ? reconcileReason
                : $"{provider} and cannot create Windows desktops, so a missing desktop cannot be recreated.";

        return new(canRecreate, canReconcile, recreateReason, reconcileReason);
    }
}

/// <summary>
/// What a reconciliation pass did, in terms the user asked for.
/// </summary>
/// <remarks>
/// Reconciliation is idempotent, so running it twice in a row must report a
/// second pass that created nothing and left everything alone. Issue #15's
/// activity view and issue #18's topology reconciliation both read this rather
/// than re-deriving counts from raw mapping statuses.
/// </remarks>
public sealed record ManagedDesktopReconciliationReport(
    int CreatedCount,
    int MatchedCount,
    int LeftAloneCount,
    int UnresolvedCount)
{
    public static ManagedDesktopReconciliationReport Empty { get; } = new(0, 0, 0, 0);

    public int TotalCount =>
        CreatedCount + MatchedCount + LeftAloneCount + UnresolvedCount;

    /// <summary>
    /// Whether this pass altered anything. False means the pass was a no-op.
    /// </summary>
    public bool ChangedAnything => CreatedCount > 0 || MatchedCount > 0;

    public string Summary
    {
        get
        {
            if (TotalCount == 0)
            {
                return "No managed desktops were mapped.";
            }

            List<string> parts = [];
            if (CreatedCount > 0)
            {
                parts.Add($"created {Describe(CreatedCount, "desktop")}");
            }

            if (MatchedCount > 0)
            {
                parts.Add($"matched {Describe(MatchedCount, "existing desktop")}");
            }

            if (LeftAloneCount > 0)
            {
                parts.Add($"left {Describe(LeftAloneCount, "already-mapped desktop")} alone");
            }

            if (UnresolvedCount > 0)
            {
                string verb = UnresolvedCount == 1 ? "needs" : "need";
                parts.Add($"{Describe(UnresolvedCount, "desktop")} still {verb} attention");
            }

            string sentence = string.Join(", ", parts);
            return $"{char.ToUpperInvariant(sentence[0])}{sentence[1..]}.";
        }
    }

    public static ManagedDesktopReconciliationReport FromSnapshot(
        ManagedDesktopReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        int created = 0;
        int matched = 0;
        int leftAlone = 0;
        int unresolved = 0;

        foreach (ManagedDesktopRuntimeMapping mapping in snapshot.Mappings)
        {
            switch (mapping.Status)
            {
                case ManagedDesktopMappingStatus.Created:
                    created++;
                    break;
                case ManagedDesktopMappingStatus.ReusedCompatibleDesktop:
                    matched++;
                    break;
                case ManagedDesktopMappingStatus.ReusedPersistedBinding:
                    leftAlone++;
                    break;
                default:
                    unresolved++;
                    break;
            }
        }

        return new(created, matched, leftAlone, unresolved);
    }

    private static string Describe(int count, string noun) =>
        count == 1 ? $"1 {noun}" : $"{count} {noun}s";
}

/// <summary>
/// The result of one Managed Desktop maintenance operation.
/// </summary>
public sealed record ManagedDesktopMaintenanceResult(
    ManagedDesktopMaintenanceOperation Operation,
    ManagedDesktopMaintenanceOutcome Outcome,
    string Summary,
    ImmutableArray<ManagedDesktopValidationMessage> Messages,
    ManagedDesktopReconciliationReport? Report = null,
    string? SemanticKey = null)
{
    public bool IsAccepted =>
        Outcome is
            ManagedDesktopMaintenanceOutcome.Applied or
            ManagedDesktopMaintenanceOutcome.NoChange;
}

/// <summary>
/// Maintains the semantic Managed Desktop definitions and their runtime
/// mappings.
/// </summary>
/// <remarks>
/// <para>
/// Nothing here deletes a real Windows desktop.
/// <see cref="RemoveFromManagementAsync"/> removes a definition from
/// DesktopShift's configuration and drops its runtime binding; the Windows
/// desktop it was mapped to keeps existing, keeps its windows, and keeps its
/// position. Deleting a real desktop is a separate destructive action that this
/// service deliberately does not offer.
/// </para>
/// <para>
/// <see cref="RecreateAsync"/> is the single seam for "make a real desktop for
/// this definition". It is idempotent — it reports
/// <see cref="ManagedDesktopMaintenanceOutcome.NoChange"/> when the definition
/// is already bound — and it applies no rate limiting of its own. A caller that
/// drives it from topology-change events must impose its own bound, or a
/// desktop the user keeps deleting would be recreated in a loop.
/// </para>
/// </remarks>
public interface IManagedDesktopMaintenanceService
{
    /// <summary>
    /// Projects the current definitions, runtime mappings, rule counts and
    /// validation into a single view.
    /// </summary>
    /// <param name="providerState">
    /// The compatibility coordinator's view of the selected provider, used to
    /// decide whether recreate and reconcile are available. A null state means
    /// the compatibility check has not run yet, so both are unavailable.
    /// </param>
    ManagedDesktopCatalog GetCatalog(DesktopTopologyProviderState? providerState = null);

    Task<ManagedDesktopMaintenanceResult> AddAsync(
        ManagedDesktopDraft draft,
        CancellationToken cancellationToken = default);

    Task<ManagedDesktopMaintenanceResult> RenameAsync(
        string semanticKey,
        string displayName,
        CancellationToken cancellationToken = default);

    Task<ManagedDesktopMaintenanceResult> MoveAsync(
        string semanticKey,
        ManagedDesktopMoveDirection direction,
        CancellationToken cancellationToken = default);

    Task<ManagedDesktopMaintenanceResult> SetRecreationPolicyAsync(
        string semanticKey,
        bool recreateWhenMissing,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Stops managing a definition. The real Windows desktop is left untouched.
    /// </summary>
    Task<ManagedDesktopMaintenanceResult> RemoveFromManagementAsync(
        string semanticKey,
        CancellationToken cancellationToken = default);

    Task<ManagedDesktopMaintenanceResult> RecreateAsync(
        string semanticKey,
        CancellationToken cancellationToken = default);

    Task<ManagedDesktopMaintenanceResult> ReconcileAllAsync(
        CancellationToken cancellationToken = default);
}

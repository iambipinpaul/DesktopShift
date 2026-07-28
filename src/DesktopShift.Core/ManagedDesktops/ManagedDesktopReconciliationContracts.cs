using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Core.ManagedDesktops;

public enum ManagedDesktopReconciliationTrigger
{
    Startup,
    CompatibilityChanged,
    ConfigurationAccepted,
    TopologyChanged,
    Manual,
}

public enum ManagedDesktopReconciliationOutcome
{
    Succeeded,
    Limited,
    Partial,
    Failed,
}

public enum ManagedDesktopMappingStatus
{
    ReusedPersistedBinding,
    ReusedCompatibleDesktop,
    Created,
    Missing,
    Ambiguous,
    Limited,
    Failed,
}

public sealed record ManagedDesktopRuntimeMapping(
    string SemanticKey,
    string DisplayName,
    int PreferredOrder,
    bool RecreateWhenMissing,
    Guid? RuntimeDesktopId,
    string? RuntimeDisplayName,
    int? RuntimePosition,
    ManagedDesktopMappingStatus Status,
    ImmutableArray<Guid> CandidateDesktopIds,
    string Code,
    string Explanation)
{
    public bool IsBound =>
        Status is
            ManagedDesktopMappingStatus.ReusedPersistedBinding or
            ManagedDesktopMappingStatus.ReusedCompatibleDesktop or
            ManagedDesktopMappingStatus.Created;
}

public sealed record ManagedDesktopReconciliationIssue(
    string Code,
    string Message);

public sealed record ManagedDesktopReconciliationSnapshot(
    DateTimeOffset ObservedAtUtc,
    ManagedDesktopReconciliationTrigger Trigger,
    string ProviderId,
    DesktopTopologyProviderMode ProviderMode,
    ManagedDesktopReconciliationOutcome Outcome,
    ImmutableArray<ManagedDesktopRuntimeMapping> Mappings,
    ImmutableArray<ManagedDesktopReconciliationIssue> Issues)
{
    public string ProviderSummary => $"{ProviderMode} Mode • {ProviderId}";

    public string Summary => Outcome switch
    {
        ManagedDesktopReconciliationOutcome.Succeeded =>
            "All configured managed desktops are bound to runtime desktops.",
        ManagedDesktopReconciliationOutcome.Limited =>
            "Managed desktop reconciliation is limited by the selected provider or active configuration.",
        ManagedDesktopReconciliationOutcome.Partial =>
            "Some configured managed desktops could not be resolved.",
        ManagedDesktopReconciliationOutcome.Failed =>
            "Managed desktop reconciliation failed without a usable complete mapping.",
        _ => Outcome.ToString(),
    };

    public static ManagedDesktopReconciliationSnapshot NotRun { get; } = new(
        DateTimeOffset.MinValue,
        ManagedDesktopReconciliationTrigger.Startup,
        "not-tested",
        DesktopTopologyProviderMode.Limited,
        ManagedDesktopReconciliationOutcome.Limited,
        [],
        [new ManagedDesktopReconciliationIssue(
            "managed_desktops.not_reconciled",
            "Managed desktops have not been reconciled yet.")]);
}

public sealed class ManagedDesktopReconciliationChangedEventArgs : EventArgs
{
    public ManagedDesktopReconciliationChangedEventArgs(
        ManagedDesktopReconciliationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);
        Snapshot = snapshot;
    }

    public ManagedDesktopReconciliationSnapshot Snapshot { get; }
}

public interface IManagedDesktopReconciliationService
{
    ManagedDesktopReconciliationSnapshot Current { get; }

    event EventHandler<ManagedDesktopReconciliationChangedEventArgs>? Changed;

    Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
        ManagedDesktopReconciliationTrigger trigger,
        CancellationToken cancellationToken = default);
}

public sealed record ManagedDesktopBinding(
    string SemanticKey,
    Guid RuntimeDesktopId);

public interface IManagedDesktopBindingStore
{
    Task<IReadOnlyList<ManagedDesktopBinding>> LoadAsync(
        CancellationToken cancellationToken = default);

    Task SaveAsync(
        IReadOnlyCollection<ManagedDesktopBinding> bindings,
        CancellationToken cancellationToken = default);
}

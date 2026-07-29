using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// Turns one Windows virtual-desktop topology notification into one semantic
/// reconciliation and, only when it is warranted, one window reconciliation.
/// </summary>
/// <remarks>
/// <para>
/// The ordering matters. Semantic mappings are resolved first, because a window
/// pass that ran against a stale mapping would move windows to the desktop that
/// was just deleted. The window pass runs afterwards, once, and only when
/// <see cref="ManagedDesktopReconciliationReport.ChangedAnything"/> says a
/// managed destination actually changed identity.
/// </para>
/// <para>
/// A reorder is deliberately not a reason to touch windows. When the user drags
/// a desktop to a new position, the runtime mapping picks up the new position
/// and the semantic key — which is what every Application Rule targets — does
/// not move at all. The windows are already where they belong; the desktop
/// under them simply has a new index.
/// </para>
/// <para>
/// Neither this service nor the provider it drives can delete or rename a real
/// Windows desktop: <see cref="Compatibility.IDesktopTopologyProvider"/> has no
/// such operation. Windows are only ever placed by the existing reassignment
/// pipeline, which moves a window only when a rule claims it.
/// </para>
/// </remarks>
public sealed class ManagedDesktopTopologyRecoveryService :
    IManagedDesktopTopologyRecoveryService
{
    private readonly IManagedDesktopReconciliationService reconciliationService;
    private readonly IManagedDesktopRecreationGate recreationGate;
    private readonly TimeProvider timeProvider;
    private readonly IWindowReassignmentService? windowReassignmentService;
    private readonly IActivityJournal? activityJournal;

    /// <param name="windowReassignmentService">
    /// The window pass to run when the mapping changed, or null in a host that
    /// registered managed-desktop reconciliation without the assignment
    /// pipeline. A missing pipeline degrades to mapping-only recovery rather
    /// than failing the notification.
    /// </param>
    /// <param name="activityJournal">
    /// Where reconciliation and suppression decisions are published, or null in
    /// a host without diagnostics.
    /// </param>
    public ManagedDesktopTopologyRecoveryService(
        IManagedDesktopReconciliationService reconciliationService,
        IManagedDesktopRecreationGate recreationGate,
        TimeProvider timeProvider,
        IWindowReassignmentService? windowReassignmentService = null,
        IActivityJournal? activityJournal = null)
    {
        this.reconciliationService = reconciliationService ??
            throw new ArgumentNullException(nameof(reconciliationService));
        this.recreationGate = recreationGate ??
            throw new ArgumentNullException(nameof(recreationGate));
        this.timeProvider = timeProvider ??
            throw new ArgumentNullException(nameof(timeProvider));
        this.windowReassignmentService = windowReassignmentService;
        this.activityJournal = activityJournal;
    }

    public async Task<ManagedDesktopTopologyRecoveryResult> HandleTopologyChangedAsync(
        string reason,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        cancellationToken.ThrowIfCancellationRequested();

        Guid correlationId = Guid.NewGuid();
        ManagedDesktopReconciliationSnapshot before = reconciliationService.Current;

        // Spending the notification before the pass, not after, is what makes
        // the cooldown converge: the pass a suppression denies still counts
        // towards lifting it.
        recreationGate.NoteTopologyEvent();
        _ = recreationGate.DrainSuppressions();

        ManagedDesktopReconciliationSnapshot after = await reconciliationService
            .ReconcileAsync(
                ManagedDesktopReconciliationTrigger.TopologyChanged,
                cancellationToken)
            .ConfigureAwait(false);

        ManagedDesktopReconciliationReport report =
            ManagedDesktopReconciliationReport.FromSnapshot(after);
        ImmutableArray<ManagedDesktopRecreationSuppression> suppressions =
            recreationGate.DrainSuppressions();
        ImmutableArray<ManagedDesktopRuntimeRemap> remaps = FindRemaps(before, after);

        bool runWindowPass =
            report.ChangedAnything &&
            windowReassignmentService is not null &&
            after.Outcome is
                ManagedDesktopReconciliationOutcome.Succeeded or
                ManagedDesktopReconciliationOutcome.Partial;
        int windowsInspected = 0;

        if (runWindowPass)
        {
            WindowReassignmentBatchResult batch = await windowReassignmentService!
                .ReassignAllAsync(cancellationToken)
                .ConfigureAwait(false);
            windowsInspected = batch.EnumeratedWindowCount;
        }

        ManagedDesktopTopologyRecoveryResult result = new(
            correlationId,
            timeProvider.GetUtcNow(),
            reason,
            DetermineOutcome(after, report, suppressions, remaps),
            after,
            report,
            runWindowPass,
            windowsInspected,
            suppressions,
            remaps);

        Publish(result);
        return result;
    }

    /// <summary>
    /// Finds the definitions that kept their runtime desktop but changed
    /// position.
    /// </summary>
    /// <remarks>
    /// A key whose runtime desktop identifier changed is not a reorder — it is a
    /// rebinding, which the report already counts. Requiring the identifier to
    /// match is what keeps a reorder from being mistaken for a recreation.
    /// </remarks>
    private static ImmutableArray<ManagedDesktopRuntimeRemap> FindRemaps(
        ManagedDesktopReconciliationSnapshot before,
        ManagedDesktopReconciliationSnapshot after)
    {
        if (before.Mappings.IsDefaultOrEmpty || after.Mappings.IsDefaultOrEmpty)
        {
            return [];
        }

        Dictionary<string, ManagedDesktopRuntimeMapping> previous =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedDesktopRuntimeMapping mapping in before.Mappings)
        {
            previous[mapping.SemanticKey] = mapping;
        }

        ImmutableArray<ManagedDesktopRuntimeRemap>.Builder remaps =
            ImmutableArray.CreateBuilder<ManagedDesktopRuntimeRemap>();

        foreach (ManagedDesktopRuntimeMapping mapping in after.Mappings)
        {
            if (mapping.RuntimeDesktopId is not Guid runtimeDesktopId ||
                !previous.TryGetValue(
                    mapping.SemanticKey,
                    out ManagedDesktopRuntimeMapping? earlier) ||
                earlier.RuntimeDesktopId != runtimeDesktopId ||
                earlier.RuntimePosition == mapping.RuntimePosition)
            {
                continue;
            }

            remaps.Add(
                new ManagedDesktopRuntimeRemap(
                    mapping.SemanticKey,
                    runtimeDesktopId,
                    earlier.RuntimePosition,
                    mapping.RuntimePosition));
        }

        return remaps.ToImmutable();
    }

    private static ManagedDesktopTopologyRecoveryOutcome DetermineOutcome(
        ManagedDesktopReconciliationSnapshot snapshot,
        ManagedDesktopReconciliationReport report,
        ImmutableArray<ManagedDesktopRecreationSuppression> suppressions,
        ImmutableArray<ManagedDesktopRuntimeRemap> remaps) =>
        snapshot.Outcome switch
        {
            ManagedDesktopReconciliationOutcome.Failed =>
                ManagedDesktopTopologyRecoveryOutcome.Failed,
            ManagedDesktopReconciliationOutcome.Limited =>
                ManagedDesktopTopologyRecoveryOutcome.Limited,
            _ when report.ChangedAnything ||
                !suppressions.IsEmpty ||
                !remaps.IsEmpty =>
                ManagedDesktopTopologyRecoveryOutcome.Reconciled,
            _ => ManagedDesktopTopologyRecoveryOutcome.NoChange,
        };

    private void Publish(ManagedDesktopTopologyRecoveryResult result)
    {
        if (activityJournal is null)
        {
            return;
        }

        activityJournal.Record(
            ActivityRecordFactory.FromTopologyRecovery(
                result,
                activityJournal.SessionId));
    }
}

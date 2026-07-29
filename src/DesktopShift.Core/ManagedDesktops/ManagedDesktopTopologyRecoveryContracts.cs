using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// What one topology-driven recovery pass did.
/// </summary>
/// <remarks>
/// A topology notification arrives for every desktop the user creates, deletes,
/// renames, reorders, or switches to, so most passes legitimately do nothing.
/// <see cref="NoChange"/> is therefore the expected result, not a defect, and it
/// is the signal the pass used to decide not to touch a single window.
/// </remarks>
public enum ManagedDesktopTopologyRecoveryOutcome
{
    /// <summary>Something about the semantic mapping actually moved.</summary>
    Reconciled,

    /// <summary>The pass ran and confirmed the mapping was already correct.</summary>
    NoChange,

    /// <summary>
    /// The selected provider cannot see the topology, so nothing was resolved.
    /// </summary>
    Limited,

    /// <summary>The pass could not produce a usable mapping.</summary>
    Failed,
}

/// <summary>
/// Whether a missing Managed Desktop may be recreated right now.
/// </summary>
/// <param name="IsAllowed">
/// <c>true</c> when recreation may proceed. A denial is never an error: it is
/// the app declining to fight a user who keeps deleting the same desktop.
/// </param>
/// <param name="Code">
/// The stable diagnostic code recorded against the decision. It is the same
/// code that reaches the runtime mapping and the activity journal, so one search
/// finds every place a suppression was decided and reported.
/// </param>
/// <param name="Reason">Why recreation was declined, in the user's terms.</param>
public sealed record ManagedDesktopRecreationDecision(
    bool IsAllowed,
    string Code,
    string Reason)
{
    /// <summary>The decision when nothing is holding recreation back.</summary>
    public static ManagedDesktopRecreationDecision Allowed { get; } = new(
        IsAllowed: true,
        "managed_desktops.recreation_allowed",
        string.Empty);
}

/// <summary>
/// One declined recreation, kept so the reason reaches structured activity.
/// </summary>
/// <param name="RemainingTopologyEvents">
/// How many further topology notifications must arrive before the suppression
/// lifts. The bound is expressed in events rather than in time on purpose: a
/// user who has stopped deleting desktops stops producing the events, so a quiet
/// machine never spends its cooldown and never resumes a fight nobody is having.
/// </param>
public sealed record ManagedDesktopRecreationSuppression(
    string SemanticKey,
    string Code,
    string Reason,
    int RemainingTopologyEvents);

/// <summary>
/// A Managed Desktop that kept its runtime desktop but changed position.
/// </summary>
/// <remarks>
/// This is what reordering looks like from the semantic side: the same
/// <see cref="RuntimeDesktopId"/>, a new <see cref="Position"/>, and a semantic
/// key that did not move at all. Application Rules target the key, so a reorder
/// changes no rule and needs no window to be touched.
/// </remarks>
public sealed record ManagedDesktopRuntimeRemap(
    string SemanticKey,
    Guid RuntimeDesktopId,
    int? PreviousPosition,
    int? Position);

/// <summary>
/// The bounded permission slip a reconciliation pass needs before it recreates
/// a Managed Desktop the user deleted.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="IManagedDesktopMaintenanceService.RecreateAsync"/> deliberately
/// applies no rate limiting, because a user who clicks Recreate means it. This
/// gate is the other half of that decision: it bounds the recreations nobody
/// asked for, the ones a topology notification would otherwise drive forever.
/// </para>
/// <para>
/// The gate is driven entirely by topology notifications. It owns no timer and
/// polls nothing, so it costs nothing on an idle machine and cannot expire while
/// the user is mid-fight with it.
/// </para>
/// </remarks>
public interface IManagedDesktopRecreationGate
{
    /// <summary>
    /// Decides whether the named Managed Desktop may be recreated, recording a
    /// suppression for <see cref="DrainSuppressions"/> when it may not.
    /// </summary>
    ManagedDesktopRecreationDecision Evaluate(string semanticKey);

    /// <summary>Reports that a desktop was in fact recreated for this key.</summary>
    void NoteRecreated(string semanticKey);

    /// <summary>
    /// Advances every cooldown by one topology notification. Called once per
    /// notification, before the reconciliation pass that notification drives.
    /// </summary>
    void NoteTopologyEvent();

    /// <summary>
    /// Takes the suppressions decided since the last call and clears them.
    /// </summary>
    /// <remarks>
    /// Draining rather than accumulating keeps the gate bounded: a machine that
    /// runs for a week reports the suppressions of the pass it is in, not every
    /// suppression it has ever decided.
    /// </remarks>
    ImmutableArray<ManagedDesktopRecreationSuppression> DrainSuppressions();
}

/// <summary>
/// Everything one topology-driven recovery pass decided.
/// </summary>
/// <param name="CorrelationId">
/// Ties the reconciliation, every suppression, and the window pass this
/// notification produced into one readable thread, exactly as an observation
/// correlation ties a decision to its move and switch.
/// </param>
/// <param name="Reason">
/// The notification reason the provider reported, such as <c>Destroyed</c> or
/// <c>Moved</c>.
/// </param>
/// <param name="WindowReconciliationRan">
/// Whether the pass followed the mapping work with a window reconciliation. It
/// runs at most once per notification, and only when the mapping actually
/// changed — a reorder or a quiet notification moves no window.
/// </param>
public sealed record ManagedDesktopTopologyRecoveryResult(
    Guid CorrelationId,
    DateTimeOffset OccurredAtUtc,
    string Reason,
    ManagedDesktopTopologyRecoveryOutcome Outcome,
    ManagedDesktopReconciliationSnapshot Snapshot,
    ManagedDesktopReconciliationReport Report,
    bool WindowReconciliationRan,
    int WindowsInspected,
    ImmutableArray<ManagedDesktopRecreationSuppression> Suppressions,
    ImmutableArray<ManagedDesktopRuntimeRemap> Remaps)
{
    /// <summary>
    /// An uninitialized <see cref="ImmutableArray{T}"/> cannot be enumerated and
    /// throws when serialized, so neither collection is allowed to stay default.
    /// </summary>
    public ImmutableArray<ManagedDesktopRecreationSuppression> Suppressions { get; init; } =
        Suppressions.IsDefault ? [] : Suppressions;

    /// <inheritdoc cref="Suppressions"/>
    public ImmutableArray<ManagedDesktopRuntimeRemap> Remaps { get; init; } =
        Remaps.IsDefault ? [] : Remaps;

    public string Summary => Outcome switch
    {
        ManagedDesktopTopologyRecoveryOutcome.Limited =>
            "The desktop topology changed, but the selected provider cannot read it, so no mapping was changed.",
        ManagedDesktopTopologyRecoveryOutcome.Failed =>
            "The desktop topology changed and the semantic mapping could not be resolved.",
        ManagedDesktopTopologyRecoveryOutcome.NoChange =>
            "The desktop topology changed; the semantic mapping was already correct, so no window was touched.",
        _ => Report.Summary,
    };
}

/// <summary>
/// Keeps semantic Managed Desktop mappings correct across desktops the user
/// deletes, adds, renames, or reorders.
/// </summary>
/// <remarks>
/// <para>
/// One notification produces one semantic reconciliation and, at most, one
/// window reconciliation. Notifications arrive in bursts — deleting a desktop in
/// Task View raises several — so a pass that would move no window does not start
/// one, and the app never answers a burst with a burst.
/// </para>
/// <para>
/// Nothing here deletes or renames a real Windows desktop, and nothing here
/// moves a window that no Application Rule claims.
/// <see cref="IDesktopTopologyProvider"/> has no delete operation at all, so
/// "unrelated desktops are never deleted" is a property of the seam rather than
/// of this code's good behavior.
/// </para>
/// </remarks>
public interface IManagedDesktopTopologyRecoveryService
{
    /// <summary>
    /// Handles one topology notification.
    /// </summary>
    /// <param name="reason">
    /// The provider's reason for the notification. It is recorded, never parsed
    /// for control flow: a reason DesktopShift does not recognize must still get
    /// a correct reconciliation.
    /// </param>
    Task<ManagedDesktopTopologyRecoveryResult> HandleTopologyChangedAsync(
        string reason,
        CancellationToken cancellationToken = default);
}

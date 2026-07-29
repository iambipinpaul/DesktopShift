using System.Collections.Immutable;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// What happened to one Managed Desktop during a naming pass.
/// </summary>
public enum ManagedDesktopNamingOutcome
{
    /// <summary>
    /// The Windows desktop already carried the configured display name, so
    /// nothing was written. The steady state, and by far the common one.
    /// </summary>
    AlreadyNamed,

    /// <summary>
    /// A name was written and Windows was confirmed to have stored it.
    /// </summary>
    Applied,

    /// <summary>
    /// The provider refused the name. The binding is untouched — a desktop that
    /// could not be named is still the right desktop.
    /// </summary>
    Failed,

    /// <summary>
    /// The name was accepted and then read back as something else, so Windows
    /// stores this name differently from how it was sent.
    /// </summary>
    /// <remarks>
    /// This is the only outcome that stops a key permanently for the session.
    /// A name that never reads back the way it was written would otherwise be
    /// rewritten on every single topology notification, forever, with no user
    /// involved — the one genuinely self-feeding loop naming can produce.
    /// </remarks>
    NotStored,

    /// <summary>
    /// The name was written but the inventory could not be re-read, so it is
    /// unknown whether it stuck. Nothing is recorded and the next pass decides.
    /// </summary>
    Unverified,

    /// <summary>
    /// The user has renamed this desktop back often enough that DesktopShift
    /// has stopped arguing and left their name in place.
    /// </summary>
    Conceded,

    /// <summary>
    /// Naming is switched off, or this Windows build never proved it lays the
    /// shell manager out where naming lives.
    /// </summary>
    Unavailable,
}

/// <param name="DesiredName">
/// The Managed Desktop's display name — what Task View was asked to show.
/// </param>
/// <param name="ObservedName">
/// What the Windows desktop was actually called when the pass looked, which is
/// null for an unnamed desktop.
/// </param>
public sealed record ManagedDesktopNamingResult(
    string SemanticKey,
    string DesiredName,
    string? ObservedName,
    Guid? RuntimeDesktopId,
    ManagedDesktopNamingOutcome Outcome,
    string Code,
    string Explanation);

/// <summary>
/// One naming pass across every bound Managed Desktop.
/// </summary>
public sealed record ManagedDesktopNamingPass(
    Guid CorrelationId,
    DateTimeOffset ObservedAtUtc,
    ImmutableArray<ManagedDesktopNamingResult> Results)
{
    public static ManagedDesktopNamingPass Empty { get; } =
        new(Guid.Empty, DateTimeOffset.MinValue, []);

    public bool AppliedAnything =>
        Results.Any(static result =>
            result.Outcome == ManagedDesktopNamingOutcome.Applied);
}

/// <param name="MaxCorrections">
/// How many times DesktopShift re-applies a name to the same desktop after the
/// user has changed it, before conceding and leaving their name alone.
///
/// The first correction is the useful one — a name was lost, or the desktop was
/// just created and has none. By the third the user is saying something, and it
/// is time to stop answering. Deliberately the same reasoning, and the same
/// number, as <see cref="ManagedDesktopRecreationCooldownOptions.MaxRecreations"/>.
///
/// Counted per session and never decayed, which is the one place this bound is
/// blunter than the recreation cooldown. That cooldown expires because
/// recreating a desktop the user wanted back is helpful again later; conceding a
/// name is not something to reverse on a timer, because the user's name is
/// already on the desktop and nothing is broken while it stays there. Restarting
/// DesktopShift starts the argument over.
/// </param>
public sealed record ManagedDesktopNamingOptions(int MaxCorrections = 3)
{
    public static ManagedDesktopNamingOptions Default { get; } = new();

    public int MaxCorrections { get; init; } =
        MaxCorrections > 0
            ? MaxCorrections
            : throw new ArgumentOutOfRangeException(nameof(MaxCorrections));
}

/// <summary>
/// Names the Windows desktops behind bound Managed Desktops.
/// </summary>
/// <remarks>
/// Deliberately not part of reconciliation. Reconciliation decides which
/// runtime desktop a semantic key means, and that answer must not depend on
/// whether a cosmetic label could be written — for the same reason the
/// recreation cooldown lives outside it rather than in it.
/// </remarks>
public interface IManagedDesktopNamingService
{
    Task<ManagedDesktopNamingPass> ApplyAsync(
        ManagedDesktopReconciliationSnapshot snapshot,
        CancellationToken cancellationToken = default);
}

using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Observation;

/// <summary>
/// The rule that answers for every window no enabled rule names.
/// </summary>
/// <remarks>
/// <para>
/// Window placement is total: an application the user has never thought about
/// must not accumulate on whatever desktop happened to be in front. A window
/// that matches no rule is therefore moved to the Windows desktop at position 0
/// rather than left alone, and this is the rule that says so.
/// </para>
/// <para>
/// The sweep is not user-authored and cannot be edited, so it is not part of any
/// configuration document. It reaches the assignment pipeline as an ordinary
/// <see cref="WindowObservationRule"/> because that pipeline requires one, and
/// the activity it produces has to stay attributable to the sweep rather than to
/// something the user wrote — which is what <see cref="RuleId"/> guarantees.
/// </para>
/// </remarks>
public static class UnmanagedWindowSweep
{
    /// <summary>
    /// The rule identifier every swept window is recorded against.
    /// </summary>
    /// <remarks>
    /// The parentheses make it something
    /// <see cref="ApplicationRuleShape.IsValidRuleId"/> rejects, so no rule a
    /// user could write is able to collide with it, and no activity row can
    /// blame a user's rule for the sweep.
    /// </remarks>
    public const string RuleId = "(unmanaged sweep)";

    /// <summary>
    /// The destination key recorded on a swept window's activity.
    /// </summary>
    /// <remarks>
    /// Not a semantic key and never looked up as one. The sweep resolves its
    /// destination by position, so this exists only to give the Activity view
    /// and the exported log something to show and filter on.
    /// </remarks>
    public const string TargetDesktopKey = "(first desktop)";

    /// <summary>What the sweep is called in user-facing text.</summary>
    public const string DisplayName = "Unmanaged window sweep";

    /// <summary>
    /// The window events the sweep answers.
    /// </summary>
    /// <remarks>
    /// <see cref="ApplicationRuleTrigger.ForegroundActivated"/> is deliberately
    /// absent. A window must never be moved out from under a click: if the sweep
    /// answered foreground events, clicking a stray window would teleport it
    /// mid-interaction and deliberate manual placement would become impossible.
    /// <see cref="ApplicationRuleTrigger.StartupReconciliation"/> is deliberately
    /// present, so windows left over from the previous session are tidied at
    /// sign-in.
    /// </remarks>
    public static ImmutableArray<ApplicationRuleTrigger> Triggers { get; } =
    [
        ApplicationRuleTrigger.WindowCreated,
        ApplicationRuleTrigger.WindowShown,
        ApplicationRuleTrigger.StartupReconciliation,
        ApplicationRuleTrigger.ManualReassignment,
    ];

    /// <summary>
    /// The rule the assignment pipeline is handed for a swept window.
    /// </summary>
    /// <remarks>
    /// It declares no identity criteria, because it is never given to the
    /// matcher: it is what the observation reaches for once the matcher has said
    /// nothing names this window.
    /// </remarks>
    public static WindowObservationRule Rule { get; } = new(
        RuleId,
        DisplayName,
        IsEnabled: true,
        TargetDesktopKey,
        Triggers,

        // The window is being moved because it has just been opened somewhere
        // it does not belong, and the switch that follows is what stops the
        // launch looking like a failure. The policy is never consulted for the
        // sweep — a switch is decided from the trigger — but this is the one
        // that describes what happens.
        DesktopSwitchPolicy.OnNewWindowActivation,
        new WindowMatchCriteria([], [], [], [], [], [], []),
        Order: int.MaxValue,
        WindowRuleDestination.FirstDesktop);

    /// <summary>
    /// Whether the sweep answers a window event of this kind.
    /// </summary>
    /// <param name="eventKind">The observed window event.</param>
    /// <returns>Whether an unnamed window should be swept.</returns>
    public static bool AnswersEvent(WindowEventKind eventKind) =>
        eventKind is
            WindowEventKind.Created or
            WindowEventKind.Shown or
            WindowEventKind.StartupReconciliation or
            WindowEventKind.ManualReassignment;

    /// <summary>
    /// Whether a swept window takes the user with it.
    /// </summary>
    /// <remarks>
    /// Only on the two events that mean the user just opened something.
    /// Switching on a startup reconciliation would bounce a user across desktops
    /// at sign-in, and switching on a manual reassignment would make a batch
    /// thrash.
    /// </remarks>
    /// <param name="eventKind">The observed window event.</param>
    /// <returns>Whether the current desktop follows the swept window.</returns>
    public static bool FollowsWindow(WindowEventKind eventKind) =>
        eventKind is WindowEventKind.Created or WindowEventKind.Shown;

    /// <summary>
    /// Whether an activity record was produced by the sweep.
    /// </summary>
    /// <param name="ruleId">The rule identifier recorded on the activity.</param>
    /// <returns>Whether the window was swept rather than claimed by a rule.</returns>
    public static bool IsSweptRuleId(string? ruleId) =>
        string.Equals(ruleId, RuleId, StringComparison.Ordinal);
}

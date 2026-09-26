namespace DesktopShift.Core.Observation;

public enum WindowSkipReason
{
    None,
    CleanupEvent,
    Coalesced,
    StaleWindow,
    ChildWindow,
    ToolWindow,
    CloakedWindow,
    InvisibleWindow,
    ShellWindow,
    SystemWindow,
    TransientWindow,
    BrowserHelperWindow,
    DesktopShiftWindow,
    IdentityAccessDenied,
    IdentityUnavailable,

    /// <summary>
    /// No rule is enabled, and the sweep does not answer this event.
    /// </summary>
    /// <remarks>
    /// Kept apart from <see cref="NoMatchingRule"/> so a user who has switched
    /// everything off reads that, rather than reading that their rules did not
    /// match.
    /// </remarks>
    NoEnabledRules,

    /// <summary>
    /// A rule names this application, but none matched this event.
    /// </summary>
    /// <remarks>
    /// This no longer means "nothing knows about this application" — that window
    /// is swept to the first desktop instead. It now means only that the
    /// application is named by a rule whose triggers do not include the one that
    /// fired, so the window is deliberately left alone.
    /// </remarks>
    NoMatchingRule,

    /// <summary>
    /// An enabled rule names this application and sends it Anywhere, so the
    /// window stays exactly where it opened.
    /// </summary>
    AllowedAnywhere,

    /// <summary>
    /// The unmanaged sweep does not answer this foreground activation, so the
    /// window is deliberately left alone.
    /// </summary>
    /// <remarks>
    /// The sweep answers window creation, window showing, startup reconciliation,
    /// and manual reassignment. It does not answer a foreground activation outside
    /// the opening grace period, because moving a window as the user switches to
    /// it would move it out from under their interaction.
    /// </remarks>
    ActivationNotSwept,

    /// <summary>
    /// Windows reported that the window's cloak state changed. The event is
    /// recorded for diagnosis but does not participate in placement yet.
    /// </summary>
    /// <remarks>
    /// Cloaking is how Windows hides a window that is not on the visible virtual
    /// desktop. This reason keeps the cloak/uncloak spike observational: a later
    /// change may let an uncloak trigger the unmanaged sweep, but only after a
    /// real capture proves that it distinguishes relocation from a desktop
    /// switch.
    /// </remarks>
    CloakStateChangeObserved,

    /// <summary>
    /// Windows reported that a move/size drag ended. Rule matching does not
    /// answer this event; tiling hears about it and reconciles once.
    /// </summary>
    TilingMoveSizeEndObserved,

    /// <summary>
    /// The decision leaves the window where it is — an Anywhere rule claims
    /// the application, or a rule that names it did not answer this event —
    /// but a pin an earlier rule left on the window could not be released, so
    /// the window may still appear on every desktop.
    /// </summary>
    /// <remarks>
    /// Reported instead of the skip the decision would otherwise record
    /// (<see cref="AllowedAnywhere"/>, <see cref="NoMatchingRule"/>) rather
    /// than beside it: the window was left where it is, but a window still
    /// pinned to every desktop is not what "left where it is" describes. The
    /// next event that repairs placement tries the release again.
    /// </remarks>
    PinReleaseFailed,
}

public sealed record WindowQualification(
    QualifiedWindow? Window,
    WindowSkipReason SkipReason)
{
    public bool IsQualified => Window is not null;

    public static WindowQualification Qualified(QualifiedWindow window)
    {
        ArgumentNullException.ThrowIfNull(window);
        return new WindowQualification(window, WindowSkipReason.None);
    }

    public static WindowQualification Skipped(WindowSkipReason reason) =>
        new(null, reason);
}

public interface IWindowClassifier
{
    WindowQualification Qualify(nint windowHandle);

    WindowSkipReason ClassifyIdentity(
        QualifiedWindow window,
        WindowIdentity identity);
}

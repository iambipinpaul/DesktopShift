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
    /// fired, so the window is deliberately left alone. It is also what a window
    /// gets when the sweep does not answer the event, which is every foreground
    /// activation.
    /// </remarks>
    NoMatchingRule,

    /// <summary>
    /// An enabled rule names this application and sends it Anywhere, so the
    /// window stays exactly where it opened.
    /// </summary>
    AllowedAnywhere,
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

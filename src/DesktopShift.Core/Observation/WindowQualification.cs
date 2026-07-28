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
    NoEnabledRules,
    NoMatchingRule,
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

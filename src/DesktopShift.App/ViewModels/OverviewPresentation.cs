namespace DesktopShift.App.ViewModels;

public sealed record OverviewPresentation(
    int EnabledRuleCount,
    int ManagedDesktopCount,
    bool IsFirstRunComplete,
    CompatibilityPresentation Compatibility);

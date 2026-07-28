namespace DesktopShift.App.ViewModels;

public sealed record RulePresentation(
    string DisplayName,
    string Status,
    string TargetDesktop,
    string ProcessNames);

public sealed record ManagedDesktopPresentation(
    string DisplayName,
    string SemanticKey,
    string Order,
    string Reconciliation);

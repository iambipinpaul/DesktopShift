namespace DesktopShift.App.ViewModels;

public sealed record CompatibilityPresentation(
    string Build,
    string BuildSupport,
    string Provider,
    string Mode,
    string Capabilities,
    string Explanation,
    string TestOutcome,
    string TestSummary,
    string TestedAt);

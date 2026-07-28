using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

public sealed record ConfigurationDocument(
    int SchemaVersion,
    ImmutableArray<ManagedDesktopDefinition> ManagedDesktops,
    ImmutableArray<ApplicationRule> ApplicationRules,
    BehaviorSettings Behavior);

public sealed record ManagedDesktopDefinition(
    string SemanticKey,
    string DisplayName,
    int PreferredOrder,
    bool RecreateWhenMissing);

public sealed record ApplicationRule(
    string Id,
    string DisplayName,
    bool IsEnabled,
    string TargetDesktopKey,
    ImmutableArray<string> ProcessNames,
    ImmutableArray<ApplicationRuleTrigger> Triggers,
    DesktopSwitchPolicy SwitchPolicy);

public sealed record BehaviorSettings(
    bool StartWithWindows,
    bool StartMinimized,
    bool CloseToTray);

public enum ApplicationRuleTrigger
{
    WindowCreated,
    WindowShown,
    ForegroundActivated,
    StartupReconciliation,
    ManualReassignment,
}

public enum DesktopSwitchPolicy
{
    Never,
    OnForegroundActivation,
    OnNewWindowActivation,
}

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

/// <summary>
/// A configured mapping from an application identity to a Managed Desktop.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="PackageFamilyNames"/> and <see cref="AppUserModelIds"/> are the
/// stable packaged identities of an application. They are preferred over
/// <see cref="ProcessNames"/> whenever the application ships as a package,
/// because the matcher ranks them above a process name.
/// </para>
/// <para>
/// A process name alone never proves window ownership. A launcher stub such as
/// <c>wt.exe</c> forwards its command line to an already running instance and
/// then exits, so it owns no window at all; the window belongs to the packaged
/// host process. Such a launcher is a launch signal only and must be left out
/// of <see cref="ProcessNames"/>.
/// </para>
/// <para>
/// Both packaged identity collections are optional and trail the required
/// members, so every existing construction site and every schema version 1
/// document written before they existed keeps working. An absent collection
/// reads as empty, never as a default array.
/// </para>
/// </remarks>
/// <param name="Id">The stable rule identifier.</param>
/// <param name="DisplayName">The rule name shown in the UI.</param>
/// <param name="IsEnabled">Whether the rule takes part in matching.</param>
/// <param name="TargetDesktopKey">
/// The semantic key of the Managed Desktop matched windows are assigned to.
/// </param>
/// <param name="ProcessNames">
/// Executable file names. The weakest identity, and never proof that the
/// named process owns the window.
/// </param>
/// <param name="Triggers">The window events that activate the rule.</param>
/// <param name="SwitchPolicy">
/// When a match may switch the foreground desktop.
/// </param>
/// <param name="PackageFamilyNames">
/// Optional packaged application identities. The strongest signal.
/// </param>
/// <param name="AppUserModelIds">
/// Optional shell application identities.
/// </param>
public sealed record ApplicationRule(
    string Id,
    string DisplayName,
    bool IsEnabled,
    string TargetDesktopKey,
    ImmutableArray<string> ProcessNames,
    ImmutableArray<ApplicationRuleTrigger> Triggers,
    DesktopSwitchPolicy SwitchPolicy,
    ImmutableArray<string> PackageFamilyNames = default,
    ImmutableArray<string> AppUserModelIds = default)
{
    /// <summary>
    /// Packaged application identities, normalized so an omitted collection is
    /// empty rather than a default array. A default array cannot be enumerated,
    /// so an unnormalized value would fail to serialize.
    /// </summary>
    public ImmutableArray<string> PackageFamilyNames { get; init; } =
        PackageFamilyNames.IsDefault ? [] : PackageFamilyNames;

    /// <summary>
    /// Shell application identities, normalized the same way as
    /// <see cref="PackageFamilyNames"/>.
    /// </summary>
    public ImmutableArray<string> AppUserModelIds { get; init; } =
        AppUserModelIds.IsDefault ? [] : AppUserModelIds;
}

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

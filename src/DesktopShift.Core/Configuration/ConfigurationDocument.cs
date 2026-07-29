using System.Collections.Immutable;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Hotkeys;

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
/// A process name is also shared by unrelated hosts. <c>msrdc.exe</c> is both
/// the Azure Virtual Desktop client and the WSL client that hosts every WSLg
/// Linux window, so naming it would claim windows that are not remote sessions.
/// </para>
/// <para>
/// <see cref="WindowClasses"/> is a refinement, not an identity. A class names
/// a window shape inside an application, so a rule that declares one still
/// needs a process name, packaged identity, or executable path to say which
/// application the shape belongs to. Declaring a class narrows a rule to the
/// windows that carry it and excludes every other surface the same process
/// creates.
/// </para>
/// <para>
/// Only the signals the identity resolver always collects are configurable
/// here. A window title and a command line are collected only when they are
/// explicitly opted into, so exposing them would let a document express a
/// refinement that could never match.
/// </para>
/// <para>
/// Every optional collection trails the required members, so every existing
/// construction site and every schema version 1 document written before they
/// existed keeps working. An absent collection reads as empty, never as a
/// default array.
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
/// <param name="ExecutablePaths">
/// Optional full executable paths. Stronger than a process name because a path
/// distinguishes two installations that share a file name.
/// </param>
/// <param name="WindowClasses">
/// Optional exact window class refinement. A rule that declares one matches
/// only windows carrying that class.
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
    ImmutableArray<string> AppUserModelIds = default,
    ImmutableArray<string> ExecutablePaths = default,
    ImmutableArray<string> WindowClasses = default)
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

    /// <summary>
    /// Full executable paths, normalized the same way as
    /// <see cref="PackageFamilyNames"/>.
    /// </summary>
    public ImmutableArray<string> ExecutablePaths { get; init; } =
        ExecutablePaths.IsDefault ? [] : ExecutablePaths;

    /// <summary>
    /// Exact window classes, normalized the same way as
    /// <see cref="PackageFamilyNames"/>.
    /// </summary>
    public ImmutableArray<string> WindowClasses { get; init; } =
        WindowClasses.IsDefault ? [] : WindowClasses;
}

/// <summary>
/// Every preference the Settings page persists.
/// </summary>
/// <remarks>
/// <para>
/// One record rather than seven, because the configuration document is the only
/// thing that is written atomically and recovered from a last-valid snapshot.
/// A preference kept anywhere else would be the one that survives a crash
/// differently from the rest.
/// </para>
/// <para>
/// Every member added after the first three trails the originals and carries the
/// value a document written before it existed should be read as. A schema
/// version 1 document with no <c>theme</c>, no <c>hotkeys</c>, and no
/// notification switches therefore still loads, and reads exactly as it behaved
/// before those settings existed — which is why the schema version did not have
/// to move.
/// </para>
/// </remarks>
/// <param name="StartWithWindows">
/// What the user asked for. Windows records separately what is actually
/// registered, and the two can disagree.
/// </param>
/// <param name="StartMinimized">Launch to the notification area only.</param>
/// <param name="CloseToTray">Closing the window hides it instead of exiting.</param>
/// <param name="Theme">
/// The appearance the shell starts in. Kept here rather than in a separate
/// preference store so it survives the same way every other setting does.
/// </param>
/// <param name="StartAssignmentPaused">
/// Whether automatic assignment starts paused. A user who pauses DesktopShift
/// because a task needs windows left alone should not have to pause it again
/// after every sign-in.
/// </param>
/// <param name="NotifyOnAssignmentFailure">
/// Whether a failed assignment earns a notification-area balloon. Successes are
/// silent by policy and are not configurable — see
/// <c>TrayNotificationPolicy</c>.
/// </param>
/// <param name="NotifyOnCompatibilityWarning">
/// Whether entering Limited Mode, or a failed compatibility test, interrupts.
/// </param>
/// <param name="AreHotkeysEnabled">
/// The master switch for global shortcuts. Off by default: a global hotkey
/// takes its combination away from every other application on the machine, so
/// nothing is claimed until the user asks for it.
/// </param>
/// <param name="Hotkeys">The chord bound to each shortcut action.</param>
public sealed record BehaviorSettings(
    bool StartWithWindows,
    bool StartMinimized,
    bool CloseToTray,
    AppTheme Theme = AppTheme.System,
    bool StartAssignmentPaused = false,
    bool NotifyOnAssignmentFailure = true,
    bool NotifyOnCompatibilityWarning = true,
    bool AreHotkeysEnabled = false,
    ImmutableArray<HotkeyBinding> Hotkeys = default)
{
    /// <summary>
    /// Hotkey bindings, normalized so an omitted collection is empty rather than
    /// a default array. A default array cannot be enumerated, so an unnormalized
    /// value would fail to serialize.
    /// </summary>
    public ImmutableArray<HotkeyBinding> Hotkeys { get; init; } =
        Hotkeys.IsDefault ? [] : Hotkeys;

    /// <summary>
    /// The hotkey configuration as the coordinator consumes it, with a binding
    /// filled in for every action so a document written before hotkeys existed
    /// still describes four shortcuts.
    /// </summary>
    public HotkeySettings ToHotkeySettings() =>
        new(AreHotkeysEnabled, HotkeyDefaults.Complete(Hotkeys));
}

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

using System;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.App.Settings;

/// <summary>
/// Everything the Settings page reads, writes, and proves things through.
/// </summary>
/// <remarks>
/// Bundled into one record for the same reason the Rules page bundles its own:
/// the page is navigated to and refreshed repeatedly, and a long parameter list
/// repeated at every refresh is where a wiring mistake hides.
/// </remarks>
/// <param name="BehaviorSettings">
/// Reads and persists every setting the configuration document carries, and
/// reports the startup registration Windows actually holds rather than the one
/// that was asked for.
/// </param>
/// <param name="ConfigurationExchange">
/// Imports and exports portable configuration. Import goes through the same
/// candidate/active split as every other edit.
/// </param>
/// <param name="Hotkeys">
/// Owns the gap between the shortcuts the document declares and the
/// combinations Windows is actually holding.
/// </param>
/// <param name="DesktopSwitchHotkeys">
/// The same job for the ten desktop-switching combinations. Separate because the
/// two sets are claimed and refused independently.
/// </param>
/// <param name="Compatibility">
/// Supplies what this Windows build allows, and runs the compatibility test on
/// request.
/// </param>
/// <param name="Diagnostics">
/// Supplies the log location and the two explicit diagnostic actions: export a
/// bundle, and clear local activity.
/// </param>
/// <param name="AssignmentPause">
/// The live pause switch. Distinct from the persisted
/// <see cref="Core.Configuration.BehaviorSettings.StartAssignmentPaused"/>,
/// which only decides what the switch starts as.
/// </param>
/// <param name="WindowReassignment">
/// Runs the manual reassignment batch behind "reassign every window now".
/// </param>
/// <param name="TimeProvider">Supplies the moment the page renders.</param>
/// <param name="ApplyAcceptedBehavior">
/// Applies settings that have live runtime effects after the configuration
/// service accepts them. Start-assignment-paused remains a next-launch choice.
/// </param>
public sealed record SettingsPageServices(
    BehaviorSettingsCommand BehaviorSettings,
    IConfigurationExchangeService ConfigurationExchange,
    IGlobalHotkeyCoordinator Hotkeys,
    ICompatibilityCoordinator Compatibility,
    IDiagnosticsCoordinator Diagnostics,
    IAutomaticAssignmentPauseController AssignmentPause,
    IWindowReassignmentService WindowReassignment,
    IThemePreferenceService ThemePreference,
    TimeProvider TimeProvider,
    Action<BehaviorSettings>? ApplyAcceptedBehavior = null,
    IDesktopSwitchHotkeyCoordinator? DesktopSwitchHotkeys = null);

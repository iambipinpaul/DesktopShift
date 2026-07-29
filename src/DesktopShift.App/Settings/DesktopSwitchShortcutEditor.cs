using DesktopShift.Core.Hotkeys;

namespace DesktopShift.App.Settings;

/// <summary>
/// Editable presentation of the desktop-switching shortcut profile.
/// </summary>
/// <remarks>
/// <para>
/// The four modifier flags are always live, even while a built-in profile is
/// selected. A user who tries Ctrl + Alt, switches to Custom to add Shift, then
/// goes back to compare would otherwise find their custom combination wiped by
/// the round trip.
/// </para>
/// <para>
/// Everything the Settings page shows about the choice — the warning, the
/// summary line, whether the modifier boxes do anything — is computed here
/// rather than in the page, because a rule that lives only in markup cannot be
/// tested.
/// </para>
/// </remarks>
public sealed class DesktopSwitchShortcutEditor
{
    public DesktopSwitchShortcutEditor(DesktopSwitchShortcutSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        IsEnabled = settings.IsEnabled;
        Profile = settings.Profile;
        Control = settings.CustomModifiers.HasFlag(HotkeyModifiers.Control);
        Alt = settings.CustomModifiers.HasFlag(HotkeyModifiers.Alt);
        Shift = settings.CustomModifiers.HasFlag(HotkeyModifiers.Shift);
        Windows = settings.CustomModifiers.HasFlag(HotkeyModifiers.Windows);
    }

    /// <summary>Whether the ten combinations are claimed at all.</summary>
    public bool IsEnabled { get; set; }

    /// <summary>The selected profile. Exactly one is active.</summary>
    public DesktopSwitchShortcutProfile Profile { get; set; }

    public bool Control { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    public bool Windows { get; set; }

    /// <summary>Whether the modifier boxes affect anything right now.</summary>
    public bool IsCustom => Profile == DesktopSwitchShortcutProfile.Custom;

    /// <summary>The modifiers the current selection actually claims.</summary>
    public HotkeyModifiers ActiveModifiers =>
        DesktopSwitchShortcuts.ModifiersFor(Profile, CustomModifiers);

    /// <summary>The modifiers the Custom profile would use.</summary>
    public HotkeyModifiers CustomModifiers
    {
        get
        {
            HotkeyModifiers modifiers = HotkeyModifiers.None;
            modifiers |= Control ? HotkeyModifiers.Control : HotkeyModifiers.None;
            modifiers |= Alt ? HotkeyModifiers.Alt : HotkeyModifiers.None;
            modifiers |= Shift ? HotkeyModifiers.Shift : HotkeyModifiers.None;
            modifiers |= Windows ? HotkeyModifiers.Windows : HotkeyModifiers.None;
            return modifiers;
        }
    }

    /// <summary>
    /// What the user has to be told before this profile is claimed, or null when
    /// it costs them nothing.
    /// </summary>
    public string? Warning => DesktopSwitchShortcuts.Warn(Profile);

    /// <summary>
    /// The line naming what the ten shortcuts will be, so the choice is legible
    /// without pressing anything.
    /// </summary>
    public string Summary => DesktopSwitchShortcuts.Summarize(ActiveModifiers);

    /// <summary>The configuration this editor describes.</summary>
    public DesktopSwitchShortcutSettings ToSettings() =>
        new(IsEnabled, Profile, CustomModifiers);
}

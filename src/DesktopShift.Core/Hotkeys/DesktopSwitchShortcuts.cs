using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// Turns a desktop-switching profile into the ten combinations it stands for,
/// and decides which profiles could never work.
/// </summary>
/// <remarks>
/// <para>
/// Pure on purpose, for the same reason <see cref="HotkeyValidation"/> is:
/// <c>RegisterHotKey</c> cannot be reached from a test, so every rule that can
/// be decided without Windows is decided here and the only question left for the
/// live registrar is the one Windows alone owns — whether something already
/// holds the combination.
/// </para>
/// <para>
/// The digit row is used rather than the numeric keypad. A keypad digit is a
/// different virtual key, is absent from most laptops, and changes meaning with
/// Num Lock, so a shortcut built on it would work on one machine and not the
/// next.
/// </para>
/// </remarks>
public static class DesktopSwitchShortcuts
{
    /// <summary>The JSON path every desktop-switching issue is reported under.</summary>
    public const string PathPrefix = "$.behavior.desktopSwitchShortcuts";

    /// <summary>The modifiers the recommended profile uses.</summary>
    public const HotkeyModifiers CtrlAltModifiers =
        HotkeyModifiers.Control | HotkeyModifiers.Alt;

    /// <summary>The modifiers the Windows-override profile uses.</summary>
    public const HotkeyModifiers WinAltModifiers =
        HotkeyModifiers.Windows | HotkeyModifiers.Alt;

    /// <summary>The lowest desktop position a shortcut can select.</summary>
    public const int MinDesktopOrdinal = 1;

    /// <summary>
    /// The highest desktop position a shortcut can select. Ten, because the
    /// digit row has ten keys and 0 is the tenth.
    /// </summary>
    public const int MaxDesktopOrdinal = 10;

    /// <summary>Every desktop position a shortcut covers, in ascending order.</summary>
    public static ImmutableArray<int> DesktopOrdinals { get; } =
    [
        .. Enumerable.Range(MinDesktopOrdinal, MaxDesktopOrdinal - MinDesktopOrdinal + 1),
    ];

    /// <summary>Every profile, in the order the Settings page lists them.</summary>
    public static ImmutableArray<DesktopSwitchShortcutProfile> Profiles { get; } =
    [
        DesktopSwitchShortcutProfile.CtrlAlt,
        DesktopSwitchShortcutProfile.WinAlt,
        DesktopSwitchShortcutProfile.Custom,
    ];

    /// <summary>
    /// The digit key that selects a desktop position.
    /// </summary>
    /// <remarks>
    /// Positions 1 through 9 use the matching digit. Position 10 uses 0, because
    /// the digit row runs 1 to 0 left to right and 0 is where a tenth key would
    /// be if the row had one.
    /// </remarks>
    /// <param name="desktopOrdinal">The one-based desktop position.</param>
    /// <returns>The digit key, or <see cref="HotkeyKey.None"/> when out of range.</returns>
    public static HotkeyKey KeyFor(int desktopOrdinal) => desktopOrdinal switch
    {
        >= MinDesktopOrdinal and < MaxDesktopOrdinal =>
            HotkeyKey.D0 + desktopOrdinal,
        MaxDesktopOrdinal => HotkeyKey.D0,
        _ => HotkeyKey.None,
    };

    /// <summary>
    /// The desktop position a digit key selects — the inverse of
    /// <see cref="KeyFor"/>.
    /// </summary>
    /// <param name="key">The digit key.</param>
    /// <returns>The one-based position, or zero when the key is not a digit.</returns>
    public static int DesktopOrdinalFor(HotkeyKey key) => key switch
    {
        HotkeyKey.D0 => MaxDesktopOrdinal,
        >= HotkeyKey.D1 and <= HotkeyKey.D9 => key - HotkeyKey.D0,
        _ => 0,
    };

    /// <summary>The modifiers a profile claims.</summary>
    /// <param name="profile">The selected profile.</param>
    /// <param name="customModifiers">
    /// What the Custom profile uses. Ignored by the other two.
    /// </param>
    public static HotkeyModifiers ModifiersFor(
        DesktopSwitchShortcutProfile profile,
        HotkeyModifiers customModifiers) => profile switch
        {
            DesktopSwitchShortcutProfile.CtrlAlt => CtrlAltModifiers,
            DesktopSwitchShortcutProfile.WinAlt => WinAltModifiers,
            DesktopSwitchShortcutProfile.Custom => customModifiers,
            _ => HotkeyModifiers.None,
        };

    /// <summary>
    /// The ten bindings a set of modifiers produces, in desktop order.
    /// </summary>
    /// <param name="modifiers">The modifiers every chord carries.</param>
    public static ImmutableArray<DesktopSwitchHotkeyBinding> Bindings(
        HotkeyModifiers modifiers) =>
    [
        .. DesktopOrdinals.Select(ordinal =>
            new DesktopSwitchHotkeyBinding(
                ordinal,
                new HotkeyChord(modifiers, KeyFor(ordinal)))),
    ];

    /// <summary>The profile's name, as the Settings page labels the choice.</summary>
    public static string Describe(DesktopSwitchShortcutProfile profile) =>
        profile switch
        {
            DesktopSwitchShortcutProfile.CtrlAlt => "Ctrl + Alt + Number",
            DesktopSwitchShortcutProfile.WinAlt => "Win + Alt + Number",
            DesktopSwitchShortcutProfile.Custom => "Custom",
            _ => profile.ToString(),
        };

    /// <summary>
    /// The one line under the profile's name, which is where the cost of
    /// choosing it is stated.
    /// </summary>
    public static string Explain(DesktopSwitchShortcutProfile profile) =>
        profile switch
        {
            DesktopSwitchShortcutProfile.CtrlAlt => "Recommended",
            DesktopSwitchShortcutProfile.WinAlt =>
                "Overrides Windows taskbar shortcuts",
            DesktopSwitchShortcutProfile.Custom =>
                "Choose any combination of Ctrl, Alt, Shift, and Win",
            _ => string.Empty,
        };

    /// <summary>
    /// What the user has to be told before a profile is claimed, or null when a
    /// profile costs them nothing.
    /// </summary>
    /// <remarks>
    /// Win + Alt + digit is the taskbar Jump List shortcut: it opens the recent
    /// files list for the nth pinned application. Claiming it means that stops
    /// working, and — because Windows hands the combination over only if the
    /// shell has not already taken it — it may not be claimable at all. Both
    /// halves of that are the user's to weigh, so both are said before they
    /// choose rather than after a registration fails.
    /// </remarks>
    public static string? Warn(DesktopSwitchShortcutProfile profile) =>
        profile switch
        {
            DesktopSwitchShortcutProfile.WinAlt =>
                "Win + Alt + a number opens the Jump List for the matching taskbar app. Using it here replaces that, and Windows may refuse the combination outright if the taskbar claimed it first — DesktopShift will say so if that happens.",
            _ => null,
        };

    /// <summary>
    /// A sentence naming the whole set, for a summary that should not list ten
    /// combinations.
    /// </summary>
    public static string Summarize(HotkeyModifiers modifiers)
    {
        if (modifiers == HotkeyModifiers.None)
        {
            return "No modifier is selected, so nothing can be registered.";
        }

        string prefix = new HotkeyChord(modifiers, HotkeyKey.D1)
            .Describe()[..^1];
        return
            $"{prefix}1 through {prefix}9 switch to Desktops 1 to 9, and {prefix}0 switches to Desktop 10.";
    }

    /// <summary>
    /// Reports every problem a desktop-switching configuration carries.
    /// </summary>
    /// <remarks>
    /// Judged on the settings' own <see cref="DesktopSwitchShortcutSettings.IsEnabled"/>
    /// flag only for the rules that depend on it. A Custom profile with no
    /// modifier is broken whether or not it is switched on, and saying so only
    /// after the switch is flipped would hide the mistake behind an unrelated
    /// toggle.
    /// </remarks>
    /// <param name="settings">The configuration as the document carries it.</param>
    /// <returns>One issue per problem.</returns>
    public static ImmutableArray<ConfigurationValidationIssue> Validate(
        DesktopSwitchShortcutSettings? settings)
    {
        if (settings is null)
        {
            return [];
        }

        ImmutableArray<ConfigurationValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();

        if (!Enum.IsDefined(settings.Profile))
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.InvalidDesktopSwitchShortcutValue,
                $"Desktop switching shortcut profile value '{(int)settings.Profile}' is not supported.",
                $"{PathPrefix}.profile",
                ConfigurationEntryKind.Hotkey,
                DesktopSwitchShortcutEntryId));
            return issues.ToImmutable();
        }

        const HotkeyModifiers supported =
            HotkeyModifiers.Alt |
            HotkeyModifiers.Control |
            HotkeyModifiers.Shift |
            HotkeyModifiers.Windows;
        if ((settings.CustomModifiers & ~supported) != 0)
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.InvalidDesktopSwitchShortcutValue,
                "The custom desktop switching shortcut contains unsupported modifier bits.",
                $"{PathPrefix}.customModifiers",
                ConfigurationEntryKind.Hotkey,
                DesktopSwitchShortcutEntryId));
            return issues.ToImmutable();
        }

        // A bare digit would be taken from every application on the machine, so
        // typing a number would stop working everywhere. Windows registers it
        // happily, which is exactly why it is refused here.
        if (settings.ActiveModifiers == HotkeyModifiers.None)
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.DesktopSwitchShortcutMissingModifier,
                "Desktop switching shortcuts need at least one of Ctrl, Alt, Shift, or Win. The number keys on their own would stop working in every other application.",
                $"{PathPrefix}.customModifiers",
                ConfigurationEntryKind.Hotkey,
                DesktopSwitchShortcutEntryId));
        }

        return issues.ToImmutable();
    }

    /// <summary>
    /// The entry id every desktop-switching issue carries, so the Settings page
    /// can find them without matching on message text.
    /// </summary>
    public const string DesktopSwitchShortcutEntryId = "DesktopSwitchShortcuts";

    /// <summary>
    /// Whether a configuration is claimable — enabled, understood, and carrying
    /// at least one modifier.
    /// </summary>
    /// <remarks>
    /// Handing an invalid profile to the registrar would claim ten bare digits
    /// or fail ten times over a problem already reported once.
    /// </remarks>
    public static bool IsRegistrable(DesktopSwitchShortcutSettings? settings) =>
        settings is not null &&
        settings.IsEnabled &&
        Validate(settings).IsEmpty;
}

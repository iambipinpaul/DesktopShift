using System.Collections.Immutable;

namespace DesktopShift.App.Settings;

/// <summary>
/// The nine areas the Settings page covers.
/// </summary>
public enum SettingsSection
{
    Startup,
    Assignment,
    Desktops,
    Compatibility,
    Notifications,
    Appearance,
    Diagnostics,
    Hotkeys,
    DesktopSwitching,
}

/// <summary>One section's heading, as the page renders it.</summary>
/// <param name="Section">Which section this is.</param>
/// <param name="Key">The stable key, used for automation names and navigation.</param>
/// <param name="Title">The heading text.</param>
/// <param name="Description">The sentence under the heading.</param>
public sealed record SettingsSectionDescriptor(
    SettingsSection Section,
    string Key,
    string Title,
    string Description);

/// <summary>
/// Names every Settings section, in the order the page shows them.
/// </summary>
/// <remarks>
/// <para>
/// The page reads its headings from here rather than carrying them in XAML. A
/// heading that lives only in markup cannot be tested, and "the Settings page
/// covers these nine areas" is exactly the kind of claim that quietly stops
/// being true when a card is moved or removed.
/// </para>
/// <para>
/// The order is the order a user meets the application in: what happens at
/// sign-in, then what it does to windows while running, then what it does to
/// the desktops themselves, then what the machine allows, then how loudly it
/// speaks, then how it looks, then how to diagnose it, and finally the two
/// optional shortcut sets — the commands first, then desktop switching, because
/// a user who wants neither can stop reading at Diagnostics.
/// </para>
/// </remarks>
public static class SettingsSectionCatalog
{
    /// <summary>Every section, in display order.</summary>
    public static ImmutableArray<SettingsSectionDescriptor> Sections { get; } =
    [
        new(
            SettingsSection.Startup,
            "startup",
            "Startup and window behavior",
            "DesktopShift runs from the notification area. These settings decide when it starts and what closing its window does."),
        new(
            SettingsSection.Assignment,
            "assignment",
            "Assignment",
            "Automatic assignment reacts to window events. Pausing stops that; it never stops an assignment you ask for."),
        new(
            SettingsSection.Desktops,
            "desktops",
            "Desktops",
            "Task View shows its own names. DesktopShift can keep those names matching your Managed Desktops, so Win+Tab reads Code and Web rather than Desktop 2 and Desktop 3."),
        new(
            SettingsSection.Compatibility,
            "compatibility",
            "Windows compatibility",
            "DesktopShift reports only capabilities proven available through its selected provider."),
        new(
            SettingsSection.Notifications,
            "notifications",
            "Notifications",
            "Successful assignments are always silent. Choose which problems are allowed to interrupt you."),
        new(
            SettingsSection.Appearance,
            "appearance",
            "Appearance",
            "Choose System, Light, or Dark. The choice is saved with the rest of your settings and applies on the next launch too."),
        new(
            SettingsSection.Diagnostics,
            "diagnostics",
            "Diagnostics",
            "Logs stay on this machine. Nothing is uploaded, and a bundle is only ever produced when you ask for one."),
        new(
            SettingsSection.Hotkeys,
            "hotkeys",
            "Global shortcuts",
            "A global shortcut takes its key combination away from every other application. Nothing is claimed until you turn shortcuts on."),
        new(
            SettingsSection.DesktopSwitching,
            "desktop-switching",
            "Desktop switching shortcut",
            "One profile puts Desktops 1 to 10 on the number row. Pick the modifiers once; the digits are fixed so the whole set stays predictable."),
    ];

    /// <summary>
    /// Finds one section's heading.
    /// </summary>
    /// <param name="section">The section wanted.</param>
    /// <returns>Its descriptor.</returns>
    /// <exception cref="ArgumentOutOfRangeException">The section is not in the catalog.</exception>
    public static SettingsSectionDescriptor Get(SettingsSection section) =>
        Sections.FirstOrDefault(descriptor => descriptor.Section == section) ??
            throw new ArgumentOutOfRangeException(
                nameof(section),
                section,
                "The settings section is not in the catalog.");
}

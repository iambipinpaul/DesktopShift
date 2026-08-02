using System.Collections.Immutable;

namespace DesktopShift.Core.Observation;

/// <summary>
/// One visible group of windows whose placement remains under Windows control.
/// </summary>
public sealed record WindowsManagedWindowEntry(
    string Id,
    string DisplayName,
    string Description,
    ImmutableArray<string> ProcessNames)
{
    public ImmutableArray<string> ProcessNames { get; init; } =
        ProcessNames.IsDefault ? [] : ProcessNames;

    public string ProcessSummary => string.Join(", ", ProcessNames);
}

/// <summary>
/// The immutable safety policy shared by Windows classification and the Rules
/// page. Entries are visible so a skipped native surface is expected behavior,
/// not a hidden exception.
/// </summary>
public static class WindowsManagedWindowCatalog
{
    private static readonly ImmutableArray<WindowsManagedWindowEntry> Items =
    [
        new(
            "credentials-and-passkeys",
            "Credentials and passkeys",
            "Sign-in, passkey, PIN, and security confirmation windows stay where Windows opens them.",
            ["CredentialUIBroker.exe"]),
        new(
            "built-in-windows-tools",
            "Built-in Windows tools",
            "Windows Settings and other operating-system tools stay where Windows opens them.",
            ["SystemSettings.exe"]),
        new(
            "windows-shell",
            "Windows shell surfaces",
            "Start, Search, lock screen, text input, taskbar, and desktop shell surfaces stay under Windows control.",
            [
                "dwm.exe",
                "LockApp.exe",
                "SearchHost.exe",
                "ShellExperienceHost.exe",
                "sihost.exe",
                "StartMenuExperienceHost.exe",
                "TextInputHost.exe",
            ]),
    ];

    private static readonly HashSet<string> ProcessNames =
        new(
            Items.SelectMany(static entry => entry.ProcessNames),
            StringComparer.OrdinalIgnoreCase);

    public static ImmutableArray<WindowsManagedWindowEntry> Entries => Items;

    public static bool IsManagedProcessName(string processName) =>
        !string.IsNullOrWhiteSpace(processName) && ProcessNames.Contains(processName);
}

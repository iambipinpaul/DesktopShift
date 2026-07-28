using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

public static class ConfigurationDefaults
{
    // Still version 1: packageFamilyNames and appUserModelIds are optional
    // trailing members, so a document written before they existed deserializes
    // with them absent and they read as empty.
    public const int CurrentSchemaVersion = 1;

    private static readonly ImmutableArray<ApplicationRuleTrigger> DefaultTriggers =
    [
        ApplicationRuleTrigger.WindowCreated,
        ApplicationRuleTrigger.WindowShown,
        ApplicationRuleTrigger.ForegroundActivated,
        ApplicationRuleTrigger.StartupReconciliation,
        ApplicationRuleTrigger.ManualReassignment,
    ];

    public static ConfigurationDocument Create()
    {
        return new ConfigurationDocument(
            CurrentSchemaVersion,
            [
                new ManagedDesktopDefinition("code", "Code", 1, true),
                new ManagedDesktopDefinition("web", "Web", 2, true),
                new ManagedDesktopDefinition("terminal", "Terminal", 3, true),
                new ManagedDesktopDefinition("remote", "Remote", 4, true),
            ],
            [
                CreateRule(
                    "vscode",
                    "Visual Studio Code",
                    "code",
                    ["Code.exe"]),
                CreateRule(
                    "browsers",
                    "Microsoft Edge and Google Chrome",
                    "web",
                    ["msedge.exe", "chrome.exe"]),
                CreateRule(
                    "windows-terminal",
                    "Windows Terminal",
                    "terminal",
                    // wt.exe is a launch signal only. It forwards its command
                    // line to the already running WindowsTerminal.exe host and
                    // exits, so it never owns the window it opens.
                    ["WindowsTerminal.exe"],
                    packageFamilyNames:
                    [
                        "Microsoft.WindowsTerminal_8wekyb3d8bbwe",
                        "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe",
                    ],
                    appUserModelIds:
                    [
                        "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
                        "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe!App",
                    ]),
                CreateRule(
                    "remote-desktop",
                    "Remote Desktop Connection",
                    "remote",
                    ["mstsc.exe"]),
            ],
            new BehaviorSettings(
                StartWithWindows: true,
                StartMinimized: true,
                CloseToTray: true));
    }

    private static ApplicationRule CreateRule(
        string id,
        string displayName,
        string targetDesktopKey,
        ImmutableArray<string> processNames,
        ImmutableArray<string> packageFamilyNames = default,
        ImmutableArray<string> appUserModelIds = default)
    {
        return new ApplicationRule(
            id,
            displayName,
            IsEnabled: true,
            targetDesktopKey,
            processNames,
            DefaultTriggers,
            DesktopSwitchPolicy.OnForegroundActivation,
            packageFamilyNames,
            appUserModelIds);
    }
}

using System.Collections.Immutable;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Core.Configuration;

public static class ConfigurationDefaults
{
    // Still version 1: packageFamilyNames, appUserModelIds, executablePaths and
    // windowClasses are optional trailing members, so a document written before
    // they existed deserializes with them absent and they read as empty. The
    // same is true of every setting added since — theme, the notification
    // switches, the assignment pause, and the hotkey bindings all trail the
    // required members of BehaviorSettings and read as their defaults when
    // absent, so a version 1 document on disk is still a current document and
    // nothing has to be migrated.
    public const int CurrentSchemaVersion = 1;

    private static readonly ImmutableArray<ApplicationRuleTrigger> AllTriggers =
    [
        ApplicationRuleTrigger.WindowCreated,
        ApplicationRuleTrigger.WindowShown,
        ApplicationRuleTrigger.ForegroundActivated,
        ApplicationRuleTrigger.StartupReconciliation,
        ApplicationRuleTrigger.ManualReassignment,
    ];

    /// <summary>
    /// Every trigger, in the order the editor and the rule summaries list them.
    /// It is also what the starter rules declare, so a rule created in the UI
    /// behaves like the ones shipped with the application until it is narrowed.
    /// </summary>
    public static ImmutableArray<ApplicationRuleTrigger> DefaultTriggers =>
        AllTriggers;

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
                    // The classic client is unpackaged, so a process name is the
                    // strongest identity it has. mstsc.exe owns every window a
                    // session produces: the connection dialog before a session
                    // exists, the TscShellContainerClass session frame, and the
                    // dialogs the frame owns.
                    //
                    // No window class is declared. Declaring one would narrow
                    // the rule to the shapes named here and silently unmanage
                    // any other session surface, and every surface mstsc.exe
                    // shows belongs on Remote anyway. A user who wants only
                    // connected sessions on Remote can add
                    // windowClasses: ["TscShellContainerClass"] to this rule.
                    //
                    // msrdc.exe is left out deliberately: the same file name is
                    // both the Azure Virtual Desktop client and the WSL client
                    // that hosts WSLg Linux windows, so it would claim windows
                    // that are not remote sessions.
                    //
                    // The packaged clients (the Store "Remote Desktop" app and
                    // "Windows App") are left out because their package family
                    // names could not be confirmed against an installed
                    // package, and an unverified identity is dead configuration.
                    ["mstsc.exe"]),
            ],
            new BehaviorSettings(
                StartWithWindows: true,
                StartMinimized: true,
                CloseToTray: true,
                // The shipped chords are written into the document even though
                // hotkeys start switched off, so the file says what the four
                // shortcuts are instead of leaving a user to discover them in
                // the UI. Nothing is claimed system-wide until
                // AreHotkeysEnabled is turned on.
                Hotkeys: HotkeyDefaults.Bindings));
    }

    private static ApplicationRule CreateRule(
        string id,
        string displayName,
        string targetDesktopKey,
        ImmutableArray<string> processNames,
        ImmutableArray<string> packageFamilyNames = default,
        ImmutableArray<string> appUserModelIds = default,
        ImmutableArray<string> executablePaths = default,
        ImmutableArray<string> windowClasses = default)
    {
        return new ApplicationRule(
            id,
            displayName,
            IsEnabled: true,
            targetDesktopKey,
            processNames,
            AllTriggers,
            DesktopSwitchPolicy.OnForegroundActivation,
            packageFamilyNames,
            appUserModelIds,
            executablePaths,
            windowClasses);
    }
}

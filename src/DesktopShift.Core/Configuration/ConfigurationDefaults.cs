using System.Collections.Immutable;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Core.Configuration;

public static class ConfigurationDefaults
{
    // Version 3 moves Windows Settings into the immutable Windows-managed
    // catalog and aligns the starter rule order with the managed desktops.
    // Version 2 put Run & Observe before IDE Development and combined the
    // untouched File Explorer and Notepad Anywhere defaults into one visible
    // group. packageFamilyNames, appUserModelIds,
    // executablePaths, windowClasses and action are optional trailing members,
    // so a document
    // written before they existed deserializes with them absent and they read as
    // empty, or in action's case as moveToDesktop — which is exactly how such a
    // document behaved. The same is true of every setting added since — theme,
    // the notification switches, the assignment pause, and the hotkey bindings
    // all trail the required members of BehaviorSettings and read as their
    // defaults when absent. The version-1 to version-2 and version-2 to
    // version-3 migrations change only untouched shipped defaults; older
    // documents otherwise keep exactly the behavior they declared.
    public const int CurrentSchemaVersion = 3;

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

    /// <summary>
    /// The configuration a first run starts from.
    /// </summary>
    /// <remarks>
    /// <para>
    /// A semantic key is durable identity written to the binding store; a
    /// display name is a label the user can retype. They are kept separate even
    /// where they read alike, because making them one value would mean renaming
    /// a desktop lost its binding and stranded the user's windows on the old
    /// one.
    /// </para>
    /// <para>
    /// The Anywhere rules exist so the system utilities a user opens constantly
    /// are exempt from the sweep that sends every unnamed window to the first
    /// desktop. Every identity below was confirmed against a real Windows 11
    /// install: an identity no running window can report is dead configuration,
    /// so an unverified one is worse than an absent one.
    /// </para>
    /// </remarks>
    /// <returns>The shipped document.</returns>
    public static ConfigurationDocument Create()
    {
        return new ConfigurationDocument(
            CurrentSchemaVersion,
            [
                new ManagedDesktopDefinition("run-observe", "Run & Observe", 1, true),
                new ManagedDesktopDefinition(
                    "ide-development",
                    "IDE Development",
                    2,
                    true),
                new ManagedDesktopDefinition(
                    "agent-development",
                    "Agent Development",
                    3,
                    true),
                new ManagedDesktopDefinition(
                    "infrastructure",
                    "Infrastructure",
                    4,
                    true),
                new ManagedDesktopDefinition("remote", "Remote", 5, true),
            ],
            [
                CreateRule(
                    "run-observe",
                    "Run & Observe",
                    "run-observe",
                    ["msedge.exe"]),
                CreateRule(
                    "ide-development",
                    "IDE Development",
                    "ide-development",
                    ["Code.exe", "devenv.exe"]),
                CreateRule(
                    "agent-development",
                    "Agent Development",
                    "agent-development",
                    ["claude.exe", "ChatGPT.exe", "codex.exe"]),
                CreateRule(
                    "infrastructure",
                    "Infrastructure",
                    "infrastructure",
                    // wt.exe and OpenConsole.exe are deliberately absent.
                    // wt.exe is a launch signal only: it forwards its command
                    // line to the already running WindowsTerminal.exe host and
                    // exits, so it never owns the window it opens.
                    // OpenConsole.exe is the windowless ConPTY host. Neither
                    // can ever match.
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
                    "remote",
                    "Remote",
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

                // One visible default groups the Windows applications opened
                // *from* wherever the user already is and expected to stay
                // there: Explorer from a taskbar pin or file dialog and Notepad
                // for a scratch note. The identities are alternatives, so one
                // rule can represent the group without behaving like a wildcard.
                //
                // Calculator and Task Manager were verified to exist and are
                // still left out. Paint and Photos are out for the stronger
                // reason that they are ordinary applications somebody may well
                // want placed.
                //
                // All four are two clicks away in the rule editor for anyone who
                // disagrees, and that is the point: an exemption the user chose
                // is better than one chosen for them.
                CreateAnywhereRule(
                    "default-anywhere",
                    "Default — stays where opened",
                    ["explorer.exe", "Notepad.exe"],
                    packageFamilyNames: ["Microsoft.WindowsNotepad_8wekyb3d8bbwe"],
                    executablePaths: [@"C:\Windows\explorer.exe"]),
            ],
            new BehaviorSettings(
                StartWithWindows: true,
                StartMinimized: true,
                CloseToTray: true,
                // Routine activity is useful while troubleshooting, but it is
                // noisy during ordinary use and writes rotating files. The
                // first-run dialog and Activity page make opting in explicit.
                RecordLocalActivity: false));
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

    /// <summary>
    /// Creates a rule that exempts an application from placement entirely.
    /// </summary>
    /// <remarks>
    /// The triggers and the switch policy are still filled in, with the same
    /// values every other shipped rule carries. Neither is read while the
    /// destination is Anywhere, and both are hidden in the editor — but a user
    /// who switches the destination back to a Managed Desktop should find a
    /// working rule rather than an empty trigger list.
    /// </remarks>
    private static ApplicationRule CreateAnywhereRule(
        string id,
        string displayName,
        ImmutableArray<string> processNames,
        ImmutableArray<string> packageFamilyNames = default,
        ImmutableArray<string> appUserModelIds = default,
        ImmutableArray<string> executablePaths = default)
    {
        return new ApplicationRule(
            id,
            displayName,
            IsEnabled: true,

            // Never read. An Anywhere rule has no destination, and a sentinel
            // key here could collide with a Managed Desktop a user really named
            // that, so the member is simply left empty.
            string.Empty,
            processNames,
            AllTriggers,
            DesktopSwitchPolicy.OnForegroundActivation,
            packageFamilyNames,
            appUserModelIds,
            executablePaths,
            WindowClasses: default,
            ApplicationRuleAction.AllowAnywhere);
    }
}

using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

public static class ConfigurationDefaults
{
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
                    ["WindowsTerminal.exe", "wt.exe"]),
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
        ImmutableArray<string> processNames)
    {
        return new ApplicationRule(
            id,
            displayName,
            IsEnabled: true,
            targetDesktopKey,
            processNames,
            DefaultTriggers,
            DesktopSwitchPolicy.OnForegroundActivation);
    }
}

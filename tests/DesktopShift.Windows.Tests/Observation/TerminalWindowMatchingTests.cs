using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class TerminalWindowMatchingTests
{
    private const string TerminalProcessName = "WindowsTerminal.exe";
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string ConsoleHostWindowClass = "ConsoleWindowClass";
    private const string StablePackageFamily =
        "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
    private const string PreviewPackageFamily =
        "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe";
    private const string StableAppUserModelId =
        "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
    private const string PreviewAppUserModelId =
        "Microsoft.WindowsTerminalPreview_8wekyb3d8bbwe!App";

    private static readonly IReadOnlyList<WindowObservationRule> DefaultRules =
        new ConfigurationWindowRuleSource(ConfigurationDefaults.Create).GetRules();

    [TestMethod]
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    [DataRow(WindowEventKind.ForegroundActivated)]
    [DataRow(WindowEventKind.StartupReconciliation)]
    [DataRow(WindowEventKind.ManualReassignment)]
    public void Match_PackagedTerminalTargetsTerminalOnEveryDefaultTrigger(
        WindowEventKind eventKind)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreatePackagedIdentity(StablePackageFamily, StableAppUserModelId),
            eventKind,
            DefaultRules);

        Assert.AreEqual("infrastructure", result!.Rule.Id);
        Assert.AreEqual("infrastructure", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, result.Strength);
    }

    [TestMethod]
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    [DataRow(WindowEventKind.ForegroundActivated)]
    [DataRow(WindowEventKind.StartupReconciliation)]
    [DataRow(WindowEventKind.ManualReassignment)]
    public void Match_PreviewTerminalTargetsTerminalOnEveryDefaultTrigger(
        WindowEventKind eventKind)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreatePackagedIdentity(PreviewPackageFamily, PreviewAppUserModelId),
            eventKind,
            DefaultRules);

        Assert.AreEqual("infrastructure", result!.Rule.Id);
        Assert.AreEqual("infrastructure", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, result.Strength);
    }

    [TestMethod]
    public void Match_UnpackagedTerminalStillTargetsTerminalByProcessName()
    {
        // A portable or unpackaged build reports no package identity at all.
        // Losing the strong signal must degrade to the process name rather
        // than to no match.
        WindowIdentity identity = CreateIdentity(TerminalProcessName);

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(identity.PackageFamilyName);
        Assert.IsNull(identity.AppUserModelId);
        Assert.AreEqual("infrastructure", result!.Rule.Id);
        Assert.AreEqual("infrastructure", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, result.Strength);
    }

    [TestMethod]
    [DataRow(StableAppUserModelId, TerminalProcessName)]
    [DataRow(PreviewAppUserModelId, TerminalProcessName)]
    [DataRow(StableAppUserModelId, "ApplicationFrameHost.exe")]
    public void Match_AppUserModelIdAloneIdentifiesTheTerminal(
        string appUserModelId,
        string processName)
    {
        WindowIdentity identity = CreateIdentity(processName) with
        {
            AppUserModelId = appUserModelId,
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(identity.PackageFamilyName);
        Assert.AreEqual("infrastructure", result!.Rule.Id);
        Assert.AreEqual("infrastructure", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.AppUserModelId, result.Strength);
    }

    [TestMethod]
    public void Match_LauncherStubIsNotATerminalWindow()
    {
        // wt.exe is a launcher stub. It forwards its command line to the
        // already running WindowsTerminal.exe host and exits, so it never owns
        // a lasting window. Treating it as a match signal assigns a desktop on
        // behalf of a window wt.exe does not own, which is the false
        // assignment this rule exists to avoid.
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity("wt.exe"),
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(result);
    }

    [TestMethod]
    [DataRow("conhost.exe")]
    [DataRow("OpenConsole.exe")]
    [DataRow("powershell.exe")]
    [DataRow("pwsh.exe")]
    [DataRow("cmd.exe")]
    public void Match_TerminalAdjacentProcessesDoNotMatchTheTerminalRule(
        string processName)
    {
        // A shell hosted inside a terminal tab is not the terminal window, and
        // neither is the console host that draws a legacy console.
        WindowIdentity identity = CreateIdentity(processName) with
        {
            WindowClass = ConsoleHostWindowClass,
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(result);
    }

    [TestMethod]
    [DataRow("MICROSOFT.WINDOWSTERMINAL_8WEKYB3D8BBWE")]
    [DataRow("microsoft.windowsterminalpreview_8wekyb3d8bbwe")]
    public void Match_PackagedTerminalIdentityIsCaseInsensitive(
        string packageFamilyName)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreatePackagedIdentity(packageFamilyName, appUserModelId: null),
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("infrastructure", result!.Rule.Id);
        Assert.AreEqual("infrastructure", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, result.Strength);
    }

    [TestMethod]
    [DataRow("windowsterminal.exe")]
    [DataRow("WINDOWSTERMINAL.EXE")]
    public void Match_TerminalProcessNameIsCaseInsensitive(string processName)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity(processName),
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("infrastructure", result!.Rule.Id);
        Assert.AreEqual("infrastructure", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, result.Strength);
    }

    [TestMethod]
    public void Match_EachTerminalWindowIsMatchedIndependently()
    {
        WindowIdentity packaged = CreatePackagedIdentity(
            StablePackageFamily,
            StableAppUserModelId) with
        {
            ProcessId = 4100,
        };
        WindowIdentity portable = CreateIdentity(TerminalProcessName) with
        {
            ProcessId = 9200,
        };
        WindowRuleMatcher matcher = new();

        WindowRuleMatch? first = matcher.Match(
            packaged,
            WindowEventKind.Shown,
            DefaultRules);
        WindowRuleMatch? second = matcher.Match(
            portable,
            WindowEventKind.ForegroundActivated,
            DefaultRules);
        WindowRuleMatch? firstAgain = matcher.Match(
            packaged,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("infrastructure", first!.Rule.Id);
        Assert.AreEqual("infrastructure", second!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, first.Strength);
        Assert.AreEqual(WindowMatchStrength.ProcessName, second.Strength);
        Assert.AreEqual(first.Strength, firstAgain!.Strength);
        Assert.AreEqual(first.Rule.TargetDesktopKey, second.Rule.TargetDesktopKey);
    }

    [TestMethod]
    [DataRow("Code.exe", "Chrome_WidgetWin_1", "ide-development", "ide-development")]
    [DataRow("msedge.exe", "Chrome_WidgetWin_1", "run-observe", "run-observe")]
    [DataRow("mstsc.exe", "TscShellContainerClass", "remote", "remote")]
    [DataRow(
        TerminalProcessName,
        TerminalWindowClass,
        "infrastructure",
        "infrastructure")]
    public void Match_DefaultRulesDoNotClaimEachOthersWindows(
        string processName,
        string windowClass,
        string expectedRuleId,
        string expectedDesktopKey)
    {
        WindowIdentity identity = CreateIdentity(processName) with
        {
            WindowClass = windowClass,
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.ForegroundActivated,
            DefaultRules);

        Assert.AreEqual(expectedRuleId, result!.Rule.Id);
        Assert.AreEqual(expectedDesktopKey, result.Rule.TargetDesktopKey);
    }

    private static WindowIdentity CreatePackagedIdentity(
        string packageFamilyName,
        string? appUserModelId) =>
        CreateIdentity(TerminalProcessName) with
        {
            PackageFamilyName = packageFamilyName,
            AppUserModelId = appUserModelId,
        };

    private static WindowIdentity CreateIdentity(string processName) =>
        new(
            1,
            processName,
            ExecutablePath: null,
            PackageFamilyName: null,
            AppUserModelId: null,
            TerminalWindowClass,
            WindowTitle: null,
            CommandLine: null);
}

using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class BrowserWindowMatchingTests
{
    private const string EdgePackageFamilyName =
        "Microsoft.MicrosoftEdge.Stable_8wekyb3d8bbwe";
    private const string EdgeExecutablePath =
        @"C:\Program Files (x86)\Microsoft\Edge\Application\msedge.exe";

    private static readonly ImmutableArray<ApplicationRuleTrigger> AllTriggers =
    [
        ApplicationRuleTrigger.WindowCreated,
        ApplicationRuleTrigger.WindowShown,
        ApplicationRuleTrigger.ForegroundActivated,
        ApplicationRuleTrigger.StartupReconciliation,
        ApplicationRuleTrigger.ManualReassignment,
    ];

    private static readonly IReadOnlyList<WindowObservationRule> DefaultRules =
        new ConfigurationWindowRuleSource(ConfigurationDefaults.Create).GetRules();

    [TestMethod]
    [DataRow("msedge.exe", WindowEventKind.Created)]
    [DataRow("msedge.exe", WindowEventKind.Shown)]
    [DataRow("msedge.exe", WindowEventKind.ForegroundActivated)]
    [DataRow("msedge.exe", WindowEventKind.StartupReconciliation)]
    [DataRow("msedge.exe", WindowEventKind.ManualReassignment)]
    public void Match_EdgeTargetsRunObserveOnEveryDefaultTrigger(
        string processName,
        WindowEventKind eventKind)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity(processName),
            eventKind,
            DefaultRules);

        Assert.AreEqual("run-observe", result!.Rule.Id);
        Assert.AreEqual("run-observe", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, result.Strength);
    }

    [TestMethod]
    public void Match_ChromeIsNotNamedByAnyShippedRule()
    {
        // The shipped Run & Observe rule names Edge only, so a Chrome window is
        // unmanaged: the sweep answers for it rather than a rule, and a user who
        // wants it placed writes the rule themselves.
        WindowIdentity chrome = CreateIdentity("chrome.exe");
        WindowRuleMatcher matcher = new();

        Assert.IsNull(matcher.Match(chrome, WindowEventKind.Shown, DefaultRules));
        Assert.IsFalse(matcher.IsNamedByAnyRule(chrome, DefaultRules));
    }

    [TestMethod]
    [DataRow("MSEDGE.EXE")]
    [DataRow("msEdge.Exe")]
    public void Match_BrowserProcessNamesAreCaseInsensitive(string processName)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity(processName),
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("run-observe", result!.Rule.Id);
        Assert.AreEqual("run-observe", result.Rule.TargetDesktopKey);
    }

    [TestMethod]
    public void Match_ProcessNameDoesNotIdentifyABrowserProfile()
    {
        WindowIdentity defaultProfile = CreateIdentity("msedge.exe") with
        {
            CommandLine = "msedge.exe --profile-directory=\"Default\"",
        };
        WindowIdentity secondProfile = CreateIdentity("msedge.exe") with
        {
            CommandLine = "msedge.exe --profile-directory=\"Profile 2\"",
        };
        WindowRuleMatcher matcher = new();

        WindowRuleMatch? first = matcher.Match(
            defaultProfile,
            WindowEventKind.Shown,
            DefaultRules);
        WindowRuleMatch? second = matcher.Match(
            secondProfile,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("run-observe", first!.Rule.Id);
        Assert.AreEqual("run-observe", second!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.ProcessName, first.Strength);
        Assert.AreEqual(WindowMatchStrength.ProcessName, second.Strength);
        Assert.AreEqual(first.Rule.TargetDesktopKey, second.Rule.TargetDesktopKey);
    }

    [TestMethod]
    [DataRow("msedgewebview2.exe")]
    [DataRow("chromedriver.exe")]
    [DataRow("chrome_proxy.exe")]
    public void Match_BrowserAdjacentProcessesDoNotMatchTheBrowsersRule(
        string processName)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity(processName),
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Match_ElectronShellReusingTheChromiumFrameClassKeepsItsOwnRule()
    {
        WindowIdentity identity = CreateIdentity("Code.exe");

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("Chrome_WidgetWin_1", identity.WindowClass);
        Assert.AreEqual("ide-development", result!.Rule.Id);
        Assert.AreEqual("ide-development", result.Rule.TargetDesktopKey);
    }

    [TestMethod]
    public void Match_PackageFamilyNameRuleOutranksTheProcessNameDefault()
    {
        WindowIdentity identity = CreateIdentity("msedge.exe") with
        {
            PackageFamilyName = EdgePackageFamilyName,
        };
        WindowObservationRule packagedRule = CreateRefinementRule(
            "browsers-packaged",
            new WindowMatchCriteria(
                [EdgePackageFamilyName],
                [],
                [],
                [],
                [],
                [],
                []));

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            [.. DefaultRules, packagedRule]);

        Assert.AreEqual("browsers-packaged", result!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, result.Strength);
    }

    [TestMethod]
    public void Match_ExecutablePathRuleOutranksTheProcessNameDefault()
    {
        WindowIdentity identity = CreateIdentity("msedge.exe") with
        {
            ExecutablePath = EdgeExecutablePath,
        };
        WindowObservationRule pathRule = CreateRefinementRule(
            "browsers-path",
            new WindowMatchCriteria(
                [],
                [],
                [EdgeExecutablePath],
                [],
                [],
                [],
                []));

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            [.. DefaultRules, pathRule]);

        Assert.AreEqual("browsers-path", result!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.ExecutablePath, result.Strength);
    }

    [TestMethod]
    public void Match_CommandLineProfileRuleIsExpressibleWithoutMatcherChanges()
    {
        WindowObservationRule workProfileRule = CreateRefinementRule(
            "browsers-work-profile",
            new WindowMatchCriteria(
                [EdgePackageFamilyName],
                [],
                [],
                [],
                [],
                [],
                ["--profile-directory=\"Profile 2\""]));
        WindowIdentity workProfile = CreateIdentity("msedge.exe") with
        {
            PackageFamilyName = EdgePackageFamilyName,
            CommandLine = "msedge.exe --profile-directory=\"Profile 2\"",
        };
        WindowIdentity personalProfile = CreateIdentity("msedge.exe") with
        {
            PackageFamilyName = EdgePackageFamilyName,
            CommandLine = "msedge.exe --profile-directory=\"Default\"",
        };
        WindowRuleMatcher matcher = new();

        WindowRuleMatch? refined = matcher.Match(
            workProfile,
            WindowEventKind.Shown,
            [.. DefaultRules, workProfileRule]);
        WindowRuleMatch? fallback = matcher.Match(
            personalProfile,
            WindowEventKind.Shown,
            [.. DefaultRules, workProfileRule]);

        Assert.AreEqual("browsers-work-profile", refined!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, refined.Strength);
        Assert.AreEqual("run-observe", fallback!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.ProcessName, fallback.Strength);
    }

    private static WindowObservationRule CreateRefinementRule(
        string id,
        WindowMatchCriteria criteria) =>
        new(
            id,
            id,
            IsEnabled: true,
            TargetDesktopKey: "web",
            AllTriggers,
            DesktopSwitchPolicy.OnForegroundActivation,
            criteria,
            Order: 99);

    private static WindowIdentity CreateIdentity(string processName) =>
        new(
            1,
            processName,
            ExecutablePath: null,
            PackageFamilyName: null,
            AppUserModelId: null,
            "Chrome_WidgetWin_1",
            WindowTitle: null,
            CommandLine: null);
}

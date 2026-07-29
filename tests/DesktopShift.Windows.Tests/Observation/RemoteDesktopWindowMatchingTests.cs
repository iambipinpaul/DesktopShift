using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class RemoteDesktopWindowMatchingTests
{
    private const string RemoteDesktopProcessName = "mstsc.exe";
    private const string SessionWindowClass = "TscShellContainerClass";
    private const string DialogWindowClass = "#32770";
    private const string ConnectionBarWindowClass = "BBarWindowClass";
    private const string RemoteDesktopExecutablePath =
        @"C:\Windows\System32\mstsc.exe";

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
    [DataRow(WindowEventKind.Created)]
    [DataRow(WindowEventKind.Shown)]
    [DataRow(WindowEventKind.ForegroundActivated)]
    [DataRow(WindowEventKind.StartupReconciliation)]
    [DataRow(WindowEventKind.ManualReassignment)]
    public void Match_SessionWindowTargetsRemoteOnEveryDefaultTrigger(
        WindowEventKind eventKind)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity(RemoteDesktopProcessName),
            eventKind,
            DefaultRules);

        Assert.AreEqual("remote", result!.Rule.Id);
        Assert.AreEqual("remote", result.Rule.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, result.Strength);
    }

    [TestMethod]
    [DataRow(SessionWindowClass)]
    [DataRow(DialogWindowClass)]
    [DataRow(ConnectionBarWindowClass)]
    public void Match_EveryRemoteDesktopSurfaceTargetsRemote(string windowClass)
    {
        // The default rule declares no window class on purpose. Every surface
        // mstsc.exe puts on screen — the connection dialog before a session
        // exists, the session frame, the connection bar — belongs on Remote, so
        // narrowing the rule to one shape would only leave the others unmanaged.
        WindowIdentity identity = CreateIdentity(RemoteDesktopProcessName) with
        {
            WindowClass = windowClass,
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("remote", result!.Rule.Id);
        Assert.AreEqual("remote", result.Rule.TargetDesktopKey);
    }

    [TestMethod]
    [DataRow("MSTSC.EXE")]
    [DataRow("mstsc.EXE")]
    public void Match_RemoteDesktopProcessNameIsCaseInsensitive(
        string processName)
    {
        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            CreateIdentity(processName),
            WindowEventKind.Shown,
            DefaultRules);

        Assert.AreEqual("remote", result!.Rule.Id);
        Assert.AreEqual("remote", result.Rule.TargetDesktopKey);
    }

    [TestMethod]
    public void Match_WslClientSharingTheAvdFileNameIsNotARemoteSession()
    {
        // msrdc.exe is both the Azure Virtual Desktop client and the WSL client
        // that hosts every WSLg Linux window. Naming it in the default rule
        // would drag Linux application windows onto Remote, so it is left out
        // and a WSLg window must not match.
        WindowIdentity identity = CreateIdentity("msrdc.exe") with
        {
            ExecutablePath = @"C:\Program Files\WSL\msrdc.exe",
            WindowClass = "RAIL_WINDOW",
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(result);
    }

    [TestMethod]
    [DataRow("rdpclip.exe")]
    [DataRow("RdpSa.exe")]
    [DataRow("CredentialUIBroker.exe")]
    [DataRow("ApplicationFrameHost.exe")]
    public void Match_RemoteDesktopAdjacentProcessesDoNotMatchOnTheirOwn(
        string processName)
    {
        // A credential prompt hosted by another process reaches the Remote rule
        // only by being owned by a session window, which the classifier
        // resolves before matching. On its own identity it must match nothing,
        // so an unowned broker window is never dragged off the user's desktop.
        WindowIdentity identity = CreateIdentity(processName) with
        {
            WindowClass = DialogWindowClass,
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            DefaultRules);

        Assert.IsNull(result);
    }

    [TestMethod]
    public void Match_EachRemoteSessionIsMatchedIndependently()
    {
        // Two simultaneous sessions are two frames in two processes. Neither
        // may stand in for the other.
        WindowIdentity first = CreateIdentity(RemoteDesktopProcessName) with
        {
            ProcessId = 3100,
            WindowTitle = "server-a - Remote Desktop Connection",
        };
        WindowIdentity second = CreateIdentity(RemoteDesktopProcessName) with
        {
            ProcessId = 3200,
            WindowTitle = "server-b - Remote Desktop Connection",
        };
        WindowRuleMatcher matcher = new();

        WindowRuleMatch? firstMatch = matcher.Match(
            first,
            WindowEventKind.Created,
            DefaultRules);
        WindowRuleMatch? secondMatch = matcher.Match(
            second,
            WindowEventKind.ForegroundActivated,
            DefaultRules);

        Assert.AreEqual("remote", firstMatch!.Rule.Id);
        Assert.AreEqual("remote", secondMatch!.Rule.Id);
        Assert.AreEqual(
            firstMatch.Rule.TargetDesktopKey,
            secondMatch.Rule.TargetDesktopKey);
        Assert.AreEqual(firstMatch.Strength, secondMatch.Strength);
    }

    [TestMethod]
    public void Match_ConfiguredWindowClassNarrowsTheRuleToSessionFramesOnly()
    {
        // Before this ticket a configured rule could not express a window class
        // at all: the rule source collapsed every rule to its process names. A
        // user who wants only connected sessions on Remote must be able to say
        // so in the document, and the matcher must honor it.
        IReadOnlyList<WindowObservationRule> rules =
            CreateRulesWithRemoteDesktopRefinement(
                windowClasses: [SessionWindowClass]);
        WindowIdentity sessionFrame = CreateIdentity(RemoteDesktopProcessName);
        WindowIdentity connectionDialog =
            CreateIdentity(RemoteDesktopProcessName) with
            {
                WindowClass = DialogWindowClass,
            };
        WindowRuleMatcher matcher = new();

        WindowRuleMatch? matched = matcher.Match(
            sessionFrame,
            WindowEventKind.Shown,
            rules);
        WindowRuleMatch? excluded = matcher.Match(
            connectionDialog,
            WindowEventKind.Shown,
            rules);

        Assert.AreEqual("remote", matched!.Rule.Id);
        Assert.AreEqual("remote", matched.Rule.TargetDesktopKey);
        Assert.IsNull(excluded);
    }

    [TestMethod]
    public void Match_ConfiguredExecutablePathOutranksAProcessName()
    {
        // A path distinguishes two installations that share a file name, so it
        // must reach the matcher as the stronger signal the ranking says it is.
        IReadOnlyList<WindowObservationRule> rules =
            CreateRulesWithRemoteDesktopRefinement(
                executablePaths: [RemoteDesktopExecutablePath]);
        WindowIdentity identity = CreateIdentity(RemoteDesktopProcessName) with
        {
            ExecutablePath = RemoteDesktopExecutablePath,
        };

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            rules);

        Assert.AreEqual("remote", result!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.ExecutablePath, result.Strength);
    }

    [TestMethod]
    public void Match_ConfiguredRefinementLosesToAStrongerRuleForTheSameWindow()
    {
        // A narrowed Remote rule must not out-rank a packaged identity that
        // claims the same window, or a refinement would quietly become the
        // strongest signal in the document.
        WindowObservationRule packagedRule = new(
            "session-broker",
            "Session Broker",
            IsEnabled: true,
            TargetDesktopKey: "code",
            AllTriggers,
            DesktopSwitchPolicy.Never,
            new WindowMatchCriteria(
                ["Contoso.SessionBroker_8wekyb3d8bbwe"],
                [],
                [],
                [],
                [],
                [],
                []),
            Order: 99);
        WindowIdentity identity = CreateIdentity(RemoteDesktopProcessName) with
        {
            PackageFamilyName = "Contoso.SessionBroker_8wekyb3d8bbwe",
        };
        IReadOnlyList<WindowObservationRule> rules =
        [
            .. CreateRulesWithRemoteDesktopRefinement(
                windowClasses: [SessionWindowClass]),
            packagedRule,
        ];

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            rules);

        Assert.AreEqual("session-broker", result!.Rule.Id);
        Assert.AreEqual(WindowMatchStrength.PackageFamilyName, result.Strength);
    }

    [TestMethod]
    [DataRow("Code.exe", "Chrome_WidgetWin_1", "ide-development", "ide-development")]
    [DataRow("msedge.exe", "Chrome_WidgetWin_1", "run-observe", "run-observe")]
    [DataRow(
        "WindowsTerminal.exe",
        "CASCADIA_HOSTING_WINDOW_CLASS",
        "infrastructure",
        "infrastructure")]
    [DataRow(RemoteDesktopProcessName, SessionWindowClass, "remote", "remote")]
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

    /// <summary>
    /// Builds the default rules with the Remote Desktop rule refined, driving
    /// the refinement through the real document projection so the test proves a
    /// configured document can express it.
    /// </summary>
    /// <param name="windowClasses">Optional window class refinement.</param>
    /// <param name="executablePaths">Optional executable path identity.</param>
    /// <returns>The projected observation rules.</returns>
    private static IReadOnlyList<WindowObservationRule>
        CreateRulesWithRemoteDesktopRefinement(
            ImmutableArray<string> windowClasses = default,
            ImmutableArray<string> executablePaths = default)
    {
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule remoteDesktop = defaults.ApplicationRules.Single(
            static rule => rule.Id == "remote");
        ConfigurationDocument refined = defaults with
        {
            ApplicationRules = defaults.ApplicationRules.Replace(
                remoteDesktop,
                remoteDesktop with
                {
                    // A `with` expression bypasses the primary constructor, so
                    // it also bypasses the normalization that turns an omitted
                    // collection into an empty one. Normalize here instead of
                    // handing the projection an unenumerable default array.
                    WindowClasses = windowClasses.IsDefault ? [] : windowClasses,
                    ExecutablePaths =
                        executablePaths.IsDefault ? [] : executablePaths,
                }),
        };

        return new ConfigurationWindowRuleSource(() => refined).GetRules();
    }

    private static WindowIdentity CreateIdentity(string processName) =>
        new(
            1,
            processName,
            ExecutablePath: null,
            PackageFamilyName: null,
            AppUserModelId: null,
            SessionWindowClass,
            WindowTitle: null,
            CommandLine: null);
}

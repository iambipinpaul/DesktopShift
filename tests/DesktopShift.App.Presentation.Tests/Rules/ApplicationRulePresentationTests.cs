using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Presentation.Tests.Rules;

/// <summary>
/// What the Rules page shows for each rule: icon, name, match summary, target,
/// triggers, enabled state, and last-match time.
/// </summary>
[TestClass]
public sealed class ApplicationRulePresentationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void EveryListItem_CarriesEveryFieldTheRulesPageShows()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        FakeIconReader icons = new();
        icons.Add(
            @"C:\Windows\System32\mstsc.exe",
            new ApplicationIcon(1, 1, [.. new byte[4]]));
        ConfigurationDocument withPath = ApplicationRuleCatalog.Replace(
            document,
            "remote",
            ApplicationRuleCatalog.Find(document, "remote")! with
            {
                ExecutablePaths = [@"C:\Windows\System32\mstsc.exe"],
            });

        ImmutableArray<ApplicationRulePresentation> items =
            ApplicationRulePresentationProjection.Project(
                withPath,
                [Matched("ide-development", Now.AddMinutes(-5))],
                Now,
                icons);

        Assert.HasCount(10, items);

        ApplicationRulePresentation ide = items[0];
        Assert.AreEqual("ide-development", ide.RuleId);
        Assert.AreEqual("IDE Development", ide.DisplayName);
        Assert.AreEqual("Process: Code.exe, devenv.exe", ide.MatchSummary);
        Assert.AreEqual("Process name only", ide.IdentityStrengthLabel);
        Assert.AreEqual("Assigns to IDE Development", ide.TargetSummary);
        Assert.AreEqual("All triggers", ide.TriggerSummary);
        Assert.AreEqual(
            "Switches on foreground activation",
            ide.SwitchPolicySummary);
        Assert.IsTrue(ide.IsEnabled);
        Assert.AreEqual("Enabled", ide.EnabledLabel);
        Assert.AreEqual("Last match 5 minutes ago", ide.LastMatchSummary);

        // No executable path, so no icon: the item falls back to a glyph rather
        // than rendering a hole.
        Assert.IsNull(ide.Icon);
        Assert.AreEqual(
            ApplicationRulePresentationProjection.DefaultRuleGlyph,
            ide.FallbackGlyph);

        ApplicationRulePresentation remote = items[4];
        Assert.IsNotNull(remote.Icon);
        Assert.AreEqual("No matches recorded", remote.LastMatchSummary);
    }

    [TestMethod]
    public void AnAnywhereRule_ShowsItsDestinationAndHidesWhatItDoesNotUse()
    {
        // Triggers and the switch policy both say when a window is moved, and an
        // Anywhere rule never moves one, so the row must not read as though it
        // were about to.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule explorer =
            ApplicationRuleCatalog.Find(document, "file-explorer")!;

        ApplicationRulePresentation item =
            ApplicationRulePresentationProjection.Project(
                explorer,
                document,
                lastMatchUtc: null,
                Now);

        Assert.IsTrue(explorer.AllowsAnywhere);
        Assert.AreEqual("Anywhere — stays where it opens", item.TargetSummary);
        Assert.AreEqual("Not used", item.TriggerSummary);
        Assert.AreEqual("Never switches desktop", item.SwitchPolicySummary);
        Assert.Contains(
            "Destination: Anywhere",
            item.AdvancedDetails,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Target key:",
            item.AdvancedDetails,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void MatchSummary_ShowsThePackagedIdentityAndNotJustTheProcessName()
    {
        // The page used to render a rule's identity as its process names alone,
        // which hid the package family name that is what actually claims a
        // Windows Terminal window.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule terminal =
            ApplicationRuleCatalog.Find(document, "infrastructure")!;

        string summary = ApplicationRulePresentationProjection.DescribeMatch(terminal);

        Assert.Contains(
            "Package: Microsoft.WindowsTerminal_8wekyb3d8bbwe",
            summary,
            StringComparison.Ordinal);
        Assert.Contains(
            "AppUserModelId: Microsoft.WindowsTerminal_8wekyb3d8bbwe!App",
            summary,
            StringComparison.Ordinal);
        Assert.Contains(
            "Process: WindowsTerminal.exe",
            summary,
            StringComparison.Ordinal);
        Assert.AreEqual(
            "Packaged identity",
            ApplicationRulePresentationProjection.DescribeIdentityStrength(terminal));

        // Strongest signal first, so the summary reads in the order the matcher
        // would rank it.
        Assert.IsTrue(
            summary.IndexOf("Package:", StringComparison.Ordinal) <
                summary.IndexOf("Process:", StringComparison.Ordinal));
    }

    [TestMethod]
    public void MatchSummary_ShowsExecutablePathsAndWindowClassRefinements()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule refined =
            ApplicationRuleCatalog.Find(document, "remote")! with
            {
                ExecutablePaths = [@"C:\Windows\System32\mstsc.exe"],
                WindowClasses = ["TscShellContainerClass"],
            };

        string summary = ApplicationRulePresentationProjection.DescribeMatch(refined);

        Assert.Contains(
            @"Path: C:\Windows\System32\mstsc.exe",
            summary,
            StringComparison.Ordinal);
        Assert.Contains(
            "Window class: TscShellContainerClass",
            summary,
            StringComparison.Ordinal);
        Assert.AreEqual(
            "Executable path",
            ApplicationRulePresentationProjection.DescribeIdentityStrength(refined));
    }

    [TestMethod]
    public void ADisabledRule_IsShownAsDisabledWithoutLosingAnything()
    {
        ConfigurationDocument document = ApplicationRuleCatalog.SetEnabled(
            ConfigurationDefaults.Create(),
            "run-observe",
            false);

        ApplicationRulePresentation browsers =
            ApplicationRulePresentationProjection.Project(document, [], Now)[1];

        Assert.IsFalse(browsers.IsEnabled);
        Assert.AreEqual("Disabled", browsers.EnabledLabel);
        Assert.Contains(
            "msedge.exe",
            browsers.MatchSummary,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ARuleTargetingAMissingDesktop_SaysSoRatherThanShowingARawKey()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule orphan =
            ApplicationRuleCatalog.Find(document, "ide-development")! with
            {
                TargetDesktopKey = "removed",
            };

        Assert.AreEqual(
            "Unknown Managed Desktop 'removed'",
            ApplicationRulePresentationProjection.DescribeTarget(orphan, document));
    }

    [TestMethod]
    public void PartialTriggerSets_AreListedInTheOrderTheEditorShowsThem()
    {
        Assert.AreEqual(
            "Window created, Foreground activated",
            ApplicationRulePresentationProjection.DescribeTriggers(
            [
                ApplicationRuleTrigger.ForegroundActivated,
                ApplicationRuleTrigger.WindowCreated,
            ]));
        Assert.AreEqual(
            "No triggers",
            ApplicationRulePresentationProjection.DescribeTriggers([]));
    }

    [TestMethod]
    [DataRow(0, "Last match just now")]
    [DataRow(1, "Last match 1 minute ago")]
    [DataRow(59, "Last match 59 minutes ago")]
    [DataRow(60, "Last match 1 hour ago")]
    [DataRow(1440, "Last match 1 day ago")]
    [DataRow(5760, "Last match 4 days ago")]
    public void LastMatchTime_IsShownInTheCoarsestUnitThatStillAnswers(
        int elapsedMinutes,
        string expected)
    {
        Assert.AreEqual(
            expected,
            ApplicationRulePresentationProjection.DescribeLastMatch(
                Now.AddMinutes(-elapsedMinutes),
                Now));
    }

    [TestMethod]
    public void LastMatchTime_ComesFromTheMostRecentMatchOfThatRuleOnly()
    {
        ImmutableDictionary<string, DateTimeOffset> lastMatches =
            ApplicationRulePresentationProjection.ProjectLastMatches(
            [
                Matched("ide-development", Now.AddHours(-3)),
                Matched("ide-development", Now.AddMinutes(-30)),
                Matched("run-observe", Now.AddHours(-1)),
                Skipped(Now),
            ]);

        Assert.AreEqual(Now.AddMinutes(-30), lastMatches["ide-development"]);
        Assert.AreEqual(Now.AddHours(-1), lastMatches["run-observe"]);
        Assert.HasCount(2, lastMatches);
    }

    [TestMethod]
    public void AutomationName_AnnouncesTheWholeItemInOneSentence()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();

        ApplicationRulePresentation terminal =
            ApplicationRulePresentationProjection.Project(document, [], Now)[3];

        foreach (string expected in new[]
        {
            "Infrastructure",
            "Enabled",
            "Packaged identity",
            "Assigns to Infrastructure",
            "All triggers",
            "No matches recorded",
        })
        {
            Assert.Contains(
                expected,
                terminal.AutomationName,
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void AdvancedDetails_NameEveryListIncludingTheEmptyOnes()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();

        string details = ApplicationRulePresentationProjection.DescribeAdvancedDetails(
            ApplicationRuleCatalog.Find(document, "ide-development")!);

        Assert.Contains(
            "Identifier: ide-development",
            details,
            StringComparison.Ordinal);
        Assert.Contains(
            "Package family names: none",
            details,
            StringComparison.Ordinal);
        Assert.Contains(
            "Process names: Code.exe",
            details,
            StringComparison.Ordinal);
        Assert.Contains("Window classes: none", details, StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReassignmentSummary_CountsOnlyTheRuleTheUserAskedAbout()
    {
        WindowReassignmentBatchResult batch = new(
            Guid.NewGuid(),
            Now,
            TimeSpan.FromMilliseconds(120),
            WindowEventKind.ManualReassignment,
            EnumeratedWindowCount: 12,
            [
                Assignment("ide-development", WindowMoveOutcome.Succeeded),
                Assignment("ide-development", WindowMoveOutcome.AlreadyCorrect),
                Assignment(
                    "ide-development",
                    WindowMoveOutcome.Failed,
                    WindowAssignmentOutcome.Failed),
                Assignment("run-observe", WindowMoveOutcome.Succeeded),
            ]);

        ApplicationRuleReassignmentSummary summary =
            ApplicationRulePresentationProjection.Summarize(batch, "ide-development");

        Assert.AreEqual(12, summary.EnumeratedWindowCount);
        Assert.AreEqual(3, summary.MatchedWindowCount);
        Assert.AreEqual(1, summary.MovedWindowCount);
        Assert.AreEqual(1, summary.AlreadyOnTargetCount);
        Assert.AreEqual(1, summary.FailedWindowCount);
        Assert.Contains(
            "Moved 1 of 3 matching windows",
            summary.Message,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReassignmentSummaryWithoutMatches_SaysHowManyWindowsWereChecked()
    {
        WindowReassignmentBatchResult batch = new(
            Guid.NewGuid(),
            Now,
            TimeSpan.Zero,
            WindowEventKind.ManualReassignment,
            EnumeratedWindowCount: 7,
            []);

        Assert.AreEqual(
            "No open window matched this rule. 7 windows were checked.",
            ApplicationRulePresentationProjection
                .Summarize(batch, "ide-development")
                .Message);
    }

    private static WindowObservationActivity Matched(
        string ruleId,
        DateTimeOffset occurredAt) =>
        new(
            occurredAt,
            EventSequence: 1,
            WindowEventKind.ForegroundActivated,
            WindowHandle: 1,
            WindowObservationOutcome.Matched,
            WindowSkipReason.None,
            ruleId,
            "code",
            new WindowSafeIdentity("Code.exe", null, null, "Chrome_WidgetWin_1"));

    private static WindowObservationActivity Skipped(DateTimeOffset occurredAt) =>
        new(
            occurredAt,
            EventSequence: 2,
            WindowEventKind.ForegroundActivated,
            WindowHandle: 2,
            WindowObservationOutcome.Skipped,
            WindowSkipReason.NoMatchingRule,
            RuleId: null,
            TargetDesktopKey: null,
            new WindowSafeIdentity("other.exe", null, null, "Other"));

    private static WindowAssignmentActivity Assignment(
        string ruleId,
        WindowMoveOutcome moveOutcome,
        WindowAssignmentOutcome outcome = WindowAssignmentOutcome.Succeeded) =>
        new(
            Guid.NewGuid(),
            Now,
            TimeSpan.FromMilliseconds(4),
            WindowEventKind.ManualReassignment,
            WindowHandle: 1,
            outcome,
            WindowAssignmentSkipReason.None,
            ruleId,
            "code",
            TargetDesktopId: Guid.NewGuid(),
            PreviousDesktopId: null,
            new WindowSafeIdentity("Code.exe", null, null, "Chrome_WidgetWin_1"),
            Error: null,
            moveOutcome);

    private sealed class FakeIconReader : IApplicationIconReader
    {
        private readonly Dictionary<string, ApplicationIcon> icons =
            new(StringComparer.OrdinalIgnoreCase);

        public void Add(string executablePath, ApplicationIcon icon) =>
            icons[executablePath] = icon;

        public ApplicationIcon? TryRead(string? executablePath) =>
            executablePath is not null &&
            icons.TryGetValue(executablePath, out ApplicationIcon? icon)
                ? icon
                : null;
    }
}

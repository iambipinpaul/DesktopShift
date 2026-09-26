using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowRuleMatcherTests
{
    private static readonly ImmutableArray<ApplicationRuleTrigger> Triggers =
        [ApplicationRuleTrigger.WindowShown];

    [TestMethod]
    public void Match_StrongerStableIdentityWinsBeforeConfigurationOrder()
    {
        WindowIdentity identity = new(
            1,
            "Code.exe",
            @"C:\Apps\Code.exe",
            "Microsoft.VisualStudioCode_1.2_x64",
            "Microsoft.VisualStudioCode",
            "Chrome_WidgetWin_1",
            "project - Visual Studio Code",
            "Code.exe project");
        WindowObservationRule processRule = CreateRule(
            "process",
            order: 0,
            WindowMatchCriteria.ForProcessNames(["Code.exe"]));
        WindowObservationRule packageRule = CreateRule(
            "package",
            order: 99,
            new WindowMatchCriteria(
                ["Microsoft.VisualStudioCode_1.2_x64"],
                [],
                [],
                [],
                [],
                [],
                []));

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            [processRule, packageRule]);

        Assert.AreEqual("package", result!.Rule.Id);
        Assert.AreEqual(
            WindowMatchStrength.PackageFamilyName,
            result.Strength);
    }

    [TestMethod]
    public void Match_SameStrengthUsesOrderThenStableId()
    {
        WindowIdentity identity = CreateIdentity("Code.exe");
        WindowObservationRule later = CreateRule(
            "z-rule",
            order: 2,
            WindowMatchCriteria.ForProcessNames(["code.EXE"]));
        WindowObservationRule first = CreateRule(
            "b-rule",
            order: 1,
            WindowMatchCriteria.ForProcessNames(["CODE.exe"]));

        WindowRuleMatch? result = new WindowRuleMatcher().Match(
            identity,
            WindowEventKind.Shown,
            [later, first]);

        Assert.AreEqual("b-rule", result!.Rule.Id);
    }

    [TestMethod]
    public void Match_TitleAndCommandLineRefinementsAreOptInAndRequired()
    {
        WindowObservationRule rule = CreateRule(
            "refined",
            order: 0,
            new WindowMatchCriteria(
                [],
                [],
                [],
                ["Code.exe"],
                ["Chrome_WidgetWin_1"],
                ["project"],
                ["--profile work"]));

        WindowIdentity missingCommandLine =
            CreateIdentity("Code.exe") with
            {
                WindowClass = "Chrome_WidgetWin_1",
                WindowTitle = "project - Code",
            };
        WindowIdentity complete = missingCommandLine with
        {
            CommandLine = "Code.exe --profile work",
        };

        Assert.IsNull(
            new WindowRuleMatcher().Match(
                missingCommandLine,
                WindowEventKind.Shown,
                [rule]));
        Assert.IsNotNull(
            new WindowRuleMatcher().Match(
                complete,
                WindowEventKind.Shown,
                [rule]));
    }

    [TestMethod]
    public void Projection_ProjectsAPinRuleOntoThePinnedDestination()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            ApplicationRules =
            [
                new ApplicationRule(
                    "music",
                    "Music",
                    IsEnabled: true,
                    "code",
                    ["Music.exe"],
                    Triggers,
                    DesktopSwitchPolicy.OnForegroundActivation,
                    Action: ApplicationRuleAction.ShowOnAllDesktops),
            ],
        };

        IReadOnlyList<WindowObservationRule> rules =
            new ConfigurationWindowRuleSource(() => document).GetRules();

        Assert.AreEqual(
            WindowRuleDestination.PinnedToAllDesktops,
            rules.Single().Destination);
        Assert.AreEqual(
            "code",
            rules.Single().TargetDesktopKey,
            "The key stays in the document untouched; the projection is what makes it unread.");
    }

    [TestMethod]
    public void IsNamedByAnyRule_CountsAPinRuleWhoseTriggersDoNotAnswerThisEvent()
    {
        WindowObservationRule pin = CreateRule(
            "music",
            order: 0,
            WindowMatchCriteria.ForProcessNames(["Code.exe"])) with
        {
            Triggers = [ApplicationRuleTrigger.WindowCreated],
            Destination = WindowRuleDestination.PinnedToAllDesktops,
        };

        bool named = new WindowRuleMatcher().IsNamedByAnyRule(
            CreateIdentity("Code.exe"),
            [pin]);

        Assert.IsTrue(
            named,
            "A pinned application the event does not answer must stay out of the sweep.");
    }

    [TestMethod]
    public void Match_APinRuleAnswersItsOwnEventSetWhateverItsTriggersStore()
    {
        // A pin rule is allowed to store no triggers at all: the field is not
        // read while the action is a pin, so validation skips it and the editor
        // hides it. Reading it here would let such a rule name an application
        // and then never pin a window of it.
        WindowObservationRule pin = CreateRule(
            "music",
            order: 0,
            WindowMatchCriteria.ForProcessNames(["Code.exe"])) with
        {
            Triggers = [],
            Destination = WindowRuleDestination.PinnedToAllDesktops,
        };
        WindowRuleMatcher matcher = new();
        WindowIdentity identity = CreateIdentity("Code.exe");

        foreach (WindowEventKind answered in new[]
        {
            WindowEventKind.Created,
            WindowEventKind.Shown,
            WindowEventKind.StartupReconciliation,
            WindowEventKind.ManualReassignment,
            WindowEventKind.ForegroundActivated,
        })
        {
            Assert.IsNotNull(
                matcher.Match(identity, answered, [pin]),
                $"A pin rule answers {answered} without reading its stored triggers.");
        }

        Assert.IsNull(
            matcher.Match(identity, WindowEventKind.MoveSizeEnded, [pin]),
            "A pin rule answers placement events, not every event it could see.");
    }

    [TestMethod]
    public void Match_APinRuleDoesNotAnswerLifecycleEventsThroughTheTriggerFallback()
    {
        // Every event kind that has no trigger of its own maps to manual
        // reassignment, so a pin rule that kept a manual trigger would answer
        // Hidden, Restored, and the rest if its stored triggers were read. A
        // pin rule reads none of them.
        WindowObservationRule pin = CreateRule(
            "music",
            order: 0,
            WindowMatchCriteria.ForProcessNames(["Code.exe"])) with
        {
            Triggers = [ApplicationRuleTrigger.ManualReassignment],
            Destination = WindowRuleDestination.PinnedToAllDesktops,
        };
        WindowRuleMatcher matcher = new();
        WindowIdentity identity = CreateIdentity("Code.exe");

        foreach (WindowEventKind unasked in new[]
        {
            WindowEventKind.Hidden,
            WindowEventKind.Minimized,
            WindowEventKind.Restored,
            WindowEventKind.StateChanged,
            WindowEventKind.MoveSizeEnded,
        })
        {
            Assert.IsNull(
                matcher.Match(identity, unasked, [pin]),
                $"{unasked} is not an event a pin rule answers.");
        }

        Assert.IsNotNull(
            matcher.Match(identity, WindowEventKind.ManualReassignment, [pin]),
            "The manual reassignment event is one a pin rule does answer.");
    }

    [TestMethod]
    public void Match_AMoveRuleStillAnswersOnlyItsStoredTriggers()
    {
        WindowObservationRule move = CreateRule(
            "vscode",
            order: 0,
            WindowMatchCriteria.ForProcessNames(["Code.exe"])) with
        {
            Triggers = [],
        };

        Assert.IsNull(
            new WindowRuleMatcher().Match(
                CreateIdentity("Code.exe"),
                WindowEventKind.Shown,
                [move]),
            "Only a pin rule ignores its stored triggers.");
    }

    private static WindowObservationRule CreateRule(
        string id,
        int order,
        WindowMatchCriteria criteria) =>
        new(
            id,
            id,
            IsEnabled: true,
            TargetDesktopKey: "code",
            Triggers,
            DesktopSwitchPolicy.OnForegroundActivation,
            criteria,
            order);

    private static WindowIdentity CreateIdentity(string processName) =>
        new(
            1,
            processName,
            null,
            null,
            null,
            "Window",
            null,
            null);
}

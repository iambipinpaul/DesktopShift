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

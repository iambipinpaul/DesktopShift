using System.Collections.Immutable;
using DesktopShift.App.Rules;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Presentation.Tests.Rules;

/// <summary>
/// The Anywhere destination as the user meets it: one more entry in the same
/// destination list, with the controls it cannot use hidden, validated without
/// reporting members it ignores, and persisted through the same document edit
/// every other rule uses.
/// </summary>
[TestClass]
public sealed class AnywhereDestinationTests
{
    [TestMethod]
    public void DestinationPicker_OffersAnywhereAfterEveryManagedDesktop()
    {
        // One list, not two. Two lists could both claim the same application and
        // would need a tie-break the user has to remember; one list already has
        // one, because the matcher resolves a tie by strength then rule order.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document));

        Assert.HasCount(
            document.ManagedDesktops.Length + 2,
            viewModel.ManagedDesktops);
        CollectionAssert.AreEqual(
            document.ManagedDesktops
                .Select(static desktop => desktop.SemanticKey)
                .ToArray(),
            viewModel.ManagedDesktops
                .Take(document.ManagedDesktops.Length)
                .Select(static choice => choice.SemanticKey)
                .ToArray());

        // Show on all desktops follows Anywhere, so the Managed Desktops still
        // come first and Anywhere still comes after all of them.
        ManagedDesktopChoice anywhere = viewModel.ManagedDesktops[^2];
        Assert.AreEqual(ApplicationRuleAction.AllowAnywhere, anywhere.Action);
        Assert.IsEmpty(anywhere.SemanticKey);
        Assert.Contains("Anywhere", anywhere.DisplayName, StringComparison.Ordinal);
    }

    [TestMethod]
    public void SelectingAnywhere_HidesTriggersAndSwitchPolicy()
    {
        // Both say when a window is moved, and an Anywhere rule never moves one.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document));

        Assert.IsFalse(viewModel.AllowsAnywhere);
        Assert.IsTrue(viewModel.IsPlacementConfigurable);

        viewModel.SelectedDesktopIndex = viewModel.ManagedDesktops.Count - 2;

        Assert.IsTrue(viewModel.AllowsAnywhere);
        Assert.IsFalse(viewModel.IsPlacementConfigurable);
        Assert.AreEqual(
            ApplicationRuleAction.AllowAnywhere,
            viewModel.ToDraft().Action);
    }

    [TestMethod]
    public void SelectingAnywhereAndBack_KeepsTheChosenManagedDesktop()
    {
        // Looking at the option must not cost the user the destination they had
        // already chosen.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document))
        {
            SelectedDesktopIndex = 2,
        };
        string chosen = viewModel.ToDraft().TargetDesktopKey;

        viewModel.SelectedDesktopIndex = viewModel.ManagedDesktops.Count - 2;

        Assert.AreEqual(chosen, viewModel.ToDraft().TargetDesktopKey);
        Assert.AreEqual(
            viewModel.ManagedDesktops.Count - 2,
            viewModel.SelectedDesktopIndex);

        viewModel.SelectedDesktopIndex = 2;

        Assert.AreEqual(2, viewModel.SelectedDesktopIndex);
        Assert.AreEqual(chosen, viewModel.ToDraft().TargetDesktopKey);
        Assert.AreEqual(
            ApplicationRuleAction.MoveToDesktop,
            viewModel.ToDraft().Action);
    }

    [TestMethod]
    public void AnExistingAnywhereRule_ReopensOnTheAnywhereEntry()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRule defaultAnywhere =
            ApplicationRuleCatalog.Find(document, "default-anywhere")!;
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForExistingRule(defaultAnywhere));

        Assert.IsTrue(viewModel.AllowsAnywhere);
        Assert.AreEqual(
            viewModel.ManagedDesktops.Count - 2,
            viewModel.SelectedDesktopIndex);
    }

    [TestMethod]
    public void AnAnywhereDraft_ValidatesWithoutADestinationOrATrigger()
    {
        // Its target key, triggers, and switch policy are all ignored, so none of
        // them may be reported as a fault. Its identity still must be there.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "paint",
            DisplayName = "Paint",
            TargetDesktopKey = string.Empty,
            ProcessNames = "mspaint.exe",
            Triggers = [],
            SwitchPolicy = DesktopSwitchPolicy.OnNewWindowActivation,
            Action = ApplicationRuleAction.AllowAnywhere,
        };

        Assert.IsEmpty(draft.Validate(document));

        ImmutableArray<ApplicationRuleValidationIssue> withoutIdentity =
            (draft with { ProcessNames = string.Empty }).Validate(document);

        Assert.IsTrue(withoutIdentity.Any(static issue =>
            issue.Code == ConfigurationValidationCode.MissingApplicationIdentity));
    }

    [TestMethod]
    public void AnAnywhereDraft_AppliesToTheDocumentAsAnAnywhereRule()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "paint",
            DisplayName = "Paint",
            ProcessNames = "mspaint.exe",
            Action = ApplicationRuleAction.AllowAnywhere,
        };

        ConfigurationDocument edited = draft.Apply(document);
        ApplicationRule saved = ApplicationRuleCatalog.Find(edited, "paint")!;

        Assert.IsTrue(saved.AllowsAnywhere);
        Assert.AreEqual(ApplicationRuleAction.AllowAnywhere, saved.Action);
        Assert.Contains("mspaint.exe", saved.ProcessNames);

        // Reopening it round-trips the destination.
        Assert.AreEqual(
            ApplicationRuleAction.AllowAnywhere,
            ApplicationRuleDraft.ForExistingRule(saved).Action);
    }

    [TestMethod]
    public void ADisabledRuleTest_PointsAtAnywhereRatherThanLeavingItUnexplained()
    {
        // Disabling a rule unmanages its application, which means the sweep takes
        // it. The editor has to say so, because the user almost certainly meant
        // Anywhere.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document) with { IsEnabled = false });

        viewModel.SetTestResult(
            new ApplicationRuleTestResult(
                EvaluatedWindowCount: 3,
                Matches: [],
                IsRuleEnabled: false,
                SupportsManualReassignment: true));

        Assert.Contains(
            "first desktop",
            viewModel.TestDetails,
            StringComparison.OrdinalIgnoreCase);
        Assert.Contains(
            "Anywhere",
            viewModel.TestDetails,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnAnywhereRuleTest_SaysTheWindowsStayPutRatherThanTalkingAboutTriggers()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document) with
            {
                Action = ApplicationRuleAction.AllowAnywhere,
            });

        viewModel.SetTestResult(
            new ApplicationRuleTestResult(
                EvaluatedWindowCount: 3,
                Matches: [],
                IsRuleEnabled: true,
                SupportsManualReassignment: false));

        Assert.Contains(
            "stay where they open",
            viewModel.TestDetails,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "manual reassignment trigger",
            viewModel.TestDetails,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void EveryShippedRule_DescribesItsIdentityAndItsDestination()
    {
        // The first-run dialog and the Rules page both show a rule by these two
        // sentences. Calculator, Task Manager, and Settings declare no process
        // name at all — they are identified by a package family name or a full
        // path — so a row built from process names alone renders blank and reads
        // as dead configuration.
        ConfigurationDocument document = ConfigurationDefaults.Create();

        foreach (ApplicationRule rule in document.ApplicationRules)
        {
            string identity =
                ApplicationRulePresentationProjection.DescribeMatch(rule);
            string destination =
                ApplicationRulePresentationProjection.DescribeTarget(rule, document);

            Assert.IsFalse(
                string.IsNullOrWhiteSpace(identity),
                $"'{rule.Id}' would render with no identity at all.");
            Assert.AreNotEqual(
                "No identity declared",
                identity,
                $"'{rule.Id}' declares nothing a window could report.");
            Assert.IsFalse(
                string.IsNullOrWhiteSpace(destination),
                $"'{rule.Id}' would render with no destination.");
        }

        // A rule identified by something other than a process name still says
        // what it is, rather than rendering as a blank row.
        Assert.Contains(
            "Package: Microsoft.WindowsNotepad_8wekyb3d8bbwe",
            ApplicationRulePresentationProjection.DescribeMatch(
                ApplicationRuleCatalog.Find(document, "default-anywhere")!),
            StringComparison.Ordinal);
        Assert.Contains(
            @"Path: C:\Windows\explorer.exe",
            ApplicationRulePresentationProjection.DescribeMatch(
                ApplicationRuleCatalog.Find(document, "default-anywhere")!),
            StringComparison.Ordinal);

        // And every Anywhere rule says so rather than showing an empty desktop.
        foreach (ApplicationRule rule in document.ApplicationRules
            .Where(static rule => rule.AllowsAnywhere))
        {
            Assert.Contains(
                "Anywhere",
                ApplicationRulePresentationProjection.DescribeTarget(rule, document),
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void ADraftForACapturedWindow_IsPreFilledFromWhatWasObserved()
    {
        // What the Activity view records is the privacy-safe identity only, so
        // that is exactly what a rule written from a swept window can carry.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        WindowSafeIdentity identity = new(
            "Discord.exe",
            "Discord.Discord_abc123456789",
            "Discord.Discord_abc123456789!App",
            "Chrome_WidgetWin_1");

        ApplicationRuleDraft draft = ApplicationRuleDraft.ForCapturedWindow(
            document,
            identity);

        Assert.AreEqual("Discord", draft.DisplayName);
        Assert.AreEqual("discord", draft.Id);
        Assert.Contains("Discord.exe", draft.ProcessNames, StringComparison.Ordinal);
        Assert.Contains(
            "Discord.Discord_abc123456789",
            draft.PackageFamilyNames,
            StringComparison.Ordinal);
        Assert.Contains(
            "Discord.Discord_abc123456789!App",
            draft.AppUserModelIds,
            StringComparison.Ordinal);

        // A window class narrows a rule to one shape, and narrowing is a decision
        // rather than a detail of what was observed.
        Assert.IsEmpty(draft.WindowClasses);
        Assert.IsEmpty(draft.Validate(document));
        Assert.IsTrue(draft.IsNewRule);
    }

    [TestMethod]
    public void ADraftForACapturedWindow_DoesNotCollideWithAnExistingRule()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        document = ApplicationRuleCatalog.Add(
            document,
            ApplicationRuleCatalog.Find(document, "default-anywhere")! with
            {
                Id = "notepad",
                DisplayName = "Existing Notepad rule",
            });
        WindowSafeIdentity identity = new(
            "Notepad.exe",
            "Microsoft.WindowsNotepad_8wekyb3d8bbwe",
            null,
            "Notepad");

        ApplicationRuleDraft draft = ApplicationRuleDraft.ForCapturedWindow(
            document,
            identity);

        Assert.AreEqual("notepad-2", draft.Id);
        Assert.IsEmpty(draft.Validate(document));
    }
}

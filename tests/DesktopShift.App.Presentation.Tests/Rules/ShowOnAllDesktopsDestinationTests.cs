using System.Collections.Immutable;
using DesktopShift.App.Rules;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.App.Presentation.Tests.Rules;

/// <summary>
/// The Show on all desktops destination as the user meets it: the third entry in
/// the same destination list, with the controls it cannot use hidden, validated
/// without reporting members it ignores, and persisted through the same document
/// edit every other rule uses.
/// </summary>
[TestClass]
public sealed class ShowOnAllDesktopsDestinationTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 9, 26, 12, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public void DestinationPicker_OffersShowOnAllDesktopsAfterAnywhere()
    {
        // Still one list. The two destinations that never move a window sit in
        // it beside the Managed Desktops rather than in a control of their own,
        // because the list already has the tie-break the user would otherwise
        // have to remember.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document));

        Assert.HasCount(
            document.ManagedDesktops.Length + 2,
            viewModel.ManagedDesktops);

        ManagedDesktopChoice anywhere = viewModel.ManagedDesktops[^2];
        Assert.AreEqual(ApplicationRuleAction.AllowAnywhere, anywhere.Action);

        ManagedDesktopChoice pin = viewModel.ManagedDesktops[^1];
        Assert.AreEqual(ApplicationRuleAction.ShowOnAllDesktops, pin.Action);
        Assert.IsEmpty(pin.SemanticKey);
        Assert.AreEqual(
            "Show on all desktops — pin this app everywhere",
            pin.DisplayName);
    }

    [TestMethod]
    public void SelectingShowOnAllDesktops_HidesTriggersAndSwitchPolicy()
    {
        // Both say when a window is moved, and a pin never moves one.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document));

        Assert.IsTrue(viewModel.IsPlacementConfigurable);

        viewModel.SelectedDesktopIndex = IndexOf(
            viewModel,
            ApplicationRuleAction.ShowOnAllDesktops);

        Assert.IsFalse(viewModel.IsPlacementConfigurable);
        Assert.AreEqual(
            ApplicationRuleAction.ShowOnAllDesktops,
            viewModel.ToDraft().Action);
    }

    [TestMethod]
    public void SelectingShowOnAllDesktopsAndBack_KeepsTheStoredPlacement()
    {
        // Looking at the option must not cost the user the destination, the
        // triggers, or the switch policy they had already chosen: all three are
        // stored underneath and read again the moment the rule moves windows.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document))
        {
            SelectedDesktopIndex = 2,
        };
        string chosen = viewModel.ToDraft().TargetDesktopKey;
        ImmutableArray<ApplicationRuleTrigger> triggers =
            viewModel.ToDraft().Triggers;
        DesktopSwitchPolicy switchPolicy = viewModel.ToDraft().SwitchPolicy;

        viewModel.SelectedDesktopIndex = IndexOf(
            viewModel,
            ApplicationRuleAction.ShowOnAllDesktops);

        Assert.AreEqual(chosen, viewModel.ToDraft().TargetDesktopKey);
        CollectionAssert.AreEqual(triggers, viewModel.ToDraft().Triggers);
        Assert.AreEqual(switchPolicy, viewModel.ToDraft().SwitchPolicy);
        Assert.AreEqual(
            IndexOf(viewModel, ApplicationRuleAction.ShowOnAllDesktops),
            viewModel.SelectedDesktopIndex);

        viewModel.SelectedDesktopIndex = 2;

        Assert.AreEqual(2, viewModel.SelectedDesktopIndex);
        Assert.AreEqual(chosen, viewModel.ToDraft().TargetDesktopKey);
        Assert.AreEqual(
            ApplicationRuleAction.MoveToDesktop,
            viewModel.ToDraft().Action);
    }

    [TestMethod]
    public void TheTwoDestinationsThatNeverMove_TellEachOtherApart()
    {
        // Both store an empty key, so a picker that looked the entry up by key
        // would reopen one of them on the other. Which entry the rule reopens on
        // is the whole of what the user sees.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document) with
            {
                Action = ApplicationRuleAction.AllowAnywhere,
            });

        int anywhere = IndexOf(viewModel, ApplicationRuleAction.AllowAnywhere);
        int pin = IndexOf(viewModel, ApplicationRuleAction.ShowOnAllDesktops);
        Assert.AreEqual(anywhere, viewModel.SelectedDesktopIndex);

        viewModel.SelectedDesktopIndex = pin;

        Assert.AreEqual(pin, viewModel.SelectedDesktopIndex);
        Assert.AreEqual(
            ApplicationRuleAction.ShowOnAllDesktops,
            viewModel.ToDraft().Action);

        viewModel.SelectedDesktopIndex = anywhere;

        Assert.AreEqual(anywhere, viewModel.SelectedDesktopIndex);
        Assert.AreEqual(
            ApplicationRuleAction.AllowAnywhere,
            viewModel.ToDraft().Action);
    }

    [TestMethod]
    public void AnExistingShowOnAllDesktopsRule_ReopensOnItsOwnEntry()
    {
        ConfigurationDocument document = WithPinRule(ConfigurationDefaults.Create());
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForExistingRule(
                ApplicationRuleCatalog.Find(document, "music")!));

        Assert.AreEqual(
            IndexOf(viewModel, ApplicationRuleAction.ShowOnAllDesktops),
            viewModel.SelectedDesktopIndex);
        Assert.IsFalse(viewModel.IsPlacementConfigurable);
    }

    [TestMethod]
    public void AShowOnAllDesktopsDraft_ValidatesWithoutADestinationOrATrigger()
    {
        // Its target key, triggers, and switch policy are all ignored, so none of
        // them may be reported as a fault — not even when the key names a Managed
        // Desktop the document no longer has. Its identity still must be there.
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "music",
            DisplayName = "Music",
            TargetDesktopKey = "removed",
            ProcessNames = "spotify.exe",
            Triggers = [],
            SwitchPolicy = DesktopSwitchPolicy.OnNewWindowActivation,
            Action = ApplicationRuleAction.ShowOnAllDesktops,
        };

        Assert.IsEmpty(draft.Validate(document));

        ImmutableArray<ApplicationRuleValidationIssue> withoutIdentity =
            (draft with { ProcessNames = string.Empty }).Validate(document);

        Assert.IsTrue(withoutIdentity.Any(static issue =>
            issue.Code == ConfigurationValidationCode.MissingApplicationIdentity));
    }

    [TestMethod]
    public void AShowOnAllDesktopsDraft_AppliesToTheDocumentAsAPinRule()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        ApplicationRuleDraft draft = ApplicationRuleDraft.ForNewRule(document) with
        {
            Id = "music",
            DisplayName = "Music",
            ProcessNames = "spotify.exe",
            Action = ApplicationRuleAction.ShowOnAllDesktops,
        };

        ConfigurationDocument edited = draft.Apply(document);
        ApplicationRule saved = ApplicationRuleCatalog.Find(edited, "music")!;

        Assert.IsTrue(saved.ShowsOnAllDesktops);
        Assert.AreEqual(ApplicationRuleAction.ShowOnAllDesktops, saved.Action);
        Assert.Contains("spotify.exe", saved.ProcessNames);

        // Reopening it round-trips the destination.
        Assert.AreEqual(
            ApplicationRuleAction.ShowOnAllDesktops,
            ApplicationRuleDraft.ForExistingRule(saved).Action);
    }

    [TestMethod]
    public void AShowOnAllDesktopsRuleTest_SaysTheWindowsArePinnedEverywhere()
    {
        ConfigurationDocument document = ConfigurationDefaults.Create();
        RuleEditorViewModel viewModel = new(
            document,
            ApplicationRuleDraft.ForNewRule(document) with
            {
                Action = ApplicationRuleAction.ShowOnAllDesktops,
            });

        viewModel.SetTestResult(
            new ApplicationRuleTestResult(
                EvaluatedWindowCount: 3,
                Matches: [],
                IsRuleEnabled: true,
                SupportsManualReassignment: false));

        Assert.Contains(
            "pins these windows to every desktop",
            viewModel.TestDetails,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "manual reassignment trigger",
            viewModel.TestDetails,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void AShowOnAllDesktopsRule_ShowsItsDestinationAndHidesWhatItDoesNotUse()
    {
        // Triggers and the switch policy both say when a window is moved, and a
        // pin never moves one, so the row must not read as though it were about
        // to. Pinning is not a move, so the trigger row does not say so either.
        ConfigurationDocument document = WithPinRule(ConfigurationDefaults.Create());
        ApplicationRule music = ApplicationRuleCatalog.Find(document, "music")!;

        ApplicationRulePresentation item =
            ApplicationRulePresentationProjection.Project(
                music,
                document,
                lastMatchUtc: null,
                Now);

        Assert.IsTrue(music.ShowsOnAllDesktops);
        Assert.AreEqual("Show on all desktops", item.TargetSummary);
        Assert.AreEqual("Not used", item.TriggerSummary);
        Assert.AreEqual("Never switches desktop", item.SwitchPolicySummary);
        Assert.Contains(
            "Destination: Show on all desktops",
            item.AdvancedDetails,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "Target key:",
            item.AdvancedDetails,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReassignmentSummaryForAPinRule_CountsPinsRatherThanMoves()
    {
        ConfigurationDocument document = WithPinRule(ConfigurationDefaults.Create());
        WindowReassignmentBatchResult batch = new(
            Guid.NewGuid(),
            Now,
            TimeSpan.FromMilliseconds(120),
            WindowEventKind.ManualReassignment,
            EnumeratedWindowCount: 2,
            [
                Pin(moveOutcome: WindowMoveOutcome.PinnedToAllDesktops),
                Pin(moveOutcome: WindowMoveOutcome.AlreadyPinnedToAllDesktops),
            ]);

        ApplicationRuleReassignmentSummary summary =
            ApplicationRulePresentationProjection.Summarize(
                batch,
                ApplicationRuleCatalog.Find(document, "music")!);

        // A pin is not a move, so it is not counted as one, and a window that
        // already had its pin is not a failure either.
        Assert.AreEqual(0, summary.MovedWindowCount);
        Assert.AreEqual(1, summary.PinnedWindowCount);
        Assert.AreEqual(1, summary.AlreadyOnTargetCount);
        Assert.AreEqual(0, summary.FailedWindowCount);
        Assert.AreEqual(
            "Pinned 1 of 2 matching windows. 1 were already pinned.",
            summary.Message);
    }

    [TestMethod]
    public void ReassignmentSummaryOnAHostThatCannotPin_SaysSoInsteadOfNothingHappened()
    {
        // Limited Mode pins nothing. Reporting "pinned 0" without a reason would
        // read as a batch that had nothing to do, which is the one thing this
        // host must not imply.
        ConfigurationDocument document = WithPinRule(ConfigurationDefaults.Create());
        WindowReassignmentBatchResult batch = new(
            Guid.NewGuid(),
            Now,
            TimeSpan.FromMilliseconds(12),
            WindowEventKind.ManualReassignment,
            EnumeratedWindowCount: 2,
            [
                Pin(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason.PinUnavailable,
                    moveOutcome: WindowMoveOutcome.NotAttempted),
                Pin(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason.PinUnavailable,
                    moveOutcome: WindowMoveOutcome.NotAttempted),
            ]);

        ApplicationRuleReassignmentSummary summary =
            ApplicationRulePresentationProjection.Summarize(
                batch,
                ApplicationRuleCatalog.Find(document, "music")!);

        Assert.AreEqual(2, summary.UnavailableWindowCount);
        Assert.AreEqual(0, summary.FailedWindowCount);
        Assert.Contains(
            "this host cannot pin windows to every desktop",
            summary.Message,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void ReassignmentSummary_WhenSomeWindowsFailedAndSomeCouldNotPin_SaysBoth()
    {
        // The two shortfalls are counted apart because they mean different
        // things, but a batch that hits both has to say both: reporting only
        // one of them would hide windows the user must still deal with.
        ConfigurationDocument document = WithPinRule(ConfigurationDefaults.Create());
        WindowReassignmentBatchResult batch = new(
            Guid.NewGuid(),
            Now,
            TimeSpan.FromMilliseconds(12),
            WindowEventKind.ManualReassignment,
            EnumeratedWindowCount: 3,
            [
                Pin(
                    WindowMoveOutcome.PinnedToAllDesktops,
                    WindowAssignmentOutcome.Succeeded),
                Pin(
                    WindowMoveOutcome.NotAttempted,
                    WindowAssignmentOutcome.Skipped,
                    WindowAssignmentSkipReason.PinUnavailable),
                Pin(
                    WindowMoveOutcome.NotAttempted,
                    WindowAssignmentOutcome.Failed),
            ]);

        ApplicationRuleReassignmentSummary summary =
            ApplicationRulePresentationProjection.Summarize(
                batch,
                ApplicationRuleCatalog.Find(document, "music")!);

        Assert.AreEqual(1, summary.PinnedWindowCount);
        Assert.AreEqual(1, summary.UnavailableWindowCount);
        Assert.AreEqual(1, summary.FailedWindowCount);
        Assert.AreEqual(
            "Pinned 1 of 3 matching windows. 1 were left alone because this host cannot pin windows to every desktop, and 1 could not be pinned.",
            summary.Message);
    }

    private static int IndexOf(
        RuleEditorViewModel viewModel,
        ApplicationRuleAction action)
    {
        for (int index = 0; index < viewModel.ManagedDesktops.Count; index++)
        {
            if (viewModel.ManagedDesktops[index].Action == action)
            {
                return index;
            }
        }

        return -1;
    }

    /// <summary>
    /// A document holding one pin rule, which is what a user's own rule looks
    /// like. No starter rule pins anything, because a shipped pin changes an
    /// application's placement for every user of the application.
    /// </summary>
    private static ConfigurationDocument WithPinRule(
        ConfigurationDocument document) =>
        ApplicationRuleCatalog.Add(
            document,
            new ApplicationRule(
                "music",
                "Music",
                IsEnabled: true,
                TargetDesktopKey: "web",
                ProcessNames: ["spotify.exe"],
                Triggers: ConfigurationDefaults.DefaultTriggers,
                SwitchPolicy: DesktopSwitchPolicy.OnForegroundActivation,
                Action: ApplicationRuleAction.ShowOnAllDesktops));

    private static WindowAssignmentActivity Pin(
        WindowMoveOutcome moveOutcome,
        WindowAssignmentOutcome outcome = WindowAssignmentOutcome.Succeeded,
        WindowAssignmentSkipReason skipReason = WindowAssignmentSkipReason.None) =>
        new(
            Guid.NewGuid(),
            Now,
            TimeSpan.FromMilliseconds(4),
            WindowEventKind.ManualReassignment,
            WindowHandle: 1,
            outcome,
            skipReason,
            RuleId: "music",
            TargetDesktopKey: "web",
            TargetDesktopId: null,
            PreviousDesktopId: null,
            new WindowSafeIdentity("Spotify.exe", null, null, "Chrome_WidgetWin_1"),
            Error: null,
            MoveOutcome: moveOutcome,
            SwitchPolicy: DesktopSwitchPolicy.Never,
            SwitchOutcome: DesktopSwitchOutcome.NotRequested,
            SwitchDecisionReason: DesktopSwitchDecisionReason.PinnedToAllDesktops);
}

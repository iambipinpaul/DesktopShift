using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Diagnostics;

/// <summary>
/// The Activity view has to tell three placement decisions apart: claimed by a
/// rule the user wrote, exempted by an Anywhere rule, and swept because nothing
/// named the application at all.
/// </summary>
[TestClass]
public sealed class SweptWindowActivityTests
{
    [TestMethod]
    public void ASweptWindow_IsRecordedAsSweptRatherThanMatched()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.MatchedObservation(
                ruleId: UnmanagedWindowSweep.RuleId,
                targetDesktopKey: UnmanagedWindowSweep.TargetDesktopKey) with
            {
                MatchedOn = null,
            },
            DiagnosticTestData.Session);

        Assert.AreEqual("observation.swept", record.ResultCode);
        Assert.AreEqual(ActivityResult.Succeeded, record.Result);
        Assert.AreEqual(
            "No rule names this application, so the window is moved to the first desktop.",
            record.Summary);

        // The internal key never reaches a reader as a raw string.
        Assert.DoesNotContain(
            UnmanagedWindowSweep.TargetDesktopKey,
            record.Summary,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void AnAnywhereWindow_SaysItStayedRatherThanNamingASkipReason()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.SkippedObservation(
                WindowSkipReason.AllowedAnywhere),
            DiagnosticTestData.Session);

        Assert.AreEqual(
            "observation.skipped.allowed_anywhere",
            record.ResultCode);
        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual(
            "An Anywhere rule names this application, so the window stays where it opened.",
            record.Summary);
    }

    [TestMethod]
    public void AWindowsManagedWindow_ExplainsWhyItStayedWhereItOpened()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.SkippedObservation(WindowSkipReason.SystemWindow),
            DiagnosticTestData.Session);

        Assert.AreEqual(
            "observation.skipped.system_window",
            record.ResultCode);
        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual(
            "Windows manages this window, so it stays where Windows opened it.",
            record.Summary);
    }

    [TestMethod]
    public void AnUnmatchedWindow_StillReadsAsNoMatchingRule()
    {
        // NoMatchingRule now means only "a rule names this app but none matched
        // this event", and it still has to be readable.
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.SkippedObservation(
                WindowSkipReason.NoMatchingRule),
            DiagnosticTestData.Session);

        Assert.AreEqual(
            "observation.skipped.no_matching_rule",
            record.ResultCode);
        Assert.AreEqual("Skipped: No matching rule.", record.Summary);
    }

    [TestMethod]
    public void AnActivationTheSweepDoesNotAnswer_ExplainsThePolicy()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.SkippedObservation(
                WindowSkipReason.ActivationNotSwept),
            DiagnosticTestData.Session);

        Assert.AreEqual(
            "observation.skipped.activation_not_swept",
            record.ResultCode);
        Assert.AreEqual(
            "DesktopShift does not move a window when you switch to it.",
            record.Summary);

        ActivityRecordDisplay display = ActivityRecordDisplay.Create(record);
        Assert.AreEqual("Not evaluated", display.Rule);
        Assert.DoesNotContain("No matching rule", display.AutomationName);
    }

    [TestMethod]
    [DataRow(WindowSkipReason.AllowedAnywhere, 17)]
    [DataRow(WindowSkipReason.ActivationNotSwept, 18)]
    [DataRow(WindowSkipReason.CloakStateChangeObserved, 19)]
    [DataRow(WindowSkipReason.PinReleaseFailed, 21)]
    public void SkipReason_NumericValueIsStable(
        WindowSkipReason reason,
        int expectedValue)
    {
        Assert.AreEqual(expectedValue, (int)reason);
    }

    [TestMethod]
    public void ASweptAssignment_NamesTheFirstDesktopInEveryStage()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    ruleId: UnmanagedWindowSweep.RuleId,
                    targetDesktopKey: UnmanagedWindowSweep.TargetDesktopKey),
                DiagnosticTestData.Session);

        Assert.HasCount(3, records);
        foreach (ActivityRecord record in records)
        {
            Assert.DoesNotContain(
                UnmanagedWindowSweep.TargetDesktopKey,
                record.Summary,
                StringComparison.Ordinal);
            Assert.Contains(
                "the first desktop",
                record.Summary,
                StringComparison.Ordinal);
        }
    }

    [TestMethod]
    public void ASweptDecisionRow_ReadsAsAPlainSentenceAndOffersTheRule()
    {
        // Discovery is the Activity page rather than a notification: the user has
        // already been taken to the window, so what they need is the rule.
        ActivityRecordDisplay display = ActivityRecordDisplay.Create(
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(
                    ruleId: UnmanagedWindowSweep.RuleId,
                    targetDesktopKey: UnmanagedWindowSweep.TargetDesktopKey),
                DiagnosticTestData.Session));

        Assert.AreEqual("No rule names this app", display.Rule);
        Assert.AreEqual("First desktop", display.Target);
        Assert.IsTrue(display.OffersRuleCreation);
        Assert.IsNotNull(display.CapturedIdentity);
        Assert.AreEqual("Code.exe", display.CapturedIdentity.ProcessName);

        // The identity offered for the rule is the privacy-safe one, so the title
        // and command line the resolver saw cannot travel into a rule editor.
        Assert.DoesNotContain(
            DiagnosticTestData.SecretTitle,
            display.CopyText,
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            DiagnosticTestData.SecretCommandLine,
            display.CopyText,
            StringComparison.Ordinal);
    }

    [TestMethod]
    public void OnlyTheDecisionRowOffersTheRule()
    {
        // The move and the result carry the same identity. Three buttons for one
        // window would be three ways to do the same thing.
        ImmutableArray<ActivityRecordDisplay> stages =
        [
            .. ActivityRecordFactory
                .FromAssignment(
                    DiagnosticTestData.Assignment(
                        ruleId: UnmanagedWindowSweep.RuleId,
                        targetDesktopKey: UnmanagedWindowSweep.TargetDesktopKey),
                    DiagnosticTestData.Session)
                .Select(ActivityRecordDisplay.Create),
        ];

        Assert.IsTrue(stages.All(static stage => !stage.OffersRuleCreation));
    }

    [TestMethod]
    public void ARuleDrivenRow_DoesNotOfferTheRuleItAlreadyHas()
    {
        ActivityRecordDisplay display = ActivityRecordDisplay.Create(
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(),
                DiagnosticTestData.Session));

        Assert.IsFalse(display.OffersRuleCreation);
        Assert.AreEqual("vscode", display.Rule);
        Assert.AreEqual("code", display.Target);
    }
}

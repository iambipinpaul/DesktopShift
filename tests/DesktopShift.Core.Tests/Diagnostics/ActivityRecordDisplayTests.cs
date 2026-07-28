using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class ActivityRecordDisplayTests
{
    [TestMethod]
    public void Decision_ShowsEveryColumnTheActivityListPromises()
    {
        ActivityRecordDisplay display = ActivityRecordDisplay.Create(
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(),
                DiagnosticTestData.Session));

        Assert.AreEqual("Decision", display.Source);
        Assert.AreEqual("Succeeded", display.ResultLabel);
        Assert.AreEqual("Code.exe", display.Application);
        Assert.AreEqual("Created", display.Trigger);
        Assert.AreEqual("code", display.Target);
        Assert.AreEqual("vscode", display.Rule);
        Assert.AreEqual("Not measured", display.Duration);
        Assert.Contains("Window class: Chrome_WidgetWin_1", display.Identity);
        Assert.Contains(
            DiagnosticTestData.Correlation.ToString(),
            display.Correlation);
        Assert.IsNotEmpty(display.Timestamp);
        Assert.AreEqual(string.Empty, display.ErrorDetails);
    }

    [TestMethod]
    public void AssignmentStages_AreNamedForAReader()
    {
        string[] sources = ActivityRecordFactory
            .FromAssignment(
                DiagnosticTestData.Assignment(),
                DiagnosticTestData.Session)
            .Select(static record => ActivityRecordDisplay.Create(record).Source)
            .ToArray();

        CollectionAssert.AreEqual(
            new[] { "Window move", "Desktop switch", "Assignment result" },
            sources);
    }

    [TestMethod]
    public void Failure_ShowsTheCodeHResultAndWindowsError()
    {
        ActivityRecordDisplay display = ActivityRecordDisplay.Create(
            ActivityRecordFactory
                .FromAssignment(
                    DiagnosticTestData.Assignment(
                        outcome: WindowAssignmentOutcome.Failed,
                        moveOutcome: WindowMoveOutcome.Failed,
                        switchOutcome: DesktopSwitchOutcome.NotRequested,
                        error: new WindowAssignmentError(
                            "window_placement.move_access_denied",
                            "Windows denied access to the window.",
                            unchecked((int)0x80070005),
                            5)),
                    DiagnosticTestData.Session)
                .First(static record =>
                    record.Source == ActivityEventSource.Move));

        Assert.AreEqual("Failed", display.ResultLabel);
        Assert.Contains("window_placement.move_access_denied", display.ErrorDetails);
        Assert.Contains("HRESULT 0x80070005", display.ErrorDetails);
        Assert.Contains("Windows error 5", display.ErrorDetails);
    }

    [TestMethod]
    public void Duration_IsRenderedAtAReadableScale()
    {
        Assert.AreEqual(
            "<1 ms",
            ActivityRecordDisplay.FormatDuration(TimeSpan.FromTicks(1)));
        Assert.AreEqual(
            "42 ms",
            ActivityRecordDisplay.FormatDuration(TimeSpan.FromMilliseconds(42)));
        Assert.AreEqual(
            "1.5 s",
            ActivityRecordDisplay.FormatDuration(TimeSpan.FromMilliseconds(1500)));
    }

    [TestMethod]
    public void CopyText_CarriesTheWholeEventAndItsCorrelation()
    {
        ActivityRecordDisplay display = ActivityRecordDisplay.Create(
            ActivityRecordFactory
                .FromAssignment(
                    DiagnosticTestData.Assignment(),
                    DiagnosticTestData.Session)
                .Last());

        Assert.Contains("Timestamp:", display.CopyText);
        Assert.Contains("Source: Assignment result", display.CopyText);
        Assert.Contains("Result: Succeeded (assignment.succeeded)", display.CopyText);
        Assert.Contains("Application: Code.exe", display.CopyText);
        Assert.Contains("Rule: vscode", display.CopyText);
        Assert.Contains("Target desktop: code", display.CopyText);
        Assert.Contains(
            $"Correlation ID: {DiagnosticTestData.Correlation:D}",
            display.CopyText);
        Assert.Contains(
            $"Session ID: {DiagnosticTestData.Session:D}",
            display.CopyText);
    }

    [TestMethod]
    public void CopyText_NeverCarriesTitlesCommandLinesOrProfilePaths()
    {
        foreach (ActivityRecordDisplay display in ActivityRecordFactory
            .FromAssignment(
                DiagnosticTestData.Assignment(),
                DiagnosticTestData.Session)
            .Select(ActivityRecordDisplay.Create)
            .Append(
                ActivityRecordDisplay.Create(
                    ActivityRecordFactory.FromObservation(
                        DiagnosticTestData.MatchedObservation(),
                        DiagnosticTestData.Session))))
        {
            Assert.DoesNotContain(DiagnosticTestData.SecretTitle, display.CopyText);
            Assert.DoesNotContain(
                DiagnosticTestData.SecretCommandLine,
                display.CopyText);
            Assert.DoesNotContain("marguerite", display.CopyText);
            Assert.DoesNotContain(
                DiagnosticTestData.SecretTitle,
                display.AutomationName);
            Assert.DoesNotContain("marguerite", display.AutomationName);
        }
    }

    [TestMethod]
    public void ErrorMessagesWithProfilePaths_AreRedactedBeforeDisplay()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.MatchedObservation(),
            DiagnosticTestData.Session) with
        {
            Error = new ActivityErrorDetail(
                "test.path",
                @"Failed while reading C:\Users\marguerite\notes.txt."),
        };

        ActivityRecordDisplay display = ActivityRecordDisplay.Create(record);

        Assert.Contains(@"%USERPROFILE%\notes.txt", display.ErrorDetails);
        Assert.DoesNotContain("marguerite", display.ErrorDetails);
    }
}

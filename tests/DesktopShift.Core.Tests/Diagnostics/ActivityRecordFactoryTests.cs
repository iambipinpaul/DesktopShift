using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class ActivityRecordFactoryTests
{
    [TestMethod]
    public void AccessDeniedTilingPlacementExplainsAutomaticFloat()
    {
        var denied = new TilingPlacementDeniedEventArgs(
            new DateTimeOffset(2026, 8, 22, 18, 0, 0, TimeSpan.Zero),
            0x1234,
            new WindowSafeIdentity(
                "AdminTool.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                WindowClass: "AdminWindow"),
            nativeErrorCode: 5);

        ActivityRecord record = ActivityRecordFactory.FromTilingPlacementDenied(
            denied,
            DiagnosticTestData.Session);

        Assert.AreEqual(ActivityEventSource.Tiling, record.Source);
        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual("tiling.placement.access_denied", record.ResultCode);
        Assert.AreEqual("AdminTool.exe", record.Application);
        Assert.Contains("runs as administrator", record.Summary);
        Assert.Contains("left floating", record.Summary);
        Assert.AreEqual(5, record.Error?.NativeErrorCode);
    }

    [TestMethod]
    public void MatchedObservation_BecomesASucceededDecision()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.MatchedObservation(),
            DiagnosticTestData.Session);

        Assert.AreEqual(ActivityEventSource.Observation, record.Source);
        Assert.AreEqual(ActivityResult.Succeeded, record.Result);
        Assert.AreEqual("observation.matched", record.ResultCode);
        Assert.AreEqual(DiagnosticTestData.Correlation, record.CorrelationId);
        Assert.AreEqual(DiagnosticTestData.Session, record.SessionId);
        Assert.AreEqual("vscode", record.RuleId);
        Assert.AreEqual("code", record.TargetDesktopKey);
        Assert.AreEqual("Code.exe", record.Application);
        Assert.AreEqual(17L, record.EventSequence);
        Assert.IsNull(record.Error);
        Assert.Contains("process name", record.Summary);
    }

    [TestMethod]
    public void PinnedObservation_NamesEveryDesktopAndNeverTheStoredKey()
    {
        // The rule's stored key is not read while its action is a pin, so the
        // decision names the destination the pin does have — every desktop —
        // rather than the key the rule happens to store or "no desktop".
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.MatchedObservation() with
            {
                Destination = WindowRuleDestination.PinnedToAllDesktops,
            },
            DiagnosticTestData.Session);

        Assert.AreEqual(ActivityResult.Succeeded, record.Result);
        Assert.AreEqual("observation.matched", record.ResultCode);
        Assert.IsNull(
            record.TargetDesktopKey,
            "A pin never repeats the key its rule stores.");
        Assert.IsTrue(record.PinnedToAllDesktops);
        Assert.AreEqual(
            "Every desktop",
            ActivityRecordDisplay.Create(record).Target);
    }

    [TestMethod]
    public void SkippedObservation_NamesTheSkipReasonInItsCode()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.SkippedObservation(
                WindowSkipReason.BrowserHelperWindow),
            DiagnosticTestData.Session);

        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual(
            "observation.skipped.browser_helper_window",
            record.ResultCode);
        Assert.IsNull(record.RuleId);
    }

    [TestMethod]
    public void ObservationWindowsError_IsCarriedAsErrorDetail()
    {
        ActivityRecord record = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.SkippedObservation(
                WindowSkipReason.IdentityAccessDenied,
                nativeErrorCode: 5),
            DiagnosticTestData.Session);

        Assert.IsNotNull(record.Error);
        Assert.AreEqual("observation.native_error", record.Error.Code);
        Assert.AreEqual(5, record.Error.NativeErrorCode);
    }

    [TestMethod]
    public void Assignment_ProducesMoveSwitchAndResultSharingOneCorrelation()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(),
                DiagnosticTestData.Session);

        Assert.HasCount(3, records);
        CollectionAssert.AreEqual(
            new[]
            {
                ActivityEventSource.Move,
                ActivityEventSource.Switch,
                ActivityEventSource.Assignment,
            },
            records.Select(static record => record.Source).ToArray());
        Assert.IsTrue(
            records.All(
                static record =>
                    record.CorrelationId == DiagnosticTestData.Correlation));
        Assert.AreEqual("move.succeeded", records[0].ResultCode);
        Assert.AreEqual("switch.succeeded", records[1].ResultCode);
        Assert.AreEqual("assignment.succeeded", records[2].ResultCode);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), records[1].Duration);
        Assert.AreEqual(TimeSpan.FromMilliseconds(42), records[2].Duration);
        Assert.AreEqual(
            DiagnosticTestData.PreviousDesktopId,
            records[0].SourceDesktopId);
        Assert.AreEqual(
            DiagnosticTestData.TargetDesktopId,
            records[0].DestinationDesktopId);
        Assert.IsNull(records[1].SourceDesktopId);
        Assert.IsNull(records[1].DestinationDesktopId);
        Assert.AreEqual(
            DiagnosticTestData.PreviousDesktopId,
            records[2].SourceDesktopId);
        Assert.AreEqual(
            DiagnosticTestData.TargetDesktopId,
            records[2].DestinationDesktopId);
    }

    [TestMethod]
    public void DecisionAndAssignment_ShareOneCorrelationAcrossAllStages()
    {
        Guid correlationId = Guid.NewGuid();

        ActivityRecord decision = ActivityRecordFactory.FromObservation(
            DiagnosticTestData.MatchedObservation(correlationId),
            DiagnosticTestData.Session);
        ImmutableArray<ActivityRecord> stages =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(correlationId),
                DiagnosticTestData.Session);

        Assert.IsTrue(
            stages
                .Add(decision)
                .All(record => record.CorrelationId == correlationId));
        Assert.HasCount(
            4,
            stages
                .Add(decision)
                .Select(static record => record.Source)
                .Distinct()
                .ToArray());
    }

    [TestMethod]
    public void SkippedAssignment_OmitsTheMoveAndNamesItsSkipReason()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason:
                        WindowAssignmentSkipReason.TargetDesktopUnresolved,
                    moveOutcome: WindowMoveOutcome.NotAttempted,
                    switchOutcome: DesktopSwitchOutcome.NotRequested),
                DiagnosticTestData.Session);

        Assert.HasCount(1, records);
        Assert.AreEqual(ActivityEventSource.Assignment, records[0].Source);
        Assert.AreEqual(
            "assignment.skipped.target_desktop_unresolved",
            records[0].ResultCode);
        Assert.AreEqual(ActivityResult.Skipped, records[0].Result);
    }

    [TestMethod]
    public void WindowNotTrackedAssignment_IsAStructuredNonActionableSkip()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason.WindowNotTracked,
                    moveOutcome: WindowMoveOutcome.NotAttempted,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    error: new WindowAssignmentError(
                        "window_placement.window_not_tracked",
                        "Windows was not tracking this window on a virtual desktop.",
                        unchecked((int)0x8002802B))),
                DiagnosticTestData.Session);

        Assert.HasCount(1, records);
        Assert.AreEqual(ActivityResult.Skipped, records[0].Result);
        Assert.AreEqual(
            "assignment.skipped.window_not_tracked",
            records[0].ResultCode);
        Assert.Contains("was not tracking", records[0].Summary);
        Assert.IsNotNull(records[0].Error);
        Assert.AreEqual("0x8002802B", records[0].Error?.HResultText);
    }

    [TestMethod]
    public void WindowUnavailableDuringMove_IsASkippedMoveAndAssignment()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason.WindowNotTracked,
                    moveOutcome: WindowMoveOutcome.WindowUnavailable,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    error: new WindowAssignmentError(
                        "window_placement.stale_window_handle",
                        "The window closed before it could be moved.",
                        unchecked((int)0x80070006))),
                DiagnosticTestData.Session);

        Assert.HasCount(2, records);
        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Skipped, move.Result);
        Assert.AreEqual("move.window_unavailable", move.ResultCode);
        Assert.Contains("became unavailable", move.Summary);
        Assert.AreEqual(
            ActivityResult.Skipped,
            records.Single(
                static record => record.Source == ActivityEventSource.Assignment)
                .Result);
    }

    [TestMethod]
    public void FailedMove_CarriesTheHResultAndWindowsErrorCode()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Failed,
                    moveOutcome: WindowMoveOutcome.Failed,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    error: new WindowAssignmentError(
                        "window_placement.move_access_denied",
                        "Windows denied access to the window.",
                        unchecked((int)0x80070005),
                        5)),
                DiagnosticTestData.Session);

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Failed, move.Result);
        Assert.AreEqual("move.failed", move.ResultCode);
        Assert.IsNotNull(move.Error);
        Assert.AreEqual(
            "window_placement.move_access_denied",
            move.Error.Code);
        Assert.AreEqual("0x80070005", move.Error.HResultText);
        Assert.AreEqual(5, move.Error.NativeErrorCode);
    }

    [TestMethod]
    public void SuppressedSwitch_IsSkippedWithoutAnError()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason
                        .SelfGeneratedForegroundSuppressed,
                    moveOutcome: WindowMoveOutcome.NotAttempted,
                    switchOutcome: DesktopSwitchOutcome.Suppressed),
                DiagnosticTestData.Session);

        ActivityRecord switchRecord = records.Single(
            static record => record.Source == ActivityEventSource.Switch);
        Assert.AreEqual(ActivityResult.Skipped, switchRecord.Result);
        Assert.AreEqual("switch.suppressed", switchRecord.ResultCode);
        Assert.IsNull(switchRecord.Error);
    }

    [TestMethod]
    public void PinnedWindow_IsASuccessItNeverCallsAMove()
    {
        // A pin is not a move: the stage reads as pinned, reduces to the same
        // success a move does, and names no desktop because the rule never
        // reads the key it stores.
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Succeeded,
                    moveOutcome: WindowMoveOutcome.PinnedToAllDesktops,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    switchReason:
                        DesktopSwitchDecisionReason.PinnedToAllDesktops),
                DiagnosticTestData.Session);

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Succeeded, move.Result);
        Assert.AreEqual("move.pinned_to_all_desktops", move.ResultCode);
        Assert.AreEqual("Pinned the window to every desktop.", move.Summary);

        ActivityRecord assignment = records.Single(
            static record => record.Source == ActivityEventSource.Assignment);
        Assert.AreEqual(ActivityResult.Succeeded, assignment.Result);
        Assert.AreEqual(
            "Pinned Code.exe to every desktop.",
            assignment.Summary);

        Assert.IsNull(
            move.TargetDesktopKey,
            "A pin names no desktop, so no stage of it repeats the key the rule stores.");
        Assert.IsNull(assignment.TargetDesktopKey);
        Assert.IsTrue(
            move.PinnedToAllDesktops,
            "The destination a pin does have — every desktop — travels on the record instead.");
        Assert.IsTrue(assignment.PinnedToAllDesktops);
        Assert.AreEqual(
            "Every desktop",
            ActivityRecordDisplay.Create(move).Target);
        Assert.AreEqual(
            "Every desktop",
            ActivityRecordDisplay.Create(assignment).Target);
    }

    [TestMethod]
    public void AlreadyPinnedWindow_IsASkipThatIsNotAFailure()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason
                        .AlreadyPinnedToAllDesktops,
                    moveOutcome: WindowMoveOutcome.AlreadyPinnedToAllDesktops,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    switchReason:
                        DesktopSwitchDecisionReason.PinnedToAllDesktops),
                DiagnosticTestData.Session);

        ActivityRecord move = records.Single(
            static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Skipped, move.Result);
        Assert.AreEqual("move.already_pinned_to_all_desktops", move.ResultCode);
        Assert.AreEqual(
            "The window was already pinned to every desktop.",
            move.Summary);

        ActivityRecord assignment = records.Single(
            static record => record.Source == ActivityEventSource.Assignment);
        Assert.AreEqual(ActivityResult.Skipped, assignment.Result);
        Assert.AreEqual(
            "assignment.skipped.already_pinned_to_all_desktops",
            assignment.ResultCode);
        Assert.AreEqual(
            "Skipped Code.exe: Already pinned to all desktops.",
            assignment.Summary);
    }

    [TestMethod]
    public void UnavailablePin_IsASkipThatSaysWhyNothingWasPinned()
    {
        // Limited Mode pins nothing. The window is left alone, which is a skip
        // and not a failure, and the summary has to say so rather than report an
        // empty pin.
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Skipped,
                    skipReason: WindowAssignmentSkipReason.PinUnavailable,
                    moveOutcome: WindowMoveOutcome.NotAttempted,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    switchReason:
                        DesktopSwitchDecisionReason.PinnedToAllDesktops),
                DiagnosticTestData.Session);

        Assert.HasCount(1, records);
        Assert.AreEqual(ActivityEventSource.Assignment, records[0].Source);
        Assert.AreEqual(ActivityResult.Skipped, records[0].Result);
        Assert.AreEqual(
            "assignment.skipped.pin_unavailable",
            records[0].ResultCode);
        Assert.Contains("Pin unavailable", records[0].Summary);
    }

    [TestMethod]
    public void FailedPin_IsAFailureThatNamesAPin()
    {
        ImmutableArray<ActivityRecord> records =
            ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(
                    outcome: WindowAssignmentOutcome.Failed,
                    moveOutcome: WindowMoveOutcome.NotAttempted,
                    switchOutcome: DesktopSwitchOutcome.NotRequested,
                    switchReason:
                        DesktopSwitchDecisionReason.PinnedToAllDesktops,
                    error: new WindowAssignmentError(
                        "assignment.pin_failed",
                        "The window could not be pinned to every desktop.")),
                DiagnosticTestData.Session);

        ActivityRecord assignment = records.Single(
            static record => record.Source == ActivityEventSource.Assignment);
        Assert.AreEqual(ActivityResult.Failed, assignment.Result);
        Assert.AreEqual("assignment.failed", assignment.ResultCode);
        Assert.AreEqual(
            "Could not pin Code.exe to every desktop.",
            assignment.Summary);
        Assert.AreEqual("assignment.pin_failed", assignment.Error?.Code);
    }

    [TestMethod]
    public void EveryProjection_CarriesOnlyPrivacySafeIdentity()
    {
        List<ActivityRecord> records =
        [
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(),
                DiagnosticTestData.Session),
            .. ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(),
                DiagnosticTestData.Session),
        ];

        foreach (ActivityRecord record in records)
        {
            WindowSafeIdentity identity = record.Identity!;
            Assert.AreEqual("Code.exe", identity.ProcessName);
            Assert.AreEqual("Chrome_WidgetWin_1", identity.WindowClass);

            string rendered = string.Join(
                " ",
                record.Summary,
                record.ResultCode,
                record.Application,
                identity.ProcessName,
                identity.PackageFamilyName,
                identity.AppUserModelId,
                identity.WindowClass,
                record.RuleId,
                record.TargetDesktopKey,
                record.Error?.Message);
            Assert.DoesNotContain(DiagnosticTestData.SecretTitle, rendered);
            Assert.DoesNotContain(DiagnosticTestData.SecretCommandLine, rendered);
            Assert.DoesNotContain(
                DiagnosticTestData.SecretExecutablePath,
                rendered);
        }
    }
}

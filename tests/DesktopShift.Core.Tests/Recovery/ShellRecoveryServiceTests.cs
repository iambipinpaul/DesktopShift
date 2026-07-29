using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Recovery;

namespace DesktopShift.Core.Tests.Recovery;

/// <summary>
/// What DesktopShift does when Explorer restarts, the machine wakes, or a
/// monitor arrives.
/// </summary>
/// <remarks>
/// Every disruption here is injected into a fake. No test restarts Explorer,
/// suspends or resumes the machine, changes a display setting, registers a real
/// hook or hotkey, creates, deletes, reorders or switches a real virtual
/// desktop, or moves a real window. Nothing sleeps: the one test that needs a
/// pass to still be running while another notification arrives holds it on a
/// <see cref="TaskCompletionSource"/> it controls.
/// </remarks>
[TestClass]
public sealed class ShellRecoveryServiceTests
{
    [TestMethod]
    public async Task AnExplorerRestart_DropsValidatesRestoresAndReconcilesOnce()
    {
        RecoveryHarness harness = RecoveryHarness.Create();

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        CollectionAssert.AreEqual(
            new[]
            {
                RecoveryStep.Invalidate,
                RecoveryStep.Validate,
                RecoveryStep.Reregister,
                RecoveryStep.Reconcile,
            },
            harness.Steps.ToArray());
        Assert.AreEqual(ShellRecoveryOutcome.Recovered, result.Outcome);
        Assert.AreEqual(ShellRecoveryState.Healthy, result.State);
        Assert.AreEqual(ShellRecoveryService.RegistrationsRestoredCode, result.Code);
        Assert.IsTrue(result.RegistrationsInvalidated);
        Assert.IsTrue(result.CapabilityValidationRan);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.IsNull(result.FailedStage);
        Assert.AreEqual(1, result.CoalescedSignalCount);

        Assert.AreEqual(1, harness.Registrations.InvalidateCount);
        Assert.AreEqual(1, harness.Compatibility.RunCount);
        Assert.AreEqual(1, harness.Registrations.ReregisterCount);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.AreEqual("ExplorerRestarted", harness.Topology.Reasons.Single());
        Assert.AreEqual(ShellRecoveryState.Healthy, harness.Service.State);
    }

    [TestMethod]
    public async Task AResumeWithLiveRegistrations_ReconcilesOnceAndValidatesNothing()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Topology.Outcome = ManagedDesktopTopologyRecoveryOutcome.Reconciled;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.SessionResumed);

        // The desktop layout can change across sleep even when the hooks
        // survive it, so the mapping is reconciled and nothing is rebuilt.
        CollectionAssert.AreEqual(
            new[] { RecoveryStep.Check, RecoveryStep.Reconcile },
            harness.Steps.ToArray());
        Assert.AreEqual(ShellRecoveryOutcome.Recovered, result.Outcome);
        Assert.AreEqual(ShellRecoveryService.ReconciledCode, result.Code);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.IsFalse(result.RegistrationsInvalidated);
        Assert.IsFalse(result.CapabilityValidationRan);
        Assert.IsFalse(result.RegistrationsRestored);

        Assert.AreEqual(1, harness.Registrations.CheckCount);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.AreEqual(0, harness.Compatibility.RunCount);
        Assert.AreEqual(0, harness.Registrations.InvalidateCount);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
    }

    [TestMethod]
    public async Task ADisplayChangeWithLiveRegistrations_ChangesNothingAtAll()
    {
        RecoveryHarness harness = RecoveryHarness.Create();

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.DisplayChanged);

        // Virtual desktops are not per-monitor, so one targeted question is the
        // whole of the work a monitor change is allowed to cost.
        CollectionAssert.AreEqual(
            new[] { RecoveryStep.Check },
            harness.Steps.ToArray());
        Assert.AreEqual(ShellRecoveryOutcome.NoActionNeeded, result.Outcome);
        Assert.AreEqual(ShellRecoveryService.IntactCode, result.Code);
        Assert.IsFalse(result.ReconciliationRan);
        Assert.AreEqual(0, harness.Topology.CallCount);
        Assert.AreEqual(0, harness.Compatibility.RunCount);
        Assert.AreEqual(0, harness.Registrations.InvalidateCount);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(ShellRecoveryState.Healthy, harness.Service.State);
    }

    [TestMethod]
    public async Task AnExplorerRestart_NeverAsksWhetherTheDeadShellsRegistrationsAreLive()
    {
        RecoveryHarness harness = RecoveryHarness.Create();

        // A probe that answered "still installed" would be describing hooks
        // owned by a process that no longer exists, so it is never asked.
        harness.Registrations.Validity = FakeNativeRegistrationSet.Intact;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(0, harness.Registrations.CheckCount);
        Assert.IsTrue(result.RegistrationsInvalidated);
        Assert.IsTrue(result.RegistrationsRestored);
    }

    [TestMethod]
    public async Task AnExplorerRestart_InvalidatesBeforeValidatingAndValidatesBeforeReregistering()
    {
        RecoveryHarness harness = RecoveryHarness.Create();

        _ = await harness.Service.RecoverAsync(ShellLifecycleSignal.ExplorerRestarted);

        // Ordering, not counts: a pass that took every step in the wrong order
        // would install a second hook beside a dead one, or take hooks against
        // a shell that was never asked whether it supports them.
        Assert.AreEqual(
            0,
            harness.Steps.IndexOf(RecoveryStep.Invalidate),
            "Stale registrations must be dropped before anything else happens.");
        Assert.AreEqual(
            1,
            harness.Steps.IndexOf(RecoveryStep.Validate),
            "Capability validation must run after the drop, not before it.");
        Assert.AreEqual(
            2,
            harness.Steps.IndexOf(RecoveryStep.Reregister),
            "Fresh hooks must be taken only after validation has succeeded.");
    }

    [TestMethod]
    public async Task CapabilityValidationThatFails_NeverTakesFreshRegistrations()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Compatibility.Outcome = CompatibilityTestOutcome.Failed;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.CapabilityValidation,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryState.Error, result.State);
        Assert.AreEqual(
            ShellRecoveryService.CapabilityValidationFailedCode,
            result.Code);
        Assert.IsTrue(result.CapabilityValidationRan);
        Assert.IsFalse(result.RegistrationsRestored);
        Assert.IsFalse(result.ReconciliationRan);

        // The whole point of the criterion: no hook is taken at all.
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(0, harness.Topology.CallCount);
        CollectionAssert.AreEqual(
            new[] { RecoveryStep.Invalidate, RecoveryStep.Validate },
            harness.Steps.ToArray());
        Assert.AreEqual(ShellRecoveryState.Error, harness.Service.State);
    }

    [TestMethod]
    public async Task AReregistrationThatFails_ReportsItsStageAndReconcilesNothing()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Registrations.ReregisterResult = NativeRegistrationResult.Failed(
            "recovery.hooks_unavailable",
            "Windows would not install the window hooks again.",
            unchecked((int)0x80070005));

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.Reregistration,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryState.Error, result.State);
        Assert.AreEqual("recovery.hooks_unavailable", result.Code);
        Assert.AreEqual<int?>(unchecked((int)0x80070005), result.HResult);
        Assert.IsFalse(result.RegistrationsRestored);
        Assert.IsFalse(result.ReconciliationRan);
        Assert.AreEqual(0, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task ValidationThatOnlyReachesLimitedMode_StillReconcilesAndKeepsRunning()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Compatibility.Outcome = CompatibilityTestOutcome.PassedLimitedMode;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        // Reduced capability, stated plainly, is a supported way to run.
        Assert.AreEqual(ShellRecoveryState.Limited, result.State);
        Assert.AreEqual(ShellRecoveryOutcome.Limited, result.Outcome);
        Assert.AreEqual(
            ShellRecoveryService.RegistrationsRestoredLimitedCode,
            result.Code);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.IsNull(result.FailedStage);
        Assert.AreEqual(1, harness.Registrations.ReregisterCount);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.AreEqual(ShellRecoveryState.Limited, harness.Service.State);
    }

    [TestMethod]
    public async Task AResumeWhoseReconciliationIsLimited_ReportsLimitedMode()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Topology.Outcome = ManagedDesktopTopologyRecoveryOutcome.Limited;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.SessionResumed);

        Assert.AreEqual(ShellRecoveryOutcome.Limited, result.Outcome);
        Assert.AreEqual(ShellRecoveryState.Limited, result.State);
        Assert.AreEqual(ShellRecoveryService.ReconciliationLimitedCode, result.Code);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.AreEqual(ShellRecoveryState.Limited, harness.Service.State);
    }

    [TestMethod]
    public async Task AFullRebuildWhoseReconciliationIsLimited_ReportsLimitedMode()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Topology.Outcome = ManagedDesktopTopologyRecoveryOutcome.Limited;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Limited, result.Outcome);
        Assert.AreEqual(ShellRecoveryState.Limited, result.State);
        Assert.AreEqual(ShellRecoveryService.ReconciliationLimitedCode, result.Code);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.AreEqual(ShellRecoveryState.Limited, harness.Service.State);
    }

    [TestMethod]
    public async Task AResumeWithBrokenRegistrations_RunsTheWholeRebuildSequence()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Registrations.Validity = FakeNativeRegistrationSet.Broken;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.SessionResumed);

        CollectionAssert.AreEqual(
            new[]
            {
                RecoveryStep.Check,
                RecoveryStep.Invalidate,
                RecoveryStep.Validate,
                RecoveryStep.Reregister,
                RecoveryStep.Reconcile,
            },
            harness.Steps.ToArray());
        Assert.AreEqual(ShellRecoveryOutcome.Recovered, result.Outcome);
        Assert.AreEqual(ShellRecoveryState.Healthy, result.State);
        Assert.IsTrue(result.RegistrationsInvalidated);
        Assert.IsTrue(result.CapabilityValidationRan);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task ABurstOfIdenticalSignals_ProducesExactlyOneReconciliation()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        TaskCompletionSource gate = new();
        harness.Topology.Gate = gate;

        // Windows raises several notifications for one physical wake. Starting
        // every pass before releasing the first is that burst, with no clock
        // involved and no chance of a flaky ordering.
        List<Task<ShellRecoveryResult>> passes = [];
        for (int notification = 0; notification < 5; notification++)
        {
            passes.Add(
                harness.Service.RecoverAsync(ShellLifecycleSignal.SessionResumed));
        }

        gate.SetResult();
        ShellRecoveryResult[] results = await Task.WhenAll(passes);

        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.AreEqual(1, harness.Registrations.CheckCount);

        ShellRecoveryResult[] folded = [.. results
            .Where(static result => result.CoalescedSignalCount == 0)];
        Assert.HasCount(4, folded);
        Assert.IsTrue(
            folded.All(static result =>
                result.Code == ShellRecoveryService.DuplicateNotificationCode));
        Assert.IsTrue(folded.All(static result => !result.ReconciliationRan));

        ShellRecoveryResult answered = results.Single(
            static result => result.CoalescedSignalCount != 0);
        Assert.AreEqual(5, answered.CoalescedSignalCount);
        Assert.IsTrue(answered.ReconciliationRan);
    }

    [TestMethod]
    public async Task ADisplayChangeDuringExplorerValidation_WaitsThenRunsItsOwnPass()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        TaskCompletionSource gate = new();
        harness.Compatibility.Gate = gate;

        Task<ShellRecoveryResult> explorer = harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);
        Task<ShellRecoveryResult> displayPass = harness.Service.RecoverAsync(
            ShellLifecycleSignal.DisplayChanged);

        // Explorer has invalidated the old registrations and is still validating.
        // The display pass is distinct, so it is not folded, but it must not enter
        // registration work until Explorer has finished the entire pass.
        Assert.AreEqual(1, harness.Registrations.InvalidateCount);
        Assert.AreEqual(1, harness.Compatibility.RunCount);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(0, harness.Registrations.CheckCount);
        Assert.IsFalse(displayPass.IsCompleted);

        gate.SetResult();
        ShellRecoveryResult restart = await explorer;
        ShellRecoveryResult display = await displayPass;

        // Signals are folded per kind. A monitor change arriving during an
        // Explorer restart is a different event and gets its own serialized pass.
        Assert.AreNotEqual(
            ShellRecoveryService.DuplicateNotificationCode,
            display.Code);
        Assert.AreEqual(1, display.CoalescedSignalCount);
        Assert.AreEqual(ShellRecoveryService.IntactCode, display.Code);
        Assert.AreEqual(1, harness.Registrations.CheckCount);

        Assert.AreEqual(1, restart.CoalescedSignalCount);
        Assert.AreEqual(ShellRecoveryOutcome.Recovered, restart.Outcome);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task ASuccessfulResumeAfterAReconciliationFailure_ClearsErrorState()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Topology.Outcome = ManagedDesktopTopologyRecoveryOutcome.Failed;

        ShellRecoveryResult failed = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryState.Error, failed.State);
        Assert.AreEqual(ShellRecoveryState.Error, harness.Service.State);

        harness.Topology.Outcome = ManagedDesktopTopologyRecoveryOutcome.NoChange;
        ShellRecoveryResult recovered = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.SessionResumed);

        Assert.AreEqual(ShellRecoveryOutcome.NoActionNeeded, recovered.Outcome);
        Assert.AreEqual(ShellRecoveryState.Healthy, recovered.State);
        Assert.AreEqual(ShellRecoveryState.Healthy, harness.Service.State);
        Assert.AreEqual(2, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task ACanceledPassWaitingForAnotherSignal_ReleasesItsCoalescerClaim()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        TaskCompletionSource gate = new();
        harness.Compatibility.Gate = gate;

        Task<ShellRecoveryResult> explorer = harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);
        using CancellationTokenSource cancellation = new();
        Task<ShellRecoveryResult> waitingDisplay = harness.Service.RecoverAsync(
            ShellLifecycleSignal.DisplayChanged,
            cancellation.Token);

        await cancellation.CancelAsync();
        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await waitingDisplay);

        Assert.AreEqual(0, harness.Registrations.CheckCount);

        gate.SetResult();
        _ = await explorer;

        ShellRecoveryResult nextDisplay = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.DisplayChanged);

        Assert.AreEqual(1, nextDisplay.CoalescedSignalCount);
        Assert.AreNotEqual(
            ShellRecoveryService.DuplicateNotificationCode,
            nextDisplay.Code);
        Assert.AreEqual(1, harness.Registrations.CheckCount);
    }

    [TestMethod]
    public async Task AFoldedNotification_DecidesNothingAndPublishesNothing()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        TaskCompletionSource gate = new();
        harness.Topology.Gate = gate;

        Task<ShellRecoveryResult> first = harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);
        ShellRecoveryResult folded = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(0, folded.CoalescedSignalCount);
        Assert.AreEqual(ShellRecoveryService.DuplicateNotificationCode, folded.Code);
        Assert.AreEqual(ShellRecoveryOutcome.NoActionNeeded, folded.Outcome);
        Assert.AreEqual(ShellRecoveryState.Healthy, folded.State);
        Assert.IsFalse(folded.ReconciliationRan);
        Assert.IsFalse(folded.RegistrationsInvalidated);
        Assert.IsFalse(folded.CapabilityValidationRan);
        Assert.IsFalse(folded.RegistrationsRestored);

        // A folded notification decided nothing, so it is not allowed to add a
        // single line to the log the folding exists to keep readable.
        Assert.IsEmpty(harness.Journal.Snapshot);

        gate.SetResult();
        ShellRecoveryResult answered = await first;

        Assert.AreEqual(2, answered.CoalescedSignalCount);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.HasCount(5, harness.Journal.Snapshot);
        Assert.IsTrue(
            harness.Journal.Snapshot.All(
                record => record.CorrelationId == answered.CorrelationId));
    }

    [TestMethod]
    public async Task AValidityCheckThatThrows_RebuildsRatherThanAssumingTheRegistrationsSurvived()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Registrations.CheckFailure =
            new InvalidOperationException("The probe could not be answered.");

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.SessionResumed);

        // A probe that cannot answer is not evidence that everything is fine.
        Assert.AreEqual(ShellRecoveryOutcome.Recovered, result.Outcome);
        Assert.IsTrue(result.RegistrationsInvalidated);
        Assert.IsTrue(result.CapabilityValidationRan);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.IsNull(result.FailedStage);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AValidityCheckThatThrows_StillEndsInAFailedResultWhenTheRebuildCannotValidate()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Registrations.CheckFailure =
            new InvalidOperationException("The probe could not be answered.");
        harness.Compatibility.Outcome = CompatibilityTestOutcome.Failed;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.DisplayChanged);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.CapabilityValidation,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryState.Error, result.State);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(0, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AnInvalidationThatThrows_FailsThePassWithoutEscaping()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Registrations.InvalidateFailure =
            new InvalidOperationException("Unhooking blew up.");

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.Invalidation,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryService.UnexpectedFailureCode, result.Code);
        Assert.AreEqual(ShellRecoveryState.Error, result.State);
        Assert.AreEqual(0, harness.Compatibility.RunCount);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(0, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AValidationThatThrows_FailsThePassWithoutEscaping()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Compatibility.Failure =
            new InvalidOperationException("The compatibility test blew up.");

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.CapabilityValidation,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryService.UnexpectedFailureCode, result.Code);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(0, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AReregistrationThatThrows_FailsThePassWithoutEscaping()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Registrations.ReregisterFailure =
            new InvalidOperationException("Hooking blew up.");

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.Reregistration,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryService.UnexpectedFailureCode, result.Code);
        Assert.IsFalse(result.RegistrationsRestored);
        Assert.AreEqual(0, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AReconciliationThatThrows_FailsThePassWithoutEscaping()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Topology.Failure =
            new InvalidOperationException("The reconciliation blew up.");

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        // Recovery failing is not a reason for the application to stop running,
        // and it is never a reason to touch Explorer.
        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.Reconciliation,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryService.ReconciliationFailedCode, result.Code);
        Assert.AreEqual(ShellRecoveryState.Error, result.State);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AReconciliationThatFails_IsReportedAgainstItsOwnStage()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Topology.Outcome = ManagedDesktopTopologyRecoveryOutcome.Failed;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.SessionResumed);

        Assert.AreEqual(ShellRecoveryOutcome.Failed, result.Outcome);
        Assert.AreEqual<ShellRecoveryStage?>(
            ShellRecoveryStage.Reconciliation,
            result.FailedStage);
        Assert.AreEqual(ShellRecoveryService.ReconciliationFailedCode, result.Code);
        Assert.IsTrue(result.ReconciliationRan);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task AFailedExplorerRecovery_IsVisibleInStructuredActivity()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        harness.Compatibility.Outcome = CompatibilityTestOutcome.Failed;

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        IReadOnlyList<ActivityRecord> records = harness.Journal.Snapshot;
        ActivityRecord failure = records.Single(
            static record =>
                record.Result == ActivityResult.Failed && record.Error is not null);

        Assert.AreEqual("recovery.explorer_restarted.failed", failure.ResultCode);
        Assert.AreEqual(
            ShellRecoveryService.CapabilityValidationFailedCode,
            failure.Error!.Code);
        Assert.AreEqual(result.CorrelationId, failure.CorrelationId);
        Assert.IsTrue(
            records.All(static record =>
                record.Source == ActivityEventSource.Recovery));
        Assert.IsTrue(
            records.All(static record =>
                record.RecoverySignal == "explorer_restarted"));

        // A recovery decision is about the shell, so no window identity travels
        // with it.
        Assert.IsTrue(records.All(static record => record.Identity is null));
        Assert.IsTrue(records.All(static record => record.Application is null));
    }

    [TestMethod]
    public async Task ARecoveredPass_PublishesEveryStepItTookUnderOneCorrelation()
    {
        RecoveryHarness harness = RecoveryHarness.Create();

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        string[] codes = [.. harness.Journal.Snapshot
            .Select(static record => record.ResultCode)];

        CollectionAssert.AreEqual(
            new[]
            {
                "recovery.explorer_restarted.recovered",
                "recovery.explorer_restarted.registrations_invalidated",
                "recovery.explorer_restarted.capability_validated",
                "recovery.explorer_restarted.registrations_restored",
                "recovery.explorer_restarted.reconciliation_ran",
            },
            codes);
        Assert.IsTrue(
            harness.Journal.Snapshot.All(
                record => record.CorrelationId == result.CorrelationId));
        Assert.IsTrue(
            harness.Journal.Snapshot.All(
                record => record.OccurredAt == RecoveryHarness.ObservedAt));
    }

    [TestMethod]
    public async Task AHostWithoutDiagnostics_StillRecoversAndRecordsNothing()
    {
        RecoveryHarness harness = RecoveryHarness.WithoutDiagnostics();

        ShellRecoveryResult result = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(ShellRecoveryOutcome.Recovered, result.Outcome);
        Assert.IsTrue(result.RegistrationsRestored);
        Assert.AreEqual(1, harness.Topology.CallCount);
        Assert.IsEmpty(harness.Journal.Snapshot);
    }

    [TestMethod]
    public async Task ACancelledCaller_SeesTheCancellationAndNoPassRuns()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        using CancellationTokenSource cancellation = new();
        await cancellation.CancelAsync();

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await harness.Service.RecoverAsync(
                ShellLifecycleSignal.ExplorerRestarted,
                cancellation.Token));

        Assert.IsEmpty(harness.Steps);
        Assert.IsEmpty(harness.Journal.Snapshot);

        // The cancelled call never claimed the signal, so the next notification
        // is not folded into a pass that does not exist.
        ShellRecoveryResult next = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);
        Assert.AreEqual(1, next.CoalescedSignalCount);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public async Task ACancellationMidPass_PropagatesAndReleasesTheCoalescer()
    {
        RecoveryHarness harness = RecoveryHarness.Create();
        using CancellationTokenSource cancellation = new();
        harness.Compatibility.OnRun = cancellation.Cancel;

        _ = await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await harness.Service.RecoverAsync(
                ShellLifecycleSignal.ExplorerRestarted,
                cancellation.Token));

        Assert.AreEqual(1, harness.Registrations.InvalidateCount);
        Assert.AreEqual(0, harness.Registrations.ReregisterCount);
        Assert.AreEqual(0, harness.Topology.CallCount);
        Assert.IsEmpty(harness.Journal.Snapshot);

        harness.Compatibility.OnRun = null;
        ShellRecoveryResult next = await harness.Service.RecoverAsync(
            ShellLifecycleSignal.ExplorerRestarted);

        Assert.AreEqual(1, next.CoalescedSignalCount);
        Assert.AreEqual(ShellRecoveryOutcome.Recovered, next.Outcome);
        Assert.AreEqual(1, harness.Topology.CallCount);
    }

    [TestMethod]
    public void AMissingDependency_IsRejectedAtConstruction()
    {
        RecoveryHarness harness = RecoveryHarness.Create();

        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
        {
            _ = new ShellRecoveryService(
                null!,
                harness.Compatibility,
                harness.Topology,
                harness.Time);
        });

        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
        {
            _ = new ShellRecoveryService(
                harness.Registrations,
                null!,
                harness.Topology,
                harness.Time);
        });

        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
        {
            _ = new ShellRecoveryService(
                harness.Registrations,
                harness.Compatibility,
                null!,
                harness.Time);
        });

        _ = Assert.ThrowsExactly<ArgumentNullException>(() =>
        {
            _ = new ShellRecoveryService(
                harness.Registrations,
                harness.Compatibility,
                harness.Topology,
                null!);
        });
    }
}

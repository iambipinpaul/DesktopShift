using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// Where event-to-move latency is measured from, and what it counts.
/// </summary>
/// <remarks>
/// Every assertion here runs against a clock the test moves by hand, so the
/// numbers are arithmetic rather than a race with the machine. The pipeline being
/// measured is the real one: the event carries a receipt reading the way the
/// WinEvent source stamps it, the processor holds an opened window the way the
/// follow policy holds it, and the assignment moves and switches through the same
/// calls.
/// </remarks>
[TestClass]
public sealed class AssignmentLatencyInstrumentationTests
{
    [TestMethod]
    public async Task Latency_StartsAtEventReceiptAndEndsAtMoveCompletion()
    {
        // The queue wait is part of what the user felt, so it is part of the
        // measurement. Starting the clock where the processor picked the event up
        // would hide the very delay a saturated queue causes.
        LatencyPipelineHarness harness = new();
        harness.Placement.QueryCost = TimeSpan.FromMilliseconds(3);
        harness.Placement.MoveCost = TimeSpan.FromMilliseconds(9);
        harness.Placement.SetCurrent(10, LatencyPipelineHarness.OtherDesktopId);

        WindowEvent published = harness.Stamp(WindowEventKind.Shown, 10);

        // The event sits in the queue while the processor is busy with something
        // else.
        harness.Clock.Advance(TimeSpan.FromMilliseconds(20));
        _ = await harness.ProcessAsync(published);

        Assert.HasCount(1, harness.Recorder.Assignments);
        AssignmentLatencySample sample = harness.Recorder.Assignments[0];

        // 20 waiting, 3 querying, 9 moving.
        Assert.AreEqual(TimeSpan.FromMilliseconds(32), sample.EventToCompletion);
        Assert.AreEqual(TimeSpan.Zero, sample.DeferredByPolicy);
        Assert.AreEqual(TimeSpan.FromMilliseconds(32), sample.PipelineLatency);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, sample.MoveOutcome);
        Assert.IsTrue(sample.Moved);
        Assert.AreEqual(WindowEventKind.Shown, sample.Trigger);

        // The assignment's own duration excludes the queue wait, which is what
        // makes a slow pass distinguishable from a long queue.
        Assert.AreEqual(TimeSpan.FromMilliseconds(12), sample.AssignmentDuration);

        // The event carried a receipt reading, which is what made the queue wait
        // measurable at all.
        Assert.AreNotEqual(0L, published.ReceivedTimestamp);
    }

    [TestMethod]
    public async Task Latency_IncludesTheDesktopSwitchThatFollowedTheMove()
    {
        // A window that landed but left the user on another desktop has not
        // finished arriving, so the switch is inside the measurement.
        LatencyPipelineHarness harness = new();
        harness.Placement.MoveCost = TimeSpan.FromMilliseconds(5);
        harness.Topology.SwitchCost = TimeSpan.FromMilliseconds(25);
        harness.Placement.SetCurrent(11, LatencyPipelineHarness.OtherDesktopId);

        // Opened first, so the activation counts as the window's first and the
        // switch policy approves following it.
        _ = await harness.ObserveAsync(WindowEventKind.Shown, 11);
        _ = await harness.ProcessAsync(WindowEventKind.ForegroundActivated, 11);

        AssignmentLatencySample activation =
            harness.Recorder.Assignments[^1];

        Assert.AreEqual(1, harness.Topology.SwitchCallCount);

        // The window was already on its desktop by the time it was activated —
        // the opening event moved it — so all 25 milliseconds of this one are the
        // switch.
        Assert.AreEqual(TimeSpan.FromMilliseconds(25), activation.EventToCompletion);
        Assert.HasCount(1, harness.Recorder.Switches);
        Assert.AreEqual(TimeSpan.FromMilliseconds(25), harness.Recorder.Switches[0]);

        // The move it followed is measured too, in its own sample.
        Assert.HasCount(2, harness.Recorder.Assignments);
        Assert.AreEqual(
            TimeSpan.FromMilliseconds(5),
            harness.Recorder.Assignments[0].EventToCompletion);
    }

    [TestMethod]
    public async Task TheFollowGracePeriod_IsReportedAsDeferralRatherThanAsLatency()
    {
        // The 150 ms wait is a deliberate policy: it is how a newly opened window
        // gets the chance to take the foreground and bring the desktop with it.
        // Counting it as latency would make the budget a budget on that policy,
        // and the only way to pass would be to stop following windows.
        LatencyPipelineHarness harness = new(TimeSpan.FromMilliseconds(150));
        harness.Placement.MoveCost = TimeSpan.FromMilliseconds(4);
        harness.Placement.SetCurrent(12, LatencyPipelineHarness.OtherDesktopId);

        _ = await harness.ObserveAsync(WindowEventKind.Shown, 12);
        Assert.IsEmpty(harness.Recorder.Assignments);

        // Nothing activated the window, so it waits out its grace period.
        harness.Clock.Advance(TimeSpan.FromMilliseconds(150));
        _ = await harness.FollowScheduler.ReleaseAllAsync();

        Assert.HasCount(1, harness.Recorder.Assignments);
        AssignmentLatencySample sample = harness.Recorder.Assignments[0];

        Assert.AreEqual(TimeSpan.FromMilliseconds(154), sample.EventToCompletion);
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), sample.DeferredByPolicy);
        Assert.AreEqual(TimeSpan.FromMilliseconds(4), sample.PipelineLatency);

        // The wall clock would have failed a 100 ms P95. The budgeted series
        // does not, and both numbers are on the record.
        Assert.IsGreaterThan(
            PerformanceBudget.NormalLocal.AssignmentLatencyP95,
            sample.EventToCompletion);
        Assert.IsLessThan(
            PerformanceBudget.NormalLocal.AssignmentLatencyP50,
            sample.PipelineLatency);
    }

    [TestMethod]
    public async Task AnAssignmentThatWasNotDeferred_ReportsNoDeferralAtAll()
    {
        // A foreground activation is never held, so its deferral must be zero
        // rather than "however long the previous hold happened to be".
        LatencyPipelineHarness harness = new(TimeSpan.FromMilliseconds(150));
        harness.Placement.MoveCost = TimeSpan.FromMilliseconds(6);
        harness.Placement.SetCurrent(13, LatencyPipelineHarness.OtherDesktopId);

        _ = await harness.ObserveAsync(WindowEventKind.Shown, 13);
        harness.Clock.Advance(TimeSpan.FromMilliseconds(2));
        _ = await harness.ProcessAsync(WindowEventKind.ForegroundActivated, 13);

        Assert.HasCount(1, harness.Recorder.Assignments);
        AssignmentLatencySample sample = harness.Recorder.Assignments[0];

        Assert.AreEqual(TimeSpan.Zero, sample.DeferredByPolicy);
        Assert.AreEqual(sample.EventToCompletion, sample.PipelineLatency);
    }

    [TestMethod]
    public async Task AnUnstampedEvent_IsMeasuredFromWhereTheProcessorPickedItUp()
    {
        // A startup pass and a manual Reassign All raise their own events, which
        // never waited in the queue. Measuring them against a queue wait they
        // never had would invent latency.
        LatencyPipelineHarness harness = new();
        harness.Placement.MoveCost = TimeSpan.FromMilliseconds(7);
        harness.Placement.SetCurrent(14, LatencyPipelineHarness.OtherDesktopId);

        harness.Clock.Advance(TimeSpan.FromMinutes(5));
        _ = await harness.ProcessAsync(
            WindowEventKind.StartupReconciliation,
            14,
            stamped: false);

        AssignmentLatencySample sample = harness.Recorder.Assignments[0];

        Assert.AreEqual(TimeSpan.FromMilliseconds(7), sample.EventToCompletion);
    }

    [TestMethod]
    public async Task AWindowAlreadyInPlace_IsMeasuredButNotCountedAsAMove()
    {
        // It went through the whole pipeline, so its latency is real. It did not
        // move, so counting it as one would overstate what the numbers cover.
        LatencyPipelineHarness harness = new();
        harness.Placement.QueryCost = TimeSpan.FromMilliseconds(2);
        harness.Placement.SetCurrent(15, LatencyPipelineHarness.ManagedDesktopId);

        _ = await harness.ProcessAsync(WindowEventKind.Shown, 15);

        AssignmentLatencySample sample = harness.Recorder.Assignments[0];

        Assert.AreEqual(WindowMoveOutcome.AlreadyCorrect, sample.MoveOutcome);
        Assert.IsFalse(sample.Moved);
        Assert.AreEqual(TimeSpan.FromMilliseconds(2), sample.EventToCompletion);
        Assert.IsEmpty(harness.Placement.Moves);
    }

    [TestMethod]
    public async Task ASkippedWindow_IsNotMeasuredAtAll()
    {
        // Nothing was assigned, so there is no assignment latency to report. A
        // zero recorded here would drag every percentile towards zero and make
        // the budget meaningless on a machine full of windows nothing names.
        LatencyPipelineHarness harness = new();

        WindowObservationActivity destroyed =
            await harness.ProcessAsync(WindowEventKind.Destroyed, 16);

        Assert.AreEqual(WindowObservationOutcome.Skipped, destroyed.Outcome);
        Assert.IsEmpty(harness.Recorder.Assignments);
    }

    [TestMethod]
    public async Task AMeasuredPipeline_StillFollowsTheWindowExactlyOnce()
    {
        // The measurement has to be an observation and nothing more. The follow
        // behaviour is asserted here against an instrumented pipeline, so a
        // recorder that ever changed a decision — by taking a lock the assignment
        // waits on, or by releasing a hold early — would fail here rather than in
        // a user's session.
        LatencyPipelineHarness measured = new(TimeSpan.FromMilliseconds(150));
        measured.Placement.SetCurrent(17, LatencyPipelineHarness.OtherDesktopId);
        _ = await measured.ObserveAsync(WindowEventKind.Shown, 17);
        WindowObservationActivity fromMeasured = await measured.ProcessAsync(
            WindowEventKind.ForegroundActivated,
            17);

        Assert.AreEqual(WindowObservationOutcome.Matched, fromMeasured.Outcome);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            fromMeasured.Assignment!.Outcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.Succeeded,
            fromMeasured.Assignment.SwitchOutcome);
        Assert.HasCount(1, measured.Placement.Moves);
        Assert.AreEqual(
            LatencyPipelineHarness.ManagedDesktopId,
            measured.Placement.Moves[0].DesktopId);
        Assert.AreEqual(1, measured.Topology.SwitchCallCount);

        // The held move was dropped rather than run, so the window is placed
        // once, by the activation — exactly as the follow policy requires.
        Assert.AreEqual(0, measured.FollowScheduler.PendingCount);
        Assert.IsTrue(measured.Suppression.HasPending((nint)17));
    }
}

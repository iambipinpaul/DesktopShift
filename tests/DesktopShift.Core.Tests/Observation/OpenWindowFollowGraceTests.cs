using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Observation;

[TestClass]
public sealed class OpenWindowFollowGraceTests
{
    [TestMethod]
    public async Task HeldAssignment_RunsWhenTheGracePeriodEnds()
    {
        ManualOpenWindowFollowScheduler scheduler = new();
        using OpenWindowFollowGrace grace = new(scheduler: scheduler);
        int ran = 0;

        grace.Hold((nint)1, () => { ran++; return Task.CompletedTask; });

        Assert.IsTrue(grace.IsHeld((nint)1));
        Assert.AreEqual(0, ran);

        Assert.AreEqual(1, await scheduler.ReleaseAllAsync());
        Assert.AreEqual(1, ran);
        Assert.IsFalse(grace.IsHeld((nint)1));
        Assert.AreEqual(0, grace.HeldCount);
    }

    [TestMethod]
    public async Task HoldingTheSameWindowTwice_RunsOnlyTheLater()
    {
        // Opening a window produces a created event and a shown event a moment
        // apart, and both reach the hold. Stacking them would move the window
        // twice.
        ManualOpenWindowFollowScheduler scheduler = new();
        using OpenWindowFollowGrace grace = new(scheduler: scheduler);
        List<string> ran = [];

        grace.Hold((nint)2, () => { ran.Add("created"); return Task.CompletedTask; });
        grace.Hold((nint)2, () => { ran.Add("shown"); return Task.CompletedTask; });

        Assert.AreEqual(1, grace.HeldCount);
        await scheduler.ReleaseAllAsync();

        Assert.HasCount(1, ran);
        Assert.AreEqual("shown", ran[0]);
    }

    [TestMethod]
    public async Task CancelledHold_NeverRuns()
    {
        ManualOpenWindowFollowScheduler scheduler = new();
        using OpenWindowFollowGrace grace = new(scheduler: scheduler);
        int ran = 0;

        grace.Hold((nint)3, () => { ran++; return Task.CompletedTask; });

        Assert.IsTrue(grace.Cancel((nint)3));
        Assert.IsFalse(grace.Cancel((nint)3));
        Assert.IsFalse(grace.IsHeld((nint)3));
        Assert.AreEqual(0, await scheduler.ReleaseAllAsync());
        Assert.AreEqual(0, ran);
    }

    [TestMethod]
    public async Task HoldsAreIndependentPerWindow()
    {
        // Two applications opening at once must not cancel each other, and the
        // activation of one must not release the other.
        ManualOpenWindowFollowScheduler scheduler = new();
        using OpenWindowFollowGrace grace = new(scheduler: scheduler);
        List<nint> ran = [];

        grace.Hold((nint)4, () => { ran.Add(4); return Task.CompletedTask; });
        grace.Hold((nint)5, () => { ran.Add(5); return Task.CompletedTask; });
        grace.Cancel((nint)4);

        await scheduler.ReleaseAllAsync();

        Assert.HasCount(1, ran);
        Assert.AreEqual((nint)5, ran[0]);
    }

    [TestMethod]
    public void ZeroGracePeriod_HoldsNothing()
    {
        // What a caller driving events by hand wants: assignment stays inline.
        using OpenWindowFollowGrace grace = new(TimeSpan.Zero);

        Assert.IsFalse(grace.IsEnabled);
        Assert.IsTrue(new OpenWindowFollowGrace().IsEnabled);
    }

    [TestMethod]
    public void NegativeGracePeriod_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            static () => new OpenWindowFollowGrace(TimeSpan.FromMilliseconds(-1)));
    }

    [TestMethod]
    public async Task Dispose_DropsWhatIsHeldRatherThanRunningIt()
    {
        // Shutdown is not the moment to start moving the user's windows.
        ManualOpenWindowFollowScheduler scheduler = new();
        OpenWindowFollowGrace grace = new(scheduler: scheduler);
        int ran = 0;

        grace.Hold((nint)6, () => { ran++; return Task.CompletedTask; });
        grace.Dispose();
        grace.Dispose();

        Assert.AreEqual(0, await scheduler.ReleaseAllAsync());
        Assert.AreEqual(0, ran);
        Assert.Throws<ObjectDisposedException>(
            () => grace.Hold((nint)7, static () => Task.CompletedTask));
    }

    [TestMethod]
    public void DefaultGracePeriod_StaysBelowPerceptibleDelay()
    {
        // It is also how long an unwanted window stays visible before it is
        // moved away, so it buys a wide margin over the millisecond an
        // activation actually takes without ever reading as lag.
        Assert.IsGreaterThan(
            TimeSpan.FromMilliseconds(50),
            OpenWindowFollowGrace.DefaultGracePeriod);
        Assert.IsLessThanOrEqualTo(
            TimeSpan.FromMilliseconds(250),
            OpenWindowFollowGrace.DefaultGracePeriod);
    }
}

using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class TilingCoordinatorTests
{
    private static readonly Guid CurrentDesktop = Guid.NewGuid();
    private static readonly Guid OtherDesktop = Guid.NewGuid();

    [TestMethod]
    public async Task DisabledSettingsPerformNoPlacementOrReadsAtAll()
    {
        Harness harness = new();
        harness.SettingsSource.Current = new TilingSettings();
        harness.Coordinator.NotifyAssignmentCompleted(
            Vouch(WindowA, CurrentDesktop));

        await harness.Coordinator.ReconcileOnceAsync();

        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(0, harness.Reader.TotalReads);
        Assert.AreEqual(0, harness.Coordinator.LastReport!.PlacedWindows);
    }

    [TestMethod]
    public async Task AssignmentVouchesWindowAndPlacesItOnThePlannedTile()
    {
        Harness harness = new();
        // The window sits somewhere off-tile so the plan must move it: visible
        // frame (200,200,500,300) with 14-pixel invisible borders all round.
        harness.Reader.Respond(WindowA, FrameState(
            frame: new TileRect(200, 200, 500, 300),
            margin: 14,
            monitor: MonitorOne,
            desktop: CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(
            Vouch(WindowA, CurrentDesktop));

        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        IReadOnlyList<TilingPlacementRequest> batch =
            harness.Executor.ApplyCalls[0];
        Assert.HasCount(1, batch);
        // One leaf owns the whole 1000x800 work area; the outer rectangle is
        // that tile grown by the 14-pixel margins on every side.
        Assert.AreEqual(new TileRect(-14, -14, 1028, 828), batch[0].WindowRectPixels);
        Assert.AreEqual((nint)WindowA, batch[0].WindowHandle);
        Assert.AreEqual(1, harness.Coordinator.LastReport!.PlacedWindows);
    }

    [TestMethod]
    public async Task PlacementSuppressesTheImmediateMoveSizeEndEcho()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(200, 200, 500, 300), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(
            Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        // Ten milliseconds after our own placement the echo lands; it must
        // not schedule another pass.
        harness.TestContextClock.Now =
            harness.TestContextClock.Now.AddMilliseconds(10);
        int reportsBefore = harness.Reports.Count;
        harness.Coordinator.NotifyMoveSizeEnded(WindowA);

        bool secondPassArrived = await harness.WaitForReportsAsync(
            reportsBefore + 1,
            timeout: TimeSpan.FromMilliseconds(400));
        Assert.IsFalse(secondPassArrived);
        Assert.HasCount(1, harness.Executor.ApplyCalls);
    }

    [TestMethod]
    public async Task MoveSizeEndAfterSuppressionExpiryTriggersReconciliation()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(200, 200, 500, 300), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(
            Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        harness.TestContextClock.Now =
            harness.TestContextClock.Now.AddSeconds(5);
        harness.Coordinator.NotifyMoveSizeEnded(WindowA);
        bool reconciledAgain = await harness.WaitForReportsAsync(2);
        Assert.IsTrue(reconciledAgain);
    }

    [TestMethod]
    public async Task MoveSizeEndForAnUntrackedWindowDoesNothing()
    {
        Harness harness = new();

        harness.Coordinator.NotifyMoveSizeEnded(WindowA);

        Assert.AreEqual(0, harness.Reports.Count);
    }

    [TestMethod]
    public async Task MaximizedWindowLeavesNoEmptyTile()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop,
            maximized: true));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.HasCount(1, harness.Executor.ApplyCalls);
        IReadOnlyList<TilingPlacementRequest> batch = harness.Executor.ApplyCalls[0];
        Assert.HasCount(1, batch);
        Assert.AreEqual((nint)WindowA, batch[0].WindowHandle);
        Assert.AreEqual(
            new TileRect(-14, -14, 1028, 828),
            batch[0].WindowRectPixels);
        Assert.AreEqual(1, harness.Coordinator.LastReport!.SkippedMaximizedWindows);
    }

    [TestMethod]
    public async Task FullScreenWindowIsTreatedLikeAMaximizedOne()
    {
        Harness harness = new();
        // A borderless frame covering the whole monitor within tolerance.
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 1000, 800), 0, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(
            1,
            harness.Coordinator.LastReport!.SkippedFullScreenWindows);
    }

    [TestMethod]
    public async Task MinimizedWindowIsRemovedFromTheVisibleLayout()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop,
            minimized: true));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(1, harness.Coordinator.LastReport!.SkippedHiddenWindows);
    }

    [TestMethod]
    public async Task MinimizeEventReleasesItsLeafBeforeTheWindowStateSettles()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        int stableBatchCount = harness.Executor.ApplyCalls.Count;

        harness.Coordinator.NotifyWindowMinimized(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        IReadOnlyList<TilingPlacementRequest> requests =
            harness.Executor.ApplyCalls.Last();
        Assert.HasCount(1, requests);
        Assert.AreEqual(WindowA, requests[0].WindowHandle);
        Assert.AreEqual(
            new TileRect(-14, -14, 1028, 828),
            requests[0].WindowRectPixels);
    }

    [TestMethod]
    public async Task MinimizeEventRevouchesASurvivorAfterFailedAssignment()
    {
        Harness harness = new();
        harness.Enumerator.Windows.AddRange([WindowA, WindowB]);
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        // An earlier assignment failure removed A from tracking even though
        // the window stayed visible. B is then minimized.
        harness.Coordinator.NotifyAssignmentCompleted(new TilingAssignmentNotification(
            WindowA,
            "anywhere",
            TilingAssignmentDisposition.NotPlaced));
        Assert.IsTrue(await harness.WaitForReportsAsync(2));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop,
            minimized: true));

        harness.Coordinator.NotifyWindowMinimized(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(3));

        IReadOnlyList<TilingPlacementRequest> requests =
            harness.Executor.ApplyCalls.Last();
        Assert.HasCount(1, requests);
        Assert.AreEqual(WindowA, requests[0].WindowHandle);
        Assert.AreEqual(
            new TileRect(-14, -14, 1028, 828),
            requests[0].WindowRectPixels);
    }

    [TestMethod]
    public async Task UnconfirmedForegroundAssignmentPreservesExistingLeaf()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        int stableBatchCount = harness.Executor.ApplyCalls.Count;

        harness.Coordinator.NotifyAssignmentCompleted(
            new TilingAssignmentNotification(
                WindowB,
                "run-observe",
                TilingAssignmentDisposition.Unconfirmed));

        await Task.Delay(50);
        Assert.AreEqual(stableBatchCount, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(1, harness.Reports.Count,
            "An inconclusive foreground assignment must not start a layout pass.");
        harness.Coordinator.Dispose();
    }

    [TestMethod]
    public async Task DisabledManagedDesktopKeepsWindowsOutOfBspLayout()
    {
        Harness harness = new();
        harness.ManagedDesktopKeys[CurrentDesktop] = "remote";
        harness.SettingsSource.Current = harness.Settings with
        {
            DisabledManagedDesktopKeys = ["REMOTE"],
        };
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(200, 200, 500, 300),
            14,
            MonitorOne,
            CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(
            Vouch(WindowA, CurrentDesktop));

        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(1, harness.Coordinator.LastReport!.FloatedWindows);
    }

    [TestMethod]
    public async Task RestoredWindowRejoinsWithoutLeavingAnEmptyTile()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop,
            minimized: true));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        Assert.HasCount(1, harness.Executor.ApplyCalls[0]);
        Assert.AreEqual(
            new TileRect(-14, -14, 1028, 828),
            harness.Executor.ApplyCalls[0][0].WindowRectPixels);

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        Assert.HasCount(2, harness.Executor.ApplyCalls[1]);
    }

    [TestMethod]
    public async Task RestoredWindowRejoinsAfterTemporaryUnreadableMinimizeTransition()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        int stableBatchCount = harness.Executor.ApplyCalls.Count;

        // Some single-instance apps briefly refuse their rectangle while
        // minimizing. The same HWND later emits a restore event.
        harness.Reader.Respond(
            WindowB,
            new TilingWindowReading(TilingReadStatus.Unavailable, null));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));
        Assert.AreEqual(stableBatchCount, harness.Executor.ApplyCalls.Count,
            "A temporary read failure must not expand the surviving window.");

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(3));

        Assert.IsFalse(
            harness.Executor.ApplyCalls
                .Skip(stableBatchCount)
                .SelectMany(static batch => batch)
                .Any(request =>
                    request.WindowHandle == WindowA &&
                    request.WindowRectPixels.Width > 600));
        harness.Coordinator.Dispose();
    }

    [TestMethod]
    public async Task RestoreEventRevouchesAHandleForgottenDuringMinimize()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        harness.Reader.Respond(
            WindowA,
            new TilingWindowReading(TilingReadStatus.WindowGone, null));
        harness.Coordinator.NotifyWindowStateChanged(WindowA);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(200, 200, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowA);
        Assert.IsTrue(await harness.WaitForReportsAsync(3));

        Assert.AreEqual(
            WindowA,
            harness.Executor.ApplyCalls.Last().Single().WindowHandle);
    }

    [TestMethod]
    public async Task UnknownRestoreReenumeratesAVisibleIncumbent()
    {
        Harness harness = new();
        harness.Enumerator.Windows.AddRange([WindowA, WindowB]);
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        // The incumbent briefly looked gone after its first placement and was
        // forgotten. It is visible again, but no new event arrives for it.
        harness.Reader.Respond(
            WindowA,
            new TilingWindowReading(TilingReadStatus.WindowGone, null));
        harness.Coordinator.NotifyWindowStateChanged(WindowA);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 1000, 800), 14, MonitorOne, CurrentDesktop));

        // A different single-instance app now restores. Its unknown state
        // event must re-enumerate both visible HWNDs before planning.
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(3));

        IReadOnlyList<TilingPlacementRequest> restored =
            harness.Executor.ApplyCalls.Last();
        Assert.HasCount(2, restored);
        CollectionAssert.AreEquivalent(
            new[] { WindowA, WindowB },
            restored.Select(static request => request.WindowHandle).ToArray());
    }

    [TestMethod]
    public async Task CloakedWindowLeavesAndUncloakedWindowRejoinsTheLayout()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop,
            cloaked: true));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        Assert.HasCount(1, harness.Executor.ApplyCalls[0]);

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        Assert.HasCount(2, harness.Executor.ApplyCalls[1]);
    }

    [TestMethod]
    public async Task BriefApplicationCloakPreservesExistingLayout()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        int stableBatchCount = harness.Executor.ApplyCalls.Count;

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop,
            cloaked: true));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        Assert.AreEqual(stableBatchCount, harness.Executor.ApplyCalls.Count,
            "A brief cloak must not make the other window expand to full width.");

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(3));

        Assert.IsFalse(
            harness.Executor.ApplyCalls
                .Skip(stableBatchCount)
                .SelectMany(static batch => batch)
                .Any(request =>
                    request.WindowHandle == WindowA &&
                    request.WindowRectPixels.Width > 600),
            "The surviving window must never receive a full-width placement.");
        harness.Coordinator.Dispose();
    }

    [TestMethod]
    public async Task VirtualDesktopShellCloakPreservesTreeOrderWithoutPlacement()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(200, 200, 500, 300), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(300, 300, 500, 300), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        Assert.HasCount(1, harness.Executor.ApplyCalls);

        // Match the first committed layout before Windows starts the desktop
        // switch. DWM shell-cloaks each window in a separate event.
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 496, 800), 14, MonitorOne, CurrentDesktop,
            cloaked: true, shellCloaked: true));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(504, 0, 496, 800), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowA);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(504, 0, 496, 800), 14, MonitorOne, CurrentDesktop,
            cloaked: true, shellCloaked: true));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(3));

        // The shell also reveals the windows one by one. Their old BSP leaves
        // must still exist regardless of the event order.
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 496, 800), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowA);
        Assert.IsTrue(await harness.WaitForReportsAsync(4));

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(504, 0, 496, 800), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(5));

        Assert.HasCount(1, harness.Executor.ApplyCalls,
            "A virtual desktop switch must not resize or reverse the windows.");
    }

    [TestMethod]
    public async Task WindowOnAnotherDesktopIsNotPlacedThisPass()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne,             OtherDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(0, harness.Coordinator.LastReport!.PlacedWindows);
        Assert.AreEqual(1, harness.Coordinator.LastReport.ConsideredWindows);
    }

    [TestMethod]
    public async Task WindowMovedToAnotherDesktopLeavesNoEmptyTile()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, OtherDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, OtherDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        IReadOnlyList<TilingPlacementRequest> last = harness.Executor.ApplyCalls[1];
        Assert.HasCount(1, last);
        Assert.AreEqual((nint)WindowA, last[0].WindowHandle);
        Assert.AreEqual(new TileRect(-14, -14, 1028, 828), last[0].WindowRectPixels);
    }

    [TestMethod]
    public async Task UnreadableWindowStaysTrackedWithoutATileAndIsReported()
    {
        Harness harness = new();
        harness.Reader.Respond(
            WindowA,
            new TilingWindowReading(TilingReadStatus.AccessDenied, null));
        harness.Enumerator.Windows.Add(WindowA);

        await harness.Coordinator.ReconcileAllWindowsAsync();

        Assert.AreEqual(1, harness.Coordinator.LastReport!.UnreadableWindows);
        // A later explicit pass tries it once again. It stays tracked so a
        // future restore/state event can recover it, but it owns no BSP leaf.
        int readsSoFar = harness.Reader.TotalReads;
        await harness.Coordinator.ReconcileOnceAsync();
        Assert.AreEqual(readsSoFar + 1, harness.Reader.TotalReads);
        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
    }

    [TestMethod]
    public async Task IgnoredRuleRemovesTheWindowFromTrackingEntirely()
    {
        Harness harness = new();
        TilingSettings current = harness.SettingsSource.Current;
        harness.SettingsSource.Current = current with
        {
            IgnoreRules = current.IgnoreRules.Add(MakeIgnoreRule("elevated-tool")),
        };
        harness.Identity.ResolveAs(WindowA, MakeIdentity("Toolbox"));
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Enumerator.Windows.Add(WindowA);

        await harness.Coordinator.ReconcileAllWindowsAsync();

        Assert.AreEqual(1, harness.Coordinator.LastReport!.IgnoredWindows);
        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        // Forgotten: no reads on the next pass.
        int readsSoFar = harness.Reader.TotalReads;
        await harness.Coordinator.ReconcileOnceAsync();
        Assert.AreEqual(readsSoFar, harness.Reader.TotalReads);
    }

    [TestMethod]
    public async Task FloatRuleWindowIsObservedButNeverPlaced()
    {
        Harness harness = new();
        TilingSettings current = harness.SettingsSource.Current;
        harness.SettingsSource.Current = current with
        {
            FloatRules = current.FloatRules.Add(MakeFloatRule("palette")),
        };
        harness.Identity.ResolveAs(WindowA, MakeIdentity("Palette"));
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(100, 100, 300, 200), 14, MonitorOne, CurrentDesktop));
        harness.Enumerator.Windows.Add(WindowA);

        await harness.Coordinator.ReconcileAllWindowsAsync();

        Assert.AreEqual(1, harness.Coordinator.LastReport!.FloatedWindows);
        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        // Still tracked, still read on every pass.
        await harness.Coordinator.ReconcileOnceAsync();
        Assert.AreEqual(2, harness.Reader.ReadCount(WindowA));
    }

    [TestMethod]
    public async Task WindowThatCannotSplitAnythingFloatsInstead()
    {
        Harness harness = new();
        harness.MonitorCatalog.Monitors.Add(MakeMonitor(
            MonitorOne,
            @"\\.\DISPLAY1",
            workArea: new TileRect(0, 0, 400, 300),
            bounds: new TileRect(0, 0, 400, 300)));
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 400, 300), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(10, 10, 300, 200), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        // The first window takes the only splittable slot; the second cannot
        // split anything and floats.
        IReadOnlyList<TilingPlacementRequest> batch = harness.Executor.ApplyCalls[0];
        Assert.HasCount(1, batch);
        Assert.AreEqual((nint)WindowA, batch[0].WindowHandle);
        Assert.AreEqual(1, harness.Coordinator.LastReport!.FloatedWindows);
    }

    [TestMethod]
    public async Task DestroyedWindowDropsItsSlotAndTheLayoutClosesOverIt()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        harness.Coordinator.NotifyWindowDestroyed(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        Assert.HasCount(2, harness.Executor.ApplyCalls);
        IReadOnlyList<TilingPlacementRequest> last =
            harness.Executor.ApplyCalls[1];
        Assert.HasCount(1, last);
        // The survivor now owns the whole work area again.
        Assert.AreEqual(new TileRect(-14, -14, 1028, 828), last[0].WindowRectPixels);
    }

    [TestMethod]
    public async Task TopologyChangeKeepsTheTreeAndReconcilesAgain()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        harness.Topology.RaiseTopologyChanged("display_changed");
        bool rebuilt = await harness.WaitForReportsAsync(2);
        Assert.IsTrue(rebuilt);

        // The pass ran, but the preserved tree was already at its target.
        Assert.HasCount(1, harness.Executor.ApplyCalls);
    }

    [TestMethod]
    public async Task CurrentChangedFreezesPlacementUntilSwitched()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));
        int stableBatchCount = harness.Executor.ApplyCalls.Count;

        harness.Topology.RaiseTopologyChanged("CurrentChanged");
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, OtherDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        Assert.AreEqual(stableBatchCount, harness.Executor.ApplyCalls.Count,
            "No partial layout may be committed while Windows is switching desktops.");

        harness.Topology.RaiseTopologyChanged("Switched");
        Assert.IsTrue(await harness.WaitForReportsAsync(3));
        Assert.AreEqual(stableBatchCount, harness.Executor.ApplyCalls.Count,
            "Switched can arrive before per-window desktop state is final.");

        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyWindowStateChanged(WindowB);
        Assert.IsTrue(await harness.WaitForReportsAsync(4));
        Assert.AreEqual(stableBatchCount, harness.Executor.ApplyCalls.Count,
            "Placement must stay frozen while the switched desktop settles.");

        Assert.IsTrue(await harness.WaitForReportsAsync(
            5,
            TimeSpan.FromSeconds(3)),
            "The bounded settling timer must start one final layout pass.");

        Assert.IsFalse(
            harness.Executor.ApplyCalls
                .Skip(stableBatchCount)
                .SelectMany(static batch => batch)
                .Any(request =>
                    request.WindowHandle == WindowA &&
                    request.WindowRectPixels.Width > 600),
            "The completed switch may repair the layout, but never through a full-width intermediate state.");
        harness.Coordinator.Dispose();
    }

    [TestMethod]
    public async Task DuplicateTopologyNotificationsCoalesceIntoOnePass()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        for (int index = 0; index < 20; index++)
        {
            harness.Topology.RaiseTopologyChanged("switched");
        }

        Assert.IsTrue(await harness.WaitForReportsAsync(2));
        await Task.Delay(100);
        Assert.HasCount(2, harness.Reports);
    }

    [TestMethod]
    public async Task FailedCurrentDesktopReadAbortsThePassWithoutPlacement()
    {
        Harness harness = new();
        harness.Topology.FailCurrentDesktop = true;
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.AreEqual(0, harness.Executor.ApplyCalls.Count);
        Assert.AreEqual(0, harness.Coordinator.LastReport!.PlacedWindows);
    }

    [TestMethod]
    public async Task EndDeferFailureIsReportedForTheWholeBatch()
    {
        Harness harness = new();
        harness.Executor.Outcome = TilingBatchOutcome.EndFailed;
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        TilingReconcileReport report = harness.Coordinator.LastReport!;
        Assert.AreEqual(TilingBatchOutcome.EndFailed, report.BatchOutcome);
        CollectionAssert.AreEquivalent(
            new[] { (nint)WindowA },
            report.BatchFailedWindows.ToArray());
        Assert.IsTrue(report.HasFailures);
    }

    [TestMethod]
    public async Task DeferSkipIsNamedInTheReport()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        harness.Executor.CustomResult = requests => new TilingBatchResult(
            TilingBatchOutcome.DeferFailed,
            [(nint)WindowA],
            [(nint)WindowB],
            []);
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        TilingReconcileReport report = harness.Coordinator.LastReport!;
        Assert.AreEqual(TilingBatchOutcome.DeferFailed, report.BatchOutcome);
        CollectionAssert.AreEquivalent(
            new[] { (nint)WindowB },
            report.BatchSkippedWindows.ToArray());
        Assert.AreEqual(1, report.PlacedWindows);
    }

    [TestMethod]
    public async Task ReadBackAdjustmentIsReportedOnceWithoutPolling()
    {
        Harness harness = new();
        // First read sees an off-tile window; after placement the foreign app
        // has clamped it, so the one read-back differs from what we asked for.
        harness.Reader.RespondPerCall(WindowA, callIndex =>
        {
            if (callIndex == 0)
            {
                return new TilingWindowReading(TilingReadStatus.Succeeded, FrameState(
                    new TileRect(200, 200, 500, 300),
                    14,
                    MonitorOne,
                    CurrentDesktop));
            }

            return new TilingWindowReading(TilingReadStatus.Succeeded, FrameState(
                new TileRect(0, 0, 900, 800),
                14,
                MonitorOne,
                CurrentDesktop));
        });
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        TilingReconcileReport report = harness.Coordinator.LastReport!;
        Assert.AreEqual(1, report.PlacedWindows);
        CollectionAssert.AreEquivalent(
            new[] { (nint)WindowA },
            report.ReadBackAdjustments.ToArray());
        // Exactly two reads happened: one to plan, one to verify. No polling.
        Assert.AreEqual(2, harness.Reader.TotalReads);
    }

    [TestMethod]
    public async Task SizeClampTriggersOneConstrainedRepairWithoutOverlap()
    {
        Harness harness = new();
        nint windowC = 0x00A1_0003;
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(windowC, FrameState(
            new TileRect(0, 400, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.RespondPerCall(WindowA, callIndex =>
        {
            if (callIndex == 0)
            {
                return new TilingWindowReading(
                    TilingReadStatus.Succeeded,
                    FrameState(
                        new TileRect(0, 0, 500, 400),
                        14,
                        MonitorOne,
                        CurrentDesktop));
            }

            if (callIndex == 1)
            {
                // The foreign app accepts the width but enforces a 500-pixel
                // visible-frame height after the first equal split.
                return new TilingWindowReading(
                    TilingReadStatus.Succeeded,
                    FrameState(
                        new TileRect(0, 0, 500, 500),
                        14,
                        MonitorOne,
                        CurrentDesktop));
            }

            TilingPlacementRequest latest = harness.Executor.ApplyCalls
                .Last()
                .Single(request => request.WindowHandle == WindowA);
            TileRect outer = latest.WindowRectPixels;
            return new TilingWindowReading(
                TilingReadStatus.Succeeded,
                FrameState(
                    new TileRect(
                        outer.X + 14,
                        outer.Y + 14,
                        outer.Width - 28,
                        outer.Height - 28),
                    14,
                    MonitorOne,
                    CurrentDesktop));
        });
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(windowC, CurrentDesktop));

        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        IReadOnlyList<TilingPlacementRequest> repair =
            harness.Executor.ApplyCalls.Last();
        Assert.HasCount(3, repair);
        TileRect[] visibleFrames = repair
            .Select(request => new TileRect(
                request.WindowRectPixels.X + 14,
                request.WindowRectPixels.Y + 14,
                request.WindowRectPixels.Width - 28,
                request.WindowRectPixels.Height - 28))
            .ToArray();
        Assert.IsGreaterThanOrEqualTo(
            500,
            visibleFrames[Array.FindIndex(
                repair.ToArray(),
                request => request.WindowHandle == WindowA)].Height);
        for (int first = 0; first < visibleFrames.Length; first++)
        {
            for (int second = first + 1; second < visibleFrames.Length; second++)
            {
                Assert.IsFalse(Overlaps(visibleFrames[first], visibleFrames[second]));
            }
        }
    }

    [TestMethod]
    public async Task TwoMonitorsStillProduceOneCommittedBatch()
    {
        Harness harness = new();
        harness.MonitorCatalog.Monitors.Add(MakeMonitor(
            MonitorTwo,
            @"\\.\DISPLAY2",
            workArea: new TileRect(1920, 0, 1920, 1040),
            bounds: new TileRect(1920, 0, 1920, 1080)));
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(1920, 0, 960, 520), 14, MonitorTwo, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        Assert.HasCount(1, harness.Executor.ApplyCalls);
        Assert.HasCount(2, harness.Executor.ApplyCalls[0]);
    }

    [TestMethod]
    public async Task NewWindowSplitsTheFocusedTileWhenPossible()
    {
        Harness harness = new();
        nint windowC = 0x00A1_0003;
        harness.Focus.WindowHandle = WindowB;
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(windowC, FrameState(
            new TileRect(500, 400, 500, 400), 14, MonitorOne, CurrentDesktop));

        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(windowC, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        IReadOnlyList<TilingPlacementRequest> batch = harness.Executor.ApplyCalls[0];
        TileRect a = batch.Single(request => request.WindowHandle == WindowA)
            .WindowRectPixels;
        TileRect b = batch.Single(request => request.WindowHandle == WindowB)
            .WindowRectPixels;
        TileRect c = batch.Single(request => request.WindowHandle == windowC)
            .WindowRectPixels;
        Assert.AreEqual(8, b.X - (a.X + a.Width) + 28);
        Assert.AreEqual(b.X, c.X);
        Assert.AreEqual(b.Width, c.Width);
    }

    [TestMethod]
    public async Task StartupReconciliationEnumeratesVouchesAndPlaces()
    {
        Harness harness = new();
        harness.Enumerator.Windows.AddRange([WindowA, WindowB]);
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));

        await harness.Coordinator.ReconcileAllWindowsAsync();

        Assert.HasCount(1, harness.Executor.ApplyCalls);
        Assert.HasCount(2, harness.Executor.ApplyCalls[0]);
    }

    [TestMethod]
    public async Task NotPlacedAssignmentUntracksTheWindow()
    {
        Harness harness = new();
        harness.Reader.Respond(WindowA, FrameState(
            new TileRect(0, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Reader.Respond(WindowB, FrameState(
            new TileRect(500, 0, 500, 400), 14, MonitorOne, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowA, CurrentDesktop));
        harness.Coordinator.NotifyAssignmentCompleted(Vouch(WindowB, CurrentDesktop));
        Assert.IsTrue(await harness.WaitForReportsAsync(1));

        // The move for B did not happen: its slot must vanish and A closes
        // over the freed space in one committed batch.
        harness.Coordinator.NotifyAssignmentCompleted(new TilingAssignmentNotification(
            WindowB,
            "anywhere",
            TilingAssignmentDisposition.NotPlaced));
        Assert.IsTrue(await harness.WaitForReportsAsync(2));

        Assert.HasCount(2, harness.Executor.ApplyCalls);
        IReadOnlyList<TilingPlacementRequest> last = harness.Executor.ApplyCalls[1];
        Assert.HasCount(1, last);
        Assert.AreEqual((nint)WindowA, last[0].WindowHandle);
        Assert.AreEqual(new TileRect(-14, -14, 1028, 828), last[0].WindowRectPixels);
    }

    [TestMethod]
    public void DestroyedUnknownWindowIsHarmless()
    {
        Harness harness = new();

        harness.Coordinator.NotifyWindowDestroyed(WindowA);

        Assert.IsNull(harness.Coordinator.LastReport);
    }

    private static readonly nint WindowA = 0x00A1_0001;

    private static readonly nint WindowB = 0x00A1_0002;

    private static readonly nint MonitorOne = 0x00A1_1001;

    private static readonly nint MonitorTwo = 0x00A1_1002;

    private TilingAssignmentNotification Vouch(
        nint handle,
        Guid desktopId) =>
        new(handle, "key", TilingAssignmentDisposition.PlacedOnTargetDesktop);

    private static TilingIdentityRule MakeIgnoreRule(string id) =>
        new(id, id, true, ProcessNames: ["Toolbox"]);

    private static TilingIdentityRule MakeFloatRule(string id) =>
        new(id, id, true, ProcessNames: ["Palette"]);

    private static WindowIdentity MakeIdentity(string processName) =>
        new(
            ProcessId: 1000,
            ProcessName: processName,
            ExecutablePath: null,
            PackageFamilyName: null,
            AppUserModelId: null,
            WindowClass: "TEST_CLASS",
            WindowTitle: null,
            CommandLine: null);

    private static bool Overlaps(TileRect first, TileRect second) =>
        first.X < second.X + second.Width &&
        first.X + first.Width > second.X &&
        first.Y < second.Y + second.Height &&
        first.Y + first.Height > second.Y;

    private static TilingMonitorInfo MakeMonitor(
        nint handle,
        string deviceKey,
        TileRect workArea,
        TileRect bounds,
        uint dpiX = 96) =>
        new(handle, deviceKey, workArea, bounds, dpiX);

    /// <summary>
    /// Builds a window state from a visible frame plus a uniform invisible
    /// border width, which fixes the outer rectangle by construction.
    /// </summary>
    private static TilingWindowState FrameState(
        TileRect frame,
        int margin,
        nint monitor,
        Guid? desktop,
        bool minimized = false,
        bool maximized = false,
        bool cloaked = false,
        bool shellCloaked = false) =>
        new(
            WindowRectPixels: new TileRect(
                frame.X - margin,
                frame.Y - margin,
                frame.Width + margin + margin,
                frame.Height + margin + margin),
            VisibleFramePixels: frame,
            IsVisible: !minimized,
            IsMinimized: minimized,
            IsMaximized: maximized,
            IsCloaked: cloaked,
            MonitorHandle: monitor,
            DesktopId: desktop,
            CloakReasons: shellCloaked
                ? TilingCloakReason.Shell
                : TilingCloakReason.None);

    private sealed class Harness
    {
        public Harness()
        {
            SettingsSource = new FakeSettingsSource();
            SettingsSource.Current = new TilingSettings(
                IsEnabled: true,
                OuterGap: 0,
                InnerGap: 8);
            MonitorCatalog = new FakeMonitorCatalog();
            MonitorCatalog.Monitors.Add(MakeMonitor(
                MonitorOne,
                @"\\.\DISPLAY1",
                workArea: new TileRect(0, 0, 1000, 800),
                bounds: new TileRect(0, 0, 1000, 800)));
            Reader = new FakeWindowReader();
            Identity = new FakeIdentitySource();
            Executor = new FakePlacementExecutor
            {
                Reader = Reader,
            };
            Topology = new FakeTopology(CurrentDesktop);
            Enumerator = new FakeEnumerator();
            Focus = new FakeFocusReader();
            ManagedDesktopKeys = [];
            Reports = [];
            Coordinator = new TilingCoordinator(
                () => SettingsSource.Current,
                MonitorCatalog,
                Reader,
                Identity,
                Executor,
                Topology,
                Enumerator,
                clock: () => TestContextClock.Now,
                coalesceDelay: TimeSpan.FromMilliseconds(5),
                focusReader: Focus,
                managedDesktopKeySource: desktopId =>
                    ManagedDesktopKeys.GetValueOrDefault(desktopId));
            Coordinator.ReconcileCompleted += (_, report) => Reports.Add(report);
        }

        /// <summary>The mutable clock shared with the coordinator.</summary>
        public SharedClock TestContextClock { get; } = new();

        public FakeSettingsSource SettingsSource { get; }

        /// <summary>The settings the coordinator reads this pass.</summary>
        public TilingSettings Settings => SettingsSource.Current;

        public FakeMonitorCatalog MonitorCatalog { get; }

        public FakeWindowReader Reader { get; }

        public FakeIdentitySource Identity { get; }

        public FakePlacementExecutor Executor { get; }

        public FakeTopology Topology { get; }

        public FakeEnumerator Enumerator { get; }

        public FakeFocusReader Focus { get; }

        public Dictionary<Guid, string> ManagedDesktopKeys { get; }

        public List<TilingReconcileReport> Reports { get; }

        public TilingCoordinator Coordinator { get; }

        /// <summary>
        /// Lets tests advance time together with the coordinator's clock.
        /// </summary>
        public sealed class SharedClock
        {
            public DateTimeOffset Now { get; set; } =
                new(2026, 3, 14, 12, 0, 0, TimeSpan.Zero);
        }

        public async Task<bool> WaitForReportsAsync(
            int count,
            TimeSpan? timeout = null)
        {
            TimeSpan budget = timeout ?? TimeSpan.FromSeconds(5);
            DateTimeOffset deadline = DateTime.UtcNow + budget;
            while (Reports.Count < count && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            return Reports.Count >= count;
        }
    }

    private sealed class FakeSettingsSource
    {
        public TilingSettings Current { get; set; } = TilingSettings.Disabled with
        {
            IsEnabled = true,
            OuterGap = 0,
            InnerGap = 8,
            MinimumTileWidth = 320,
            MinimumTileHeight = 240,
        };
    }

    private sealed class FakeMonitorCatalog : ITilingMonitorCatalog
    {
        public List<TilingMonitorInfo> Monitors { get; } = [];

        public IReadOnlyList<TilingMonitorInfo> ReadMonitors() => Monitors;
    }

    private sealed class FakeWindowReader : ITilingWindowReader
    {
        private readonly Dictionary<nint, TilingWindowReading> responses = [];

        private readonly Dictionary<nint, Func<int, TilingWindowReading>>
            perCallResponses = [];

        private readonly Dictionary<nint, int> callCounts = [];

        public int TotalReads { get; private set; }

        public void Respond(nint handle, TilingWindowState state) =>
            responses[handle] = new TilingWindowReading(
                TilingReadStatus.Succeeded,
                state);

        public void Respond(nint handle, TilingWindowReading reading) =>
            responses[handle] = reading;

        public void RespondPerCall(
            nint handle,
            Func<int, TilingWindowReading> responder) =>
            perCallResponses[handle] = responder;

        public int ReadCount(nint handle) =>
            callCounts.TryGetValue(handle, out int count) ? count : 0;

        public TilingWindowReading Read(nint windowHandle)
        {
            TotalReads++;
            callCounts.TryGetValue(windowHandle, out int calls);
            callCounts[windowHandle] = calls + 1;

            if (perCallResponses.TryGetValue(windowHandle, out var perCall))
            {
                return perCall(calls);
            }

            return responses.TryGetValue(windowHandle, out TilingWindowReading? reading) &&
                    reading is not null
                ? reading
                : new TilingWindowReading(TilingReadStatus.WindowGone, null);
        }

        /// <summary>
        /// Simulates Windows honoring a move while keeping the same invisible
        /// frame margins and state flags.
        /// </summary>
        public void UpdateWindowRect(nint windowHandle, TileRect windowRect)
        {
            if (!responses.TryGetValue(windowHandle, out TilingWindowReading? reading) ||
                reading?.State is not TilingWindowState state)
            {
                return;
            }

            (int left, int top, int right, int bottom) = state.FrameMargins;
            TileRect visibleFrame = new(
                windowRect.X + left,
                windowRect.Y + top,
                Math.Max(windowRect.Width - left - right, 0),
                Math.Max(windowRect.Height - top - bottom, 0));
            responses[windowHandle] = new TilingWindowReading(
                TilingReadStatus.Succeeded,
                state with
                {
                    WindowRectPixels = windowRect,
                    VisibleFramePixels = visibleFrame,
                });
        }
    }

    private sealed class FakeIdentitySource : ITilingIdentitySource
    {
        private readonly Dictionary<nint, WindowIdentity?> identities = [];

        public void ResolveAs(nint handle, WindowIdentity identity) =>
            identities[handle] = identity;

        public ValueTask<WindowIdentity?> ResolveAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            // A miss stands for an ordinary, identifiable application: the
            // classifier tiles it. Tests opt into null (unresolvable) or a
            // specific identity explicitly.
            identities.TryGetValue(windowHandle, out WindowIdentity? identity);
            return ValueTask.FromResult<WindowIdentity?>(
                identity ?? MakeDefaultIdentity());
        }

        internal static WindowIdentity MakeDefaultIdentity() => new(
            ProcessId: 4242,
            ProcessName: "App",
            ExecutablePath: @"C:\Apps\App.exe",
            PackageFamilyName: null,
            AppUserModelId: null,
            WindowClass: "AppWindow",
            WindowTitle: "unused",
            CommandLine: null);
    }

    private sealed class FakePlacementExecutor : ITilingPlacementExecutor
    {
        public List<IReadOnlyList<TilingPlacementRequest>> ApplyCalls { get; } = [];

        /// <summary>
        /// When set, committed moves are written back here so later passes
        /// observe windows already at their target, like the real desktop.
        /// </summary>
        public FakeWindowReader? Reader { get; set; }

        public TilingBatchOutcome Outcome { get; set; } = TilingBatchOutcome.Committed;

        public Func<
            IReadOnlyList<TilingPlacementRequest>,
            TilingBatchResult>? CustomResult { get; set; }

        public TilingBatchResult Apply(IReadOnlyList<TilingPlacementRequest> placements)
        {
            ApplyCalls.Add(placements);
            TilingBatchResult result;
            if (CustomResult is not null)
            {
                result = CustomResult(placements);
            }
            else
            {
                result = Outcome switch
                {
                    TilingBatchOutcome.Committed => TilingBatchResult.CommittedAll(
                        placements.Select(request => request.WindowHandle).ToArray()),
                    TilingBatchOutcome.EndFailed => new TilingBatchResult(
                        TilingBatchOutcome.EndFailed,
                        [],
                        [],
                        placements.Select(request => request.WindowHandle).ToArray()),
                    _ => new TilingBatchResult(Outcome, [], [], []),
                };
            }

            if (Reader is not null)
            {
                Dictionary<nint, TileRect> wanted = placements.ToDictionary(
                    request => request.WindowHandle,
                    request => request.WindowRectPixels);
                foreach (nint handle in result.CommittedWindows)
                {
                    if (wanted.TryGetValue(handle, out TileRect rect))
                    {
                        Reader.UpdateWindowRect(handle, rect);
                    }
                }
            }

            return result;
        }
    }

    private sealed class FakeTopology : IDesktopTopologyProvider
    {
        private readonly List<EventHandler<DesktopTopologyChangedEventArgs>> handlers = [];

        public FakeTopology(Guid currentDesktopId)
        {
            CurrentDesktopId = currentDesktopId;
        }

        public Guid CurrentDesktopId { get; set; }

        public bool FailCurrentDesktop { get; set; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add
            {
                if (value is not null)
                {
                    handlers.Add(value);
                }
            }

            remove
            {
                if (value is not null)
                {
                    _ = handlers.Remove(value);
                }
            }
        }

        public void RaiseTopologyChanged(string reason)
        {
            var args = new DesktopTopologyChangedEventArgs(reason);
            foreach (EventHandler<DesktopTopologyChangedEventArgs> handler in handlers)
            {
                handler(this, args);
            }
        }

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.tiling",
            "Test",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: true);

        public VirtualDesktopCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, true);

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.
                    Succeeded([]));

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default)
        {
            if (FailCurrentDesktop)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult<Guid>.Failed(
                        "test.failed",
                        "The current desktop could not be read."));
            }

            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(CurrentDesktopId));
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Unsupported(
                    "test.unsupported",
                    "Unsupported"));

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    private sealed class FakeEnumerator : ITopLevelWindowEnumerator
    {
        public List<nint> Windows { get; } = [];

        public IReadOnlyList<nint> Enumerate() => Windows;
    }

    private sealed class FakeFocusReader : ITilingFocusReader
    {
        public nint WindowHandle { get; set; }

        public nint ReadFocusedWindow() => WindowHandle;
    }
}

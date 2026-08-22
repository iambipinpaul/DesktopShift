using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class TilingWindowStateRulesTests
{
    private static readonly TileRect Monitor = new(0, 0, 1920, 1080);

    [TestMethod]
    public void AVisibleNormalWindowCoveringTheMonitorReadsAsFullScreen()
    {
        // One pixel short on each edge: some presentations overdraw by a pixel,
        // which stays inside the tolerance.
        TilingWindowState state = State(
            new TileRect(0, 0, 1919, 1079),
            visible: true);

        Assert.IsTrue(TilingWindowStateRules.IsFullScreen(state, Monitor));
    }

    [TestMethod]
    public void AWindowBeyondTheToleranceIsNotFullScreen()
    {
        TilingWindowState state = State(
            new TileRect(0, 0, 1900, 1000),
            visible: true);

        Assert.IsFalse(TilingWindowStateRules.IsFullScreen(state, Monitor));
    }

    [TestMethod]
    public void MaximizedWindowsNeverCountAsFullScreen()
    {
        // The maximized policy skips them anyway; the heuristic must not
        // conflate the two states.
        TilingWindowState state = State(Monitor, visible: true, maximized: true);

        Assert.IsFalse(TilingWindowStateRules.IsFullScreen(state, Monitor));
    }

    [TestMethod]
    public void HiddenOrMinimizedWindowsAreNotFullScreen()
    {
        Assert.IsFalse(TilingWindowStateRules.IsFullScreen(
            State(Monitor, visible: false), Monitor));
        Assert.IsFalse(TilingWindowStateRules.IsFullScreen(
            State(Monitor, visible: true, minimized: true), Monitor));
    }

    [TestMethod]
    public void FrameMarginsMeasureTheInvisibleBorder()
    {
        // X/Y/Width/Height: the outer rect spans (-14,-14)-(1934,1054), so
        // each edge extends 14 pixels beyond the visible frame.
        TileRect windowRect = new(-14, -14, 1948, 1068);
        TileRect visibleFrame = new(0, 0, 1920, 1040);
        TilingWindowState state = new(
            windowRect,
            visibleFrame,
            IsVisible: true,
            IsMinimized: false,
            IsMaximized: false,
            IsCloaked: false,
            MonitorHandle: 1,
            DesktopId: null);

        // Placing means growing the tile by these margins so the visible
        // frame — not the invisible border — lands on the planned rectangle.
        Assert.AreEqual((14, 14, 14, 14), state.FrameMargins);
    }

    [TestMethod]
    public void ZeroMarginsLeaveATileUnchanged()
    {
        TilingWindowState state = new(
            new TileRect(0, 0, 500, 300),
            new TileRect(0, 0, 500, 300),
            IsVisible: true,
            IsMinimized: false,
            IsMaximized: false,
            IsCloaked: false,
            MonitorHandle: 1,
            DesktopId: null);

        Assert.AreEqual((0, 0, 0, 0), state.FrameMargins);
    }

    private static TilingWindowState State(
        TileRect visibleFrame,
        bool visible,
        bool minimized = false,
        bool maximized = false) =>
        new(
            visibleFrame,
            visibleFrame,
            visible,
            minimized,
            maximized,
            IsCloaked: false,
            MonitorHandle: 1,
            DesktopId: null);
}

using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class BspOuterGapTests
{
    [TestMethod]
    public void OneWindowUsesTheWorkAreaMinusTheOuterGap()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(1);

        BspPlacement placement = BspLayoutPlanner.Plan(
            tree,
            new TileRect(10, 20, 1001, 801),
            innerGapPixels: 10,
            outerGapPixels: 5)[0];

        Assert.AreEqual(new TileRect(15, 25, 991, 791), placement.Rect);
    }

    [TestMethod]
    public void InnerGapIsAppliedInsideTheOuterGap()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(2);

        var placements = BspLayoutPlanner.Plan(
            tree,
            new TileRect(0, 0, 1001, 801),
            innerGapPixels: 11,
            outerGapPixels: 7);

        Assert.AreEqual(7, placements[0].Rect.X);
        Assert.AreEqual(7, placements[0].Rect.Y);
        Assert.AreEqual(
            11,
            placements[1].Rect.X -
                (placements[0].Rect.X + placements[0].Rect.Width));
        Assert.AreEqual(
            994,
            placements[1].Rect.X + placements[1].Rect.Width);
    }
}

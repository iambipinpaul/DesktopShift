using System.Collections.Immutable;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class BspConstrainedLayoutTests
{
    [TestMethod]
    public void LargerApplicationMinimumStillProducesNonOverlappingBspTiles()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(7);
        LeafToken constrained = tree.Leaves[3];
        IReadOnlyDictionary<LeafToken, BspLeafMinimum> minimums = tree.Leaves
            .ToDictionary(
                token => token,
                token => token == constrained
                    ? new BspLeafMinimum(480, 600)
                    : new BspLeafMinimum(320, 240));

        ImmutableArray<BspPlacement> placements =
            BspLayoutPlanner.PlanConstrained(
                tree,
                new TileRect(0, 0, 1920, 1022),
                innerGapPixels: 10,
                outerGapPixels: 5,
                minimums);

        Assert.HasCount(7, placements);
        Assert.IsGreaterThanOrEqualTo(
            600,
            placements.Single(p => p.Token == constrained).Rect.Height);

        foreach (BspPlacement placement in placements)
        {
            BspLeafMinimum minimum = minimums[placement.Token];
            Assert.IsGreaterThanOrEqualTo(minimum.Width, placement.Rect.Width);
            Assert.IsGreaterThanOrEqualTo(minimum.Height, placement.Rect.Height);
            Assert.IsGreaterThanOrEqualTo(5, placement.Rect.X);
            Assert.IsGreaterThanOrEqualTo(5, placement.Rect.Y);
            Assert.IsLessThanOrEqualTo(1915, placement.Rect.X + placement.Rect.Width);
            Assert.IsLessThanOrEqualTo(1017, placement.Rect.Y + placement.Rect.Height);
        }

        for (int first = 0; first < placements.Length; first++)
        {
            for (int second = first + 1; second < placements.Length; second++)
            {
                Assert.IsFalse(Overlaps(
                    placements[first].Rect,
                    placements[second].Rect));
            }
        }
    }

    private static bool Overlaps(TileRect first, TileRect second) =>
        first.X < second.X + second.Width &&
        first.X + first.Width > second.X &&
        first.Y < second.Y + second.Height &&
        first.Y + first.Height > second.Y;
}

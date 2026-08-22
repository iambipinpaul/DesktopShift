using System.Collections.Immutable;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

[TestClass]
public sealed class BspTreeTests
{
    [TestMethod]
    public void FirstAdmissionFillsTheWholeWorkArea()
    {
        BspTree tree = new();
        Assert.IsTrue(tree.TryAdmit(
            TilingTestSupport.WorkArea,
            TilingTestSupport.Gap,
            TilingTestSupport.MinimumWidth,
            TilingTestSupport.MinimumHeight,
            out LeafToken first));

        ImmutableArray<BspPlacement> plan = Plan(tree);
        Assert.HasCount(1, plan);
        Assert.AreEqual(TilingTestSupport.WorkArea, TilingTestSupport.RectOf(plan, first));
    }

    [TestMethod]
    public void SecondAdmissionSplitsAlongTheLongerSideWithTheGapBetween()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(1);
        Assert.IsTrue(tree.TryAdmit(
            TilingTestSupport.WorkArea,
            TilingTestSupport.Gap,
            TilingTestSupport.MinimumWidth,
            TilingTestSupport.MinimumHeight,
            out LeafToken second));

        ImmutableArray<LeafToken> leaves = tree.Leaves;
        LeafToken first = leaves[0];
        TileRect left = TilingTestSupport.RectOf(Plan(tree), first);
        TileRect right = TilingTestSupport.RectOf(Plan(tree), second);

        // 1920 wide: usable 1912, first child gets the floor of half.
        Assert.AreEqual(956, left.Width);
        Assert.AreEqual(1920 - 8 - 956, right.Width);
        Assert.AreEqual(left.X + left.Width + TilingTestSupport.Gap, right.X);
        Assert.AreEqual(TilingTestSupport.WorkArea.Height, left.Height);
        Assert.AreEqual(TilingTestSupport.WorkArea.Height, right.Height);
    }

    [TestMethod]
    public void ThirdAdmissionSplitsTheLargestLeafUsingStableOrderForTies()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(2);
        Assert.IsTrue(tree.TryAdmit(
            TilingTestSupport.WorkArea,
            TilingTestSupport.Gap,
            TilingTestSupport.MinimumWidth,
            TilingTestSupport.MinimumHeight,
            out LeafToken third));

        ImmutableArray<LeafToken> leaves = tree.Leaves;
        Assert.HasCount(3, leaves);

        // Both halves have the same area. Stable leaf order breaks the tie,
        // so the oldest (left) half is split.
        TileRect oldest = TilingTestSupport.RectOf(Plan(tree), leaves[0]);
        TileRect secondLeaf = TilingTestSupport.RectOf(Plan(tree), leaves[2]);
        TileRect newest = TilingTestSupport.RectOf(Plan(tree), third);
        Assert.AreEqual(oldest.X, newest.X);
        Assert.AreEqual(oldest.Width, newest.Width);
        Assert.AreEqual((1040 - 8) / 2, oldest.Height);
        Assert.AreEqual(oldest.Y + oldest.Height + 8, newest.Y);
        Assert.AreEqual(956, secondLeaf.Width);
    }

    [TestMethod]
    public void AdmissionSplitsTheFocusedLeafWhenItCanSplit()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(2);
        LeafToken focused = tree.Leaves[1];

        Assert.IsTrue(tree.TryAdmit(
            TilingTestSupport.WorkArea,
            TilingTestSupport.Gap,
            TilingTestSupport.MinimumWidth,
            TilingTestSupport.MinimumHeight,
            focused,
            out LeafToken admitted));

        TileRect focusedRect = TilingTestSupport.RectOf(Plan(tree), focused);
        TileRect admittedRect = TilingTestSupport.RectOf(Plan(tree), admitted);
        Assert.AreEqual(focusedRect.X, admittedRect.X);
        Assert.AreEqual(focusedRect.Width, admittedRect.Width);
    }

    [TestMethod]
    public void RemovingAnUnknownLeafDoesNotChangeTheTree()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(2);
        ImmutableArray<LeafToken> before = tree.Leaves;

        Assert.IsFalse(tree.Remove(LeafToken.CreateNext(long.MaxValue)));

        CollectionAssert.AreEqual(before.ToArray(), tree.Leaves.ToArray());
    }

    [TestMethod]
    public void AdmissionFailsWhenNoTileCanSplitAndTheTreeIsUnchanged()
    {
        // A work area barely large enough for two minimum tiles side by side:
        // after two admissions nothing can give up half again.
        TileRect smallArea = new(0, 0, 700, 300);
        BspTree tree = new();
        Assert.IsTrue(tree.TryAdmit(smallArea, 0, 320, 240, out LeafToken _));
        Assert.IsTrue(tree.TryAdmit(smallArea, 0, 320, 240, out LeafToken _));

        int countBefore = tree.Count;
        bool admitted = tree.TryAdmit(smallArea, 0, 320, 240, out LeafToken rejected);

        Assert.IsFalse(admitted);
        Assert.AreEqual(default, rejected);
        Assert.HasCount(countBefore, tree.Leaves);
    }

    [TestMethod]
    public void RemovalCollapsesTheParentSoTheSiblingInherits()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(2);
        LeafToken removed = tree.Leaves[1];

        Assert.IsTrue(tree.Remove(removed));

        ImmutableArray<BspPlacement> plan = Plan(tree);
        Assert.HasCount(1, plan);
        Assert.AreEqual(
            TilingTestSupport.WorkArea,
            TilingTestSupport.RectOf(plan, tree.Leaves[0]));
    }

    [TestMethod]
    public void RemovingALeafKeepsUnrelatedTilesAndTheSiblingInherits()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(3);
        ImmutableArray<LeafToken> before = tree.Leaves;
        TileRect unrelatedBefore =
            TilingTestSupport.RectOf(Plan(tree), before[2]);

        tree.Remove(before[1]);

        // The other root branch takes no part in the collapsed split, so its
        // rectangle is byte-for-byte unchanged.
        Assert.AreEqual(
            unrelatedBefore,
            TilingTestSupport.RectOf(Plan(tree), tree.Leaves[1]));

        // The sibling of the removed tile reclaims the whole parent split,
        // which is exactly the rectangle it held before the newcomer split it.
        TileRect siblingBeforeAdmission = new(
            0,
            0,
            (TilingTestSupport.WorkArea.Width - TilingTestSupport.Gap) / 2,
            TilingTestSupport.WorkArea.Height);
        Assert.AreEqual(
            siblingBeforeAdmission,
            TilingTestSupport.RectOf(Plan(tree), tree.Leaves[0]));
    }

    [TestMethod]
    public void RemovingTheLastLeafEmptiesTheTree()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(1);
        Assert.IsTrue(tree.Remove(tree.Leaves[0]));
        Assert.HasCount(0, tree.Leaves);
        Assert.IsFalse(tree.Remove(tree.Leaves.IsEmpty ? default : tree.Leaves[0]));
    }

    [TestMethod]
    public void StableOrderSurvivesRemovals()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(4);
        ImmutableArray<LeafToken> original = tree.Leaves;

        tree.Remove(original[1]);
        tree.Remove(original[3]);

        ImmutableArray<LeafToken> now = tree.Leaves;
        Assert.HasCount(2, now);
        Assert.AreEqual(original[0], now[0]);
        Assert.AreEqual(original[2], now[1]);
    }

    [TestMethod]
    public void PruningOwnerlessLeavesClosesEverySilentTile()
    {
        var workspace = new TilingWorkspaceState(
            new TilingWorkspaceKey(Guid.NewGuid(), @"\\.\DISPLAY1"));
        for (int index = 0; index < 4; index++)
        {
            Assert.IsTrue(workspace.TryAdmit(
                TilingTestSupport.WorkArea,
                TilingTestSupport.Gap,
                TilingTestSupport.MinimumWidth,
                TilingTestSupport.MinimumHeight,
                preferredLeaf: null,
                out LeafToken _));
        }

        ImmutableArray<LeafToken> admitted = workspace.Tree.Leaves;
        workspace.PruneTo(new HashSet<LeafToken> { admitted[0], admitted[2] });

        CollectionAssert.AreEqual(
            new[] { admitted[0], admitted[2] },
            workspace.Tree.Leaves.ToArray());
        Assert.HasCount(2, BspLayoutPlanner.Plan(
            workspace.Tree,
            TilingTestSupport.WorkArea,
            TilingTestSupport.Gap));
    }

    [TestMethod]
    public void SameInputsProduceTheSamePlanByteForByte()
    {
        BspTree tree = TilingTestSupport.AdmittedTree(5);
        ImmutableArray<BspPlacement> one = Plan(tree);
        ImmutableArray<BspPlacement> again = Plan(tree);

        Assert.HasCount(one.Length, again);
        for (int index = 0; index < one.Length; index++)
        {
            Assert.IsTrue(one[index].Token == again[index].Token);
            Assert.AreEqual(one[index].Rect, again[index].Rect);
        }
    }

    [TestMethod]
    public void WorkAreasAreIndependentPerWorkspaceState()
    {
        var keyA = new TilingWorkspaceKey(Guid.NewGuid(), @"\\.\DISPLAY1");
        var keyB = new TilingWorkspaceKey(Guid.NewGuid(), @"\\.\DISPLAY1");
        var catalog = new TilingLayoutCatalog();

        TilingWorkspaceState a = catalog.GetOrCreate(keyA);
        TilingWorkspaceState b = catalog.GetOrCreate(keyB);
        a.TryAdmit(
            TilingTestSupport.WorkArea,
            8,
            320,
            240,
            preferredLeaf: null,
            out LeafToken _);

        Assert.HasCount(1, a.Tree.Leaves);
        Assert.HasCount(0, b.Tree.Leaves);
    }

    private static ImmutableArray<BspPlacement> Plan(BspTree tree)
    {
        return BspLayoutPlanner.Plan(
            tree,
            TilingTestSupport.WorkArea,
            TilingTestSupport.Gap);
    }
}

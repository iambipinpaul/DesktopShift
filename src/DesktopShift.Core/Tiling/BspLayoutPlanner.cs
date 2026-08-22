using System.Collections.Immutable;

namespace DesktopShift.Core.Tiling;

/// <summary>One planned tile: a leaf and the rectangle it should occupy.</summary>
/// <param name="Token">The layout slot.</param>
/// <param name="Rect">
/// The physical-pixel rectangle to place, already gap-adjusted.
/// </param>
public sealed record BspPlacement(LeafToken Token, TileRect Rect);

/// <summary>
/// Turns a BSP tree plus one monitor's geometry into tile rectangles.
/// </summary>
/// <remarks>
/// <para>
/// Pure arithmetic: no Win32, no clocks, no randomness. The same tree and the
/// same inputs always produce the same rectangles, which is what makes a
/// deterministic startup rebuild possible — replay the same admissions into an
/// empty tree and the layout is identical without any state having survived
/// the restart.
/// </para>
/// <para>
/// A split divides the parent rectangle along its longer side, because cutting
/// the longer side wastes less aspect ratio than cutting the shorter one. The
/// first child receives the floor of half the usable space and the second
/// receives the remainder, so no pixel is invented or lost and the split point
/// does not depend on floating-point rounding mode.
/// </para>
/// </remarks>
public static class BspLayoutPlanner
{
    /// <summary>
    /// Computes the rectangle every leaf should occupy right now.
    /// </summary>
    /// <param name="tree">The tree to lay out.</param>
    /// <param name="workAreaPixels">
    /// The monitor's working area — the area outside taskbars and appbars — in
    /// physical pixels.
    /// </param>
    /// <param name="gapPixels">The gap between neighbouring tiles, scaled.</param>
    /// <returns>One placement per leaf, oldest first.</returns>
    public static ImmutableArray<BspPlacement> Plan(
        BspTree tree,
        TileRect workAreaPixels,
        int gapPixels)
    {
        return Plan(tree, workAreaPixels, gapPixels, outerGapPixels: 0);
    }

    /// <summary>Computes placements with separate inner and outer gaps.</summary>
    public static ImmutableArray<BspPlacement> Plan(
        BspTree tree,
        TileRect workAreaPixels,
        int innerGapPixels,
        int outerGapPixels)
    {
        List<BspPlacement> placements = [];
        int inset = Math.Max(outerGapPixels, 0);
        TileRect layoutArea = new(
            workAreaPixels.X + inset,
            workAreaPixels.Y + inset,
            Math.Max(workAreaPixels.Width - (inset * 2), 0),
            Math.Max(workAreaPixels.Height - (inset * 2), 0));
        Collect(tree.Root, layoutArea, innerGapPixels, placements);
        return [.. placements];
    }

    /// <summary>
    /// Computes a BSP layout that also respects a physical-pixel minimum for
    /// each leaf. This is used after a foreign application proves through
    /// read-back that it cannot accept the normal equal split.
    /// </summary>
    public static ImmutableArray<BspPlacement> PlanConstrained(
        BspTree tree,
        TileRect workAreaPixels,
        int innerGapPixels,
        int outerGapPixels,
        IReadOnlyDictionary<LeafToken, BspLeafMinimum> minimums)
    {
        ArgumentNullException.ThrowIfNull(tree);
        ArgumentNullException.ThrowIfNull(minimums);
        return BspConstrainedLayoutPlanner.Plan(
            tree,
            workAreaPixels,
            innerGapPixels,
            outerGapPixels,
            minimums);
    }

    /// <summary>
    /// Whether a leaf occupying <paramref name="rect"/> can give up half of
    /// itself along its longer side and still leave both halves no smaller
    /// than the minimums.
    /// </summary>
    internal static bool CanSplit(
        TileRect rect,
        int gapPixels,
        int minimumWidthPixels,
        int minimumHeightPixels)
    {
        if (rect.IsEmpty)
        {
            return false;
        }

        return rect.Width >= rect.Height
            ? FitsTwoAcross(rect.Width, gapPixels, minimumWidthPixels)
            : FitsTwoAcross(rect.Height, gapPixels, minimumHeightPixels);
    }

    /// <summary>
    /// Divides one rectangle into two around a gap along the chosen axis.
    /// </summary>
    internal static void Split(
        TileRect rect,
        bool splitHorizontally,
        int gapPixels,
        out TileRect first,
        out TileRect second)
    {
        if (splitHorizontally)
        {
            int firstWidth = HalfOfUsable(rect.Width, gapPixels);
            first = new TileRect(rect.X, rect.Y, firstWidth, rect.Height);
            second = new TileRect(
                rect.X + firstWidth + gapPixels,
                rect.Y,
                Math.Max(rect.Width - gapPixels - firstWidth, 0),
                rect.Height);
        }
        else
        {
            int firstHeight = HalfOfUsable(rect.Height, gapPixels);
            first = new TileRect(rect.X, rect.Y, rect.Width, firstHeight);
            second = new TileRect(
                rect.X,
                rect.Y + firstHeight + gapPixels,
                rect.Width,
                Math.Max(rect.Height - gapPixels - firstHeight, 0));
        }
    }

    private static void Collect(
        BspTree.Node? node,
        TileRect rect,
        int gapPixels,
        List<BspPlacement> placements)
    {
        if (node is null)
        {
            return;
        }

        if (node is BspTree.LeafNode leaf)
        {
            placements.Add(new BspPlacement(leaf.Token, rect));
            return;
        }

        BspTree.BranchNode branch = (BspTree.BranchNode)node;
        Split(rect, branch.SplitHorizontally, gapPixels, out TileRect first, out TileRect second);
        Collect(branch.First, first, gapPixels, placements);
        Collect(branch.Second, second, gapPixels, placements);
    }

    private static bool FitsTwoAcross(int span, int gapPixels, int minimumPixels)
    {
        return (span - gapPixels) / 2 >= minimumPixels;
    }

    private static int HalfOfUsable(int span, int gapPixels)
    {
        return Math.Max((span - gapPixels) / 2, 0);
    }
}

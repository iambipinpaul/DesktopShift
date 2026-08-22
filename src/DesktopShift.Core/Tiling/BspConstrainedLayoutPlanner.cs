using System.Collections.Immutable;

namespace DesktopShift.Core.Tiling;

/// <summary>A leaf's proven minimum visible-frame size in physical pixels.</summary>
public sealed record BspLeafMinimum(int Width, int Height);

/// <summary>
/// Builds a deterministic slicing layout for applications with different
/// minimum sizes.
/// </summary>
internal static class BspConstrainedLayoutPlanner
{
    private const int MaximumCandidatesPerRange = 64;

    public static ImmutableArray<BspPlacement> Plan(
        BspTree tree,
        TileRect workAreaPixels,
        int innerGapPixels,
        int outerGapPixels,
        IReadOnlyDictionary<LeafToken, BspLeafMinimum> minimums)
    {
        int inset = Math.Max(outerGapPixels, 0);
        int gap = Math.Max(innerGapPixels, 0);
        TileRect layoutArea = new(
            workAreaPixels.X + inset,
            workAreaPixels.Y + inset,
            Math.Max(workAreaPixels.Width - (inset * 2), 0),
            Math.Max(workAreaPixels.Height - (inset * 2), 0));
        ImmutableArray<LeafToken> leaves = tree.Leaves;
        if (leaves.IsEmpty || layoutArea.IsEmpty)
        {
            return [];
        }

        List<PackingNode>[,] candidates =
            new List<PackingNode>[leaves.Length, leaves.Length];
        for (int index = 0; index < leaves.Length; index++)
        {
            BspLeafMinimum minimum = minimums.TryGetValue(
                leaves[index],
                out BspLeafMinimum? configured)
                ? configured
                : new BspLeafMinimum(1, 1);
            candidates[index, index] =
            [new PackingLeaf(
                leaves[index],
                Math.Max(minimum.Width, 1),
                Math.Max(minimum.Height, 1))];
        }

        for (int length = 2; length <= leaves.Length; length++)
        {
            for (int start = 0; start + length <= leaves.Length; start++)
            {
                int end = start + length - 1;
                List<PackingNode> range = [];
                for (int split = start; split < end; split++)
                {
                    foreach (PackingNode first in candidates[start, split])
                    {
                        foreach (PackingNode second in candidates[split + 1, end])
                        {
                            AddCandidate(
                                range,
                                new PackingBranch(
                                    first,
                                    second,
                                    SplitHorizontally: true,
                                    first.MinimumWidth + gap + second.MinimumWidth,
                                    Math.Max(first.MinimumHeight, second.MinimumHeight)),
                                layoutArea);
                            AddCandidate(
                                range,
                                new PackingBranch(
                                    first,
                                    second,
                                    SplitHorizontally: false,
                                    Math.Max(first.MinimumWidth, second.MinimumWidth),
                                    first.MinimumHeight + gap + second.MinimumHeight),
                                layoutArea);
                        }
                    }
                }

                candidates[start, end] = range;
            }
        }

        List<PackingNode> fullRange = candidates[0, leaves.Length - 1];
        if (fullRange.Count == 0)
        {
            // The reported minimums cannot all fit. Keep the normal BSP plan;
            // the coordinator will report the foreign adjustment without
            // inventing rectangles outside the monitor work area.
            return BspLayoutPlanner.Plan(
                tree,
                workAreaPixels,
                innerGapPixels,
                outerGapPixels);
        }

        double targetAspect = (double)layoutArea.Width / layoutArea.Height;
        PackingNode selected = fullRange
            .OrderBy(candidate => AspectDistance(candidate, targetAspect))
            .ThenByDescending(static candidate =>
                (long)candidate.MinimumWidth * candidate.MinimumHeight)
            .First();
        List<BspPlacement> placements = [];
        Allocate(selected, layoutArea, gap, placements);
        return [.. placements];
    }

    private static void AddCandidate(
        List<PackingNode> candidates,
        PackingNode candidate,
        TileRect layoutArea)
    {
        if (candidate.MinimumWidth > layoutArea.Width ||
            candidate.MinimumHeight > layoutArea.Height)
        {
            return;
        }

        if (candidates.Any(existing =>
            existing.MinimumWidth <= candidate.MinimumWidth &&
            existing.MinimumHeight <= candidate.MinimumHeight))
        {
            return;
        }

        candidates.RemoveAll(existing =>
            candidate.MinimumWidth <= existing.MinimumWidth &&
            candidate.MinimumHeight <= existing.MinimumHeight);
        candidates.Add(candidate);
        if (candidates.Count > MaximumCandidatesPerRange)
        {
            candidates.Sort(static (first, second) =>
                ((long)first.MinimumWidth * first.MinimumHeight).CompareTo(
                    (long)second.MinimumWidth * second.MinimumHeight));
            candidates.RemoveRange(
                MaximumCandidatesPerRange,
                candidates.Count - MaximumCandidatesPerRange);
        }
    }

    private static double AspectDistance(PackingNode candidate, double targetAspect)
    {
        double candidateAspect =
            (double)candidate.MinimumWidth / candidate.MinimumHeight;
        return Math.Abs(Math.Log(candidateAspect / targetAspect));
    }

    private static void Allocate(
        PackingNode node,
        TileRect rect,
        int gap,
        List<BspPlacement> placements)
    {
        if (node is PackingLeaf leaf)
        {
            placements.Add(new BspPlacement(leaf.Token, rect));
            return;
        }

        PackingBranch branch = (PackingBranch)node;
        if (branch.SplitHorizontally)
        {
            int usable = Math.Max(rect.Width - gap, 0);
            int firstWidth = AllocateSpan(
                usable,
                branch.First.MinimumWidth,
                branch.Second.MinimumWidth);
            TileRect first = new(rect.X, rect.Y, firstWidth, rect.Height);
            TileRect second = new(
                rect.X + firstWidth + gap,
                rect.Y,
                Math.Max(usable - firstWidth, 0),
                rect.Height);
            Allocate(branch.First, first, gap, placements);
            Allocate(branch.Second, second, gap, placements);
            return;
        }

        int usableHeight = Math.Max(rect.Height - gap, 0);
        int firstHeight = AllocateSpan(
            usableHeight,
            branch.First.MinimumHeight,
            branch.Second.MinimumHeight);
        TileRect top = new(rect.X, rect.Y, rect.Width, firstHeight);
        TileRect bottom = new(
            rect.X,
            rect.Y + firstHeight + gap,
            rect.Width,
            Math.Max(usableHeight - firstHeight, 0));
        Allocate(branch.First, top, gap, placements);
        Allocate(branch.Second, bottom, gap, placements);
    }

    private static int AllocateSpan(int available, int firstMinimum, int secondMinimum)
    {
        int minimumTotal = firstMinimum + secondMinimum;
        int extra = Math.Max(available - minimumTotal, 0);
        if (minimumTotal <= 0)
        {
            return available / 2;
        }

        int firstExtra = (int)Math.Round(
            extra * ((double)firstMinimum / minimumTotal),
            MidpointRounding.AwayFromZero);
        return Math.Clamp(firstMinimum + firstExtra, firstMinimum, available - secondMinimum);
    }

    private abstract record PackingNode(int MinimumWidth, int MinimumHeight);

    private sealed record PackingLeaf(
        LeafToken Token,
        int Width,
        int Height) : PackingNode(Width, Height);

    private sealed record PackingBranch(
        PackingNode First,
        PackingNode Second,
        bool SplitHorizontally,
        int Width,
        int Height) : PackingNode(Width, Height);
}

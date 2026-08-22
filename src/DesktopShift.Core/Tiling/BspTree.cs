using System.Collections.Immutable;

namespace DesktopShift.Core.Tiling;

/// <summary>
/// A binary space partition over layout slots, with insertion and removal.
/// </summary>
/// <remarks>
/// <para>
/// The tree holds slots (<see cref="LeafToken"/>), never windows. Every
/// mutation is a rebuild of the path from root to the changed node, so a
/// reference to the tree can never observe a half-applied change.
/// </para>
/// <para>
/// Insertion scans leaves newest-first and splits the first one that can give
/// up half of itself and still respect the minimum tile size. Newest-first is
/// what keeps the layout stable: the window a user just added lands beside the
/// window they were last looking at, not beside whatever happened to be
/// admitted first. When no leaf can split — every tile is already at the
/// minimum — admission fails and the caller floats the window instead; a tree
/// that cannot grow honestly says so rather than shrinking existing tiles
/// below usability.
/// </para>
/// <para>
/// Removal collapses the parent split so the surviving sibling inherits the
/// vacated space on the next plan. Nothing else about the tree moves, which is
/// why closing one window does not shuffle the others.
/// </para>
/// </remarks>
public sealed class BspTree
{
    private long nextSequence;

    /// <summary>Creates an empty tree. Each tree owns its own state.</summary>
    public BspTree()
    {
    }

    private BspTree(Node? root)
    {
        Root = root;
    }

    /// <summary>Creates a fresh empty tree.</summary>
    public static BspTree CreateEmpty() => new();

    /// <summary>How many leaves the tree currently holds.</summary>
    public int Count => Leaves.Length;

    /// <summary>The leaves in stable order: oldest admitted first.</summary>
    public ImmutableArray<LeafToken> Leaves
    {
        get
        {
            List<LeafToken> tokens = [];
            CollectLeaves(Root, tokens);
            return [.. tokens];
        }
    }

    internal Node? Root { get; private set; }

    /// <summary>Whether the tree holds this token.</summary>
    public bool Contains(LeafToken token)
    {
        return FindLeaf(Root, token) is not null;
    }

    /// <summary>
    /// Admits a new leaf, splitting an existing one to make room.
    /// </summary>
    /// <param name="workAreaPixels">
    /// The monitor working area the tree is being laid out into, in physical
    /// pixels. Feasibility depends on it: a large monitor can keep splitting
    /// where a small one must stop.
    /// </param>
    /// <param name="gapPixels">The scaled gap between neighbouring tiles.</param>
    /// <param name="minimumWidthPixels">The scaled minimum tile width.</param>
    /// <param name="minimumHeightPixels">
    /// The scaled minimum tile height.
    /// </param>
    /// <param name="admitted">The new leaf's token when this returns true.</param>
    /// <returns>
    /// Whether room was found. When false the tree is unchanged and the caller
    /// should float the window.
    /// </returns>
    public bool TryAdmit(
        TileRect workAreaPixels,
        int gapPixels,
        int minimumWidthPixels,
        int minimumHeightPixels,
        out LeafToken admitted)
    {
        return TryAdmit(
            workAreaPixels,
            gapPixels,
            minimumWidthPixels,
            minimumHeightPixels,
            preferredLeaf: null,
            out admitted);
    }

    /// <summary>
    /// Admits a new leaf. A splittable preferred leaf wins. Otherwise, the
    /// largest splittable leaf wins, with stable leaf order used for ties.
    /// </summary>
    public bool TryAdmit(
        TileRect workAreaPixels,
        int gapPixels,
        int minimumWidthPixels,
        int minimumHeightPixels,
        LeafToken? preferredLeaf,
        out LeafToken admitted)
    {
        LeafToken token = LeafToken.CreateNext(++nextSequence);

        if (Root is null)
        {
            Root = new LeafNode(token);
            admitted = token;
            return true;
        }

        ImmutableArray<BspPlacement> placements =
            BspLayoutPlanner.Plan(this, workAreaPixels, gapPixels);
        Dictionary<LeafToken, TileRect> byToken =
            placements.ToDictionary(static placement => placement.Token, static p => p.Rect);

        ImmutableArray<LeafToken> leaves = Leaves;
        LeafToken? candidate = null;
        long candidateArea = -1;
        foreach (LeafToken leaf in leaves)
        {
            if (!byToken.TryGetValue(leaf, out TileRect rect) ||
                !BspLayoutPlanner.CanSplit(
                    rect,
                    gapPixels,
                    minimumWidthPixels,
                    minimumHeightPixels))
            {
                continue;
            }

            if (preferredLeaf == leaf)
            {
                candidate = leaf;
                break;
            }

            long area = (long)rect.Width * rect.Height;
            if (area > candidateArea)
            {
                candidate = leaf;
                candidateArea = area;
            }
        }

        if (candidate is LeafToken selected &&
            byToken.TryGetValue(selected, out TileRect selectedRect))
        {
            bool splitHorizontally = selectedRect.Width >= selectedRect.Height;
            Root = ReplaceLeaf(
                Root,
                selected,
                new BranchNode(new LeafNode(selected), new LeafNode(token), splitHorizontally));
            admitted = token;
            return true;
        }

        admitted = default;
        return false;
    }

    /// <summary>
    /// Removes a leaf and collapses its parent so the sibling inherits the
    /// space.
    /// </summary>
    /// <returns>Whether the tree held the leaf.</returns>
    public bool Remove(LeafToken token)
    {
        if (Root is null)
        {
            return false;
        }

        if (!Contains(token))
        {
            return false;
        }

        if (Root is LeafNode only && only.Token == token)
        {
            Root = null;
            return true;
        }

        Node replacement = Replace(Root, token)
            ?? throw new InvalidOperationException("Tree root vanished during removal.");
        Root = replacement;
        return true;
    }

    private static Node Replace(Node node, LeafToken token)
    {
        if (node is LeafNode leaf)
        {
            bool removed = leaf.Token == token;
            return removed ? RemovedNode.Instance : leaf;
        }

        BspTree.BranchNode branch = (BspTree.BranchNode)node;
        Node first = Replace(branch.First, token);
        Node second = Replace(branch.Second, token);

        // Exactly one side loses its leaf; the other side takes over the
        // parent's whole rectangle.
        if (first is RemovedNode)
        {
            return second;
        }

        if (second is RemovedNode)
        {
            return first;
        }

        return branch with { First = first, Second = second };
    }

    private static Node ReplaceLeaf(Node node, LeafToken existing, Node branch)
    {
        if (node is LeafNode leaf)
        {
            return leaf.Token == existing ? branch : leaf;
        }

        BspTree.BranchNode current = (BspTree.BranchNode)node;
        return current with
        {
            First = ReplaceLeaf(current.First, existing, branch),
            Second = ReplaceLeaf(current.Second, existing, branch),
        };
    }

    private static void CollectLeaves(Node? node, List<LeafToken> tokens)
    {
        if (node is null)
        {
            return;
        }

        if (node is LeafNode leaf)
        {
            tokens.Add(leaf.Token);
            return;
        }

        BspTree.BranchNode branch = (BspTree.BranchNode)node;
        CollectLeaves(branch.First, tokens);
        CollectLeaves(branch.Second, tokens);
    }

    private static LeafNode? FindLeaf(Node? node, LeafToken token)
    {
        while (true)
        {
            if (node is null)
            {
                return null;
            }

            if (node is LeafNode leaf)
            {
                return leaf.Token == token ? leaf : null;
            }

            BspTree.BranchNode branch = (BspTree.BranchNode)node;
            LeafNode? found = FindLeaf(branch.First, token);
            node = found ?? branch.Second;
            if (found is not null)
            {
                return found;
            }
        }
    }

    internal abstract record Node;

    internal sealed record LeafNode(LeafToken Token) : Node;

    internal sealed record BranchNode(Node First, Node Second, bool SplitHorizontally) : Node;

    /// <summary>A sentinel marking a subtree whose single leaf was removed.</summary>
    private sealed record RemovedNode : Node
    {
        public static readonly RemovedNode Instance = new();
    }
}

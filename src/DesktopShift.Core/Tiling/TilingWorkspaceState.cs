namespace DesktopShift.Core.Tiling;

/// <summary>
/// Which layout a window belongs to: one virtual desktop on one monitor.
/// </summary>
/// <remarks>
/// <para>
/// The monitor half is a runtime monitor key supplied by the Windows adapter —
/// an identifier that is valid only for the current display topology and is
/// rebuilt whenever displays change. It is deliberately not an HMONITOR:
/// handle values are reused after topology changes and are meaningless across
/// restarts, so treating one as identity would quietly file windows under a
/// layout they were never admitted to.
/// </para>
/// </remarks>
/// <param name="DesktopId">The virtual desktop's identifier.</param>
/// <param name="MonitorKey">
/// The current-topology monitor identifier, as reported by the adapter.
/// </param>
public sealed record TilingWorkspaceKey(Guid DesktopId, string MonitorKey)
{
    /// <summary>A title-free form for diagnostics.</summary>
    public string ToDiagnosticString()
    {
        return $"desktop {DesktopId.ToString("N")[..8]} on {MonitorKey}";
    }
}

/// <summary>
/// The layout state for exactly one (desktop, monitor) pair.
/// </summary>
/// <remarks>
/// <para>
/// In memory only. Nothing here survives an app restart, by decision: the
/// coordinator rebuilds each workspace deterministically at startup and after
/// desktop switches by replaying the windows it observes, so persisted tree
/// state would be one more thing to migrate, corrupt, and reconcile — for no
/// capability the rebuild does not already provide.
/// </para>
/// </remarks>
public sealed class TilingWorkspaceState
{
    /// <summary>Starts with an empty tree of its own.</summary>
    public TilingWorkspaceState(TilingWorkspaceKey key)
    {
        Key = key;
        Tree = BspTree.CreateEmpty();
    }

    /// <summary>The workspace this state belongs to.</summary>
    public TilingWorkspaceKey Key { get; }

    /// <summary>The workspace's tree.</summary>
    public BspTree Tree { get; private set; }

    /// <summary>
    /// Admits a slot, floating the caller's problem when nothing can split.
    /// See <see cref="BspTree.TryAdmit"/>.
    /// </summary>
    public bool TryAdmit(
        TileRect workAreaPixels,
        int gapPixels,
        int minimumWidthPixels,
        int minimumHeightPixels,
        LeafToken? preferredLeaf,
        out LeafToken admitted)
    {
        return Tree.TryAdmit(
            workAreaPixels,
            gapPixels,
            minimumWidthPixels,
            minimumHeightPixels,
            preferredLeaf,
            out admitted);
    }

    /// <summary>Removes a slot from this workspace.</summary>
    public bool Remove(LeafToken token)
    {
        return Tree.Remove(token);
    }

    /// <summary>
    /// Removes every leaf that has no owner in the current observable window
    /// set. This prevents an interrupted assignment or a missed destroy event
    /// from leaving a permanent empty tile.
    /// </summary>
    public void PruneTo(IReadOnlySet<LeafToken> ownedTokens)
    {
        ArgumentNullException.ThrowIfNull(ownedTokens);
        foreach (LeafToken token in Tree.Leaves)
        {
            if (!ownedTokens.Contains(token))
            {
                _ = Tree.Remove(token);
            }
        }
    }
}

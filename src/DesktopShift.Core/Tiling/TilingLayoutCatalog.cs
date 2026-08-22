namespace DesktopShift.Core.Tiling;

/// <summary>
/// Every live workspace layout, addressed by desktop and monitor.
/// </summary>
/// <remarks>
/// <para>
/// One catalog for the whole application. Workspaces appear when a window is
/// first admitted to them and disappear when their desktop is closed or their
/// monitor key is retired by a display change — the coordinator owns both
/// triggers, the catalog only the bookkeeping.
/// </para>
/// </remarks>
public sealed class TilingLayoutCatalog
{
    private readonly Dictionary<TilingWorkspaceKey, TilingWorkspaceState> workspaces = [];

    /// <summary>
    /// Returns the workspace's state, creating an empty one when this pair has
    /// none yet.
    /// </summary>
    public TilingWorkspaceState GetOrCreate(TilingWorkspaceKey key)
    {
        if (!workspaces.TryGetValue(key, out TilingWorkspaceState? state))
        {
            state = new TilingWorkspaceState(key);
            workspaces.Add(key, state);
        }

        return state;
    }

    /// <summary>Whether this pair currently has layout state.</summary>
    public bool Contains(TilingWorkspaceKey key)
    {
        return workspaces.ContainsKey(key);
    }

    /// <summary>
    /// Forgets a workspace entirely. Used when a desktop closes or a display
    /// topology change retires every old monitor key at once.
    /// </summary>
    public bool Remove(TilingWorkspaceKey key)
    {
        return workspaces.Remove(key);
    }

    /// <summary>Drops every workspace. Used on display topology changes.</summary>
    public void Clear()
    {
        workspaces.Clear();
    }

    /// <summary>The keys of all live workspaces.</summary>
    public IReadOnlyCollection<TilingWorkspaceKey> Keys => [.. workspaces.Keys];
}

namespace DesktopShift.Core.Tiling;

/// <summary>
/// The identity of one slot in the layout — not of a window.
/// </summary>
/// <remarks>
/// <para>
/// A leaf token is created when a window is admitted and dies when the window
/// leaves the tree. It is deliberately not an HWND: handles are recycled by
/// Windows, so a tree keyed on them would silently hand one application's tile
/// to another after a handle reuse. The coordinator owns whatever map it needs
/// between tokens and windows; the tree only knows tokens.
/// </para>
/// <para>
/// <see cref="Sequence"/> is the stable order token. Leaves enumerate oldest
/// first, and insertion scans newest first, so both orders are derived from
/// this counter rather than from traversal accidents.
/// </para>
/// </remarks>
/// <param name="Id">A unique identifier for diagnostics.</param>
/// <param name="Sequence">
/// When this leaf was admitted, relative to its siblings in the same
/// workspace. Lower means admitted earlier.
/// </param>
public readonly record struct LeafToken(Guid Id, long Sequence)
{
    /// <summary>Creates the next token for a workspace.</summary>
    /// <param name="sequence">The workspace's next sequence number.</param>
    public static LeafToken CreateNext(long sequence)
    {
        return new LeafToken(Guid.NewGuid(), sequence);
    }

    /// <summary>A short, title-free form for diagnostics.</summary>
    public string ToDiagnosticString()
    {
        return $"leaf {Id.ToString("N")[..8]}#{Sequence}";
    }
}

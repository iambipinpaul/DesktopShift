namespace DesktopShift.Core.Tiling;

/// <summary>
/// One tile's rectangle in physical pixels.
/// </summary>
/// <remarks>
/// <para>
/// Physical pixels, not DPI-scaled units, because this is what a window
/// placement is made of and what a monitor work area reports. The DPI-scaled
/// settings — gaps and minimums — are converted once per plan, at the edge,
/// and everything inside the planner works in this type.
/// </para>
/// </remarks>
/// <param name="X">Left edge in physical pixels.</param>
/// <param name="Y">Top edge in physical pixels.</param>
/// <param name="Width">Width in physical pixels.</param>
/// <param name="Height">Height in physical pixels.</param>
public readonly record struct TileRect(int X, int Y, int Width, int Height)
{
    /// <summary>The horizontal centre of the rectangle.</summary>
    public int CenterX => X + (Width / 2);

    /// <summary>The vertical centre of the rectangle.</summary>
    public int CenterY => Y + (Height / 2);

    /// <summary>Whether every dimension is positive.</summary>
    public bool IsEmpty => Width <= 0 || Height <= 0;

    /// <summary>
    /// Whether the rectangle's centre lies inside another rectangle. Membership
    /// by centre rather than by any corner keeps the answer stable for windows
    /// that straddle a monitor boundary: the window belongs to the monitor it
    /// is mostly on, which is where its centre is.
    /// </summary>
    public bool CenterIsInside(TileRect other)
    {
        return CenterX >= other.X &&
            CenterX < other.X + other.Width &&
            CenterY >= other.Y &&
            CenterY < other.Y + other.Height;
    }
}

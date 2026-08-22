namespace DesktopShift.Core.Tiling;

/// <summary>
/// The space between neighbouring tiles, expressed in DPI-scaled units.
/// </summary>
/// <remarks>
/// <para>
/// DPI-scaled means "as it would look on a 96-DPI display". One gap setting,
/// one visual result on every monitor: 8 units is 8 physical pixels at 100%,
/// 12 at 150%, 20 at 250%. Physical-pixel gaps cannot do this — they would
/// need a different document per scale factor or would look wrong everywhere
/// but one.
/// </para>
/// <para>
/// Scaling happens per monitor at plan time, because one layout pass can span
/// monitors running different scale factors.
/// </para>
/// </remarks>
/// <param name="DipUnits">The gap in DPI-scaled units, zero or more.</param>
public sealed record TileGap(int DipUnits)
{
    /// <summary>Scales this gap for one monitor's scale factor.</summary>
    /// <param name="scaleFactor">Monitor DPI divided by 96.</param>
    /// <returns>The gap in physical pixels, rounded to nearest.</returns>
    public int ScaleToPixels(double scaleFactor)
    {
        return (int)Math.Round(DipUnits * scaleFactor, MidpointRounding.AwayFromZero);
    }
}

/// <summary>
/// The smallest tile the planner will create by splitting, in DPI-scaled
/// units.
/// </summary>
/// <remarks>
/// Below this floor an inserted window would rather float than crush an
/// existing tile, because a tile too small to show its own caption bar is not
/// a usable home for anything.
/// </remarks>
/// <param name="WidthDip">The narrowest splittable width, DPI-scaled.</param>
/// <param name="HeightDip">The shortest splittable height, DPI-scaled.</param>
public sealed record TileMinimum(int WidthDip, int HeightDip)
{
    /// <summary>Scales both floors for one monitor's scale factor.</summary>
    /// <param name="scaleFactor">Monitor DPI divided by 96.</param>
    /// <returns>The floors in physical pixels, rounded to nearest.</returns>
    public (int WidthPixels, int HeightPixels) ScaleToPixels(double scaleFactor)
    {
        return (
            (int)Math.Round(WidthDip * scaleFactor, MidpointRounding.AwayFromZero),
            (int)Math.Round(HeightDip * scaleFactor, MidpointRounding.AwayFromZero));
    }
}

using System.Collections.Immutable;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Tiling;

/// <summary>Shared geometry for planner tests.</summary>
internal static class TilingTestSupport
{
    /// <summary>A 1920×1040 working area: a 1080p display above its taskbar.</summary>
    public static readonly TileRect WorkArea = new(0, 0, 1920, 1040);

    public const int Gap = 8;

    public const int MinimumWidth = 320;

    public const int MinimumHeight = 240;

    public static BspTree AdmittedTree(int count)
    {
        BspTree tree = new();
        for (int index = 0; index < count; index++)
        {
            bool admitted = tree.TryAdmit(
                WorkArea,
                Gap,
                MinimumWidth,
                MinimumHeight,
                out LeafToken _);
            Assert.IsTrue(admitted, $"Admission {index + 1} of {count} failed.");
        }

        return tree;
    }

    public static TileRect RectOf(
        ImmutableArray<BspPlacement> placements,
        LeafToken token)
    {
        return placements.First(placement => placement.Token == token).Rect;
    }
}

namespace DesktopShift.Core.Tiling;

/// <summary>
/// Placement rules that read only a window's reported state.
/// </summary>
/// <remarks>
/// Windows exposes no general full-screen query for arbitrary foreign windows,
/// so full screen is a documented heuristic, decided here where it can be
/// tested without a display: a visible, non-maximized window whose visible
/// frame covers the whole monitor within a small tolerance. The tolerance
/// absorbs the one-pixel overdraw some games use and rounding at monitor edges.
/// </remarks>
public static class TilingWindowStateRules
{
    /// <summary>
    /// How many pixels short of the monitor edge a frame may fall and still
    /// count as covering the monitor.
    /// </summary>
    public const int FullScreenTolerancePixels = 8;

    /// <summary>
    /// Whether a window's state reads as a full-screen presentation of
    /// <paramref name="monitorBounds"/>.
    /// </summary>
    public static bool IsFullScreen(
        TilingWindowState state,
        TileRect monitorBounds,
        int tolerancePixels = FullScreenTolerancePixels)
    {
        ArgumentNullException.ThrowIfNull(state);
        if (!state.IsVisible || state.IsMinimized || state.IsMaximized)
        {
            return false;
        }

        // A borderless full-screen presentation has no invisible border. A
        // normal window on a small monitor can cover the monitor while still
        // wearing its resize borders; those margins say "normal window", not
        // "full-screen presentation".
        (int left, int top, int right, int bottom) = state.FrameMargins;
        if (left > tolerancePixels ||
            top > tolerancePixels ||
            right > tolerancePixels ||
            bottom > tolerancePixels)
        {
            return false;
        }

        TileRect frame = state.VisibleFramePixels;
        return frame.X <= monitorBounds.X + tolerancePixels &&
            frame.Y <= monitorBounds.Y + tolerancePixels &&
            frame.X + frame.Width >= monitorBounds.X + monitorBounds.Width - tolerancePixels &&
            frame.Y + frame.Height >= monitorBounds.Y + monitorBounds.Height - tolerancePixels;
    }
}

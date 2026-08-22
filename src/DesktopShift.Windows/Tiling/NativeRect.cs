namespace DesktopShift.Windows.Tiling;

/// <summary>A Win32 RECT as raw physical pixels.</summary>
/// <remarks>
/// Shared by the tiling adapters so every P/Invoke signature speaks the same
/// shape. Screen coordinates are signed 32-bit values exactly as Windows
/// reports them.
/// </remarks>
/// <param name="Left">The x coordinate of the top-left corner.</param>
/// <param name="Top">The y coordinate of the top-left corner.</param>
/// <param name="Right">The x coordinate of the bottom-right corner.</param>
/// <param name="Bottom">The y coordinate of the bottom-right corner.</param>
[System.Runtime.InteropServices.StructLayout(
    System.Runtime.InteropServices.LayoutKind.Sequential)]
internal readonly record struct NativeRect(
    int Left,
    int Top,
    int Right,
    int Bottom);

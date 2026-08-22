using System.Runtime.InteropServices;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Windows.Tiling;

/// <summary>Reads the current foreground window without changing focus.</summary>
public sealed class WindowsTilingFocusReader : ITilingFocusReader
{
    public nint ReadFocusedWindow() => NativeMethods.GetForegroundWindow();

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
    }
}

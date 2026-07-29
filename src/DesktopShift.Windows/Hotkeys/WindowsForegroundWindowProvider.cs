using System.Runtime.InteropServices;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>The User32 boundary used to identify the foreground window.</summary>
public interface IWindowsForegroundWindowNativeApi
{
    nint GetForegroundWindow();
}

/// <summary>Reports the HWND that currently receives foreground input.</summary>
public sealed class WindowsForegroundWindowProvider : IForegroundWindowProvider
{
    private readonly IWindowsForegroundWindowNativeApi native;

    public WindowsForegroundWindowProvider()
        : this(new User32ForegroundWindowNativeApi())
    {
    }

    public WindowsForegroundWindowProvider(
        IWindowsForegroundWindowNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);
        this.native = native;
    }

    public nint GetForegroundWindow() => native.GetForegroundWindow();

    private sealed class User32ForegroundWindowNativeApi
        : IWindowsForegroundWindowNativeApi
    {
        public nint GetForegroundWindow() =>
            NativeMethods.GetForegroundWindow();
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        internal static extern nint GetForegroundWindow();
    }
}

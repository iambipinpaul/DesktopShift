using System.Runtime.InteropServices;
using System.Text;

namespace DesktopShift.Windows.Observation;

public interface IWindowsWindowNativeApi
{
    bool IsWindow(nint windowHandle);

    bool IsWindowVisible(nint windowHandle);

    nint GetRootOwner(nint windowHandle);

    nint GetOwner(nint windowHandle);

    long GetWindowStyle(nint windowHandle);

    long GetWindowExtendedStyle(nint windowHandle);

    bool IsCloaked(nint windowHandle);

    nint GetShellWindow();

    nint GetDesktopWindow();

    uint GetWindowProcessId(nint windowHandle);

    string GetWindowClass(nint windowHandle);

    string? GetWindowTitle(nint windowHandle);

    /// <summary>
    /// Visits the descendants of <paramref name="windowHandle"/> until
    /// <paramref name="onChild"/> returns <see langword="false"/>.
    /// </summary>
    /// <remarks>
    /// Used to find the process that owns the content inside a frame window,
    /// so enumeration stops as soon as the caller has what it came for rather
    /// than walking a tree it does not need.
    /// </remarks>
    /// <param name="windowHandle">The window whose descendants are visited.</param>
    /// <param name="onChild">
    /// Receives each descendant and returns whether to keep going. It must not
    /// throw: it runs inside a native callback.
    /// </param>
    void EnumerateChildWindows(nint windowHandle, Func<nint, bool> onChild);
}

public sealed class WindowsWindowNativeApi : IWindowsWindowNativeApi
{
    private const uint GetAncestorRootOwner = 3;
    private const uint GetWindowOwner = 4;
    private const int WindowStyle = -16;
    private const int WindowExtendedStyle = -20;
    private const uint DwmWindowAttributeCloaked = 14;

    public bool IsWindow(nint windowHandle) =>
        NativeMethods.IsWindow(windowHandle);

    public bool IsWindowVisible(nint windowHandle) =>
        NativeMethods.IsWindowVisible(windowHandle);

    public nint GetRootOwner(nint windowHandle) =>
        NativeMethods.GetAncestor(windowHandle, GetAncestorRootOwner);

    /// <summary>
    /// Reads a window's owner through <c>GetWindow(GW_OWNER)</c>, which Windows
    /// documents as the way to obtain an owner. <c>GetAncestor(GA_ROOTOWNER)</c>
    /// walks the chain <c>GetParent</c> returns, and <c>GetParent</c> reports an
    /// owner only for a <c>WS_POPUP</c> window, so it cannot be relied on alone.
    /// </summary>
    /// <param name="windowHandle">The window whose owner is read.</param>
    /// <returns>The owner window, or zero when the window has none.</returns>
    public nint GetOwner(nint windowHandle) =>
        NativeMethods.GetWindow(windowHandle, GetWindowOwner);

    public long GetWindowStyle(nint windowHandle) =>
        NativeMethods.GetWindowLongPtr(windowHandle, WindowStyle).ToInt64();

    public long GetWindowExtendedStyle(nint windowHandle) =>
        NativeMethods.GetWindowLongPtr(windowHandle, WindowExtendedStyle).ToInt64();

    public bool IsCloaked(nint windowHandle)
    {
        int cloaked = 0;
        int result = NativeMethods.DwmGetWindowAttribute(
            windowHandle,
            DwmWindowAttributeCloaked,
            ref cloaked,
            sizeof(int));
        return result >= 0 && cloaked != 0;
    }

    public nint GetShellWindow() => NativeMethods.GetShellWindow();

    public nint GetDesktopWindow() => NativeMethods.GetDesktopWindow();

    public uint GetWindowProcessId(nint windowHandle)
    {
        NativeMethods.GetWindowThreadProcessId(windowHandle, out uint processId);
        return processId;
    }

    public string GetWindowClass(nint windowHandle)
    {
        StringBuilder buffer = new(256);
        int length = NativeMethods.GetClassName(
            windowHandle,
            buffer,
            buffer.Capacity);
        return length > 0 ? buffer.ToString() : string.Empty;
    }

    public string? GetWindowTitle(nint windowHandle)
    {
        int length = NativeMethods.GetWindowTextLength(windowHandle);
        if (length <= 0)
        {
            return null;
        }

        StringBuilder buffer = new(length + 1);
        return NativeMethods.GetWindowText(
            windowHandle,
            buffer,
            buffer.Capacity) > 0
                ? buffer.ToString()
                : null;
    }

    public void EnumerateChildWindows(nint windowHandle, Func<nint, bool> onChild)
    {
        ArgumentNullException.ThrowIfNull(onChild);

        NativeMethods.EnumChildWindowsCallback callback =
            (child, context) =>
            {
                _ = context;
                return onChild(child);
            };

        _ = NativeMethods.EnumChildWindows(windowHandle, callback, 0);
        GC.KeepAlive(callback);
    }

    private static class NativeMethods
    {
        [UnmanagedFunctionPointer(CallingConvention.Winapi)]
        internal delegate bool EnumChildWindowsCallback(
            nint windowHandle,
            nint context);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumChildWindows(
            nint parentWindow,
            EnumChildWindowsCallback callback,
            nint context);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint windowHandle);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindowVisible(nint windowHandle);

        [DllImport("user32.dll")]
        internal static extern nint GetAncestor(
            nint windowHandle,
            uint flags);

        [DllImport("user32.dll")]
        internal static extern nint GetWindow(
            nint windowHandle,
            uint command);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowLongPtrW",
            SetLastError = true)]
        internal static extern nint GetWindowLongPtr(
            nint windowHandle,
            int index);

        [DllImport("dwmapi.dll")]
        internal static extern int DwmGetWindowAttribute(
            nint windowHandle,
            uint attribute,
            ref int value,
            int valueSize);

        [DllImport("user32.dll")]
        internal static extern nint GetShellWindow();

        [DllImport("user32.dll")]
        internal static extern nint GetDesktopWindow();

        [DllImport("user32.dll")]
        internal static extern uint GetWindowThreadProcessId(
            nint windowHandle,
            out uint processId);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetClassNameW",
            CharSet = CharSet.Unicode)]
        internal static extern int GetClassName(
            nint windowHandle,
            StringBuilder className,
            int maxCount);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowTextLengthW",
            CharSet = CharSet.Unicode)]
        internal static extern int GetWindowTextLength(nint windowHandle);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetWindowTextW",
            CharSet = CharSet.Unicode)]
        internal static extern int GetWindowText(
            nint windowHandle,
            StringBuilder text,
            int maxCount);
    }
}

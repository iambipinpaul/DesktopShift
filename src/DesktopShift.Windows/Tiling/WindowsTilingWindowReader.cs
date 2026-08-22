using System.Runtime.InteropServices;
using DesktopShift.Core.Tiling;
using DesktopShift.Windows.Assignments;

namespace DesktopShift.Windows.Tiling;

/// <summary>
/// Reads everything tiling needs to know about one window, once, on demand.
/// </summary>
/// <remarks>
/// <para>
/// Every read is a fresh documented query — no cached state, no polling. A
/// window that refuses a read produces a structured
/// <see cref="TilingReadStatus"/> instead of an exception, because elevated and
/// protected windows refusing reads is expected here: DesktopShift answers it
/// by skipping the window, never by reaching for elevation or broader rights.
/// </para>
/// <para>
/// The visible frame comes from DWM extended frame bounds, which is what the
/// user sees; the outer rectangle adds invisible resize borders that would
/// otherwise make every tile slightly larger than asked for.
/// </para>
/// </remarks>
public sealed class WindowsTilingWindowReader : ITilingWindowReader, IDisposable
{
    private readonly ITilingWindowStateApi stateApi;
    private readonly IDocumentedVirtualDesktopManagerApi desktopManager;

    public WindowsTilingWindowReader()
        : this(
            new TilingWindowNativeApi(),
            new DocumentedVirtualDesktopManagerApi())
    {
    }

    internal WindowsTilingWindowReader(
        ITilingWindowStateApi stateApi,
        IDocumentedVirtualDesktopManagerApi desktopManager)
    {
        this.stateApi =
            stateApi ?? throw new ArgumentNullException(nameof(stateApi));
        this.desktopManager = desktopManager ??
            throw new ArgumentNullException(nameof(desktopManager));
    }

    public TilingWindowReading Read(nint windowHandle)
    {
        if (!stateApi.IsWindow(windowHandle))
        {
            return new TilingWindowReading(TilingReadStatus.WindowGone, null);
        }

        bool visible = stateApi.IsWindowVisible(windowHandle);
        if (!stateApi.TryGetWindowRect(windowHandle, out NativeRect windowRect))
        {
            return ReadRefused(windowHandle);
        }

        // Extended frame bounds can be refused on windows DWM knows nothing
        // about; falling back to the outer rectangle keeps those placeable with
        // zero margins rather than unplaceable.
        TileRect visibleFrame = stateApi.TryGetExtendedFrameBounds(
                windowHandle,
                out NativeRect frameBounds)
            ? FromNative(frameBounds)
            : FromNative(windowRect);

        DocumentedDesktopIdResult desktopId = desktopManager.GetWindowDesktopId(windowHandle);

        return new TilingWindowReading(
            TilingReadStatus.Succeeded,
            new TilingWindowState(
                FromNative(windowRect),
                visibleFrame,
                visible,
                stateApi.IsIconic(windowHandle),
                stateApi.IsZoomed(windowHandle),
                stateApi.TryIsCloaked(windowHandle, out int cloaked) && cloaked != 0,
                stateApi.MonitorFromWindow(windowHandle),
                desktopId.IsSuccess ? desktopId.DesktopId : null));
    }

    public void Dispose() => desktopManager.Dispose();

    private static TilingWindowReading ReadRefused(nint windowHandle)
    {
        int error = Marshal.GetLastPInvokeError();
        TilingReadStatus status = error switch
        {
            NativeMethodsErrorAccessDenied => TilingReadStatus.AccessDenied,
            _ => TilingReadStatus.Unavailable,
        };
        return new TilingWindowReading(status, null);
    }

    private const int NativeMethodsErrorAccessDenied = 5;

    private static TileRect FromNative(NativeRect rect) =>
        new(rect.Left, rect.Top, rect.Right - rect.Left, rect.Bottom - rect.Top);

    internal interface ITilingWindowStateApi
    {
        bool IsWindow(nint windowHandle);

        bool IsWindowVisible(nint windowHandle);

        bool IsIconic(nint windowHandle);

        bool IsZoomed(nint windowHandle);

        bool TryGetWindowRect(nint windowHandle, out NativeRect rect);

        bool TryGetExtendedFrameBounds(nint windowHandle, out NativeRect bounds);

        bool TryIsCloaked(nint windowHandle, out int cloakedValue);

        nint MonitorFromWindow(nint windowHandle);
    }

    internal sealed class TilingWindowNativeApi : ITilingWindowStateApi
    {
        internal static int DwmExtendedFrameBounds => 9;

        internal static int DwmCloaked => 14;

        public bool IsWindow(nint windowHandle) =>
            NativeMethods.IsWindow(windowHandle);

        public bool IsWindowVisible(nint windowHandle) =>
            NativeMethods.IsWindowVisible(windowHandle);

        public bool IsIconic(nint windowHandle) =>
            NativeMethods.IsIconic(windowHandle);

        public bool IsZoomed(nint windowHandle) =>
            NativeMethods.IsZoomed(windowHandle);

        public bool TryGetWindowRect(nint windowHandle, out NativeRect rect)
        {
            Marshal.SetLastPInvokeError(0);
            bool succeeded = NativeMethods.GetWindowRect(windowHandle, out rect);
            return succeeded;
        }

        // Attribute 9 is DWMWA_EXTENDED_FRAME_BOUNDS:
        // https://learn.microsoft.com/windows/win32/api/dwmapi/ne-dwmapi-dwmwindowattribute
        public bool TryGetExtendedFrameBounds(nint windowHandle, out NativeRect bounds) =>
            NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                DwmExtendedFrameBounds,
                out bounds,
                Marshal.SizeOf<NativeRect>()) == 0;

        // Attribute 14 is DWMWA_CLOAKED: non-zero when the shell holds the
        // window off-screen.
        public bool TryIsCloaked(nint windowHandle, out int cloakedValue)
        {
            cloakedValue = 0;
            return NativeMethods.DwmGetWindowAttribute(
                windowHandle,
                DwmCloaked,
                out cloakedValue,
                sizeof(int)) == 0;
        }

        // MONITOR_DEFAULTTONEAREST keeps a window that straddles monitors on
        // one of them instead of returning nothing.
        public nint MonitorFromWindow(nint windowHandle) =>
            NativeMethods.MonitorFromWindow(windowHandle, 2);

        private static class NativeMethods
        {
            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWindow(nint windowHandle);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsWindowVisible(nint windowHandle);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsIconic(nint windowHandle);

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool IsZoomed(nint windowHandle);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool GetWindowRect(
                nint windowHandle,
                out NativeRect rect);

            [DllImport("dwmapi.dll")]
            internal static extern int DwmGetWindowAttribute(
                nint windowHandle,
                int attribute,
                out NativeRect value,
                int size);

            [DllImport("dwmapi.dll")]
            internal static extern int DwmGetWindowAttribute(
                nint windowHandle,
                int attribute,
                out int value,
                int size);

            [DllImport("user32.dll")]
            internal static extern nint MonitorFromWindow(
                nint windowHandle,
                uint flags);
        }
    }
}

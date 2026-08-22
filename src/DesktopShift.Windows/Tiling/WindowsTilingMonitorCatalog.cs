using System.Runtime.InteropServices;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Windows.Tiling;

/// <summary>
/// Reads monitors and work areas for the current display topology.
/// </summary>
/// <remarks>
/// The identity returned for each monitor is the display device name —
/// <c>\\.\DISPLAY1</c> and friends — which is what a runtime monitor key is
/// built from. It is read fresh on every call, so a topology change is picked
/// up by the next read without any cached HMONITOR pretending to still be
/// meaningful. No polling lives here: callers read when reconciliation says to.
/// </remarks>
public sealed class WindowsTilingMonitorCatalog : ITilingMonitorCatalog
{
    private readonly ITilingMonitorApi monitorApi;

    public WindowsTilingMonitorCatalog()
        : this(new TilingMonitorNativeApi())
    {
    }

    internal WindowsTilingMonitorCatalog(ITilingMonitorApi monitorApi)
    {
        this.monitorApi =
            monitorApi ?? throw new ArgumentNullException(nameof(monitorApi));
    }

    public IReadOnlyList<TilingMonitorInfo> ReadMonitors()
    {
        List<TilingMonitorInfo> monitors = [];
        foreach (nint handle in monitorApi.EnumerateMonitors())
        {
            MonitorReading reading = monitorApi.ReadMonitor(handle);
            if (!reading.Succeeded || string.IsNullOrEmpty(reading.DeviceKey))
            {
                // A monitor whose own descriptor cannot be read has no stable
                // key to offer a layout, so it hosts no tiling workspace.
                continue;
            }

            monitors.Add(new TilingMonitorInfo(
                handle,
                reading.DeviceKey,
                new TileRect(
                    reading.WorkArea.Left,
                    reading.WorkArea.Top,
                    reading.WorkArea.Right - reading.WorkArea.Left,
                    reading.WorkArea.Bottom - reading.WorkArea.Top),
                new TileRect(
                    reading.MonitorBounds.Left,
                    reading.MonitorBounds.Top,
                    reading.MonitorBounds.Right - reading.MonitorBounds.Left,
                    reading.MonitorBounds.Bottom - reading.MonitorBounds.Top),
                reading.DpiX));
        }

        return monitors;
    }
}

internal sealed record MonitorReading(
    bool Succeeded,
    string DeviceKey = "",
    NativeRect MonitorBounds = default,
    NativeRect WorkArea = default,
    uint DpiX = 96);

internal interface ITilingMonitorApi
{
    IReadOnlyList<nint> EnumerateMonitors();

    MonitorReading ReadMonitor(nint monitorHandle);
}

internal sealed class TilingMonitorNativeApi : ITilingMonitorApi
{
    public IReadOnlyList<nint> EnumerateMonitors()
    {
        List<nint> handles = [];

        // The clip rectangle is passed through untouched; every monitor is
        // wanted, so no filtering happens here.
        _ = NativeMethods.EnumDisplayMonitors(
            0,
            0,
            (monitor, _, _, _) =>
            {
                handles.Add(monitor);
                return true;
            },
            0);
        return handles;
    }

    public MonitorReading ReadMonitor(nint monitorHandle)
    {
        var info = MONITORINFOEX.Create();
        if (!NativeMethods.GetMonitorInfo(monitorHandle, ref info))
        {
            return new MonitorReading(false);
        }

        // MDT_EFFECTIVE_DPI is the factor the system actually scales windows
        // by for this monitor, which is exactly what gap and minimum units
        // need to become pixels:
        // https://learn.microsoft.com/windows/win32/api/shellscalingapi/ne-shellscalingapi-monitor_dpi_type
        int dpiResult = NativeMethods.GetDpiForMonitor(
            monitorHandle,
            0,
            out uint dpiX,
            out _);
        if (dpiResult < 0 || dpiX == 0)
        {
            dpiX = 96;
        }

        return new MonitorReading(
            true,
            info.szDevice,
            info.rcMonitor,
            info.rcWork,
            dpiX);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate bool MonitorEnumProc(
        nint monitor,
        nint deviceContext,
        nint rectangle,
        nint data);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct MONITORINFOEX
    {
        public int cbSize;
        public NativeRect rcMonitor;
        public NativeRect rcWork;
        public uint dwFlags;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)]
        public string szDevice;

        public static MONITORINFOEX Create() =>
            new()
            {
                cbSize = Marshal.SizeOf<MONITORINFOEX>(),
                szDevice = string.Empty,
            };
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool EnumDisplayMonitors(
            nint deviceContext,
            nint clipRectangle,
            MonitorEnumProc callback,
            nint data);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool GetMonitorInfo(
            nint monitor,
            ref MONITORINFOEX info);

        [DllImport("shcore.dll")]
        internal static extern int GetDpiForMonitor(
            nint monitor,
            int dpiType,
            out uint dpiX,
            out uint dpiY);
    }
}

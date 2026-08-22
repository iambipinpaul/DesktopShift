using DesktopShift.Core.Tiling;
using DesktopShift.Windows.Tiling;

namespace DesktopShift.Windows.Tests.Tiling;

[TestClass]
public sealed class WindowsTilingMonitorCatalogTests
{
    [TestMethod]
    public void ReadMonitors_MapsWorkAreaDeviceKeyAndDpi()
    {
        FakeMonitorApi api = new();
        api.Add(
            0x3001,
            new NativeRect(0, 0, 2560, 1440),
            new NativeRect(0, 0, 2560, 1392),
            @"\\.\DISPLAY1",
            dpiX: 120);
        WindowsTilingMonitorCatalog catalog = new(api);

        IReadOnlyList<TilingMonitorInfo> monitors = catalog.ReadMonitors();

        Assert.HasCount(1, monitors);
        TilingMonitorInfo monitor = monitors[0];
        Assert.AreEqual(@"\\.\DISPLAY1", monitor.DeviceKey);
        Assert.AreEqual(new TileRect(0, 0, 2560, 1392), monitor.WorkAreaPixels);
        Assert.AreEqual(new TileRect(0, 0, 2560, 1440), monitor.MonitorBoundsPixels);
        Assert.AreEqual(120u, monitor.DpiX);
        Assert.AreEqual((nint)0x3001, monitor.Handle);
    }

    [TestMethod]
    public void ReadMonitors_SkipsAMonitorThatRefusesItsDescriptor()
    {
        FakeMonitorApi api = new();
        api.Add(0x3001, default, default, string.Empty, succeed: false);
        api.Add(
            0x3002,
            new NativeRect(0, 0, 1920, 1080),
            new NativeRect(0, 0, 1920, 1040),
            @"\\.\DISPLAY2");
        WindowsTilingMonitorCatalog catalog = new(api);

        IReadOnlyList<TilingMonitorInfo> monitors = catalog.ReadMonitors();

        // A monitor without a readable descriptor has no runtime key to offer,
        // so it hosts no tiling workspace.
        Assert.HasCount(1, monitors);
        Assert.AreEqual(@"\\.\DISPLAY2", monitors[0].DeviceKey);
    }

    [TestMethod]
    public void ReadMonitors_ReadsFreshEveryCallSoTopologyChangesArePickedUp()
    {
        FakeMonitorApi api = new();
        api.Add(
            0x3001,
            new NativeRect(0, 0, 1920, 1080),
            new NativeRect(0, 0, 1920, 1040),
            @"\\.\DISPLAY1");
        WindowsTilingMonitorCatalog catalog = new(api);

        _ = catalog.ReadMonitors();
        api.Clear();
        api.Add(
            0x4001,
            new NativeRect(0, 0, 3840, 2160),
            new NativeRect(0, 0, 3840, 2112),
            @"\\.\DISPLAY3");

        IReadOnlyList<TilingMonitorInfo> monitors = catalog.ReadMonitors();

        Assert.HasCount(1, monitors);
        Assert.AreEqual(@"\\.\DISPLAY3", monitors[0].DeviceKey);
        Assert.AreEqual(new TileRect(0, 0, 3840, 2112), monitors[0].WorkAreaPixels);
    }

    private sealed class FakeMonitorApi : ITilingMonitorApi
    {
        private readonly List<nint> handles = [];
        private readonly Dictionary<nint, MonitorReading> readings = [];

        public void Add(
            nint handle,
            NativeRect fullBounds,
            NativeRect workArea,
            string deviceKey,
            uint dpiX = 96,
            bool succeed = true)
        {
            handles.Add(handle);
            readings[handle] =
                new MonitorReading(succeed, deviceKey, fullBounds, workArea, dpiX);
        }

        public void Clear()
        {
            handles.Clear();
            readings.Clear();
        }

        public IReadOnlyList<nint> EnumerateMonitors() => handles;

        public MonitorReading ReadMonitor(nint monitorHandle) => readings[monitorHandle];
    }
}

using DesktopShift.Core.Tiling;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.Tiling;
using static DesktopShift.Windows.Tiling.WindowsTilingWindowReader.TilingWindowNativeApi;

namespace DesktopShift.Windows.Tests.Tiling;

[TestClass]
public sealed class WindowsTilingWindowReaderTests
{
    [TestMethod]
    public void NativeApi_UsesTheDocumentedDwmAttributeIds()
    {
        Assert.AreEqual(
            9,
            WindowsTilingWindowReader.TilingWindowNativeApi.DwmExtendedFrameBounds);
        Assert.AreEqual(
            14,
            WindowsTilingWindowReader.TilingWindowNativeApi.DwmCloaked);
    }

    [TestMethod]
    public void Read_MapsEverythingPlacementNeeds()
    {
        FakeStateApi stateApi = new()
        {
            // A typical visible frame of 1920x1040 at (0,0): the invisible
            // border extends 14 pixels left/top and right, 14 bottom.
            WindowRect = new NativeRect(-14, -14, 1934, 1054),
            FrameBounds = new NativeRect(0, 0, 1920, 1040),
            VisibleState = true,
            ZoomedState = false,
            IconicState = false,
            CloakedValue = 0,
            MonitorHandle = 0x5001,
        };
        FakeDesktopManager desktopManager = new(desktopId: Guid.NewGuid());
        using WindowsTilingWindowReader reader = new(stateApi, desktopManager);

        TilingWindowReading reading = reader.Read(0x6001);

        Assert.AreEqual(TilingReadStatus.Succeeded, reading.Status);
        TilingWindowState state = reading.State!;
        Assert.AreEqual(new TileRect(-14, -14, 1948, 1068), state.WindowRectPixels);
        Assert.AreEqual(new TileRect(0, 0, 1920, 1040), state.VisibleFramePixels);
        Assert.IsTrue(state.IsVisible);
        Assert.IsFalse(state.IsMaximized);
        Assert.IsFalse(state.IsMinimized);
        Assert.IsFalse(state.IsCloaked);
        Assert.AreEqual((nint)0x5001, state.MonitorHandle);
        Assert.IsNotNull(state.DesktopId);

        // The outer rectangle adds 14 pixels on every side beyond the visible
        // frame — exactly what placement must grow a tile by.
        Assert.AreEqual((14, 14, 14, 14), state.FrameMargins);
    }

    [TestMethod]
    public void Read_FallsBackToTheOuterRectangleWhenDwmRefusesFrameBounds()
    {
        FakeStateApi stateApi = new()
        {
            WindowRect = new NativeRect(10, 20, 110, 220),
            FailFrameBounds = true,
        };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(null));

        TilingWindowReading reading = reader.Read(0x6001);

        Assert.AreEqual(TilingReadStatus.Succeeded, reading.Status);
        Assert.AreEqual(
            new TileRect(10, 20, 100, 200),
            reading.State!.VisibleFramePixels);
    }

    [TestMethod]
    public void Read_ReportsCloakOnlyWhenWindowsSaysNonZero()
    {
        FakeStateApi stateApi = new()
        {
            WindowRect = new NativeRect(0, 0, 100, 100),
            CloakedValue = 1,
        };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(null));

        TilingWindowState state = reader.Read(0x6001).State!;

        Assert.IsTrue(state.IsCloaked);
        Assert.IsFalse(state.IsShellCloaked);
    }

    [TestMethod]
    public void Read_MapsTheDwmShellCloakReason()
    {
        FakeStateApi stateApi = new()
        {
            WindowRect = new NativeRect(0, 0, 100, 100),
            CloakedValue = 2,
        };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(null));

        TilingWindowState state = reader.Read(0x6001).State!;

        Assert.IsTrue(state.IsCloaked);
        Assert.IsTrue(state.IsShellCloaked);
        Assert.AreEqual(TilingCloakReason.Shell, state.CloakReasons);
    }

    [TestMethod]
    public void Read_TreatsADwmCloakFailureAsNotCloaked()
    {
        FakeStateApi stateApi = new()
        {
            WindowRect = new NativeRect(0, 0, 100, 100),
            FailCloak = true,
        };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(null));

        TilingWindowState state = reader.Read(0x6001).State!;

        Assert.IsFalse(state.IsCloaked);
    }

    [TestMethod]
    public void Read_MapsMinimizedAndMaximizedFlags()
    {
        FakeStateApi maximizedApi = new()
        {
            WindowRect = new NativeRect(0, 0, 100, 100),
            VisibleState = true,
            ZoomedState = true,
        };
        using WindowsTilingWindowReader maximizedReader =
            new(maximizedApi, new FakeDesktopManager(null));

        TilingWindowState maximized = maximizedReader.Read(0x6001).State!;

        Assert.IsTrue(maximized.IsMaximized);

        FakeStateApi minimizedApi = new()
        {
            WindowRect = new NativeRect(0, 0, 100, 100),
            VisibleState = true,
            IconicState = true,
        };
        using WindowsTilingWindowReader minimedReader =
            new(minimizedApi, new FakeDesktopManager(null));

        TilingWindowState minimized = minimedReader.Read(0x6001).State!;

        Assert.IsTrue(minimized.IsMinimized);
    }

    [TestMethod]
    public void Read_ADeadHandleIsReportedAsGoneWithoutOtherReads()
    {
        FakeStateApi stateApi = new() { WindowExists = false };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(null));

        TilingWindowReading reading = reader.Read(0x6001);

        Assert.AreEqual(TilingReadStatus.WindowGone, reading.Status);
        Assert.IsNull(reading.State);
        Assert.IsFalse(stateApi.GetWindowRectCalled);
    }

    [TestMethod]
    public void Read_AnAccessDeniedRectBecomesAStructuredRefusal()
    {
        FakeStateApi stateApi = new()
        {
            WindowRect = default,
            GetWindowRectFailsWithError = 5,
        };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(null));

        TilingWindowReading reading = reader.Read(0x6001);

        // Elevated windows refusing reads is an expected answer here, not an
        // exception: skip and report.
        Assert.AreEqual(TilingReadStatus.AccessDenied, reading.Status);
        Assert.IsNull(reading.State);
    }

    [TestMethod]
    public void Read_ARefusedDesktopGuidLeavesItNullButKeepsTheState()
    {
        FakeStateApi stateApi = new()
        {
            WindowRect = new NativeRect(0, 0, 100, 100),
        };
        using WindowsTilingWindowReader reader =
            new(stateApi, new FakeDesktopManager(desktopId: null, hResult: -2147024891));

        TilingWindowReading reading = reader.Read(0x6001);

        Assert.AreEqual(TilingReadStatus.Succeeded, reading.Status);
        Assert.IsNull(reading.State!.DesktopId);
    }

    private sealed class FakeStateApi
        : WindowsTilingWindowReader.ITilingWindowStateApi
    {
        public bool WindowExists { get; init; } = true;

        public bool VisibleState { get; init; }

        public bool IconicState { get; init; }

        public bool ZoomedState { get; init; }

        public NativeRect WindowRect { get; init; }

        public NativeRect FrameBounds { get; init; }

        public int CloakedValue { get; init; }

        public bool FailFrameBounds { get; init; }

        public bool FailCloak { get; init; }

        public int GetWindowRectFailsWithError { get; init; }

        public bool GetWindowRectCalled { get; private set; }

        public nint MonitorHandle { get; init; }

        public bool IsWindow(nint windowHandle) => WindowExists;

        public bool IsWindowVisible(nint windowHandle) => VisibleState;

        public bool IsIconic(nint windowHandle) => IconicState;

        public bool IsZoomed(nint windowHandle) => ZoomedState;

        public bool TryGetWindowRect(nint windowHandle, out NativeRect rect)
        {
            GetWindowRectCalled = true;
            if (GetWindowRectFailsWithError != 0)
            {
                System.Runtime.InteropServices.Marshal.SetLastPInvokeError(
                    GetWindowRectFailsWithError);
                rect = default;
                return false;
            }

            rect = WindowRect;
            return true;
        }

        public bool TryGetExtendedFrameBounds(nint windowHandle, out NativeRect bounds)
        {
            if (FailFrameBounds)
            {
                bounds = default;
                return false;
            }

            bounds = FrameBounds;
            return true;
        }

        public bool TryIsCloaked(nint windowHandle, out int cloakedValue)
        {
            if (FailCloak)
            {
                cloakedValue = 0;
                return false;
            }

            cloakedValue = CloakedValue;
            return true;
        }

        public nint MonitorFromWindow(nint windowHandle) => MonitorHandle;
    }

    private sealed class FakeDesktopManager(Guid? desktopId, int hResult = 0)
        : IDocumentedVirtualDesktopManagerApi, IDisposable
    {
        public DocumentedDesktopIdResult GetWindowDesktopId(nint windowHandle) =>
            new(desktopId ?? Guid.Empty, hResult, "GetWindowDesktopId");

        public void Dispose()
        {
        }
    }
}

using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.VirtualDesktops;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.Tests.VirtualDesktops;

[TestClass]
public sealed class NativeBridgeOptInContractTests
{
    [TestMethod]
    public void NativeBridge_ExportsMutationBoundariesAndPinsMoveStagesWithoutInvokingThem()
    {
        string libraryPath = Path.Combine(
            AppContext.BaseDirectory,
            "DesktopShift.NativeBridge.dll");

        Assert.IsTrue(
            NativeLibrary.TryLoad(libraryPath, out nint library),
            $"Could not load the native bridge at {libraryPath}.");
        try
        {
            Assert.IsTrue(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_CreateDesktop",
                    out nint entryPoint));
            Assert.AreNotEqual(0, entryPoint);
            Assert.IsTrue(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_SwitchDesktop",
                    out entryPoint));
            Assert.AreNotEqual(0, entryPoint);
            Assert.IsTrue(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_MoveWindowToDesktop",
                    out entryPoint));
            Assert.AreNotEqual(0, entryPoint);

            // Naming is the one mutation boundary that targets a desktop rather
            // than a window. It is asserted present here, alongside the others,
            // rather than left to be discovered — the point of this file is that
            // every way the bridge can change the machine is written down.
            Assert.IsTrue(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_SetDesktopName",
                    out entryPoint));
            Assert.AreNotEqual(0, entryPoint);
            Assert.IsTrue(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_MoveDesktop",
                    out entryPoint));
            Assert.AreNotEqual(0, entryPoint);

            // Pinning changes what a window is shown on rather than which
            // desktop it lives on, so it is written down here with the other
            // ways the bridge can change the machine, and its read-only probe
            // sits beside it because that probe is what makes the pin slots
            // reachable at all.
            foreach (string export in new[]
            {
                "DesktopShiftNative_IsWindowPinned",
                "DesktopShiftNative_PinWindow",
                "DesktopShiftNative_UnpinWindow",
                "DesktopShiftNative_ProbeWindowPin",
            })
            {
                Assert.IsTrue(
                    NativeLibrary.TryGetExport(library, export, out entryPoint),
                    $"The native bridge does not export {export}.");
                Assert.AreNotEqual(0, entryPoint);
            }

            // The app-id pin slots are declared only so the view slots land at
            // their real vtable offsets. Nothing reaches them, so nothing
            // exports a way to.
            Assert.IsFalse(
                NativeLibrary.TryGetExport(
                    library,
                    "DesktopShiftNative_PinAppId",
                    out _));

            IReadOnlyDictionary<string, uint> stages = Enum
                .GetValues<NativeMethods.NativeStage>()
                .ToDictionary(
                    static stage => stage.ToString(),
                    static stage => (uint)stage,
                    StringComparer.Ordinal);
            Assert.AreEqual(12U, stages["ApplicationViewActivation"]);
            Assert.AreEqual(13U, stages["WindowMove"]);
            Assert.AreEqual(14U, stages["LayoutProbe"]);
            Assert.AreEqual(15U, stages["DesktopRename"]);
            Assert.AreEqual(16U, stages["ApplicationViewLookup"]);
            Assert.AreEqual(17U, stages["DesktopReorder"]);
            Assert.AreEqual(18U, stages["WindowPin"]);
        }
        finally
        {
            NativeLibrary.Free(library);
        }
    }

    [TestMethod]
    public async Task CurrentMachine_HarmlessInventoryContractOnly()
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "DESKTOPSHIFT_RUN_NATIVE_CONTRACT_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set DESKTOPSHIFT_RUN_NATIVE_CONTRACT_TESTS=1 on a designated Windows test machine.");
        }

        OperatingSystemVersion version = GetCurrentWindowsVersion();
        WindowsBuildInfo build = new(
            OperatingSystem.IsWindows(),
            version.Major,
            version.Minor,
            version.Build,
            version.Revision,
            RuntimeInformation.OSArchitecture);
        using ValidatedVirtualDesktopTopologyProvider provider = new();

        DesktopTopologyProviderResult compatibility =
            await provider.TestCompatibilityAsync(build);
        Assert.IsTrue(compatibility.IsSuccess);
        Assert.AreEqual(
            DesktopTopologyProviderMode.Full,
            provider.Identity.Mode,
            provider.LastFallback?.Error.Message);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> inventory =
            await provider.EnumerateDesktopsAsync();
        DesktopTopologyProviderResult<Guid> current =
            await provider.GetCurrentDesktopIdAsync();

        Assert.IsTrue(inventory.IsSuccess, inventory.Error?.Message);
        Assert.IsNotEmpty(inventory.Value!);
        Assert.IsTrue(current.IsSuccess, current.Error?.Message);
        Assert.HasCount(1, inventory.Value!.Where(static desktop => desktop.IsCurrent));
        Assert.AreEqual(
            current.Value,
            inventory.Value!.Single(static desktop => desktop.IsCurrent).Id);

        DesktopTopologyProviderResult switchResult =
            await provider.SwitchDesktopAsync(current.Value);
        DesktopTopologyProviderResult<Guid> afterSwitch =
            await provider.GetCurrentDesktopIdAsync();

        Assert.IsTrue(switchResult.IsSuccess, switchResult.Error?.Message);
        Assert.IsTrue(afterSwitch.IsSuccess, afterSwitch.Error?.Message);
        Assert.AreEqual(current.Value, afterSwitch.Value);

        // Switch targets only the already-current desktop, so this contract
        // does not change the user's active desktop. It deliberately does not
        // create, remove, rename, or move any desktop or window.
    }

    private static OperatingSystemVersion GetCurrentWindowsVersion()
    {
        Version version = Environment.OSVersion.Version;
        return new OperatingSystemVersion(
            version.Major,
            version.Minor,
            version.Build,
            Math.Max(version.Revision, 0));
    }

    private sealed record OperatingSystemVersion(
        int Major,
        int Minor,
        int Build,
        int Revision);
}

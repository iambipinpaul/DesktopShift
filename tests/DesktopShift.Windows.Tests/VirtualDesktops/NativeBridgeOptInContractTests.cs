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

            IReadOnlyDictionary<string, uint> stages = Enum
                .GetValues<NativeMethods.NativeStage>()
                .ToDictionary(
                    static stage => stage.ToString(),
                    static stage => (uint)stage,
                    StringComparer.Ordinal);
            Assert.AreEqual(12U, stages["ApplicationViewActivation"]);
            Assert.AreEqual(13U, stages["WindowMove"]);
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

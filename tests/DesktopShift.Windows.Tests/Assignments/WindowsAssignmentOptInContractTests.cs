using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.VirtualDesktops;

namespace DesktopShift.Windows.Tests.Assignments;

[TestClass]
public sealed class WindowsAssignmentOptInContractTests
{
    [TestMethod]
    [DataRow(0)]
    [DataRow(29999)]
    public void CurrentMachine_TestOwnedWindowSupportsQueryAndEnumeration(int buildOverride)
    {
        if (!string.Equals(
                Environment.GetEnvironmentVariable(
                    "DESKTOPSHIFT_RUN_NATIVE_CONTRACT_TESTS"),
                "1",
                StringComparison.Ordinal))
        {
            Assert.Inconclusive(
                "Set DESKTOPSHIFT_RUN_NATIVE_CONTRACT_TESTS=1 on a designated interactive Windows test machine.");
        }

        using TestTopLevelWindow testWindow = new();
        nint windowHandle = testWindow.Handle;
        using ValidatedVirtualDesktopTopologyProvider provider = new();
        DesktopTopologyProviderResult compatibility =
            provider.TestCompatibilityAsync(GetCurrentBuildInfo(buildOverride))
                .AsTask()
                .GetAwaiter()
                .GetResult();
        Assert.IsTrue(compatibility.IsSuccess, compatibility.Error?.Message);
        Assert.AreEqual(
            DesktopTopologyProviderMode.Full,
            provider.Identity.Mode,
            provider.LastFallback?.Error.Message);
        using WindowsWindowDesktopPlacementService placement = new(provider);
        DesktopTopologyProviderResult<Guid> placementResult;
        int remainingAttempts = 50;
        do
        {
            placementResult = placement.GetWindowDesktopIdAsync(windowHandle)
                .AsTask()
                .GetAwaiter()
                .GetResult();
            if (placementResult.IsSuccess)
            {
                break;
            }

            Thread.Sleep(20);
        }
        while (--remainingAttempts > 0);
        IReadOnlyList<nint> windows =
            new WindowsTopLevelWindowEnumerator().Enumerate();

        Assert.IsTrue(
            placementResult.IsSuccess,
            $"{placementResult.Error?.Code} " +
            $"0x{placementResult.Error?.HResult:X8}: " +
            placementResult.Error?.Message);
        Assert.AreNotEqual(Guid.Empty, placementResult.Value);
        Assert.Contains(windowHandle, windows);
        DesktopTopologyProviderResult<Guid> currentDesktop = provider.GetCurrentDesktopIdAsync()
            .AsTask().GetAwaiter().GetResult();
        Assert.IsTrue(currentDesktop.IsSuccess, currentDesktop.Error?.Message);
        Assert.AreEqual(currentDesktop.Value, placementResult.Value);

        DesktopTopologyProviderResult moveResult =
            placement.MoveWindowToDesktopAsync(
                    windowHandle,
                    placementResult.Value)
                .AsTask()
                .GetAwaiter()
                .GetResult();
        DesktopTopologyProviderResult<Guid> afterMove =
            placement.GetWindowDesktopIdAsync(windowHandle)
                .AsTask()
                .GetAwaiter()
                .GetResult();

        Assert.IsTrue(moveResult.IsSuccess, moveResult.Error?.Message);
        Assert.IsTrue(afterMove.IsSuccess, afterMove.Error?.Message);
        Assert.AreEqual(placementResult.Value, afterMove.Value);

        // The move contract is invoked only for this test-owned HWND and uses
        // the exact GUID already returned for it, so placement is unchanged.
    }

    private static WindowsBuildInfo GetCurrentBuildInfo(int buildOverride)
    {
        Version version = Environment.OSVersion.Version;
        return new WindowsBuildInfo(
            OperatingSystem.IsWindows(),
            version.Major,
            version.Minor,
            buildOverride == 0 ? version.Build : buildOverride,
            Math.Max(version.Revision, 0),
            RuntimeInformation.OSArchitecture);
    }
}

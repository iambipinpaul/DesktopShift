using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Compatibility;

namespace DesktopShift.Windows.Tests.Compatibility;

[TestClass]
public sealed class LimitedVirtualDesktopTopologyServiceTests
{
    [TestMethod]
    public async Task CompatibilityTest_OnUnknownWindowsBuild_ReportsDocumentedCapabilities()
    {
        LimitedVirtualDesktopTopologyService provider = new();
        WindowsBuildInfo build = new(
            isWindows: true,
            major: 10,
            minor: 0,
            build: 29999,
            revision: 42,
            Architecture.X64);

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(build);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.IsFalse(provider.Identity.UsesPrivateApis);
        Assert.IsTrue(provider.Capabilities.CanGetWindowDesktopId);
        Assert.IsTrue(provider.Capabilities.CanMoveWindowToDesktop);
        Assert.IsFalse(provider.Capabilities.HasPrivateTopologyCapabilities);
    }

    [TestMethod]
    public async Task TopologyOperations_ReturnUnsupported()
    {
        LimitedVirtualDesktopTopologyService provider = new();

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> enumerate =
            await provider.EnumerateDesktopsAsync();
        DesktopTopologyProviderResult<Guid> current =
            await provider.GetCurrentDesktopIdAsync();
        DesktopTopologyProviderResult<Guid> create =
            await provider.CreateDesktopAsync();
        DesktopTopologyProviderResult switchResult =
            await provider.SwitchDesktopAsync(Guid.NewGuid());
        DesktopTopologyProviderResult notifications =
            await provider.StartTopologyNotificationsAsync();

        AssertUnsupported(enumerate.Outcome, enumerate.Error);
        AssertUnsupported(current.Outcome, current.Error);
        AssertUnsupported(create.Outcome, create.Error);
        AssertUnsupported(switchResult.Outcome, switchResult.Error);
        AssertUnsupported(notifications.Outcome, notifications.Error);
    }

    [TestMethod]
    public async Task CompatibilityTest_OnNonWindowsPlatform_FailsWithoutProbing()
    {
        LimitedVirtualDesktopTopologyService provider = new();
        WindowsBuildInfo build = new(
            isWindows: false,
            major: 6,
            minor: 0,
            build: 0,
            revision: 0,
            Architecture.X64);

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(build);

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual("platform.not_windows", result.Error?.Code);
    }

    private static void AssertUnsupported(
        DesktopTopologyResultOutcome outcome,
        DesktopTopologyProviderError? error)
    {
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, outcome);
        Assert.AreEqual("desktop_topology.unsupported_in_limited_mode", error?.Code);
    }
}

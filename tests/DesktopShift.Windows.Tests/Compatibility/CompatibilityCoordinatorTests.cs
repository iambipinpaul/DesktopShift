using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Compatibility;

namespace DesktopShift.Windows.Tests.Compatibility;

[TestClass]
public sealed class CompatibilityCoordinatorTests
{
    [TestMethod]
    public async Task RunCompatibilityTest_UnknownBuild_PublishesLimitedUiStateAndDiagnostics()
    {
        WindowsBuildInfo build = CreateBuild(29999);
        CompatibilityCoordinator coordinator = new(
            new StubBuildInfoProvider(build),
            new LimitedVirtualDesktopTopologyService());
        CompatibilityStatusChangedEventArgs? changed = null;
        coordinator.StatusChanged += (_, eventArgs) => changed = eventArgs;

        CompatibilityTestResult result =
            await coordinator.RunCompatibilityTestAsync();

        Assert.AreEqual(CompatibilityTestOutcome.PassedLimitedMode, result.Outcome);
        Assert.AreEqual(
            WindowsBuildSupport.UnknownWindows11Build,
            coordinator.Current.BuildAssessment.Support);
        Assert.AreEqual(
            DesktopTopologyProviderAvailability.Ready,
            coordinator.Current.Provider.Availability);
        Assert.IsTrue(coordinator.Current.IsLimitedMode);
        Assert.AreEqual(build.ExactVersion, coordinator.Current.Build.ExactVersion);
        Assert.AreEqual("Windows documented API", coordinator.Current.Provider.Identity.DisplayName);
        Assert.IsFalse(coordinator.Current.Provider.Capabilities.CanEnumerateDesktops);
        StringAssert.Contains(coordinator.Current.Provider.Explanation, "Limited Mode");
        Assert.IsNotNull(changed);
        Assert.AreSame(coordinator.Current, changed.Status);
        Assert.IsTrue(
            result.Diagnostics.Any(
                static diagnostic => diagnostic.EventName == "Compatibility.BuildDetected"));
        Assert.IsTrue(
            result.Diagnostics.Any(
                static diagnostic => diagnostic.EventName == "Compatibility.ProviderSelected"));
        Assert.IsTrue(
            result.Diagnostics.Any(
                static diagnostic => diagnostic.EventName == "Compatibility.CapabilitiesChecked"));
    }

    [TestMethod]
    public async Task RunCompatibilityTest_ProviderFailure_PublishesStructuredFailure()
    {
        DesktopTopologyProviderError failure = new(
            "provider.validation_failed",
            "Harmless provider validation failed.",
            unchecked((int)0x80004005),
            5);
        CompatibilityCoordinator coordinator = new(
            new StubBuildInfoProvider(CreateBuild(26100)),
            new StubProvider(
                new DesktopTopologyProviderResult(
                    DesktopTopologyResultOutcome.Failed,
                    failure)));

        CompatibilityTestResult result =
            await coordinator.RunCompatibilityTestAsync();

        Assert.AreEqual(CompatibilityTestOutcome.Failed, result.Outcome);
        Assert.AreEqual(
            DesktopTopologyProviderAvailability.Failed,
            coordinator.Current.Provider.Availability);

        CompatibilityDiagnostic diagnostic = result.Diagnostics.Single(
            static item => item.EventName == "Compatibility.ProviderCheckFailed");
        Assert.AreEqual(failure.HResult, diagnostic.HResult);
        Assert.AreEqual(failure.NativeErrorCode, diagnostic.NativeErrorCode);
        Assert.AreEqual(failure.Code, diagnostic.Properties["code"]);
    }

    [TestMethod]
    public async Task RunCompatibilityTest_ProviderThrows_ConvertsExceptionToFailure()
    {
        CompatibilityCoordinator coordinator = new(
            new StubBuildInfoProvider(CreateBuild(26100)),
            new ThrowingProvider());

        CompatibilityTestResult result =
            await coordinator.RunCompatibilityTestAsync();

        Assert.AreEqual(CompatibilityTestOutcome.Failed, result.Outcome);
        StringAssert.Contains(result.Summary, "probe exploded");
        Assert.AreEqual(
            "provider.compatibility_test_exception",
            result.Diagnostics.Single(
                static item => item.EventName == "Compatibility.ProviderCheckFailed")
                .Properties["code"]);
    }

    private static WindowsBuildInfo CreateBuild(int buildNumber) =>
        new(
            isWindows: true,
            major: 10,
            minor: 0,
            build: buildNumber,
            revision: 1,
            Architecture.X64);

    private sealed class StubBuildInfoProvider : IWindowsBuildInfoProvider
    {
        private readonly WindowsBuildInfo build;

        public StubBuildInfoProvider(WindowsBuildInfo build)
        {
            this.build = build;
        }

        public WindowsBuildInfo GetCurrent() => build;
    }

    private class StubProvider : IDesktopTopologyProvider
    {
        private readonly DesktopTopologyProviderResult result;

        public StubProvider(DesktopTopologyProviderResult result)
        {
            this.result = result;
        }

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.provider",
            "Test provider",
            "1",
            DesktopTopologyProviderMode.Limited,
            UsesPrivateApis: false);

        public VirtualDesktopCapabilities Capabilities =>
            VirtualDesktopCapabilities.DocumentedLimited;

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add
            {
            }

            remove
            {
            }
        }

        public virtual ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(result);

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ThrowingProvider : StubProvider
    {
        public ThrowingProvider()
            : base(DesktopTopologyProviderResult.Succeeded())
        {
        }

        public override ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("probe exploded");
    }
}

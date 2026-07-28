using System.Collections.Immutable;
using System.Runtime.InteropServices;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.VirtualDesktops;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.Tests.VirtualDesktops;

[TestClass]
public sealed class ValidatedVirtualDesktopTopologyProviderTests
{
    private static readonly WindowsBuildInfo Build26200 = new(
        isWindows: true,
        major: 10,
        minor: 0,
        build: 26200,
        revision: 8117,
        Architecture.X64);

    [TestMethod]
    public async Task ValidatedAdapter_EnablesManagedDesktopCreationCapability()
    {
        FakeNativeBridge bridge = new(CreateSnapshot());
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(Build26200);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DesktopTopologyProviderMode.Full, provider.Identity.Mode);
        Assert.AreEqual("windows.shell.25h2.26200", provider.Identity.Id);
        Assert.IsTrue(provider.Identity.UsesPrivateApis);
        Assert.IsTrue(provider.Capabilities.CanEnumerateDesktops);
        Assert.IsTrue(provider.Capabilities.CanGetCurrentDesktop);
        Assert.IsTrue(provider.Capabilities.CanObserveTopologyChanges);
        Assert.IsTrue(provider.Capabilities.CanCreateDesktop);
        Assert.IsFalse(provider.Capabilities.CanSwitchDesktop);
        Assert.IsNull(provider.LastFallback);
        Assert.AreEqual(1, bridge.ValidationCount);
    }

    [TestMethod]
    public async Task ValidatedAdapter_EnumerationReturnsOrderedInventoryAndCurrentMarker()
    {
        NativeDesktopSnapshot snapshot = CreateSnapshot();
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    new FakeNativeBridge(snapshot))));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, result.Value!);
        Assert.AreEqual("Code", result.Value![0].DisplayName);
        Assert.AreEqual(0, result.Value[0].Position);
        Assert.IsFalse(result.Value[0].IsCurrent);
        Assert.AreEqual("Web", result.Value[1].DisplayName);
        Assert.AreEqual(1, result.Value[1].Position);
        Assert.IsTrue(result.Value[1].IsCurrent);
    }

    [TestMethod]
    public async Task ActivationFailure_FallsBackToLimitedModeWithStructuredReason()
    {
        NativeBridgeError error = new(
            "native.shell_activation",
            "ShellActivation",
            "Shell activation was denied.",
            unchecked((int)0x80070005));
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(error)));

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(Build26200);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.AreEqual(
            VirtualDesktopCapabilities.DocumentedLimited,
            provider.Capabilities);
        Assert.IsNotNull(provider.LastFallback);
        Assert.AreEqual("windows.shell.25h2.26200", provider.LastFallback.RequestedProviderId);
        Assert.AreEqual("native.shell_activation", provider.LastFallback.Error.Code);
        Assert.AreEqual(error.HResult, provider.LastFallback.Error.HResult);
    }

    [TestMethod]
    [DataRow(22631, "windows.shell.23h2.22631")]
    [DataRow(26100, "windows.shell.24h2.26100")]
    [DataRow(26200, "windows.shell.25h2.26200")]
    [DataRow(28000, "windows.shell.26h1.28000")]
    public async Task BuildSelection_PreservesExactFamilyInFallback(
        int buildNumber,
        string expectedProviderId)
    {
        int requestedBuild = 0;
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(build =>
            {
                requestedBuild = build;
                return NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                    new NativeBridgeError(
                        "native.build_selection",
                        "BuildSelection",
                        "The adapter is not release-validated.",
                        unchecked((int)0x80040201)));
            }));
        WindowsBuildInfo buildInfo = new(
            true,
            10,
            0,
            buildNumber,
            1,
            Architecture.X64);

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(buildInfo);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(buildNumber, requestedBuild);
        Assert.AreEqual(expectedProviderId, provider.LastFallback?.RequestedProviderId);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
    }

    [TestMethod]
    public async Task ValidationFailure_DisposesUntrustedAdapterAndFallsBack()
    {
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            ValidationResult = NativeBridgeResult.Failed(
                new NativeBridgeError(
                    "native.behavior_validation",
                    "BehaviorValidation",
                    "The current desktop was absent from the inventory.",
                    unchecked((int)0x80040202))),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(Build26200);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.IsTrue(bridge.IsDisposed);
        Assert.AreEqual(
            "native.behavior_validation",
            provider.LastFallback?.Error.Code);
    }

    [TestMethod]
    public async Task NotificationRegistrationFailure_FallsBackAndPublishesProviderChange()
    {
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            NotificationResult = NativeBridgeResult.Failed(
                new NativeBridgeError(
                    "native.notification_registration",
                    "NotificationRegistration",
                    "Registration failed.",
                    unchecked((int)0x80004005))),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        DesktopTopologyChangedEventArgs? change = null;
        provider.TopologyChanged += (_, args) => change = args;

        DesktopTopologyProviderResult result =
            await provider.TestCompatibilityAsync(Build26200);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.IsNotNull(provider.LastFallback);
        Assert.AreEqual("ProviderFallbackActivated", change?.Reason);
        Assert.IsTrue(bridge.IsDisposed);
    }

    [TestMethod]
    public async Task TopologyNotification_IsProjectedWithoutAnyMutatingDesktopCall()
    {
        FakeNativeBridge bridge = new(CreateSnapshot());
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);
        string? reason = null;
        provider.TopologyChanged += (_, args) => reason = args.Reason;

        bridge.RaiseTopologyChanged("Created");

        Assert.AreEqual("Created", reason);
        Assert.AreEqual(0, bridge.MutationCount);
    }

    [TestMethod]
    public async Task ValidatedAdapter_CreatesDesktopAndStillRejectsSwitch()
    {
        Guid createdDesktopId =
            Guid.Parse("33333333-3333-3333-3333-333333333333");
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            CreationResult = NativeBridgeResult<Guid>.Succeeded(createdDesktopId),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<Guid> create =
            await provider.CreateDesktopAsync();
        DesktopTopologyProviderResult switchResult =
            await provider.SwitchDesktopAsync(Guid.NewGuid());

        Assert.IsTrue(create.IsSuccess);
        Assert.AreEqual(createdDesktopId, create.Value);
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, switchResult.Outcome);
        Assert.AreEqual("desktop_topology.switch_not_implemented", switchResult.Error?.Code);
        Assert.AreEqual(1, bridge.MutationCount);
    }

    [TestMethod]
    public async Task CreationFailure_IsStructuredWithoutDiscardingValidatedInventory()
    {
        NativeBridgeError creationError = new(
            "native.desktop_creation",
            "DesktopCreation",
            "The Windows Shell rejected desktop creation.",
            unchecked((int)0x80004005));
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            CreationResult = NativeBridgeResult<Guid>.Failed(creationError),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<Guid> result =
            await provider.CreateDesktopAsync();

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(creationError.Code, result.Error?.Code);
        Assert.AreEqual(creationError.HResult, result.Error?.HResult);
        Assert.AreEqual(DesktopTopologyProviderMode.Full, provider.Identity.Mode);
        Assert.IsNull(provider.LastFallback);
        Assert.IsFalse(bridge.IsDisposed);
        Assert.AreEqual(1, bridge.MutationCount);
    }

    [TestMethod]
    public async Task LimitedMode_ReportsCreationAsUnsupportedWithoutNativeMutation()
    {
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            ValidationResult = NativeBridgeResult.Failed(
                new NativeBridgeError(
                    "native.behavior_validation",
                    "BehaviorValidation",
                    "Validation failed.",
                    unchecked((int)0x80004005))),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<Guid> result =
            await provider.CreateDesktopAsync();

        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, result.Outcome);
        Assert.AreEqual(0, bridge.MutationCount);
    }

    [TestMethod]
    public void NativeCreationStage_MapsToStableStructuredProviderCode()
    {
        NativeMethods.NativeError nativeError = new()
        {
            HResult = unchecked((int)0x80004005),
            Stage = NativeMethods.NativeStage.DesktopCreation,
            Message = "Creation failed.",
        };

        NativeBridgeError result =
            ShellNativeVirtualDesktopBridgeFactory.ToError(
                nativeError,
                nativeError.HResult,
                "fallback");

        Assert.AreEqual("native.desktop_creation", result.Code);
        Assert.AreEqual("DesktopCreation", result.Stage);
        Assert.AreEqual(nativeError.HResult, result.HResult);
    }

    [TestMethod]
    public async Task Coordinator_UsesPostValidationIdentityAndReportsFallbackDiagnostic()
    {
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                    new NativeBridgeError(
                        "native.build_selection",
                        "BuildSelection",
                        "The adapter is not release-validated.",
                        unchecked((int)0x80040201)))));
        CompatibilityCoordinator coordinator = new(
            new StubBuildInfoProvider(Build26200),
            provider);

        CompatibilityTestResult result =
            await coordinator.RunCompatibilityTestAsync();

        Assert.AreEqual(CompatibilityTestOutcome.PassedLimitedMode, result.Outcome);
        Assert.AreEqual(
            DesktopTopologyProviderMode.Limited,
            coordinator.Current.Provider.Identity.Mode);
        Assert.IsTrue(
            result.Diagnostics.Any(
                static diagnostic =>
                    diagnostic.EventName == "Compatibility.ProviderFallbackActivated" &&
                    diagnostic.Properties["requestedProviderId"] ==
                    "windows.shell.25h2.26200"));
    }

    private static ValidatedVirtualDesktopTopologyProvider CreateProvider(
        INativeVirtualDesktopBridgeFactory bridgeFactory) =>
        new(new LimitedVirtualDesktopTopologyService(), bridgeFactory);

    private static NativeDesktopSnapshot CreateSnapshot()
    {
        Guid code = Guid.Parse("11111111-1111-1111-1111-111111111111");
        Guid web = Guid.Parse("22222222-2222-2222-2222-222222222222");
        return new NativeDesktopSnapshot(
            [
                new NativeDesktopDescriptor(web, "Web", 1),
                new NativeDesktopDescriptor(code, "Code", 0),
            ],
            web);
    }

    private sealed class FakeNativeBridgeFactory(
        Func<int, NativeBridgeResult<INativeVirtualDesktopBridge>> create)
        : INativeVirtualDesktopBridgeFactory
    {
        public NativeBridgeResult<INativeVirtualDesktopBridge> TryCreate(int windowsBuild) =>
            create(windowsBuild);
    }

    private sealed class FakeNativeBridge(NativeDesktopSnapshot snapshot)
        : INativeVirtualDesktopBridge
    {
        private Action<string>? topologyChanged;

        public NativeBridgeResult ValidationResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult NotificationResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult<Guid> CreationResult { get; init; } =
            NativeBridgeResult<Guid>.Succeeded(
                Guid.Parse("33333333-3333-3333-3333-333333333333"));

        public int ValidationCount { get; private set; }

        public int MutationCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public NativeBridgeResult Validate()
        {
            ValidationCount++;
            return ValidationResult;
        }

        public NativeBridgeResult<NativeDesktopSnapshot> ReadSnapshot() =>
            NativeBridgeResult<NativeDesktopSnapshot>.Succeeded(snapshot);

        public NativeBridgeResult<Guid> CreateDesktop()
        {
            MutationCount++;
            return CreationResult;
        }

        public NativeBridgeResult StartNotifications(Action<string> onTopologyChanged)
        {
            topologyChanged = onTopologyChanged;
            return NotificationResult;
        }

        public void RaiseTopologyChanged(string reason) =>
            topologyChanged?.Invoke(reason);

        public void Dispose() => IsDisposed = true;
    }

    private sealed class StubBuildInfoProvider(WindowsBuildInfo build)
        : IWindowsBuildInfoProvider
    {
        public WindowsBuildInfo GetCurrent() => build;
    }
}

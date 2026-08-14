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
        Assert.IsTrue(provider.Capabilities.CanSwitchDesktop);
        Assert.IsTrue(provider.Capabilities.CanReorderDesktop);
        Assert.IsNull(provider.LastFallback);
        Assert.AreEqual(1, bridge.ValidationCount);
    }

    [TestMethod]
    public async Task ValidatedExtendedLayout_ReordersTheRequestedDesktop()
    {
        Guid desktopId = Guid.Parse("22222222-2222-2222-2222-222222222222");
        FakeNativeBridge bridge = new(CreateSnapshot());
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult result =
            await provider.MoveDesktopAsync(desktopId, targetPosition: 0);

        Assert.IsTrue(result.IsSuccess, result.Error?.Message);
        Assert.AreEqual(desktopId, bridge.LastReorderedDesktopId);
        Assert.AreEqual(0, bridge.LastReorderPosition);
        Assert.AreEqual(1, bridge.ReorderCount);
    }

    [TestMethod]
    public async Task FailedExtendedLayoutProbe_LeavesReorderingUnsupported()
    {
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            ProbeResult = NativeBridgeResult.Failed(
                new NativeBridgeError(
                    "test.layout_shifted",
                    "LayoutProbe",
                    "The manager layout did not match.",
                    unchecked((int)0x80004005))),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult result =
            await provider.MoveDesktopAsync(Guid.NewGuid(), targetPosition: 0);

        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, result.Outcome);
        Assert.IsFalse(provider.Capabilities.CanReorderDesktop);
        Assert.AreEqual(0, bridge.ReorderCount);
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
    public async Task ValidatedAdapter_CreatesAndSwitchesDesktops()
    {
        Guid createdDesktopId =
            Guid.Parse("33333333-3333-3333-3333-333333333333");
        Guid targetDesktopId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
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
            await provider.SwitchDesktopAsync(targetDesktopId);

        Assert.IsTrue(create.IsSuccess);
        Assert.AreEqual(createdDesktopId, create.Value);
        Assert.IsTrue(switchResult.IsSuccess);
        Assert.AreEqual(targetDesktopId, bridge.LastSwitchedDesktopId);
        Assert.AreEqual(2, bridge.MutationCount);
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
    public async Task SwitchFailure_IsStructuredWithoutDiscardingValidatedInventory()
    {
        NativeBridgeError switchError = new(
            "native.desktop_switch",
            "DesktopSwitch",
            "The Windows Shell rejected desktop switching.",
            unchecked((int)0x80004005));
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            SwitchResult = NativeBridgeResult.Failed(switchError),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult result =
            await provider.SwitchDesktopAsync(
                Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(switchError.Code, result.Error?.Code);
        Assert.AreEqual(switchError.HResult, result.Error?.HResult);
        Assert.AreEqual(DesktopTopologyProviderMode.Full, provider.Identity.Mode);
        Assert.IsNull(provider.LastFallback);
        Assert.IsFalse(bridge.IsDisposed);
        Assert.AreEqual(1, bridge.MutationCount);
    }

    [TestMethod]
    public async Task WindowMove_UsesValidatedBridgeAndKeepsOperationalFailureStructured()
    {
        nint windowHandle = (nint)0x1234;
        Guid desktopId =
            Guid.Parse("11111111-1111-1111-1111-111111111111");
        NativeBridgeError moveError = new(
            "native.window_move",
            "WindowMove",
            "The Windows Shell rejected the window move.",
            unchecked((int)0x80070005));
        FakeNativeBridge bridge = new(CreateSnapshot())
        {
            MoveResult = NativeBridgeResult.Failed(moveError),
        };
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(bridge)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        NativeBridgeResult result =
            ((IValidatedWindowDesktopMover)provider)
                .MoveWindowToDesktop(windowHandle, desktopId);

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual(moveError, result.Error);
        Assert.AreEqual(windowHandle, bridge.LastMovedWindowHandle);
        Assert.AreEqual(desktopId, bridge.LastMovedDesktopId);
        Assert.AreEqual(DesktopTopologyProviderMode.Full, provider.Identity.Mode);
        Assert.IsNull(provider.LastFallback);
        Assert.IsFalse(bridge.IsDisposed);
    }

    [TestMethod]
    public async Task WindowMove_AfterRevalidationUsesReplacementBridge()
    {
        FakeNativeBridge first = new(CreateSnapshot());
        FakeNativeBridge second = new(CreateSnapshot());
        int activationCount = 0;
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    activationCount++ == 0 ? first : second)));

        _ = await provider.TestCompatibilityAsync(Build26200);
        _ = await provider.TestCompatibilityAsync(Build26200);
        _ = ((IValidatedWindowDesktopMover)provider).MoveWindowToDesktop(
            (nint)0x2468,
            Guid.Parse("11111111-1111-1111-1111-111111111111"));

        Assert.IsTrue(first.IsDisposed);
        Assert.AreEqual(0, first.MoveCount);
        Assert.AreEqual(1, second.MoveCount);
    }

    [TestMethod]
    public async Task WindowMove_SerializesAdapterReplacementUntilMoveCompletes()
    {
        using ManualResetEventSlim moveEntered = new();
        using ManualResetEventSlim releaseMove = new();
        using ManualResetEventSlim replacementValidated = new();
        FakeNativeBridge first = new(CreateSnapshot())
        {
            MoveEntered = moveEntered,
            ReleaseMove = releaseMove,
        };
        FakeNativeBridge second = new(CreateSnapshot())
        {
            ValidationEntered = replacementValidated,
        };
        int activationCount = 0;
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    activationCount++ == 0 ? first : second)));
        _ = await provider.TestCompatibilityAsync(Build26200);

        Task<NativeBridgeResult> move = Task.Run(
            () => ((IValidatedWindowDesktopMover)provider)
                .MoveWindowToDesktop(
                    (nint)0x1357,
                    Guid.Parse("11111111-1111-1111-1111-111111111111")));
        Assert.IsTrue(moveEntered.Wait(TimeSpan.FromSeconds(5)));

        Task<DesktopTopologyProviderResult> replacement = Task.Run(
            async () => await provider.TestCompatibilityAsync(Build26200));
        Assert.IsTrue(replacementValidated.Wait(TimeSpan.FromSeconds(5)));
        Assert.IsFalse(first.IsDisposed);
        Assert.IsFalse(replacement.IsCompleted);

        releaseMove.Set();
        _ = await move;
        _ = await replacement;

        Assert.IsTrue(first.IsDisposed);
        Assert.AreEqual(1, first.MoveCount);
        Assert.AreEqual(0, second.MoveCount);
    }

    [TestMethod]
    public void WindowMove_InLimitedModeReturnsUnavailableWithoutNativeCall()
    {
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                    new NativeBridgeError(
                        "test.unexpected_activation",
                        "Test",
                        "The factory must not be called.",
                        unchecked((int)0x8000FFFF)))));

        NativeBridgeResult result =
            ((IValidatedWindowDesktopMover)provider).MoveWindowToDesktop(
                (nint)0x1234,
                Guid.NewGuid());

        Assert.IsFalse(result.IsSuccess);
        Assert.AreEqual("native.window_move_unavailable", result.Error?.Code);
        Assert.AreEqual(unchecked((int)0x80070032), result.Error?.HResult);
    }

    [TestMethod]
    public async Task LimitedMode_ReportsMutationsAsUnsupportedWithoutNativeCall()
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
        DesktopTopologyProviderResult switchResult =
            await provider.SwitchDesktopAsync(Guid.NewGuid());

        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, result.Outcome);
        Assert.AreEqual(
            DesktopTopologyResultOutcome.Unsupported,
            switchResult.Outcome);
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
    public void NativeSwitchStage_MapsToStableStructuredProviderCode()
    {
        NativeMethods.NativeError nativeError = new()
        {
            HResult = unchecked((int)0x80004005),
            Stage = NativeMethods.NativeStage.DesktopSwitch,
            Message = "Switch failed.",
        };

        NativeBridgeError result =
            ShellNativeVirtualDesktopBridgeFactory.ToError(
                nativeError,
                nativeError.HResult,
                "fallback");

        Assert.AreEqual("native.desktop_switch", result.Code);
        Assert.AreEqual("DesktopSwitch", result.Stage);
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

    /// <summary>
    /// A shell that went away mid-call. This is the exact code Windows returns
    /// once Explorer has been killed and the COM proxies the adapter holds have
    /// become stale.
    /// </summary>
    private static NativeBridgeError DeadShellError() => new(
        "native.enumeration",
        "Enumeration",
        "The virtual-desktop count probe failed.",
        unchecked((int)0x800706BA));

    [TestMethod]
    public async Task DeadShellDuringEnumeration_RebuildsTheAdapterInsteadOfDemoting()
    {
        FakeNativeBridge dead = new(CreateSnapshot())
        {
            SnapshotResult =
                NativeBridgeResult<NativeDesktopSnapshot>.Failed(DeadShellError()),
        };
        FakeNativeBridge restarted = new(CreateSnapshot());
        Queue<FakeNativeBridge> bridges = new([dead, restarted]);
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    bridges.Dequeue())));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        Assert.IsTrue(result.IsSuccess);
        Assert.HasCount(2, result.Value!);
        Assert.AreEqual(DesktopTopologyProviderMode.Full, provider.Identity.Mode);
        Assert.IsNull(provider.LastFallback);
        Assert.AreEqual(1, provider.ReconnectCount);
        Assert.IsTrue(dead.IsDisposed);
        Assert.IsFalse(restarted.IsDisposed);

        // The rebuild proves what activation proves, and does it once.
        Assert.AreEqual(1, restarted.ValidationCount);
    }

    [TestMethod]
    public async Task ProviderRebuild_AnnouncesThatHeldDesktopIdsMustBeLookedUpAgain()
    {
        FakeNativeBridge dead = new(CreateSnapshot())
        {
            SnapshotResult =
                NativeBridgeResult<NativeDesktopSnapshot>.Failed(DeadShellError()),
        };
        Queue<FakeNativeBridge> bridges =
            new([dead, new FakeNativeBridge(CreateSnapshot())]);
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    bridges.Dequeue())));
        _ = await provider.TestCompatibilityAsync(Build26200);
        List<string> reasons = [];
        provider.TopologyChanged += (_, args) => reasons.Add(args.Reason);

        _ = await provider.EnumerateDesktopsAsync();

        Assert.Contains("ProviderReconnected", reasons);
    }

    [TestMethod]
    public async Task RefusalThatIsNotATransportFailure_StillDemotesToLimitedMode()
    {
        NativeBridgeError refusal = new(
            "native.enumeration",
            "Enumeration",
            "The shell refused the inventory probe.",
            unchecked((int)0x80004005));
        int activations = 0;
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
            {
                activations++;
                return NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    new FakeNativeBridge(CreateSnapshot())
                    {
                        SnapshotResult =
                            NativeBridgeResult<NativeDesktopSnapshot>.Failed(refusal),
                    });
            }));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.IsNotNull(provider.LastFallback);
        Assert.AreEqual(0, provider.ReconnectCount);
        Assert.AreEqual(1, activations);
    }

    [TestMethod]
    public async Task ShellThatStaysDead_DemotesAfterOneRebuildRatherThanRetrying()
    {
        int activations = 0;
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
            {
                activations++;
                return NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    new FakeNativeBridge(CreateSnapshot())
                    {
                        SnapshotResult = NativeBridgeResult<NativeDesktopSnapshot>
                            .Failed(DeadShellError()),
                    });
            }));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.IsNotNull(provider.LastFallback);
        Assert.AreEqual(unchecked((int)0x800706BA), provider.LastFallback.Error.HResult);
        Assert.AreEqual(1, provider.ReconnectCount);
        Assert.AreEqual(2, activations);
    }

    [TestMethod]
    public async Task RebuildThatCannotValidate_LeavesTheProviderInLimitedMode()
    {
        FakeNativeBridge dead = new(CreateSnapshot())
        {
            SnapshotResult =
                NativeBridgeResult<NativeDesktopSnapshot>.Failed(DeadShellError()),
        };
        FakeNativeBridge unusable = new(CreateSnapshot())
        {
            ValidationResult = NativeBridgeResult.Failed(
                new NativeBridgeError(
                    "native.behavior_validation",
                    "BehaviorValidation",
                    "The restarted shell did not satisfy the contract.",
                    unchecked((int)0x80040201))),
        };
        Queue<FakeNativeBridge> bridges = new([dead, unusable]);
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    bridges.Dequeue())));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        Assert.AreEqual(DesktopTopologyResultOutcome.Failed, result.Outcome);
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.AreEqual(0, provider.ReconnectCount);
        Assert.IsTrue(unusable.IsDisposed);
    }

    /// <summary>
    /// A rebuild that just succeeded must not be attempted again on the very
    /// next failure. Foreground bursts produce many calls per second, and one
    /// full COM activation each would be its own outage.
    /// </summary>
    [TestMethod]
    public async Task SecondFailureInsideTheCooldown_DoesNotRebuildAgain()
    {
        FakeNativeBridge dead = new(CreateSnapshot())
        {
            SnapshotResult =
                NativeBridgeResult<NativeDesktopSnapshot>.Failed(DeadShellError()),
        };
        FakeNativeBridge alsoDead = new(CreateSnapshot())
        {
            SnapshotResult =
                NativeBridgeResult<NativeDesktopSnapshot>.Failed(DeadShellError()),
        };
        int activations = 0;
        Queue<FakeNativeBridge> bridges = new([dead, alsoDead]);
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
            {
                activations++;
                return NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    bridges.Count > 0
                        ? bridges.Dequeue()
                        : new FakeNativeBridge(CreateSnapshot()));
            }));
        _ = await provider.TestCompatibilityAsync(Build26200);

        _ = await provider.EnumerateDesktopsAsync();
        _ = await provider.EnumerateDesktopsAsync();
        _ = await provider.EnumerateDesktopsAsync();

        // One activation for the compatibility test, one for the single
        // permitted rebuild, and none for the calls that followed it.
        Assert.AreEqual(2, activations);
        Assert.AreEqual(1, provider.ReconnectCount);
    }

    [TestMethod]
    public async Task DeadShellDuringSwitch_RebuildsAndCompletesTheSwitch()
    {
        Guid target = Guid.Parse("11111111-1111-1111-1111-111111111111");
        FakeNativeBridge dead = new(CreateSnapshot())
        {
            SwitchResult = NativeBridgeResult.Failed(DeadShellError()),
        };
        FakeNativeBridge restarted = new(CreateSnapshot());
        Queue<FakeNativeBridge> bridges = new([dead, restarted]);
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                    bridges.Dequeue())));
        _ = await provider.TestCompatibilityAsync(Build26200);

        DesktopTopologyProviderResult result =
            await provider.SwitchDesktopAsync(target);

        Assert.IsTrue(result.IsSuccess);
        Assert.AreEqual(target, restarted.LastSwitchedDesktopId);
        Assert.AreEqual(1, provider.ReconnectCount);
    }

    [TestMethod]
    public async Task TransportFailureWithoutAnEarlierFullMode_NeverRebuilds()
    {
        using ValidatedVirtualDesktopTopologyProvider provider = CreateProvider(
            new FakeNativeBridgeFactory(_ =>
                NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                    DeadShellError())));

        _ = await provider.TestCompatibilityAsync(Build26200);
        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
            await provider.EnumerateDesktopsAsync();

        // Limited Mode answers from the documented provider, and no adapter this
        // provider never validated may be activated behind the caller's back.
        Assert.AreEqual(DesktopTopologyProviderMode.Limited, provider.Identity.Mode);
        Assert.AreEqual(0, provider.ReconnectCount);
        Assert.AreEqual(DesktopTopologyResultOutcome.Unsupported, result.Outcome);
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

        public NativeBridgeResult SwitchResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult MoveResult { get; init; } =
            NativeBridgeResult.Succeeded;

        /// <summary>
        /// What the read-only vtable layout probe answers.
        /// </summary>
        /// <remarks>
        /// Defaults to success so existing tests describe a build that can be
        /// named on. A test that wants the opposite sets this and asserts that
        /// naming, and only naming, went away.
        /// </remarks>
        public NativeBridgeResult ProbeResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult RenameResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public NativeBridgeResult DesktopReorderResult { get; init; } =
            NativeBridgeResult.Succeeded;

        public ManualResetEventSlim? ValidationEntered { get; init; }

        public ManualResetEventSlim? MoveEntered { get; init; }

        public ManualResetEventSlim? ReleaseMove { get; init; }

        public int ValidationCount { get; private set; }

        public int MutationCount { get; private set; }

        public bool IsDisposed { get; private set; }

        public Guid LastSwitchedDesktopId { get; private set; }

        public nint LastMovedWindowHandle { get; private set; }

        public Guid LastMovedDesktopId { get; private set; }

        public int MoveCount { get; private set; }

        public int ReorderCount { get; private set; }

        public int ProbeCount { get; private set; }

        public int SnapshotCount { get; private set; }

        /// <summary>
        /// What the inventory read answers, or null to answer with the snapshot
        /// this bridge was built around.
        /// </summary>
        /// <remarks>
        /// Defaults to null so every existing test keeps describing a healthy
        /// adapter. A test that wants a dead shell sets a transport failure here
        /// and asserts on what the provider does about it.
        /// </remarks>
        public NativeBridgeResult<NativeDesktopSnapshot>? SnapshotResult { get; init; }

        public Guid LastRenamedDesktopId { get; private set; }

        public string? LastRequestedName { get; private set; }

        public Guid LastReorderedDesktopId { get; private set; }

        public int LastReorderPosition { get; private set; }

        public NativeBridgeResult Validate()
        {
            ValidationEntered?.Set();
            ValidationCount++;
            return ValidationResult;
        }

        public NativeBridgeResult<NativeDesktopSnapshot> ReadSnapshot()
        {
            SnapshotCount++;
            return SnapshotResult ??
                NativeBridgeResult<NativeDesktopSnapshot>.Succeeded(snapshot);
        }

        public NativeBridgeResult<Guid> CreateDesktop()
        {
            MutationCount++;
            return CreationResult;
        }

        public NativeBridgeResult SwitchDesktop(Guid desktopId)
        {
            MutationCount++;
            LastSwitchedDesktopId = desktopId;
            return SwitchResult;
        }

        // Looking a desktop up changes nothing, so this deliberately stays out
        // of MutationCount. A probe that showed up as a mutation would hide the
        // very thing that count exists to prove.
        public NativeBridgeResult ProbeDesktopLookup()
        {
            ProbeCount++;
            return ProbeResult;
        }

        public NativeBridgeResult SetDesktopName(Guid desktopId, string name)
        {
            MutationCount++;
            LastRenamedDesktopId = desktopId;
            LastRequestedName = name;
            return RenameResult;
        }

        public NativeBridgeResult MoveDesktop(Guid desktopId, int targetPosition)
        {
            MutationCount++;
            ReorderCount++;
            LastReorderedDesktopId = desktopId;
            LastReorderPosition = targetPosition;
            return DesktopReorderResult;
        }

        public NativeBridgeResult MoveWindowToDesktop(
            nint windowHandle,
            Guid desktopId)
        {
            MoveEntered?.Set();
            if (ReleaseMove is not null &&
                !ReleaseMove.Wait(TimeSpan.FromSeconds(5)))
            {
                return NativeBridgeResult.Failed(
                    new NativeBridgeError(
                        "test.move_timeout",
                        "Test",
                        "The test did not release the blocked move.",
                        unchecked((int)0x800705B4)));
            }

            MutationCount++;
            MoveCount++;
            LastMovedWindowHandle = windowHandle;
            LastMovedDesktopId = desktopId;
            return MoveResult;
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

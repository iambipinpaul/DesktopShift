using System.Runtime.InteropServices;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Assignments;

[TestClass]
public sealed class RemoteDesktopAssignmentAcceptanceTests
{
    private const string SessionWindowClass = "TscShellContainerClass";
    private const string DialogWindowClass = "#32770";
    private const string RemoteDesktopProcessName = "mstsc.exe";
    private const string CredentialBrokerProcessName = "CredentialUIBroker.exe";
    // Named by no shipped rule at all — not even an Anywhere one — so it is
    // genuinely unmanaged rather than deliberately exempt.
    private const string UnrelatedProcessName = "mspaint.exe";
    private const string UnrelatedWindowClass = "MSPaintApp";

    private static readonly Guid RemoteDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();
    private static readonly Guid FirstDesktopId = Guid.NewGuid();
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NewRemoteDesktopWindow_IsAssignedToRemote()
    {
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(901);
        harness.Placement.SetCurrent(901, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(200, WindowEventKind.Created, (nint)901, Now));

        Assert.AreEqual(WindowObservationOutcome.Matched, observation.Outcome);
        Assert.AreEqual("remote", observation.RuleId);
        Assert.AreEqual("remote", observation.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, observation.MatchedOn);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            observation.Assignment!.Outcome);
        Assert.AreEqual(RemoteDesktopId, observation.Assignment.TargetDesktopId);
        Assert.AreEqual(
            WindowMoveOutcome.Succeeded,
            observation.Assignment.MoveOutcome);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual((nint)901, harness.Placement.Moves[0].WindowHandle);
        Assert.AreEqual(RemoteDesktopId, harness.Placement.Moves[0].DesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task SimultaneousSessions_AreAssignedIndependently()
    {
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(911);
        harness.IdentityResolver.SetRemoteDesktopSession(912);
        harness.IdentityResolver.SetRemoteDesktopSession(913);
        harness.Placement.SetCurrent(911, OtherDesktopId);
        harness.Placement.SetCurrent(912, OtherDesktopId);
        harness.Placement.SetCurrent(913, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity first = await processor.ProcessAsync(
            new WindowEvent(210, WindowEventKind.Created, (nint)911, Now));
        WindowObservationActivity second = await processor.ProcessAsync(
            new WindowEvent(211, WindowEventKind.Created, (nint)912, Now));
        WindowObservationActivity third = await processor.ProcessAsync(
            new WindowEvent(212, WindowEventKind.Shown, (nint)913, Now));

        WindowAssignmentActivity[] assignments =
        [
            first.Assignment!,
            second.Assignment!,
            third.Assignment!,
        ];
        Assert.IsTrue(assignments.All(
            static assignment =>
                assignment.Outcome == WindowAssignmentOutcome.Succeeded &&
                assignment.RuleId == "remote" &&
                assignment.TargetDesktopId == RemoteDesktopId));

        // Three sessions, three correlations, three moves. One session never
        // stands in for another.
        Assert.HasCount(
            3,
            assignments
                .Select(static assignment => assignment.CorrelationId)
                .Distinct()
                .ToArray());
        nint[] movedHandles = harness.Placement.Moves
            .Select(static move => move.WindowHandle)
            .ToArray();
        Assert.HasCount(3, movedHandles);
        Assert.HasCount(3, movedHandles.Distinct().ToArray());
        Assert.Contains((nint)911, movedHandles);
        Assert.Contains((nint)912, movedHandles);
        Assert.Contains((nint)913, movedHandles);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task OwnedReconnectDialog_FollowsItsSessionWindow()
    {
        // A reconnection dialog is owned by the session frame. The classifier
        // resolves ownership before identity, so the event that named the
        // dialog is answered on behalf of the frame: the frame's handle is what
        // moves, and the dialog is carried along by Windows because it is owned.
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(921);
        harness.Classifier.SetOwner(922, 921);
        harness.Placement.SetCurrent(921, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity dialog = await processor.ProcessAsync(
            new WindowEvent(220, WindowEventKind.Created, (nint)922, Now));
        WindowObservationActivity session = await processor.ProcessAsync(
            new WindowEvent(
                221,
                WindowEventKind.Shown,
                (nint)921,
                Now.AddSeconds(30)));

        Assert.AreEqual("remote", dialog.RuleId);
        Assert.AreEqual("remote", session.RuleId);

        // The event named the dialog; Activity, identity and the move all name
        // the frame that owns it.
        Assert.AreEqual((nint)921, dialog.WindowHandle);
        Assert.AreEqual((nint)921, dialog.Assignment!.WindowHandle);
        Assert.AreEqual(RemoteDesktopProcessName, dialog.Identity!.ProcessName);
        Assert.AreEqual(SessionWindowClass, dialog.Identity.WindowClass);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual((nint)921, harness.Placement.Moves[0].WindowHandle);
        Assert.AreEqual(RemoteDesktopId, harness.Placement.Moves[0].DesktopId);
        Assert.DoesNotContain(
            (nint)922,
            harness.Placement.Moves
                .Select(static move => move.WindowHandle)
                .ToArray());

        // The frame's own event finds it already placed: the dialog answered on
        // its behalf, so nothing moves twice.
        Assert.AreEqual(
            WindowMoveOutcome.AlreadyCorrect,
            session.Assignment!.MoveOutcome);
        Assert.AreEqual(1, harness.Placement.MoveAttemptCount);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task OwnedCredentialPromptFromAnotherProcess_FollowsItsSession()
    {
        // Windows hosts some credential UI in CredentialUIBroker.exe. The
        // prompt is still owned by the session frame, and ownership decides:
        // the broker's own identity never reaches the matcher.
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(931);
        harness.Classifier.SetOwner(932, 931);
        harness.Placement.SetCurrent(931, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity prompt = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(230, WindowEventKind.Shown, (nint)932, Now));

        Assert.AreEqual(WindowObservationOutcome.Matched, prompt.Outcome);
        Assert.AreEqual("remote", prompt.RuleId);
        Assert.AreEqual((nint)931, prompt.WindowHandle);
        Assert.AreEqual(RemoteDesktopProcessName, prompt.Identity!.ProcessName);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            prompt.Assignment!.Outcome);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual((nint)931, harness.Placement.Moves[0].WindowHandle);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task UnownedCredentialBrokerWindow_IsNeverGuessedOntoRemote()
    {
        // Without an owner there is nothing tying the broker window to a
        // session, so putting it on Remote would be a guess. It is not guessed
        // at — but it is not abandoned either: no rule names it, so it is swept
        // to the first desktop like any other unmanaged window.
        RemoteDesktopHarness harness = new();
        harness.Classifier.SetWindowClass(941, DialogWindowClass);
        harness.IdentityResolver.SetProcess(941, CredentialBrokerProcessName);
        harness.Placement.SetCurrent(941, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(240, WindowEventKind.Shown, (nint)941, Now));

        Assert.AreEqual(WindowObservationOutcome.Matched, observation.Outcome);
        Assert.AreEqual(UnmanagedWindowSweep.RuleId, observation.RuleId);
        Assert.AreEqual(FirstDesktopId, observation.Assignment!.TargetDesktopId);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            observation.Assignment.Outcome);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual(FirstDesktopId, harness.Placement.Moves[0].DesktopId);
        Assert.AreNotEqual(RemoteDesktopId, harness.Placement.Moves[0].DesktopId);

        // Tidied away, but the user stays where they are. A broker window
        // appears on its own account and nothing activated this one, so being
        // dragged to another desktop by it would interrupt whatever they were
        // actually doing.
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
        Assert.AreEqual(OtherDesktopId, harness.Topology.CurrentDesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task ForegroundActivation_SwitchesOnceAndDoesNotLoop()
    {
        // The switch DesktopShift performs raises a foreground event for the
        // same window. Answering that event with another switch is the focus
        // loop this suppression exists to prevent, and a later genuine
        // activation must still be honored.
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(951);
        harness.Placement.SetCurrent(951, OtherDesktopId);
        harness.Topology.CurrentDesktopId = OtherDesktopId;
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity activated = await processor.ProcessAsync(
            new WindowEvent(
                250,
                WindowEventKind.ForegroundActivated,
                (nint)951,
                Now));
        WindowObservationActivity selfGenerated = await processor.ProcessAsync(
            new WindowEvent(
                251,
                WindowEventKind.ForegroundActivated,
                (nint)951,
                Now.AddMilliseconds(1)));
        harness.Topology.CurrentDesktopId = OtherDesktopId;
        WindowObservationActivity laterGenuine = await processor.ProcessAsync(
            new WindowEvent(
                252,
                WindowEventKind.ForegroundActivated,
                (nint)951,
                Now.AddMilliseconds(500)));

        Assert.AreEqual(
            DesktopSwitchPolicy.OnForegroundActivation,
            activated.Assignment!.SwitchPolicy);
        Assert.AreEqual(
            DesktopSwitchOutcome.Succeeded,
            activated.Assignment.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchOutcome.Suppressed,
            selfGenerated.Assignment!.SwitchOutcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.SelfGeneratedForegroundSuppressed,
            selfGenerated.Assignment.SkipReason);
        Assert.AreEqual(
            activated.Assignment.CorrelationId,
            selfGenerated.Assignment.RelatedCorrelationId);
        Assert.AreEqual(
            DesktopSwitchOutcome.Succeeded,
            laterGenuine.Assignment!.SwitchOutcome);
        Assert.AreEqual(2, harness.Topology.SwitchCallCount);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task FullScreenSessionWindowsRefuses_IsReportedNotRetried()
    {
        // A full-screen session frame is a window Windows manages itself and may
        // refuse to move. The refusal is recorded once, with the code, message
        // and HRESULT Windows gave, and no retry is issued behind the user's
        // back. Each new event makes exactly one fresh attempt.
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(961);
        harness.Placement.SetCurrent(961, OtherDesktopId);
        harness.Placement.RefuseMovesFor(961);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity entered = await processor.ProcessAsync(
            new WindowEvent(260, WindowEventKind.Shown, (nint)961, Now));
        WindowObservationActivity repeated = await processor.ProcessAsync(
            new WindowEvent(
                261,
                WindowEventKind.ForegroundActivated,
                (nint)961,
                Now.AddSeconds(5)));

        Assert.AreEqual(
            WindowAssignmentOutcome.Failed,
            entered.Assignment!.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Failed, entered.Assignment.MoveOutcome);
        Assert.AreEqual(
            "window_placement.move_failed",
            entered.Assignment.Error!.Code);
        Assert.AreEqual(
            FakePlacementService.RefusedHResult,
            entered.Assignment.Error.HResult);
        Assert.IsFalse(string.IsNullOrWhiteSpace(entered.Assignment.Error.Message));
        Assert.AreEqual(
            WindowAssignmentOutcome.Failed,
            repeated.Assignment!.Outcome);

        // Two events, two attempts. A refusal never re-drives itself.
        Assert.AreEqual(2, harness.Placement.MoveAttemptCount);
        Assert.IsEmpty(harness.Placement.Moves);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task FullScreenExitRestoresANormalMove()
    {
        // Leaving full screen makes the frame movable again. Nothing had to be
        // remembered or retried: the next event simply succeeds.
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(971);
        harness.Placement.SetCurrent(971, OtherDesktopId);
        harness.Placement.RefuseMovesFor(971);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity refused = await processor.ProcessAsync(
            new WindowEvent(270, WindowEventKind.Shown, (nint)971, Now));
        harness.Placement.AllowMovesFor(971);
        WindowObservationActivity afterExit = await processor.ProcessAsync(
            new WindowEvent(
                271,
                WindowEventKind.Shown,
                (nint)971,
                Now.AddSeconds(10)));

        Assert.AreEqual(WindowMoveOutcome.Failed, refused.Assignment!.MoveOutcome);
        Assert.AreEqual(
            WindowMoveOutcome.Succeeded,
            afterExit.Assignment!.MoveOutcome);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual(RemoteDesktopId, harness.Placement.Moves[0].DesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task AccessDeniedMove_KeepsItsOwnFailureCodeInActivity()
    {
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(981);
        harness.Placement.SetCurrent(981, OtherDesktopId);
        harness.Placement.DenyAccessFor(981);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(280, WindowEventKind.Shown, (nint)981, Now));

        Assert.AreEqual(
            WindowAssignmentOutcome.Failed,
            observation.Assignment!.Outcome);
        Assert.AreEqual(
            "window_placement.move_access_denied",
            observation.Assignment.Error!.Code);
        Assert.AreEqual(
            FakePlacementService.AccessDeniedHResult,
            observation.Assignment.Error.HResult);
        Assert.AreEqual(1, harness.Placement.MoveAttemptCount);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task IdentityAccessDenial_IsUnderstandableAndMovesNothing()
    {
        // A session running elevated refuses to describe itself. The observation
        // says why, and no window is moved on a guess.
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.DenyAccessFor(991);
        harness.Placement.SetCurrent(991, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(290, WindowEventKind.Shown, (nint)991, Now));

        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(
            WindowSkipReason.IdentityAccessDenied,
            observation.SkipReason);
        Assert.AreEqual(5, observation.NativeErrorCode);
        Assert.IsNull(observation.Assignment);
        Assert.IsEmpty(harness.Placement.Moves);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task RemoteWindowAlreadyOnRemote_IsSkippedWithoutMoving()
    {
        RemoteDesktopHarness harness = new();
        harness.IdentityResolver.SetRemoteDesktopSession(995);
        harness.Placement.SetCurrent(995, RemoteDesktopId);
        harness.Topology.CurrentDesktopId = RemoteDesktopId;
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(295, WindowEventKind.Shown, (nint)995, Now));

        Assert.AreEqual("remote", observation.RuleId);
        Assert.AreEqual(
            WindowAssignmentOutcome.Skipped,
            observation.Assignment!.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            observation.Assignment.SkipReason);
        Assert.AreEqual(
            WindowMoveOutcome.AlreadyCorrect,
            observation.Assignment.MoveOutcome);
        Assert.AreEqual(0, harness.Placement.MoveAttemptCount);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task UnrelatedWindow_IsNeverDraggedOntoRemote()
    {
        RemoteDesktopHarness harness = new();
        harness.Classifier.SetWindowClass(996, UnrelatedWindowClass);
        harness.IdentityResolver.SetProcess(996, UnrelatedProcessName);
        harness.Placement.SetCurrent(996, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    296,
                    WindowEventKind.ForegroundActivated,
                    (nint)996,
                    Now));

        // The sweep never answers a foreground activation: a window must not be
        // moved out from under a click.
        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(
            WindowSkipReason.ActivationNotSwept,
            observation.SkipReason);
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);

        await host.StopAsync();
    }

    private static IHost CreateHost(RemoteDesktopHarness harness)
    {
        ConfigurationDocument configuration = ConfigurationDefaults.Create();

        return DesktopShiftHost.Create(services =>
        {
            services.AssignOpenedWindowsInline();
            services.AddSingleton<IConfigurationService>(
                new ActiveConfigurationService(configuration));
            services.AddSingleton<ICompatibilityCoordinator>(
                new ReadyCompatibilityCoordinator());
            services.AddSingleton<IManagedDesktopReconciliationService>(
                new RemoteBoundReconciliationService());
            services.AddSingleton<IDesktopTopologyProvider>(harness.Topology);
            services.AddSingleton<IWindowClassifier>(harness.Classifier);
            services.AddSingleton<IWindowIdentityResolver>(harness.IdentityResolver);
            services.AddSingleton<IWindowEventSource>(new NoopWindowEventSource());
            services.AddSingleton<IWindowDesktopPlacementService>(harness.Placement);
            services.AddSingleton<ITopLevelWindowEnumerator>(harness.Enumerator);
            services.AddDesktopShiftObservation();
            services.AddDesktopShiftAssignments();
        });
    }

    private sealed class RemoteDesktopHarness
    {
        public FakePlacementService Placement { get; } = new();

        public FakeTopologyProvider Topology { get; } = new()
        {
            CurrentDesktopId = OtherDesktopId,
        };

        public ITopLevelWindowEnumerator Enumerator { get; } =
            new FixedWindowEnumerator([]);

        public OwnershipAwareClassifier Classifier { get; } = new();

        public RemoteDesktopIdentityResolver IdentityResolver { get; } = new();
    }

    private sealed class ActiveConfigurationService(
        ConfigurationDocument configuration) : IConfigurationService
    {
        public ConfigurationState CurrentState { get; } = new(
            configuration,
            configuration,
            [],
            Now);

        public Task<ConfigurationState> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(CurrentState);
        }

        public Task<ConfigurationSaveResult> SaveCandidateAsync(
            ConfigurationDocument candidate,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();
    }

    private sealed class ReadyCompatibilityCoordinator : ICompatibilityCoordinator
    {
        private static readonly WindowsBuildInfo Build =
            new(true, 10, 0, 26200, 1, Architecture.X64);
        private static readonly DesktopTopologyProviderIdentity Identity =
            new(
                "test.full",
                "Test Full",
                "1",
                DesktopTopologyProviderMode.Full,
                UsesPrivateApis: false);
        private static readonly VirtualDesktopCapabilities Capabilities =
            new(true, true, true, true, true, false, true);

        public CompatibilityStatus Current { get; } = new(
            Build,
            WindowsBuildClassifier.Classify(Build),
            new DesktopTopologyProviderState(
                Identity,
                Capabilities,
                DesktopTopologyProviderAvailability.Ready,
                "Ready"),
            new CompatibilityTestResult(
                CompatibilityTestOutcome.PassedFullMode,
                Now,
                "Ready",
                []));

        public event EventHandler<CompatibilityStatusChangedEventArgs>? StatusChanged
        {
            add { }
            remove { }
        }

        public ValueTask<CompatibilityTestResult> RunCompatibilityTestAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current.LastTest);
    }

    private sealed class RemoteBoundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            Now,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "remote",
                "Remote",
                4,
                true,
                RemoteDesktopId,
                "Remote",
                3,
                ManagedDesktopMappingStatus.ReusedPersistedBinding,
                [],
                "test.bound",
                "Bound")],
            []);

        public event EventHandler<ManagedDesktopReconciliationChangedEventArgs>? Changed
        {
            add { }
            remove { }
        }

        public Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
            ManagedDesktopReconciliationTrigger trigger,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }

    /// <summary>
    /// Stands in for the Windows classifier's ownership resolution: a window
    /// with a registered owner qualifies as its owner, exactly as
    /// <c>GetWindow(GW_OWNER)</c> makes the real classifier behave.
    /// </summary>
    private sealed class OwnershipAwareClassifier : IWindowClassifier
    {
        private readonly Dictionary<nint, nint> owners = [];
        private readonly Dictionary<nint, string> windowClasses = [];

        public void SetOwner(nint windowHandle, nint owner) =>
            owners[windowHandle] = owner;

        public void SetWindowClass(nint windowHandle, string windowClass) =>
            windowClasses[windowHandle] = windowClass;

        public WindowQualification Qualify(nint windowHandle)
        {
            nint rootOwner = windowHandle;
            while (owners.TryGetValue(rootOwner, out nint owner))
            {
                rootOwner = owner;
            }

            return WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    rootOwner,
                    checked((uint)(long)rootOwner),
                    windowClasses.TryGetValue(rootOwner, out string? windowClass)
                        ? windowClass
                        : SessionWindowClass));
        }

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class RemoteDesktopIdentityResolver : IWindowIdentityResolver
    {
        private readonly Dictionary<nint, string> processNames = [];
        private readonly HashSet<nint> accessDenied = [];

        public void SetRemoteDesktopSession(nint windowHandle) =>
            processNames[windowHandle] = RemoteDesktopProcessName;

        public void SetProcess(nint windowHandle, string processName) =>
            processNames[windowHandle] = processName;

        public void DenyAccessFor(nint windowHandle) =>
            accessDenied.Add(windowHandle);

        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (accessDenied.Contains(window.RootWindowHandle))
            {
                return ValueTask.FromResult(
                    WindowIdentityResolution.Failed(
                        WindowIdentityResolutionFailure.AccessDenied,
                        nativeErrorCode: 5));
            }

            return ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        processNames.TryGetValue(
                            window.RootWindowHandle,
                            out string? processName)
                            ? processName
                            : RemoteDesktopProcessName,
                        ExecutablePath: null,
                        PackageFamilyName: null,
                        AppUserModelId: null,
                        window.WindowClass,
                        WindowTitle: null,
                        CommandLine: null)));
        }
    }

    private sealed class FixedWindowEnumerator(IReadOnlyList<nint> windows) :
        ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() => windows;
    }

    private sealed class FakePlacementService : IWindowDesktopPlacementService
    {
        public const int RefusedHResult = unchecked((int)0x8002802B);
        public const int AccessDeniedHResult = unchecked((int)0x80070005);

        private readonly Dictionary<nint, Guid> currentDesktopIds = [];
        private readonly HashSet<nint> refused = [];
        private readonly HashSet<nint> denied = [];

        public List<(nint WindowHandle, Guid DesktopId)> Moves { get; } = [];

        public int MoveAttemptCount { get; private set; }

        public void SetCurrent(nint windowHandle, Guid desktopId) =>
            currentDesktopIds[windowHandle] = desktopId;

        /// <summary>
        /// Makes Windows refuse to move the window, the way it refuses a
        /// full-screen remote session frame.
        /// </summary>
        /// <param name="windowHandle">The window that cannot be moved.</param>
        public void RefuseMovesFor(nint windowHandle) =>
            refused.Add(windowHandle);

        public void AllowMovesFor(nint windowHandle) =>
            refused.Remove(windowHandle);

        public void DenyAccessFor(nint windowHandle) => denied.Add(windowHandle);

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    currentDesktopIds.TryGetValue(windowHandle, out Guid desktopId)
                        ? desktopId
                        : OtherDesktopId));
        }

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            MoveAttemptCount++;
            if (denied.Contains(windowHandle))
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Failed(
                        "window_placement.move_access_denied",
                        "Windows denied access to the window, so it was not moved to the requested virtual desktop.",
                        AccessDeniedHResult));
            }

            if (refused.Contains(windowHandle))
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Failed(
                        "window_placement.move_failed",
                        "Windows refused to move the window to the requested virtual desktop. A window can be refused while it is in a state Windows manages itself, such as a full-screen remote session.",
                        RefusedHResult));
            }

            Moves.Add((windowHandle, desktopId));
            currentDesktopIds[windowHandle] = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    private sealed class FakeTopologyProvider : IDesktopTopologyProvider
    {
        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "test.full",
            "Test",
            "1",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: true);

        public VirtualDesktopCapabilities Capabilities { get; } =
            new(true, true, true, true, true, true, true);

        public Guid CurrentDesktopId { get; set; }

        public int SwitchCallCount { get; private set; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged
        {
            add { }
            remove { }
        }

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        /// <summary>
        /// Reports a two-desktop machine so the sweep can resolve position 0.
        /// </summary>
        /// <remarks>
        /// The first desktop is deliberately not the one Remote is bound to, so
        /// a swept window landing there is distinguishable from a rule-driven
        /// assignment.
        /// </remarks>
        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>> EnumerateDesktopsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>.Succeeded(
                    (IReadOnlyList<VirtualDesktopDescriptor>)
                    [
                        new VirtualDesktopDescriptor(
                            FirstDesktopId,
                            "Desktop 1",
                            0,
                            CurrentDesktopId == FirstDesktopId),
                        new VirtualDesktopDescriptor(
                            RemoteDesktopId,
                            "Remote",
                            3,
                            CurrentDesktopId == RemoteDesktopId),
                    ]));

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(CurrentDesktopId));
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Unsupported(
                    "test.unsupported",
                    "Unsupported"));

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            SwitchCallCount++;
            CurrentDesktopId = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    private sealed class NoopWindowEventSource : IWindowEventSource
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Dispose() => IsRunning = false;
    }
}

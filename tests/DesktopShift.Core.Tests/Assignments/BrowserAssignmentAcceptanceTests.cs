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
public sealed class BrowserAssignmentAcceptanceTests
{
    private const string BrowserWindowClass = "Chrome_WidgetWin_1";
    private const string PrivateWindowTitle = "private banking - Edge";
    private const string PrivateCommandLine =
        "msedge.exe --profile-directory=\"Profile 2\"";

    private static readonly Guid WebDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task Host_AssignsEveryBrowserWindowToWebIndependently()
    {
        BrowserHarness harness = new([401, 402, 403]);
        harness.IdentityResolver.SetProcessName(301, "msedge.exe");
        harness.IdentityResolver.SetProcessName(302, "chrome.exe");
        harness.IdentityResolver.SetProcessName(303, "msedge.exe");
        harness.IdentityResolver.SetProcessName(401, "msedge.exe");
        harness.IdentityResolver.SetProcessName(402, "chrome.exe");
        harness.IdentityResolver.SetProcessName(403, "chrome.exe");
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();
        harness.Placement.SetCurrent(301, OtherDesktopId);
        harness.Placement.SetCurrent(302, OtherDesktopId);
        harness.Placement.SetCurrent(303, OtherDesktopId);
        harness.Placement.Moves.Clear();

        WindowObservationActivity edgeCreated = await processor.ProcessAsync(
            new WindowEvent(10, WindowEventKind.Created, (nint)301, Now));
        WindowObservationActivity chromeShown = await processor.ProcessAsync(
            new WindowEvent(11, WindowEventKind.Shown, (nint)302, Now));
        WindowObservationActivity secondEdgeCreated = await processor.ProcessAsync(
            new WindowEvent(12, WindowEventKind.Created, (nint)303, Now));

        WindowAssignmentActivity[] eventAssignments =
        [
            edgeCreated.Assignment!,
            chromeShown.Assignment!,
            secondEdgeCreated.Assignment!,
        ];
        Assert.IsTrue(eventAssignments.All(
            static assignment =>
                assignment.Outcome == WindowAssignmentOutcome.Succeeded &&
                assignment.RuleId == "browsers" &&
                assignment.TargetDesktopKey == "web" &&
                assignment.TargetDesktopId == WebDesktopId));
        Assert.AreEqual(WindowEventKind.Created, edgeCreated.Assignment!.Trigger);
        Assert.AreEqual(WindowEventKind.Shown, chromeShown.Assignment!.Trigger);
        Assert.AreEqual("msedge.exe", edgeCreated.Identity!.ProcessName);
        Assert.AreEqual("chrome.exe", chromeShown.Identity!.ProcessName);
        Assert.HasCount(
            3,
            eventAssignments
                .Select(static assignment => assignment.CorrelationId)
                .Distinct()
                .ToArray());
        Assert.AreEqual(3, harness.Placement.Moves.Count);

        harness.Placement.SetCurrent(401, OtherDesktopId);
        harness.Placement.SetCurrent(402, OtherDesktopId);
        harness.Placement.SetCurrent(403, OtherDesktopId);
        harness.Placement.Moves.Clear();

        WindowReassignmentBatchResult manual = await host.Services
            .GetRequiredService<IWindowReassignmentService>()
            .ReassignAllAsync();

        Assert.AreEqual(3, manual.EnumeratedWindowCount);
        Assert.HasCount(3, manual.Assignments);
        Assert.IsTrue(manual.Assignments.All(
            static assignment =>
                assignment.Trigger == WindowEventKind.ManualReassignment &&
                assignment.Outcome == WindowAssignmentOutcome.Succeeded &&
                assignment.RuleId == "browsers" &&
                assignment.TargetDesktopKey == "web" &&
                assignment.TargetDesktopId == WebDesktopId));
        Assert.HasCount(
            3,
            manual.Assignments
                .Select(static assignment => assignment.CorrelationId)
                .Distinct()
                .ToArray());
        Assert.AreEqual(3, harness.Placement.Moves.Count);

        // Recorded activity carries the privacy-safe identity only. The window
        // title and the profile command line the resolver saw never leave it.
        IWindowAssignmentActivityProjection projection =
            host.Services.GetRequiredService<IWindowAssignmentActivityProjection>();
        Assert.IsTrue(projection.Snapshot.All(
            static assignment =>
                assignment.Identity.WindowClass == BrowserWindowClass &&
                (assignment.Identity.ProcessName == "msedge.exe" ||
                    assignment.Identity.ProcessName == "chrome.exe") &&
                !assignment.Identity.ToString().Contains(
                    PrivateWindowTitle,
                    StringComparison.Ordinal) &&
                !assignment.Identity.ToString().Contains(
                    PrivateCommandLine,
                    StringComparison.Ordinal)));

        await host.StopAsync();
    }

    [TestMethod]
    public async Task BrowserWindowAlreadyOnWeb_IsSkippedAndStaysSkipped()
    {
        BrowserHarness harness = new([]);
        harness.Placement.SetCurrent(501, WebDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity first = await processor.ProcessAsync(
            new WindowEvent(20, WindowEventKind.Shown, (nint)501, Now));
        WindowObservationActivity repeat = await processor.ProcessAsync(
            new WindowEvent(
                21,
                WindowEventKind.Shown,
                (nint)501,
                Now.AddMilliseconds(500)));

        Assert.AreEqual("browsers", first.RuleId);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, first.AssignmentOutcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            first.Assignment!.SkipReason);
        Assert.AreEqual(WindowMoveOutcome.AlreadyCorrect, first.Assignment.MoveOutcome);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, repeat.AssignmentOutcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            repeat.Assignment!.SkipReason);
        Assert.AreNotEqual(
            first.Assignment.CorrelationId,
            repeat.Assignment.CorrelationId);
        Assert.IsEmpty(harness.Placement.Moves);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task BrowserHelperWindow_IsSkippedBeforeIdentityAndAssignment()
    {
        BrowserHarness harness = new([]);
        harness.Classifier.SetSkipReason(601, WindowSkipReason.BrowserHelperWindow);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(30, WindowEventKind.Created, (nint)601, Now));

        Assert.AreEqual(WindowObservationOutcome.Skipped, observation.Outcome);
        Assert.AreEqual(
            WindowSkipReason.BrowserHelperWindow,
            observation.SkipReason);
        Assert.IsNull(observation.Identity);
        Assert.IsNull(observation.RuleId);
        Assert.IsNull(observation.TargetDesktopKey);
        Assert.IsNull(observation.Assignment);
        Assert.IsNull(observation.AssignmentOutcome);
        Assert.IsEmpty(harness.IdentityResolver.ResolvedWindowHandles);
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.IsEmpty(
            host.Services
                .GetRequiredService<IWindowAssignmentActivityProjection>()
                .Snapshot);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task OwnedBrowserDialog_IsAssignedAgainstItsFrameWithoutAnExtraMove()
    {
        BrowserHarness harness = new([]);
        harness.Classifier.SetRootWindowHandle(701, 700);
        harness.Placement.SetCurrent(700, WebDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(40, WindowEventKind.Shown, (nint)701, Now));

        Assert.AreEqual((nint)700, observation.WindowHandle);
        Assert.AreEqual((nint)700, observation.Assignment!.WindowHandle);
        Assert.AreEqual("browsers", observation.Assignment.RuleId);
        Assert.AreEqual("web", observation.Assignment.TargetDesktopKey);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, observation.Assignment.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            observation.Assignment.SkipReason);
        Assert.IsEmpty(harness.Placement.Moves);
        Assert.HasCount(1, harness.IdentityResolver.ResolvedWindowHandles);
        Assert.AreEqual(
            (nint)700,
            harness.IdentityResolver.ResolvedWindowHandles[0]);

        await host.StopAsync();
    }

    private static IHost CreateHost(BrowserHarness harness)
    {
        ConfigurationDocument configuration = ConfigurationDefaults.Create();

        return DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<IConfigurationService>(
                new ActiveConfigurationService(configuration));
            services.AddSingleton<ICompatibilityCoordinator>(
                new ReadyCompatibilityCoordinator());
            services.AddSingleton<IManagedDesktopReconciliationService>(
                new WebBoundReconciliationService());
            services.AddSingleton<IWindowClassifier>(harness.Classifier);
            services.AddSingleton<IWindowIdentityResolver>(harness.IdentityResolver);
            services.AddSingleton<IWindowEventSource>(new NoopWindowEventSource());
            services.AddSingleton<IWindowDesktopPlacementService>(harness.Placement);
            services.AddSingleton<ITopLevelWindowEnumerator>(harness.Enumerator);
            services.AddDesktopShiftObservation();
            services.AddDesktopShiftAssignments();
        });
    }

    private sealed class BrowserHarness(IReadOnlyList<nint> windows)
    {
        public FakePlacementService Placement { get; } = new();

        public ITopLevelWindowEnumerator Enumerator { get; } =
            new FixedWindowEnumerator(windows);

        public BrowserWindowClassifier Classifier { get; } = new();

        public BrowserIdentityResolver IdentityResolver { get; } = new();
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

    private sealed class WebBoundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            Now,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "web",
                "Web",
                2,
                true,
                WebDesktopId,
                "Web",
                1,
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

    private sealed class BrowserWindowClassifier : IWindowClassifier
    {
        private readonly Dictionary<nint, WindowSkipReason> skipReasons = [];
        private readonly Dictionary<nint, nint> rootWindowHandles = [];

        public void SetSkipReason(nint windowHandle, WindowSkipReason reason) =>
            skipReasons[windowHandle] = reason;

        public void SetRootWindowHandle(nint windowHandle, nint rootWindowHandle) =>
            rootWindowHandles[windowHandle] = rootWindowHandle;

        public WindowQualification Qualify(nint windowHandle)
        {
            if (skipReasons.TryGetValue(windowHandle, out WindowSkipReason reason))
            {
                return WindowQualification.Skipped(reason);
            }

            nint rootWindowHandle = rootWindowHandles.TryGetValue(
                windowHandle,
                out nint root)
                ? root
                : windowHandle;
            return WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    rootWindowHandle,
                    checked((uint)(long)rootWindowHandle),
                    BrowserWindowClass));
        }

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class BrowserIdentityResolver : IWindowIdentityResolver
    {
        private readonly Dictionary<nint, string> processNames = [];

        public List<nint> ResolvedWindowHandles { get; } = [];

        public void SetProcessName(nint windowHandle, string processName) =>
            processNames[windowHandle] = processName;

        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedWindowHandles.Add(window.RootWindowHandle);
            return ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        processNames.TryGetValue(
                            window.RootWindowHandle,
                            out string? processName)
                            ? processName
                            : "msedge.exe",
                        ExecutablePath: @"C:\private\msedge.exe",
                        PackageFamilyName: null,
                        AppUserModelId: null,
                        window.WindowClass,
                        PrivateWindowTitle,
                        PrivateCommandLine)));
        }
    }

    private sealed class FixedWindowEnumerator(IReadOnlyList<nint> windows) :
        ITopLevelWindowEnumerator
    {
        public IReadOnlyList<nint> Enumerate() => windows;
    }

    private sealed class FakePlacementService : IWindowDesktopPlacementService
    {
        private readonly Dictionary<nint, Guid> currentDesktopIds = [];

        public List<(nint WindowHandle, Guid DesktopId)> Moves { get; } = [];

        public void SetCurrent(nint windowHandle, Guid desktopId) =>
            currentDesktopIds[windowHandle] = desktopId;

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
            nint windowHandle,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Guid current = currentDesktopIds.TryGetValue(
                windowHandle,
                out Guid desktopId)
                ? desktopId
                : OtherDesktopId;
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(current));
        }

        public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
            nint windowHandle,
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Moves.Add((windowHandle, desktopId));
            currentDesktopIds[windowHandle] = desktopId;
            return ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
        }
    }

    private sealed class NoopWindowEventSource : IWindowEventSource
    {
        public bool IsRunning { get; private set; }

        public void Start() => IsRunning = true;

        public void Dispose() => IsRunning = false;
    }
}

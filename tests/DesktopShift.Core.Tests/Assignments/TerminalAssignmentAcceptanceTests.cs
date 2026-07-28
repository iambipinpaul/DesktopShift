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
public sealed class TerminalAssignmentAcceptanceTests
{
    private const string TerminalWindowClass = "CASCADIA_HOSTING_WINDOW_CLASS";
    private const string TerminalProcessName = "WindowsTerminal.exe";
    private const string TerminalPackageFamilyName =
        "Microsoft.WindowsTerminal_8wekyb3d8bbwe";
    private const string TerminalAppUserModelId =
        "Microsoft.WindowsTerminal_8wekyb3d8bbwe!App";
    private const string LauncherProcessName = "wt.exe";
    private const string UnrelatedProcessName = "notepad.exe";
    private const string UnrelatedWindowClass = "Notepad";

    private static readonly Guid TerminalDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 15, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task NewTerminalWindow_IsAssignedToTerminalOnItsPackageIdentity()
    {
        TerminalHarness harness = new([]);
        harness.IdentityResolver.SetPackagedTerminal(801);
        harness.Placement.SetCurrent(801, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(100, WindowEventKind.Created, (nint)801, Now));

        Assert.AreEqual(WindowObservationOutcome.Matched, observation.Outcome);
        Assert.AreEqual("windows-terminal", observation.RuleId);
        Assert.AreEqual("terminal", observation.TargetDesktopKey);

        // The packaged identity, not the executable name, selected the rule.
        Assert.AreEqual(
            WindowMatchStrength.PackageFamilyName,
            observation.MatchedOn);
        Assert.AreEqual(
            TerminalPackageFamilyName,
            observation.Identity!.PackageFamilyName);

        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            observation.Assignment!.Outcome);
        Assert.AreEqual("windows-terminal", observation.Assignment.RuleId);
        Assert.AreEqual("terminal", observation.Assignment.TargetDesktopKey);
        Assert.AreEqual(TerminalDesktopId, observation.Assignment.TargetDesktopId);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, observation.Assignment.MoveOutcome);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual((nint)801, harness.Placement.Moves[0].WindowHandle);
        Assert.AreEqual(TerminalDesktopId, harness.Placement.Moves[0].DesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task MultipleTerminalWindows_AreAssignedIndependently()
    {
        TerminalHarness harness = new([]);
        harness.IdentityResolver.SetPackagedTerminal(811);
        harness.IdentityResolver.SetPackagedTerminal(812);
        harness.IdentityResolver.SetPackagedTerminal(813);
        harness.Placement.SetCurrent(811, OtherDesktopId);
        harness.Placement.SetCurrent(812, OtherDesktopId);
        harness.Placement.SetCurrent(813, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity first = await processor.ProcessAsync(
            new WindowEvent(110, WindowEventKind.Created, (nint)811, Now));
        WindowObservationActivity second = await processor.ProcessAsync(
            new WindowEvent(111, WindowEventKind.Created, (nint)812, Now));
        WindowObservationActivity third = await processor.ProcessAsync(
            new WindowEvent(112, WindowEventKind.Shown, (nint)813, Now));

        WindowAssignmentActivity[] assignments =
        [
            first.Assignment!,
            second.Assignment!,
            third.Assignment!,
        ];
        Assert.IsTrue(assignments.All(
            static assignment =>
                assignment.Outcome == WindowAssignmentOutcome.Succeeded &&
                assignment.RuleId == "windows-terminal" &&
                assignment.TargetDesktopKey == "terminal" &&
                assignment.TargetDesktopId == TerminalDesktopId));

        // Each window carries its own correlation and its own move. One window
        // never stands in for another.
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
        Assert.Contains((nint)811, movedHandles);
        Assert.Contains((nint)812, movedHandles);
        Assert.Contains((nint)813, movedHandles);
        Assert.IsTrue(harness.Placement.Moves.All(
            static move => move.DesktopId == TerminalDesktopId));

        await host.StopAsync();
    }

    [TestMethod]
    public async Task NewTabInAnExistingTerminalWindow_DoesNotMoveTheWindowAgain()
    {
        // Win32 reality: a Windows Terminal tab is a XAML element hosted inside
        // the single CASCADIA_HOSTING_WINDOW_CLASS frame window. Opening a tab
        // creates no top-level HWND, so the shell raises no window-created event
        // for it. The only events that can arrive are repeats for the frame that
        // is already placed, and those must not move it a second time.
        TerminalHarness harness = new([]);
        harness.IdentityResolver.SetPackagedTerminal(821);
        harness.Placement.SetCurrent(821, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity windowOpened = await processor.ProcessAsync(
            new WindowEvent(120, WindowEventKind.Shown, (nint)821, Now));
        WindowObservationActivity tabShown = await processor.ProcessAsync(
            new WindowEvent(
                121,
                WindowEventKind.Shown,
                (nint)821,
                Now.AddMilliseconds(500)));
        WindowObservationActivity tabActivated = await processor.ProcessAsync(
            new WindowEvent(
                122,
                WindowEventKind.ForegroundActivated,
                (nint)821,
                Now.AddMilliseconds(1000)));

        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            windowOpened.Assignment!.Outcome);
        Assert.AreEqual(
            WindowAssignmentOutcome.Skipped,
            tabShown.Assignment!.Outcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            tabShown.Assignment.SkipReason);
        // A foreground repeat may still drive a desktop switch depending on the
        // rule's switch policy, so the placement decision, not the overall
        // outcome, is what proves the window was left where it already was.
        Assert.AreEqual(
            WindowMoveOutcome.AlreadyCorrect,
            tabActivated.Assignment!.MoveOutcome);

        // Every event described the same HWND: no tab ever became a window.
        Assert.HasCount(
            1,
            new[] { windowOpened, tabShown, tabActivated }
                .Select(static activity => activity.WindowHandle)
                .Distinct()
                .ToArray());
        Assert.AreEqual((nint)821, windowOpened.WindowHandle);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual((nint)821, harness.Placement.Moves[0].WindowHandle);
        Assert.AreEqual(TerminalDesktopId, harness.Placement.Moves[0].DesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task ForwardedLauncherInvocation_NeverMovesAnUnrelatedWindow()
    {
        // wt.exe forwards its command line to the already running
        // WindowsTerminal.exe host and exits, so it never owns a window. Only
        // the host window may be claimed; the launcher and whatever the user was
        // working in must be left exactly where they are.
        TerminalHarness harness = new([]);
        harness.Classifier.SetWindowClass(832, UnrelatedWindowClass);
        harness.IdentityResolver.SetUnpackagedProcess(831, LauncherProcessName);
        harness.IdentityResolver.SetUnpackagedProcess(832, UnrelatedProcessName);
        harness.IdentityResolver.SetPackagedTerminal(833);
        harness.Placement.SetCurrent(831, OtherDesktopId);
        harness.Placement.SetCurrent(832, OtherDesktopId);
        harness.Placement.SetCurrent(833, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity launcher = await processor.ProcessAsync(
            new WindowEvent(130, WindowEventKind.Created, (nint)831, Now));
        WindowObservationActivity unrelated = await processor.ProcessAsync(
            new WindowEvent(
                131,
                WindowEventKind.ForegroundActivated,
                (nint)832,
                Now));
        WindowObservationActivity terminalHost = await processor.ProcessAsync(
            new WindowEvent(132, WindowEventKind.Shown, (nint)833, Now));

        Assert.AreEqual(WindowObservationOutcome.Skipped, launcher.Outcome);
        Assert.AreEqual(WindowSkipReason.NoMatchingRule, launcher.SkipReason);
        Assert.IsNull(launcher.RuleId);
        Assert.IsNull(launcher.TargetDesktopKey);
        Assert.IsNull(launcher.Assignment);
        Assert.IsNull(launcher.MatchedOn);

        Assert.AreEqual(WindowObservationOutcome.Skipped, unrelated.Outcome);
        Assert.AreEqual(WindowSkipReason.NoMatchingRule, unrelated.SkipReason);
        Assert.IsNull(unrelated.RuleId);
        Assert.IsNull(unrelated.Assignment);
        Assert.AreEqual(UnrelatedProcessName, unrelated.Identity!.ProcessName);

        Assert.AreEqual(WindowObservationOutcome.Matched, terminalHost.Outcome);
        Assert.AreEqual("windows-terminal", terminalHost.RuleId);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            terminalHost.Assignment!.Outcome);

        // Only the host window moved. The launcher and the unrelated window the
        // user was in were never dragged onto the terminal desktop.
        nint[] movedHandles = harness.Placement.Moves
            .Select(static move => move.WindowHandle)
            .ToArray();
        Assert.HasCount(1, movedHandles);
        Assert.Contains((nint)833, movedHandles);
        Assert.DoesNotContain((nint)831, movedHandles);
        Assert.DoesNotContain((nint)832, movedHandles);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task UnpackagedTerminalWindow_IsStillAssignedOnItsProcessName()
    {
        // A portable or unpackaged build reports no package family name and no
        // AppUserModelId. The rule must degrade to the process name instead of
        // leaving the window unmanaged.
        TerminalHarness harness = new([]);
        harness.IdentityResolver.SetUnpackagedProcess(841, TerminalProcessName);
        harness.Placement.SetCurrent(841, OtherDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(140, WindowEventKind.Created, (nint)841, Now));

        Assert.IsNull(observation.Identity!.PackageFamilyName);
        Assert.IsNull(observation.Identity.AppUserModelId);
        Assert.AreEqual(WindowObservationOutcome.Matched, observation.Outcome);
        Assert.AreEqual("windows-terminal", observation.RuleId);
        Assert.AreEqual("terminal", observation.TargetDesktopKey);
        Assert.AreEqual(WindowMatchStrength.ProcessName, observation.MatchedOn);
        Assert.AreEqual(
            WindowAssignmentOutcome.Succeeded,
            observation.Assignment!.Outcome);
        Assert.AreEqual(TerminalDesktopId, observation.Assignment.TargetDesktopId);
        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual((nint)841, harness.Placement.Moves[0].WindowHandle);
        Assert.AreEqual(TerminalDesktopId, harness.Placement.Moves[0].DesktopId);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task TerminalWindowAlreadyOnTerminal_IsSkippedAndStaysSkipped()
    {
        TerminalHarness harness = new([]);
        harness.IdentityResolver.SetPackagedTerminal(851);
        harness.Placement.SetCurrent(851, TerminalDesktopId);
        using IHost host = CreateHost(harness);
        await host.StartAsync();
        WindowObservationProcessor processor =
            host.Services.GetRequiredService<WindowObservationProcessor>();

        WindowObservationActivity first = await processor.ProcessAsync(
            new WindowEvent(150, WindowEventKind.Shown, (nint)851, Now));
        WindowObservationActivity repeat = await processor.ProcessAsync(
            new WindowEvent(
                151,
                WindowEventKind.Shown,
                (nint)851,
                Now.AddMilliseconds(500)));

        Assert.AreEqual("windows-terminal", first.RuleId);
        Assert.AreEqual("terminal", first.TargetDesktopKey);
        Assert.AreEqual(WindowAssignmentOutcome.Skipped, first.AssignmentOutcome);
        Assert.AreEqual(
            WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
            first.Assignment!.SkipReason);
        Assert.AreEqual(WindowMoveOutcome.AlreadyCorrect, first.Assignment.MoveOutcome);
        Assert.AreEqual("windows-terminal", repeat.RuleId);
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

    private static IHost CreateHost(TerminalHarness harness)
    {
        ConfigurationDocument configuration = ConfigurationDefaults.Create();

        return DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<IConfigurationService>(
                new ActiveConfigurationService(configuration));
            services.AddSingleton<ICompatibilityCoordinator>(
                new ReadyCompatibilityCoordinator());
            services.AddSingleton<IManagedDesktopReconciliationService>(
                new TerminalBoundReconciliationService());
            services.AddSingleton<IWindowClassifier>(harness.Classifier);
            services.AddSingleton<IWindowIdentityResolver>(harness.IdentityResolver);
            services.AddSingleton<IWindowEventSource>(new NoopWindowEventSource());
            services.AddSingleton<IWindowDesktopPlacementService>(harness.Placement);
            services.AddSingleton<ITopLevelWindowEnumerator>(harness.Enumerator);
            services.AddDesktopShiftObservation();
            services.AddDesktopShiftAssignments();
        });
    }

    private sealed class TerminalHarness(IReadOnlyList<nint> windows)
    {
        public FakePlacementService Placement { get; } = new();

        public ITopLevelWindowEnumerator Enumerator { get; } =
            new FixedWindowEnumerator(windows);

        public TerminalWindowClassifier Classifier { get; } = new();

        public TerminalIdentityResolver IdentityResolver { get; } = new();
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

    private sealed class TerminalBoundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            Now,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "terminal",
                "Terminal",
                3,
                true,
                TerminalDesktopId,
                "Terminal",
                2,
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

    private sealed class TerminalWindowClassifier : IWindowClassifier
    {
        private readonly Dictionary<nint, string> windowClasses = [];

        public void SetWindowClass(nint windowHandle, string windowClass) =>
            windowClasses[windowHandle] = windowClass;

        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    checked((uint)(long)windowHandle),
                    windowClasses.TryGetValue(windowHandle, out string? windowClass)
                        ? windowClass
                        : TerminalWindowClass));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    private sealed class TerminalIdentityResolver : IWindowIdentityResolver
    {
        private static readonly ResolvedSignals PackagedTerminal = new(
            TerminalProcessName,
            TerminalPackageFamilyName,
            TerminalAppUserModelId);

        private readonly Dictionary<nint, ResolvedSignals> signals = [];

        public List<nint> ResolvedWindowHandles { get; } = [];

        public void SetPackagedTerminal(nint windowHandle) =>
            signals[windowHandle] = PackagedTerminal;

        public void SetUnpackagedProcess(nint windowHandle, string processName) =>
            signals[windowHandle] = new ResolvedSignals(
                processName,
                PackageFamilyName: null,
                AppUserModelId: null);

        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedWindowHandles.Add(window.RootWindowHandle);
            ResolvedSignals resolved = signals.TryGetValue(
                window.RootWindowHandle,
                out ResolvedSignals? configured)
                ? configured
                : PackagedTerminal;
            return ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        resolved.ProcessName,
                        ExecutablePath: null,
                        resolved.PackageFamilyName,
                        resolved.AppUserModelId,
                        window.WindowClass,
                        WindowTitle: "pwsh",
                        CommandLine: null)));
        }

        private sealed record ResolvedSignals(
            string ProcessName,
            string? PackageFamilyName,
            string? AppUserModelId);
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

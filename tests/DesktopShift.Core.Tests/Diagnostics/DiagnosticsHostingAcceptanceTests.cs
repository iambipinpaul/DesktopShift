using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Diagnostics;
using DesktopShift.Infrastructure.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Core.Tests.Diagnostics;

/// <summary>
/// Proves the diagnostics pipeline end to end on a real host: one window event
/// produces correlated decision, move, switch, and result records; those records
/// reach the rolling log and an exported bundle; and none of them carry the
/// window title, command line, executable path, or user profile the identity
/// resolver saw.
/// </summary>
[TestClass]
public sealed class DiagnosticsHostingAcceptanceTests
{
    private const string SecretTitle = "Quarterly-Salaries.xlsx — Private";
    private const string SecretCommandLine =
        "--profile-directory=\"Person 3\" --token=hunter2";
    private const string SecretExecutablePath =
        @"C:\Users\marguerite\AppData\Local\Programs\Code\Code.exe";

    private static readonly Guid CodeDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();

    [TestMethod]
    public async Task RecordingStartsOff_AndRetainsNoRoutineActivity()
    {
        FakePlacementService placement = new();
        placement.SetCurrent(99, OtherDesktopId);
        using IHost host = CreateHost(placement, recordActivity: false);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    0,
                    WindowEventKind.Created,
                    (nint)99,
                    DateTimeOffset.UtcNow));

        Assert.IsFalse(
            host.Services
                .GetRequiredService<IActivityRecordingController>()
                .IsEnabled);
        Assert.IsEmpty(
            host.Services
                .GetRequiredService<IActivityJournalProjection>()
                .Snapshot);
        Assert.IsEmpty(
            host.Services.GetRequiredService<RollingDiagnosticLog>().Files());

        await host.StopAsync();
    }

    [TestMethod]
    public async Task OneAssignment_ProducesCorrelatedDecisionMoveSwitchAndResult()
    {
        FakePlacementService placement = new();
        placement.SetCurrent(101, OtherDesktopId);
        using IHost host = CreateHost(placement);
        await host.StartAsync();

        WindowObservationActivity observation = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    1,
                    WindowEventKind.Created,
                    (nint)101,
                    DateTimeOffset.UtcNow));

        IActivityJournalProjection journal =
            host.Services.GetRequiredService<IActivityJournalProjection>();
        IReadOnlyList<ActivityRecord> records = journal.Snapshot;

        Assert.AreNotEqual(Guid.Empty, observation.CorrelationId);
        Assert.AreEqual(
            observation.CorrelationId,
            observation.Assignment!.CorrelationId,
            "The assignment must reuse the observation's correlation.");
        Assert.IsTrue(
            records.All(record =>
                record.CorrelationId == observation.CorrelationId),
            "Every recorded stage must share one correlation.");
        CollectionAssert.AreEquivalent(
            new[]
            {
                ActivityEventSource.Observation,
                ActivityEventSource.Move,
                ActivityEventSource.Assignment,
            },
            records.Select(static record => record.Source).ToArray());
        Assert.IsTrue(
            records.All(record => record.SessionId == journal.SessionId));

        await host.StopAsync();
    }

    [TestMethod]
    public async Task SkippedObservation_IsJournalledWithItsReason()
    {
        FakePlacementService placement = new();
        using IHost host = CreateHost(placement);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    2,
                    WindowEventKind.Destroyed,
                    (nint)202,
                    DateTimeOffset.UtcNow));

        ActivityRecord record = host.Services
            .GetRequiredService<IActivityJournalProjection>()
            .Snapshot
            .Single();
        Assert.AreEqual(ActivityResult.Skipped, record.Result);
        Assert.AreEqual("observation.skipped.cleanup_event", record.ResultCode);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task MoveFailure_ReachesTheJournalWithItsHResult()
    {
        FakePlacementService placement = new();
        placement.SetCurrent(303, OtherDesktopId);
        placement.FailMoves.Add((nint)303);
        using IHost host = CreateHost(placement);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    3,
                    WindowEventKind.Created,
                    (nint)303,
                    DateTimeOffset.UtcNow));

        ActivityRecord move = host.Services
            .GetRequiredService<IActivityJournalProjection>()
            .Snapshot
            .Single(static record => record.Source == ActivityEventSource.Move);
        Assert.AreEqual(ActivityResult.Failed, move.Result);
        Assert.AreEqual("0x80070005", move.Error!.HResultText);
        Assert.AreEqual(5, move.Error.NativeErrorCode);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task RollingLogAndBundle_CarryNoTitleCommandLineOrProfilePath()
    {
        using TemporaryDirectory root = new();
        FakePlacementService placement = new();
        placement.SetCurrent(404, OtherDesktopId);
        using IHost host = CreateHost(placement, root.Path);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    4,
                    WindowEventKind.Created,
                    (nint)404,
                    DateTimeOffset.UtcNow));

        IDiagnosticsCoordinator diagnostics =
            host.Services.GetRequiredService<IDiagnosticsCoordinator>();
        RollingDiagnosticLog log =
            host.Services.GetRequiredService<RollingDiagnosticLog>();

        string logText = string.Concat(
            log.ReadAll().Select(static file => file.Content));
        Assert.IsNotEmpty(logText);
        AssertNoSecrets(logText);

        using MemoryStream destination = new();
        DiagnosticBundleSummary summary =
            await diagnostics.ExportBundleAsync(destination);
        AssertNoSecrets(ReadArchiveText(destination));
        Assert.IsGreaterThan(0, summary.ActivityRecordCount);
        Assert.IsGreaterThan(0, summary.LogFileCount);

        await host.StopAsync();
    }

    [TestMethod]
    public async Task ExportedBundle_IsAReadableArchiveWithAManifest()
    {
        using TemporaryDirectory root = new();
        FakePlacementService placement = new();
        placement.SetCurrent(505, OtherDesktopId);
        using IHost host = CreateHost(placement, root.Path);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    5,
                    WindowEventKind.Created,
                    (nint)505,
                    DateTimeOffset.UtcNow));

        DiagnosticBundleSummary summary = await host.Services
            .GetRequiredService<IDiagnosticsCoordinator>()
            .ExportBundleToDefaultLocationAsync();

        Assert.IsNotNull(summary.FilePath);
        Assert.IsTrue(File.Exists(summary.FilePath));
        Assert.IsTrue(
            summary.FilePath.StartsWith(root.Path, StringComparison.Ordinal),
            "The bundle must land inside the application's own folder.");

        using ZipArchive archive = ZipFile.OpenRead(summary.FilePath);
        using StreamReader reader = new(archive.GetEntry("manifest.json")!.Open());
        using JsonDocument manifest = JsonDocument.Parse(reader.ReadToEnd());
        Assert.AreEqual(
            "DesktopShift",
            manifest.RootElement.GetProperty("product").GetString());
        Assert.IsNotNull(archive.GetEntry("activity.json"));

        await host.StopAsync();
    }

    [TestMethod]
    public async Task ClearLocalActivity_EmptiesTheJournalAndTheLogFiles()
    {
        using TemporaryDirectory root = new();
        FakePlacementService placement = new();
        placement.SetCurrent(606, OtherDesktopId);
        using IHost host = CreateHost(placement, root.Path);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    6,
                    WindowEventKind.Created,
                    (nint)606,
                    DateTimeOffset.UtcNow));

        IDiagnosticsCoordinator diagnostics =
            host.Services.GetRequiredService<IDiagnosticsCoordinator>();
        string logDirectory = diagnostics.LogLocation.DirectoryPath;
        Assert.IsNotEmpty(Directory.GetFiles(logDirectory, "activity-*.log"));

        diagnostics.ClearLocalActivity();

        Assert.IsEmpty(diagnostics.Activity.Snapshot);
        Assert.IsEmpty(Directory.GetFiles(logDirectory, "activity-*.log"));

        await host.StopAsync();
    }

    [TestMethod]
    public async Task DefaultHost_UsesTheInMemoryLogStore()
    {
        FakePlacementService placement = new();
        placement.SetCurrent(707, OtherDesktopId);
        using IHost host = CreateHost(placement);
        await host.StartAsync();

        _ = await host.Services
            .GetRequiredService<WindowObservationProcessor>()
            .ProcessAsync(
                new WindowEvent(
                    7,
                    WindowEventKind.Created,
                    (nint)707,
                    DateTimeOffset.UtcNow));

        Assert.IsInstanceOfType<InMemoryDiagnosticLogStore>(
            host.Services.GetRequiredService<IDiagnosticLogStore>(),
            "The in-memory store must be the default so no test can write real logs.");
        Assert.IsNotEmpty(
            host.Services.GetRequiredService<RollingDiagnosticLog>().Files());

        await host.StopAsync();
    }

    private static void AssertNoSecrets(string text)
    {
        Assert.DoesNotContain(SecretTitle, text);
        Assert.DoesNotContain(SecretCommandLine, text);
        Assert.DoesNotContain(SecretExecutablePath, text);
        Assert.DoesNotContain("Quarterly", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("Person 3", text);
        Assert.DoesNotContain("marguerite", text);
        Assert.Contains("Code.exe", text);
    }

    private static string ReadArchiveText(MemoryStream destination)
    {
        destination.Position = 0;
        using ZipArchive archive = new(
            destination,
            ZipArchiveMode.Read,
            leaveOpen: true);
        return string.Join(
            "\n",
            archive.Entries.Select(static entry =>
            {
                using StreamReader reader = new(entry.Open());
                return reader.ReadToEnd();
            }));
    }

    private static IHost CreateHost(
        FakePlacementService placement,
        string? logRootDirectory = null,
        bool recordActivity = true)
    {
        ConfigurationDocument configuration = ConfigurationDefaults.Create();

        IHost host = DesktopShiftHost.Create(services =>
        {
            services.AssignOpenedWindowsInline();
            services.AddSingleton<IConfigurationService>(
                new ActiveConfigurationService(configuration));
            services.AddSingleton<ICompatibilityCoordinator>(
                new ReadyCompatibilityCoordinator());
            services.AddSingleton<IManagedDesktopReconciliationService>(
                new ReadyReconciliationService());
            services.AddSingleton<IWindowClassifier>(new AcceptAllClassifier());
            services.AddSingleton<IWindowIdentityResolver>(
                new SensitiveIdentityResolver());
            services.AddSingleton<IWindowEventSource>(new NoopWindowEventSource());
            services.AddSingleton<IWindowDesktopPlacementService>(placement);
            services.AddSingleton<ITopLevelWindowEnumerator>(
                new FixedWindowEnumerator([]));

            if (logRootDirectory is not null)
            {
                services.AddSingleton<IDiagnosticLogLocation>(
                    new LocalAppDataDiagnosticLogLocation(
                        logRootDirectory,
                        userProfilePath: null));
                services.AddSingleton<IDiagnosticLogStore, FileSystemDiagnosticLogStore>();
            }

            services.AddDesktopShiftObservation();
            services.AddDesktopShiftAssignments();
            services.AddDesktopShiftDiagnostics();
        });

        host.Services
            .GetRequiredService<IActivityRecordingController>()
            .SetEnabled(recordActivity);
        return host;
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DesktopShift.Tests",
                Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }

    private sealed class ActiveConfigurationService(
        ConfigurationDocument configuration) : IConfigurationService
    {
        public ConfigurationState CurrentState { get; } = new(
            configuration,
            configuration,
            [],
            DateTimeOffset.UtcNow);

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
                DateTimeOffset.UtcNow,
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

    private sealed class ReadyReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "ide-development",
                "IDE Development",
                1,
                true,
                CodeDesktopId,
                "Code",
                0,
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

    private sealed class AcceptAllClassifier : IWindowClassifier
    {
        public WindowQualification Qualify(nint windowHandle) =>
            WindowQualification.Qualified(
                new QualifiedWindow(
                    windowHandle,
                    windowHandle,
                    checked((uint)(long)windowHandle),
                    "CodeWindow"));

        public WindowSkipReason ClassifyIdentity(
            QualifiedWindow window,
            WindowIdentity identity) =>
            WindowSkipReason.None;
    }

    /// <summary>
    /// Resolves an identity carrying every field that must never be recorded,
    /// so a leak anywhere downstream is a literal string match.
    /// </summary>
    private sealed class SensitiveIdentityResolver : IWindowIdentityResolver
    {
        public ValueTask<WindowIdentityResolution> ResolveAsync(
            QualifiedWindow window,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                WindowIdentityResolution.Succeeded(
                    new WindowIdentity(
                        window.ProcessId,
                        "Code.exe",
                        SecretExecutablePath,
                        PackageFamilyName: null,
                        AppUserModelId: null,
                        window.WindowClass,
                        SecretTitle,
                        SecretCommandLine)));
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

        public HashSet<nint> FailMoves { get; } = [];

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
            if (FailMoves.Contains(windowHandle))
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Failed(
                        "window_placement.move_access_denied",
                        "Windows denied access to the window.",
                        unchecked((int)0x80070005),
                        nativeErrorCode: 5));
            }

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

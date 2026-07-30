using System.IO.Compression;
using System.Text.Json;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;
using DesktopShift.Infrastructure.Diagnostics;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// A performance report leaves the machine the way everything else does: inside a
/// diagnostic bundle the user asked for, and nowhere else.
/// </summary>
/// <remarks>
/// The report is also the only part of a bundle worth diffing between two runs, so
/// it gets its own entry rather than being folded into the manifest, and its
/// durations are plain milliseconds so two runs can be compared without parsing a
/// <see cref="TimeSpan"/> literal.
/// </remarks>
[TestClass]
public sealed class PerformanceBundleTests
{
    private static readonly DateTimeOffset Occurred =
        new(2026, 7, 30, 9, 15, 0, TimeSpan.Zero);

    [TestMethod]
    public void AnExportedBundle_CarriesTheReportAsItsOwnEntry()
    {
        using MemoryStream destination = new();

        DiagnosticBundleSummary summary = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(
                Manifest(),
                [],
                [],
                CreateReport(latencyMilliseconds: 12, idleCpuPercent: 0.3)));

        using ZipArchive archive = OpenArchive(destination);
        Assert.IsNotNull(
            archive.GetEntry(DiagnosticBundleWriter.PerformanceEntryName));
        Assert.IsTrue(summary.IncludesPerformanceReport);
    }

    [TestMethod]
    public void AHostWithNoMonitor_OmitsTheEntryRatherThanExportingAnEmptyOne()
    {
        // "Not measured" and "measured as zero" are different findings. An empty
        // report would claim the second.
        using MemoryStream destination = new();

        DiagnosticBundleSummary summary = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(Manifest(), [], []));

        using ZipArchive archive = OpenArchive(destination);
        Assert.IsNull(
            archive.GetEntry(DiagnosticBundleWriter.PerformanceEntryName));
        Assert.IsFalse(summary.IncludesPerformanceReport);
    }

    [TestMethod]
    public void TheExportedEntry_ReadsAsMillisecondsAndMegabytes()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(
                Manifest(),
                [],
                [],
                CreateReport(latencyMilliseconds: 12, idleCpuPercent: 0.3)));

        using JsonDocument document = ReadJson(
            destination,
            DiagnosticBundleWriter.PerformanceEntryName);
        JsonElement root = document.RootElement;

        Assert.AreEqual(
            12d,
            root.GetProperty("assignmentLatency")
                .GetProperty("p99Milliseconds")
                .GetDouble());
        Assert.AreEqual(
            50L,
            root.GetProperty("assignmentsMeasured").GetInt64());
        Assert.AreEqual(0.3d, root.GetProperty("idleCpuPercent").GetDouble(), 0.0001d);
        Assert.AreEqual(
            32d,
            root.GetProperty("privateMemoryMegabytes").GetDouble(),
            0.0001d);
        Assert.IsTrue(root.GetProperty("budgetMet").GetBoolean());
        Assert.IsFalse(root.GetProperty("releaseBlocked").GetBoolean());
        Assert.IsEmpty(root.GetProperty("releaseBlockers").EnumerateArray());

        // The budget is carried with the figures, so a blocker read a year later
        // is still readable against the thresholds it was judged by.
        Assert.AreEqual(
            30d,
            root.GetProperty("budget")
                .GetProperty("assignmentLatencyP50Milliseconds")
                .GetDouble());
    }

    [TestMethod]
    public void AMissedFigure_IsExportedAsAReleaseBlockerWithItsReason()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(
                Manifest(),
                [],
                [],
                CreateReport(latencyMilliseconds: 400, idleCpuPercent: 5d)));

        using JsonDocument document = ReadJson(
            destination,
            DiagnosticBundleWriter.PerformanceEntryName);
        JsonElement root = document.RootElement;

        Assert.IsTrue(root.GetProperty("releaseBlocked").GetBoolean());
        Assert.IsFalse(root.GetProperty("budgetMet").GetBoolean());

        JsonElement[] blockers =
            [.. root.GetProperty("releaseBlockers").EnumerateArray()];
        Assert.HasCount(4, blockers);
        foreach (JsonElement blocker in blockers)
        {
            Assert.IsNotEmpty(blocker.GetProperty("metric").GetString()!);
            Assert.IsNotEmpty(blocker.GetProperty("budget").GetString()!);
            Assert.IsNotEmpty(blocker.GetProperty("measured").GetString()!);
            Assert.IsNotEmpty(blocker.GetProperty("consequence").GetString()!);
        }
    }

    [TestMethod]
    public void AnUnmeasurableIntervalIsExportedAsNull_NotAsZero()
    {
        StubProcessResourceSampler sampler = new();
        sampler.EnqueueInterval(TimeSpan.FromMilliseconds(20), cpuPercent: 70d);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(
                Manifest(),
                [],
                [],
                monitor.CreateReport()));

        using JsonDocument document = ReadJson(
            destination,
            DiagnosticBundleWriter.PerformanceEntryName);

        Assert.IsFalse(
            document.RootElement.TryGetProperty(
                "idleCpuPercent",
                out JsonElement _),
            "A processor figure the interval could not support was exported anyway.");
    }

    [TestMethod]
    public void TheReportCarriesNoIdentityAtAll()
    {
        // The rest of a bundle is privacy-safe because the records themselves
        // carry only safe identity. The report needs no such argument: it is
        // counts and durations, with nothing in it that came from a window.
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(
                Manifest(),
                [],
                [],
                CreateReport(latencyMilliseconds: 12, idleCpuPercent: 0.3)));

        using ZipArchive archive = OpenArchive(destination);
        using StreamReader reader = new(
            archive.GetEntry(DiagnosticBundleWriter.PerformanceEntryName)!.Open());
        string text = reader.ReadToEnd();

        Assert.DoesNotContain("Measured.exe", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(@"C:\", text, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("Chrome_WidgetWin", text, StringComparison.OrdinalIgnoreCase);
    }

    [TestMethod]
    public void TheCoordinatorBuildsTheReportOnlyWhenABundleIsAsked()
    {
        // Nothing measures on a schedule. Reading a counter is a consequence of
        // the user pressing Export, and of nothing else.
        CountingReportSource reports = new();
        RollingDiagnosticLog log = new(
            new InMemoryDiagnosticLogStore(),
            RollingDiagnosticLogOptions.Default);
        DiagnosticsCoordinator coordinator = new(
            new BoundedActivityJournal(TimeProvider.System),
            log,
            new StubLogLocation(),
            TimeProvider.System,
            reports);

        Assert.AreEqual(0, reports.CallCount);

        _ = coordinator.BuildContents();
        Assert.AreEqual(1, reports.CallCount);

        DiagnosticBundleContents contents = coordinator.BuildContents();
        Assert.AreEqual(2, reports.CallCount);
        Assert.IsNotNull(contents.Performance);
    }

    private static PerformanceReport CreateReport(
        double latencyMilliseconds,
        double idleCpuPercent)
    {
        StubProcessResourceSampler sampler = new();
        sampler.EnqueueInterval(TimeSpan.FromMinutes(10), idleCpuPercent);
        PerformanceMonitor monitor = new(TimeProvider.System, sampler);

        for (int index = 0; index < 50; index++)
        {
            monitor.RecordAssignment(
                new AssignmentLatencySample(
                    WindowEventKind.Shown,
                    WindowMoveOutcome.Succeeded,
                    TimeSpan.FromMilliseconds(latencyMilliseconds),
                    TimeSpan.Zero,
                    TimeSpan.FromMilliseconds(latencyMilliseconds)));
        }

        return monitor.CreateReport();
    }

    private static DiagnosticBundleManifest Manifest() =>
        new(
            "DesktopShift",
            "0.1.0.0",
            Occurred,
            Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa"),
            Occurred,
            new DiagnosticEnvironmentSummary(
                "Microsoft Windows 10.0.26200",
                "X64",
                ".NET 10.0.0"),
            @"%USERPROFILE%\AppData\Local\DesktopShift\logs",
            1024,
            3,
            0,
            0,
            DiagnosticBundleWriter.PrivacyNotice);

    private static ZipArchive OpenArchive(MemoryStream destination)
    {
        destination.Position = 0;
        return new ZipArchive(destination, ZipArchiveMode.Read, leaveOpen: true);
    }

    private static JsonDocument ReadJson(
        MemoryStream destination,
        string entryName)
    {
        using ZipArchive archive = OpenArchive(destination);
        ZipArchiveEntry entry = archive.GetEntry(entryName)!;
        using StreamReader reader = new(entry.Open());
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private sealed class CountingReportSource : IPerformanceReportSource
    {
        private readonly PerformanceMonitor monitor = new(
            TimeProvider.System,
            new StubProcessResourceSampler());

        public int CallCount { get; private set; }

        public PerformanceReport CreateReport()
        {
            CallCount++;
            return monitor.CreateReport();
        }
    }

    private sealed class StubLogLocation : IDiagnosticLogLocation
    {
        public string DirectoryPath => Path.Combine(Path.GetTempPath(), "desktopshift-tests");

        public string DisplayPath => @"%TEMP%\desktopshift-tests";
    }
}

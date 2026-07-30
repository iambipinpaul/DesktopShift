using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using DesktopShift.Core;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Performance;

namespace DesktopShift.Infrastructure.Diagnostics;

/// <summary>
/// Carries out the user-initiated diagnostic actions the Activity view offers.
/// </summary>
/// <remarks>
/// Nothing here runs on its own. Clearing drops the journal and the log files
/// the application created, and exporting writes one archive and stops — the
/// bundle is never opened, uploaded, or sent anywhere.
/// </remarks>
public sealed class DiagnosticsCoordinator : IDiagnosticsCoordinator
{
    private readonly IActivityJournal journal;
    private readonly RollingDiagnosticLog log;
    private readonly TimeProvider timeProvider;
    private readonly IPerformanceReportSource? performanceReports;
    private readonly string bundleDirectoryPath;
    private readonly string? userProfilePath;

    /// <param name="performanceReports">
    /// Where the exported performance report comes from, or null in a host with
    /// no performance monitor. A bundle without one is still a valid bundle: the
    /// entry is simply absent rather than present and empty, so a reader can tell
    /// "not measured" from "measured as zero".
    /// </param>
    public DiagnosticsCoordinator(
        IActivityJournal journal,
        RollingDiagnosticLog log,
        IDiagnosticLogLocation logLocation,
        TimeProvider timeProvider,
        IPerformanceReportSource? performanceReports = null)
        : this(
            journal,
            log,
            logLocation,
            timeProvider,
            (logLocation as LocalAppDataDiagnosticLogLocation)?.BundleDirectoryPath ??
                logLocation.DirectoryPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            performanceReports)
    {
    }

    internal DiagnosticsCoordinator(
        IActivityJournal journal,
        RollingDiagnosticLog log,
        IDiagnosticLogLocation logLocation,
        TimeProvider timeProvider,
        string bundleDirectoryPath,
        string? userProfilePath,
        IPerformanceReportSource? performanceReports = null)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logLocation);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectoryPath);

        this.journal = journal;
        this.log = log;
        this.timeProvider = timeProvider;
        this.performanceReports = performanceReports;
        this.bundleDirectoryPath = bundleDirectoryPath;
        this.userProfilePath = userProfilePath;
        LogLocation = logLocation;
    }

    public IActivityJournalProjection Activity => journal;

    public IDiagnosticLogLocation LogLocation { get; }

    public void ClearLocalActivity()
    {
        journal.Clear();
        log.Clear();
    }

    public ValueTask<DiagnosticBundleSummary> ExportBundleAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);
        cancellationToken.ThrowIfCancellationRequested();

        return ValueTask.FromResult(
            DiagnosticBundleWriter.Write(destination, BuildContents()));
    }

    public async ValueTask<DiagnosticBundleSummary>
        ExportBundleToDefaultLocationAsync(
            CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        _ = Directory.CreateDirectory(bundleDirectoryPath);
        string path = Path.Combine(
            bundleDirectoryPath,
            DiagnosticBundleWriter.CreateFileName(timeProvider.GetUtcNow()));

        DiagnosticBundleSummary summary;
        await using (FileStream file = new(
            path,
            FileMode.Create,
            FileAccess.Write,
            FileShare.None))
        {
            summary = DiagnosticBundleWriter.Write(file, BuildContents());
        }

        return summary with
        {
            FilePath = path,
            DisplayPath = DiagnosticRedaction.Redact(path, userProfilePath),
        };
    }

    /// <summary>
    /// Assembles the bundle from the journal, the rolling log, and the
    /// performance counters.
    /// </summary>
    /// <remarks>
    /// The performance report is built here, at export time, and nowhere else.
    /// That is the only moment anything reads a processor or memory counter, so
    /// asking for a bundle is the whole cost of the measurement.
    /// </remarks>
    /// <returns>The contents to archive.</returns>
    public DiagnosticBundleContents BuildContents()
    {
        ImmutableArray<ActivityRecord> activity = [.. journal.Snapshot];
        ImmutableArray<DiagnosticBundleFile> logs = log.ReadAll();

        return new DiagnosticBundleContents(
            new DiagnosticBundleManifest(
                ProductInfo.ApplicationName,
                ResolveVersion(),
                timeProvider.GetUtcNow(),
                journal.SessionId,
                journal.SessionStartedAtUtc,
                DescribeEnvironment(),
                LogLocation.DisplayPath,
                log.Options.MaxFileSizeBytes,
                log.Options.MaxFileCount,
                activity.Length,
                logs.Length,
                DiagnosticBundleWriter.PrivacyNotice),
            activity,
            logs,
            performanceReports?.CreateReport());
    }

    private static DiagnosticEnvironmentSummary DescribeEnvironment() =>
        new(
            RuntimeInformation.OSDescription,
            RuntimeInformation.ProcessArchitecture.ToString(),
            RuntimeInformation.FrameworkDescription);

    private static string ResolveVersion() =>
        (Assembly.GetEntryAssembly() ??
            typeof(DiagnosticsCoordinator).Assembly)
            .GetName()
            .Version?
            .ToString() ??
        "0.0.0.0";
}

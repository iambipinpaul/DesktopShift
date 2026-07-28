using System.Collections.Immutable;
using System.Reflection;
using System.Runtime.InteropServices;
using DesktopShift.Core;
using DesktopShift.Core.Diagnostics;

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
    private readonly string bundleDirectoryPath;
    private readonly string? userProfilePath;

    public DiagnosticsCoordinator(
        IActivityJournal journal,
        RollingDiagnosticLog log,
        IDiagnosticLogLocation logLocation,
        TimeProvider timeProvider)
        : this(
            journal,
            log,
            logLocation,
            timeProvider,
            (logLocation as LocalAppDataDiagnosticLogLocation)?.BundleDirectoryPath ??
                logLocation.DirectoryPath,
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    internal DiagnosticsCoordinator(
        IActivityJournal journal,
        RollingDiagnosticLog log,
        IDiagnosticLogLocation logLocation,
        TimeProvider timeProvider,
        string bundleDirectoryPath,
        string? userProfilePath)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(log);
        ArgumentNullException.ThrowIfNull(logLocation);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentException.ThrowIfNullOrWhiteSpace(bundleDirectoryPath);

        this.journal = journal;
        this.log = log;
        this.timeProvider = timeProvider;
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
    /// Assembles the bundle from the journal and the rolling log.
    /// </summary>
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
            logs);
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

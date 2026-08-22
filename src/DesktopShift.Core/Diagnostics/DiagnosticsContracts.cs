using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Diagnostics;

/// <summary>
/// Where the application's own log files live.
/// </summary>
public interface IDiagnosticLogLocation
{
    /// <summary>The real directory, for opening it on the user's request.</summary>
    string DirectoryPath { get; }

    /// <summary>The same directory with the user profile redacted.</summary>
    string DisplayPath { get; }
}

/// <summary>
/// The user-initiated diagnostic actions the Activity view offers.
/// </summary>
/// <remarks>
/// Every action here is explicit. Nothing is exported, opened, uploaded, or
/// deleted unless the user asked for it, and clearing removes only records and
/// log files the application itself created.
/// </remarks>
public interface IDiagnosticsCoordinator
{
    /// <summary>The bounded activity journal the view reads.</summary>
    IActivityJournalProjection Activity { get; }

    /// <summary>Where the rolling logs are written.</summary>
    IDiagnosticLogLocation LogLocation { get; }

    /// <summary>
    /// Drops the in-memory journal and deletes the rolling log files the
    /// application owns.
    /// </summary>
    void ClearLocalActivity();

    /// <summary>
    /// Writes a diagnostic bundle into a caller-supplied stream. The bundle is
    /// produced and nothing more: it is never opened, uploaded, or shared.
    /// </summary>
    /// <param name="destination">The writable, seekable stream to fill.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <returns>What the bundle contained.</returns>
    ValueTask<DiagnosticBundleSummary> ExportBundleAsync(
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Writes a diagnostic bundle into the application's own diagnostics
    /// folder and reports where it landed.
    /// </summary>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <returns>What the bundle contained, including its path.</returns>
    ValueTask<DiagnosticBundleSummary> ExportBundleToDefaultLocationAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Turns the pipeline's own activity into privacy-safe journal entries and log
/// lines.
/// </summary>
/// <remarks>
/// This is the single crossing point between the assignment pipeline and
/// anything that is displayed, written to disk, or exported, which is what
/// makes the privacy invariant checkable in one place.
/// </remarks>
public sealed class ActivityDiagnosticsRecorder
{
    private readonly IActivityJournal journal;
    private readonly IDiagnosticLogWriter log;

    public ActivityDiagnosticsRecorder(
        IActivityJournal journal,
        IDiagnosticLogWriter log)
    {
        ArgumentNullException.ThrowIfNull(journal);
        ArgumentNullException.ThrowIfNull(log);
        this.journal = journal;
        this.log = log;
    }

    public void Record(WindowObservationActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Record(
            [ActivityRecordFactory.FromObservation(activity, journal.SessionId)]);
    }

    public void Record(WindowAssignmentActivity activity)
    {
        ArgumentNullException.ThrowIfNull(activity);
        Record(ActivityRecordFactory.FromAssignment(activity, journal.SessionId));
    }

    public void Record(TilingPlacementDeniedEventArgs denied)
    {
        ArgumentNullException.ThrowIfNull(denied);
        Record(
            [ActivityRecordFactory.FromTilingPlacementDenied(
                denied,
                journal.SessionId)]);
    }

    private void Record(ImmutableArray<ActivityRecord> records)
    {
        foreach (ActivityRecord record in records)
        {
            journal.Record(record);
            log.Write(record);
        }
    }
}

using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Diagnostics;

/// <summary>
/// Feeds the assignment pipeline's activity into the diagnostics journal and
/// the rolling log.
/// </summary>
/// <remarks>
/// <para>
/// This subscribes to the existing bounded projections rather than sitting in
/// the assignment path. Diagnostics must never be able to fail an assignment,
/// and an observer that can only be notified after the fact cannot.
/// </para>
/// <para>
/// For the same reason every callback swallows its own failures: a full disk or
/// a locked log file is a diagnostics problem, not a reason to stop moving the
/// user's windows.
/// </para>
/// </remarks>
internal sealed class DiagnosticActivityHostedService(
    IWindowObservationActivityProjection observationProjection,
    IWindowAssignmentActivityProjection assignmentProjection,
    ActivityDiagnosticsRecorder recorder,
    TilingCoordinator? tilingCoordinator = null) : IHostedService, IDisposable
{
    private bool subscribed;
    private bool disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);

        if (subscribed)
        {
            return Task.CompletedTask;
        }

        // Subscription only. The journal describes this session, and replaying
        // the projections' existing contents here would duplicate anything
        // that arrived while the host was still starting.
        subscribed = true;
        observationProjection.ActivityRecorded += OnObservationRecorded;
        assignmentProjection.ActivityRecorded += OnAssignmentRecorded;
        if (tilingCoordinator is not null)
        {
            tilingCoordinator.PlacementDenied += OnTilingPlacementDenied;
        }
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        Unsubscribe();
    }

    private void Unsubscribe()
    {
        if (!subscribed)
        {
            return;
        }

        subscribed = false;
        observationProjection.ActivityRecorded -= OnObservationRecorded;
        assignmentProjection.ActivityRecorded -= OnAssignmentRecorded;
        if (tilingCoordinator is not null)
        {
            tilingCoordinator.PlacementDenied -= OnTilingPlacementDenied;
        }
    }

    private void OnObservationRecorded(
        object? sender,
        WindowObservationActivityRecordedEventArgs args) =>
        Record(args.Activity);

    private void OnAssignmentRecorded(
        object? sender,
        WindowAssignmentActivityRecordedEventArgs args) =>
        Record(args.Activity);

    private void OnTilingPlacementDenied(
        object? sender,
        TilingPlacementDeniedEventArgs args)
    {
        try
        {
            recorder.Record(args);
        }
        catch (Exception)
        {
            // Recording diagnostics cannot invalidate a layout pass.
        }
    }

    private void Record(WindowObservationActivity activity)
    {
        try
        {
            recorder.Record(activity);
        }
        catch (Exception)
        {
            // Recording diagnostics can never invalidate an observation.
        }
    }

    private void Record(WindowAssignmentActivity activity)
    {
        try
        {
            recorder.Record(activity);
        }
        catch (Exception)
        {
            // Recording diagnostics can never invalidate an assignment.
        }
    }
}

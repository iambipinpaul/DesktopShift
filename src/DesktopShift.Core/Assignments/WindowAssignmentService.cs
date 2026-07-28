using DesktopShift.Core.Compatibility;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Core.Assignments;

public sealed class WindowAssignmentService(
    IWindowDesktopPlacementService placementService,
    IManagedDesktopReconciliationService reconciliationService,
    IWindowAssignmentActivitySink activitySink,
    TimeProvider timeProvider) : IWindowAssignmentService
{
    public async ValueTask<WindowAssignmentActivity> AssignAsync(
        WindowAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        Guid correlationId = Guid.NewGuid();
        DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
        long startedTimestamp = timeProvider.GetTimestamp();

        ManagedDesktopRuntimeMapping? mapping =
            reconciliationService.Current.Mappings.FirstOrDefault(
                item => string.Equals(
                    item.SemanticKey,
                    request.Rule.TargetDesktopKey,
                    StringComparison.OrdinalIgnoreCase));
        if (mapping is not { IsBound: true, RuntimeDesktopId: Guid targetDesktopId })
        {
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Skipped,
                    WindowAssignmentSkipReason.TargetDesktopUnresolved,
                    mapping?.RuntimeDesktopId,
                    previousDesktopId: null,
                    error: null),
                cancellationToken).ConfigureAwait(false);
        }

        DesktopTopologyProviderResult<Guid> currentDesktop;
        try
        {
            currentDesktop = await placementService
                .GetWindowDesktopIdAsync(request.WindowHandle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Failed,
                    WindowAssignmentSkipReason.None,
                    targetDesktopId,
                    previousDesktopId: null,
                    new WindowAssignmentError(
                        "assignment.current_desktop_exception",
                        "The window's current desktop could not be queried.",
                        exception.HResult)),
                cancellationToken).ConfigureAwait(false);
        }

        if (!currentDesktop.IsSuccess || currentDesktop.Value == Guid.Empty)
        {
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Failed,
                    WindowAssignmentSkipReason.None,
                    targetDesktopId,
                    previousDesktopId: null,
                    ToAssignmentError(
                        currentDesktop.Error,
                        "assignment.current_desktop_failed",
                        "The window's current desktop could not be queried.")),
                cancellationToken).ConfigureAwait(false);
        }

        Guid previousDesktopId = currentDesktop.Value;
        if (previousDesktopId == targetDesktopId)
        {
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Skipped,
                    WindowAssignmentSkipReason.AlreadyOnTargetDesktop,
                    targetDesktopId,
                    previousDesktopId,
                    error: null),
                cancellationToken).ConfigureAwait(false);
        }

        DesktopTopologyProviderResult move;
        try
        {
            move = await placementService
                .MoveWindowToDesktopAsync(
                    request.WindowHandle,
                    targetDesktopId,
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            move = DesktopTopologyProviderResult.Failed(
                "assignment.move_exception",
                "The window move failed unexpectedly.",
                exception.HResult);
        }

        WindowAssignmentActivity activity = move.IsSuccess
            ? CreateActivity(
                request,
                correlationId,
                startedAtUtc,
                startedTimestamp,
                WindowAssignmentOutcome.Succeeded,
                WindowAssignmentSkipReason.None,
                targetDesktopId,
                previousDesktopId,
                error: null)
            : CreateActivity(
                request,
                correlationId,
                startedAtUtc,
                startedTimestamp,
                WindowAssignmentOutcome.Failed,
                WindowAssignmentSkipReason.None,
                targetDesktopId,
                previousDesktopId,
                ToAssignmentError(
                    move.Error,
                    "assignment.move_failed",
                    "The window could not be moved to its managed desktop."));
        // The move result is post-commit state. Persist it even when the caller's
        // token becomes cancelled immediately after the documented API returns.
        return await RecordAsync(activity, CancellationToken.None)
            .ConfigureAwait(false);
    }

    private WindowAssignmentActivity CreateActivity(
        WindowAssignmentRequest request,
        Guid correlationId,
        DateTimeOffset startedAtUtc,
        long startedTimestamp,
        WindowAssignmentOutcome outcome,
        WindowAssignmentSkipReason skipReason,
        Guid? targetDesktopId,
        Guid? previousDesktopId,
        WindowAssignmentError? error) =>
        new(
            correlationId,
            startedAtUtc,
            timeProvider.GetElapsedTime(startedTimestamp),
            request.Trigger,
            request.WindowHandle,
            outcome,
            skipReason,
            request.Rule.Id,
            request.Rule.TargetDesktopKey,
            targetDesktopId,
            previousDesktopId,
            request.Identity,
            error);

    private async ValueTask<WindowAssignmentActivity> RecordAsync(
        WindowAssignmentActivity activity,
        CancellationToken cancellationToken)
    {
        await activitySink.RecordAsync(activity, cancellationToken)
            .ConfigureAwait(false);
        return activity;
    }

    private static WindowAssignmentError ToAssignmentError(
        DesktopTopologyProviderError? error,
        string fallbackCode,
        string fallbackMessage) =>
        error is null
            ? new WindowAssignmentError(fallbackCode, fallbackMessage)
            : new WindowAssignmentError(
                error.Code,
                error.Message,
                error.HResult,
                error.NativeErrorCode);
}

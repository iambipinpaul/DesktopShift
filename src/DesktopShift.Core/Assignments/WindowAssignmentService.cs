using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Assignments;

public sealed class WindowAssignmentService(
    IWindowDesktopPlacementService placementService,
    IManagedDesktopReconciliationService reconciliationService,
    IWindowAssignmentActivitySink activitySink,
    TimeProvider timeProvider,
    IDesktopSwitchCoordinator? switchCoordinator = null,
    IForegroundSwitchSuppression? suppression = null,
    INewWindowActivationTracker? activationTracker = null) :
    IWindowAssignmentService
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
        bool isFirstForegroundActivation = false;

        if (request.Trigger == WindowEventKind.ForegroundActivated)
        {
            if (suppression is not null &&
                suppression.TryConsume(
                    request.WindowHandle,
                    out Guid relatedCorrelationId))
            {
                return await RecordAsync(
                    CreateActivity(
                        request,
                        correlationId,
                        startedAtUtc,
                        startedTimestamp,
                        WindowAssignmentOutcome.Skipped,
                        WindowAssignmentSkipReason.SelfGeneratedForegroundSuppressed,
                        targetDesktopId: null,
                        previousDesktopId: null,
                        error: null,
                        WindowMoveOutcome.NotAttempted,
                        new DesktopSwitchResult(
                            DesktopSwitchOutcome.Suppressed,
                            DesktopSwitchDecisionReason.SelfGeneratedForegroundEvent,
                            TimeSpan.Zero,
                            RelatedCorrelationId: relatedCorrelationId)),
                    cancellationToken).ConfigureAwait(false);
            }

            isFirstForegroundActivation =
                activationTracker?.ConsumeFirstForegroundActivation(
                    request.WindowHandle) == true;
        }

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
        WindowMoveOutcome moveOutcome = WindowMoveOutcome.AlreadyCorrect;
        if (previousDesktopId != targetDesktopId)
        {
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
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
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

            if (!move.IsSuccess)
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
                        previousDesktopId,
                        ToAssignmentError(
                            move.Error,
                            "assignment.move_failed",
                            "The window could not be moved to its managed desktop."),
                        WindowMoveOutcome.Failed),
                    CancellationToken.None).ConfigureAwait(false);
            }

            moveOutcome = WindowMoveOutcome.Succeeded;
        }

        DesktopSwitchResult switchResult;
        if (switchCoordinator is null)
        {
            switchResult = new DesktopSwitchResult(
                DesktopSwitchOutcome.NotRequested,
                request.Trigger == WindowEventKind.ForegroundActivated &&
                request.Rule.SwitchPolicy == DesktopSwitchPolicy.Never
                    ? DesktopSwitchDecisionReason.PolicyNever
                    : DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
                TimeSpan.Zero);
        }
        else
        {
            try
            {
                switchResult = await switchCoordinator.ApplyAsync(
                    new DesktopSwitchRequest(
                        correlationId,
                        request.Trigger,
                        request.WindowHandle,
                        targetDesktopId,
                        request.Rule.SwitchPolicy,
                        isFirstForegroundActivation),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                switchResult = new DesktopSwitchResult(
                    DesktopSwitchOutcome.Failed,
                    DesktopSwitchDecisionReason.SwitchFailed,
                    TimeSpan.Zero,
                    new WindowAssignmentError(
                        "assignment.switch_exception",
                        "Desktop switching failed unexpectedly.",
                        exception.HResult));
            }
        }

        WindowAssignmentOutcome outcome =
            switchResult.Outcome == DesktopSwitchOutcome.Failed
                ? WindowAssignmentOutcome.Failed
                : moveOutcome == WindowMoveOutcome.Succeeded ||
                    switchResult.Outcome == DesktopSwitchOutcome.Succeeded
                    ? WindowAssignmentOutcome.Succeeded
                    : WindowAssignmentOutcome.Skipped;
        WindowAssignmentSkipReason skipReason =
            outcome == WindowAssignmentOutcome.Skipped &&
            moveOutcome == WindowMoveOutcome.AlreadyCorrect
                ? WindowAssignmentSkipReason.AlreadyOnTargetDesktop
                : WindowAssignmentSkipReason.None;
        WindowAssignmentActivity activity = CreateActivity(
            request,
            correlationId,
            startedAtUtc,
            startedTimestamp,
            outcome,
            skipReason,
            targetDesktopId,
            previousDesktopId,
            switchResult.Error,
            moveOutcome,
            switchResult);

        // Move/switch results are post-commit state. Persist them even when the
        // caller's token becomes cancelled immediately after an OS call returns.
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
        WindowAssignmentError? error,
        WindowMoveOutcome moveOutcome = WindowMoveOutcome.NotAttempted,
        DesktopSwitchResult? switchResult = null) =>
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
            error,
            moveOutcome,
            request.Rule.SwitchPolicy,
            switchResult?.Outcome ?? DesktopSwitchOutcome.NotRequested,
            switchResult?.DecisionReason ??
                DesktopSwitchDecisionReason.BackgroundEventMoveOnly,
            switchResult?.Duration ?? TimeSpan.Zero,
            switchResult?.RelatedCorrelationId);

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

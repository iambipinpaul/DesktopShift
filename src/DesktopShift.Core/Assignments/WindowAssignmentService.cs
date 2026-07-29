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
    INewWindowActivationTracker? activationTracker = null,
    IFirstDesktopLocator? firstDesktopLocator = null) :
    IWindowAssignmentService
{
    public async ValueTask<WindowAssignmentActivity> AssignAsync(
        WindowAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        // The observation already minted a correlation for this window event.
        // Reusing it is what makes the decision, the move, the switch, and the
        // result one readable story instead of four unrelated rows.
        Guid correlationId = request.CorrelationId == Guid.Empty
            ? Guid.NewGuid()
            : request.CorrelationId;
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

        DesktopResolution resolution = await ResolveTargetDesktopAsync(
            request,
            cancellationToken).ConfigureAwait(false);
        if (!resolution.IsBound)
        {
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Skipped,
                    WindowAssignmentSkipReason.TargetDesktopUnresolved,
                    resolution.DesktopId,
                    previousDesktopId: null,
                    resolution.Error),
                cancellationToken).ConfigureAwait(false);
        }

        Guid targetDesktopId = resolution.DesktopId!.Value;

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

        // A window Windows has not placed on a desktop yet is not a refusal, and
        // it arrives two ways: the not-tracked code, and a successful query that
        // carries no identifier. Either way there is no current desktop to
        // compare the target against, which is a different thing from the answer
        // being unavailable.
        bool hasCurrentDesktop =
            currentDesktop.IsSuccess && currentDesktop.Value != Guid.Empty;
        bool isUnplacedWindow =
            !hasCurrentDesktop &&
            (currentDesktop.IsSuccess ||
                currentDesktop.Error?.Code ==
                    "window_placement.window_not_tracked");
        if (!hasCurrentDesktop && !isUnplacedWindow)
        {
            bool windowNotTracked =
                IsWindowNotTracked(currentDesktop.Error);
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    windowNotTracked
                        ? WindowAssignmentOutcome.Skipped
                        : WindowAssignmentOutcome.Failed,
                    windowNotTracked
                        ? WindowAssignmentSkipReason.WindowNotTracked
                        : WindowAssignmentSkipReason.None,
                    targetDesktopId,
                    previousDesktopId: null,
                    ToAssignmentError(
                        currentDesktop.Error,
                        "assignment.current_desktop_failed",
                        "The window's current desktop could not be queried.")),
                cancellationToken).ConfigureAwait(false);
        }

        Guid? previousDesktopId =
            hasCurrentDesktop ? currentDesktop.Value : null;
        WindowMoveOutcome moveOutcome = WindowMoveOutcome.AlreadyCorrect;

        // An unplaced window is moved rather than abandoned. Moving a window
        // that already sits on the target desktop is a no-op, so being wrong
        // costs nothing, and if Windows still has not placed the window it
        // refuses with an HRESULT of its own instead of the window being left
        // behind until it next takes focus.
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
                bool windowNotTracked =
                    IsWindowNotTracked(move.Error);
                return await RecordAsync(
                    CreateActivity(
                        request,
                        correlationId,
                        startedAtUtc,
                        startedTimestamp,
                        windowNotTracked
                            ? WindowAssignmentOutcome.Skipped
                            : WindowAssignmentOutcome.Failed,
                        windowNotTracked
                            ? WindowAssignmentSkipReason.WindowNotTracked
                            : WindowAssignmentSkipReason.None,
                        targetDesktopId,
                        previousDesktopId,
                        ToAssignmentError(
                            move.Error,
                            "assignment.move_failed",
                            "The window could not be moved to its managed desktop."),
                        windowNotTracked
                            ? WindowMoveOutcome.WindowUnavailable
                            : WindowMoveOutcome.Failed),
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
                        isFirstForegroundActivation,
                        request.Rule.Destination ==
                            WindowRuleDestination.FirstDesktop),
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

    /// <summary>
    /// Where a request's rule sends the window, and whether that destination
    /// resolved to a desktop that exists right now.
    /// </summary>
    /// <param name="DesktopId">
    /// The runtime desktop, or the one a mapping named without being bound to
    /// it, so an unresolved assignment can still say what it was aiming at.
    /// </param>
    /// <param name="IsBound">Whether the window can actually be moved there.</param>
    /// <param name="Error">Why the destination did not resolve, when it did not.</param>
    private readonly record struct DesktopResolution(
        Guid? DesktopId,
        bool IsBound,
        WindowAssignmentError? Error = null);

    /// <summary>
    /// Resolves a rule's destination to a runtime desktop.
    /// </summary>
    /// <remarks>
    /// The sweep resolves by position rather than through reconciliation, which
    /// is the whole reason it is a dependable fallback: a Managed Desktop can
    /// come back missing or ambiguous, and position 0 cannot stop existing.
    /// </remarks>
    private async ValueTask<DesktopResolution> ResolveTargetDesktopAsync(
        WindowAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        if (request.Rule.Destination == WindowRuleDestination.FirstDesktop)
        {
            if (firstDesktopLocator is null)
            {
                return new DesktopResolution(
                    null,
                    IsBound: false,
                    new WindowAssignmentError(
                        "assignment.first_desktop_unavailable",
                        "This host cannot locate the first desktop, so unmanaged windows are left where they opened."));
            }

            DesktopTopologyProviderResult<Guid> first = await firstDesktopLocator
                .GetFirstDesktopIdAsync(cancellationToken)
                .ConfigureAwait(false);
            return first.IsSuccess && first.Value != Guid.Empty
                ? new DesktopResolution(first.Value, IsBound: true)
                : new DesktopResolution(
                    null,
                    IsBound: false,
                    ToAssignmentError(
                        first.Error,
                        "assignment.first_desktop_unresolved",
                        "The first desktop could not be located."));
        }

        ManagedDesktopRuntimeMapping? mapping =
            reconciliationService.Current.Mappings.FirstOrDefault(
                item => string.Equals(
                    item.SemanticKey,
                    request.Rule.TargetDesktopKey,
                    StringComparison.OrdinalIgnoreCase));

        return mapping is { IsBound: true, RuntimeDesktopId: Guid bound }
            ? new DesktopResolution(bound, IsBound: true)
            : new DesktopResolution(mapping?.RuntimeDesktopId, IsBound: false);
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

    private static bool IsWindowNotTracked(
        DesktopTopologyProviderError? error) =>
        error?.Code is
            "window_placement.window_not_tracked" or
            "window_placement.stale_window_handle";
}

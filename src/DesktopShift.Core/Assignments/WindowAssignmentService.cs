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
    IFirstDesktopLocator? firstDesktopLocator = null,
    PerWindowAssignmentGate? windowGate = null) :
    IWindowAssignmentService
{
    /// <summary>
    /// Keeps two assignments for one window from running at once.
    /// </summary>
    /// <remarks>
    /// Built here rather than required from the caller, so it is never null. The
    /// application shares one assignment service, which makes one gate per
    /// service one gate per window; a caller that constructs its own service
    /// gets the same protection without having to know this type exists. The
    /// parameter is there for tests that want to read the gate afterwards.
    /// </remarks>
    private readonly PerWindowAssignmentGate gate =
        windowGate ?? new PerWindowAssignmentGate();

    /// <summary>
    /// Places one window according to its rule, and switches the desktop if the
    /// rule's policy says the user asked for it.
    /// </summary>
    /// <remarks>
    /// Held against <see cref="PerWindowAssignmentGate"/> from end to end, so
    /// the placement this reads is the placement it acts on. Two assignments for
    /// the same window run one after the other; assignments for different
    /// windows do not wait for each other at all.
    /// </remarks>
    /// <param name="request">The window, the rule, and the correlation to use.</param>
    /// <param name="cancellationToken">Abandons the assignment.</param>
    /// <returns>What was decided and what it did.</returns>
    public async ValueTask<WindowAssignmentActivity> AssignAsync(
        WindowAssignmentRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        using PerWindowAssignmentGate.Lease lease = await gate
            .AcquireAsync(request.WindowHandle, cancellationToken)
            .ConfigureAwait(false);
        return await AssignExclusiveAsync(request, cancellationToken)
            .ConfigureAwait(false);
    }

    private async ValueTask<WindowAssignmentActivity> AssignExclusiveAsync(
        WindowAssignmentRequest request,
        CancellationToken cancellationToken)
    {
        // The observation already minted a correlation for this window event.
        // Reusing it is what makes the decision, the move, the switch, and the
        // result one readable story instead of four unrelated rows.
        Guid correlationId = request.CorrelationId == Guid.Empty
            ? Guid.NewGuid()
            : request.CorrelationId;
        DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
        long startedTimestamp = timeProvider.GetTimestamp();
        bool isFirstForegroundActivation = false;

        // A pin is the whole placement, so it is decided before anything that
        // belongs to a move: no desktop is resolved, the switch policy is never
        // consulted, and the mover is never reached. That is what keeps a
        // pinned window from being moved out from under the user while its rule
        // says the window belongs everywhere.
        if (request.Rule.Destination == WindowRuleDestination.PinnedToAllDesktops)
        {
            return await PinAsync(
                request,
                correlationId,
                startedAtUtc,
                startedTimestamp,
                cancellationToken).ConfigureAwait(false);
        }

        // Only the events that repair placement release a pin, and they release
        // it here, before the destination is resolved and before the window's
        // current desktop is read. Those steps exist to place the window, and a
        // pin an earlier rule left must not outlive that rule just because
        // placement cannot happen: an unresolved destination or an unreadable
        // desktop leaves the pin dropped rather than carried to some later
        // event. A release that is refused fails the assignment, because the
        // window may still be on every desktop and nothing below may read as
        // though it were free of the pin.
        if (WindowPinRelease.IsRepairEvent(request.Trigger))
        {
            DesktopTopologyProviderResult repairRelease;
            try
            {
                repairRelease = await WindowPinRelease
                    .ReleaseHeldPinAsync(
                        placementService,
                        request.WindowHandle,
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
                repairRelease = DesktopTopologyProviderResult.Failed(
                    "assignment.unpin_exception",
                    "The window's pin could not be released.",
                    exception.HResult);
            }

            if (!repairRelease.IsSuccess)
            {
                return await RecordAsync(
                    CreateActivity(
                        request,
                        correlationId,
                        startedAtUtc,
                        startedTimestamp,
                        WindowAssignmentOutcome.Failed,
                        WindowAssignmentSkipReason.None,
                        targetDesktopId: null,
                        previousDesktopId: null,
                        ToAssignmentError(
                            repairRelease.Error,
                            "assignment.unpin_failed",
                            "The window's pin could not be released before moving it."),
                        WindowMoveOutcome.Failed),
                    CancellationToken.None).ConfigureAwait(false);
            }
        }

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

        if (isUnplacedWindow &&
            request.Trigger == WindowEventKind.ForegroundActivated)
        {
            // A foreground event normally belongs to an existing visible
            // window. One temporary "not tracked" answer during activation is
            // not proof that the window needs to move. Moving it to the same
            // desktop makes the Shell cloak it and causes visible BSP flicker.
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Skipped,
                    WindowAssignmentSkipReason.WindowNotTracked,
                    targetDesktopId,
                    previousDesktopId: null,
                    ToAssignmentError(
                        currentDesktop.Error,
                        "window_placement.window_not_tracked",
                        "Windows temporarily stopped reporting the window's virtual desktop."),
                    WindowMoveOutcome.WindowUnavailable),
                cancellationToken).ConfigureAwait(false);
        }

        bool needsMove = previousDesktopId != targetDesktopId;

        // A non-repair event never releases a pin: a foreground activation must
        // never move a window out from under a click, and the lifecycle events a
        // move rule answers through the manual reassignment fallback are not
        // repairs either. Ask whether a pin is held before doing anything: a
        // window whose rule stopped pinning it keeps the pin — and its place —
        // until a repair event drops it, while a window that holds no pin moves
        // exactly as it always did.
        if (needsMove && !WindowPinRelease.IsRepairEvent(request.Trigger))
        {
            DesktopTopologyProviderResult<bool> heldPin;
            try
            {
                heldPin = await WindowPinRelease
                    .QueryHeldPinAsync(
                        placementService,
                        request.WindowHandle,
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
                heldPin = DesktopTopologyProviderResult<bool>.Failed(
                    "assignment.unpin_exception",
                    "The window's pin could not be released.",
                    exception.HResult);
            }

            if (!heldPin.IsSuccess)
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
                            heldPin.Error,
                            "assignment.unpin_failed",
                            "The window's pin could not be released before moving it."),
                        WindowMoveOutcome.Failed),
                    CancellationToken.None).ConfigureAwait(false);
            }

            if (heldPin.Value)
            {
                return await RecordAsync(
                    CreateActivity(
                        request,
                        correlationId,
                        startedAtUtc,
                        startedTimestamp,
                        WindowAssignmentOutcome.Skipped,
                        WindowAssignmentSkipReason.PinHeldUntilRepairEvent,
                        targetDesktopId,
                        previousDesktopId,
                        error: null,
                        WindowMoveOutcome.NotAttempted,
                        PinnedSwitchResult),
                    cancellationToken).ConfigureAwait(false);
            }
        }

        // An unplaced window is moved rather than abandoned. Moving a window
        // that already sits on the target desktop is a no-op, so being wrong
        // costs nothing, and if Windows still has not placed the window it
        // refuses with an HRESULT of its own instead of the window being left
        // behind until it next takes focus.
        if (needsMove)
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
                        isFirstForegroundActivation),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested &&
                moveOutcome != WindowMoveOutcome.Succeeded)
            {
                // Nothing has happened to the window yet, so unwinding loses
                // nothing and the caller's shutdown proceeds as it asked.
                throw;
            }
            catch (OperationCanceledException) when (
                cancellationToken.IsCancellationRequested)
            {
                // The move already landed. Throwing here would leave the user's
                // window somewhere new with nothing in Activity saying who put
                // it there, so the cancellation is reported as the reason the
                // switch did not follow rather than as a lost assignment.
                switchResult = new DesktopSwitchResult(
                    DesktopSwitchOutcome.NotRequested,
                    DesktopSwitchDecisionReason.SwitchCancelled,
                    TimeSpan.Zero);
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
    /// Pins one window to every virtual desktop, and records what happened.
    /// </summary>
    /// <remarks>
    /// Nothing is moved and no desktop is resolved: the pin itself is the
    /// placement. The three answers mirror a move's, so Activity reads pinned
    /// beside moved, already pinned beside already correct, and a pin that
    /// could not be applied beside a move that could not be made. On a host
    /// that never proved a pinning surface the outcome is a deliberate skip
    /// that says so, rather than a failure the user cannot act on.
    /// </remarks>
    private async ValueTask<WindowAssignmentActivity> PinAsync(
        WindowAssignmentRequest request,
        Guid correlationId,
        DateTimeOffset startedAtUtc,
        long startedTimestamp,
        CancellationToken cancellationToken)
    {
        DesktopTopologyProviderResult<bool> pinned;
        try
        {
            pinned = await placementService
                .GetWindowPinnedAsync(request.WindowHandle, cancellationToken)
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
                    targetDesktopId: null,
                    previousDesktopId: null,
                    new WindowAssignmentError(
                        "assignment.pin_state_exception",
                        "The window's pin state could not be queried.",
                        exception.HResult),
                    WindowMoveOutcome.NotAttempted,
                    PinnedSwitchResult),
                cancellationToken).ConfigureAwait(false);
        }

        if (!pinned.IsSuccess)
        {
            bool isUnsupported =
                pinned.Outcome == DesktopTopologyResultOutcome.Unsupported;
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    isUnsupported
                        ? WindowAssignmentOutcome.Skipped
                        : WindowAssignmentOutcome.Failed,
                    isUnsupported
                        ? WindowAssignmentSkipReason.PinUnavailable
                        : WindowAssignmentSkipReason.None,
                    targetDesktopId: null,
                    previousDesktopId: null,
                    ToAssignmentError(
                        pinned.Error,
                        "assignment.pin_state_failed",
                        "The window's pin state could not be queried."),
                    WindowMoveOutcome.NotAttempted,
                    PinnedSwitchResult),
                CancellationToken.None).ConfigureAwait(false);
        }

        if (pinned.Value)
        {
            // Already pinned is this rule's answer to "already on the target
            // desktop": the window is where the rule wants it, and nothing was
            // asked of the Shell this time.
            return await RecordAsync(
                CreateActivity(
                    request,
                    correlationId,
                    startedAtUtc,
                    startedTimestamp,
                    WindowAssignmentOutcome.Skipped,
                    WindowAssignmentSkipReason.AlreadyPinnedToAllDesktops,
                    targetDesktopId: null,
                    previousDesktopId: null,
                    error: null,
                    WindowMoveOutcome.AlreadyPinnedToAllDesktops,
                    PinnedSwitchResult),
                cancellationToken).ConfigureAwait(false);
        }

        DesktopTopologyProviderResult pin;
        try
        {
            pin = await placementService
                .PinWindowAsync(request.WindowHandle, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            pin = DesktopTopologyProviderResult.Failed(
                "assignment.pin_exception",
                "The window pin failed unexpectedly.",
                exception.HResult);
        }

        bool pinUnsupported =
            pin.Outcome == DesktopTopologyResultOutcome.Unsupported;

        // A pin result is post-commit state, so it is persisted even when the
        // caller's token becomes cancelled immediately after the call returns.
        return await RecordAsync(
            CreateActivity(
                request,
                correlationId,
                startedAtUtc,
                startedTimestamp,
                pin.IsSuccess
                    ? WindowAssignmentOutcome.Succeeded
                    : pinUnsupported
                        ? WindowAssignmentOutcome.Skipped
                        : WindowAssignmentOutcome.Failed,
                pinUnsupported
                    ? WindowAssignmentSkipReason.PinUnavailable
                    : WindowAssignmentSkipReason.None,
                targetDesktopId: null,
                previousDesktopId: null,
                pin.IsSuccess
                    ? null
                    : ToAssignmentError(
                        pin.Error,
                        "assignment.pin_failed",
                        "The window could not be pinned to every desktop."),
                pin.IsSuccess
                    ? WindowMoveOutcome.PinnedToAllDesktops
                    : WindowMoveOutcome.NotAttempted,
                PinnedSwitchResult),
            CancellationToken.None).ConfigureAwait(false);
    }

    /// <summary>
    /// The switch answer every pin gives: none was considered.
    /// </summary>
    /// <remarks>
    /// A pin never relocates the window, and the switch policy only says when a
    /// move may switch the foreground desktop. Recording the reason keeps
    /// Activity from reading as though a switch had been weighed and refused.
    /// </remarks>
    private static DesktopSwitchResult PinnedSwitchResult { get; } = new(
        DesktopSwitchOutcome.NotRequested,
        DesktopSwitchDecisionReason.PinnedToAllDesktops,
        TimeSpan.Zero);

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

using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;

namespace DesktopShift.Windows.Recovery;

/// <summary>
/// The two native registrations DesktopShift holds on Windows: the WinEvent
/// hooks that feed window observation, and the virtual-desktop topology
/// notification registration.
/// </summary>
/// <remarks>
/// <para>
/// Everything here is either a read or a re-registration. Nothing in this type
/// can create, delete, rename, reorder, or switch a desktop, and nothing in it
/// can move a window: the only topology calls it makes are a single current
/// desktop query and the notification registration itself.
/// </para>
/// <para>
/// Checking costs one query and nothing else. There is no loop, no retry, and no
/// poll, because a machine nobody is touching produces no signals and must
/// therefore do no work at all.
/// </para>
/// </remarks>
public sealed class WindowsNativeRegistrationSet : INativeRegistrationSet
{
    /// <summary>Reported when the WinEvent hooks could not be taken again.</summary>
    private const string WindowHooksCode = "recovery.window_hooks_not_restored";

    /// <summary>Reported when topology notifications could not be re-registered.</summary>
    private const string TopologyNotificationsCode =
        "recovery.topology_notifications_not_restored";

    private readonly IWindowEventSource eventSource;
    private readonly IRestartableWindowEventSource restartableEventSource;
    private readonly IDesktopTopologyProvider topologyProvider;

    /// <param name="eventSource">
    /// The window event source whose hooks recovery drops and takes again. It
    /// must also implement <see cref="IRestartableWindowEventSource"/>.
    /// </param>
    /// <param name="topologyProvider">
    /// The desktop topology provider that holds the notification registration.
    /// </param>
    /// <exception cref="ArgumentException">
    /// <paramref name="eventSource"/> cannot be restarted, and so could never
    /// recover from an Explorer restart.
    /// </exception>
    public WindowsNativeRegistrationSet(
        IWindowEventSource eventSource,
        IDesktopTopologyProvider topologyProvider)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        ArgumentNullException.ThrowIfNull(topologyProvider);

        // Composition is the right place to find this out. A source that cannot
        // be stopped would let every recovery pass report success while the
        // hooks stayed dead, and the only symptom a user would ever see is that
        // windows silently stopped being assigned after an Explorer restart.
        // Failing here, loudly, at the moment the graph is built, is far better
        // than never recovering and never saying so.
        if (eventSource is not IRestartableWindowEventSource restartable)
        {
            throw new ArgumentException(
                "Recovery needs a window event source whose hooks can be dropped " +
                "and taken again, so it must implement " +
                nameof(IRestartableWindowEventSource) + ".",
                nameof(eventSource));
        }

        this.eventSource = eventSource;
        restartableEventSource = restartable;
        this.topologyProvider = topologyProvider;
    }

    /// <summary>
    /// Reads whether the registrations DesktopShift holds are still real.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The hook answer is direct: the source knows whether it currently holds
    /// hooks. The topology answer has no direct equivalent — Windows offers no
    /// "is my notification registration still valid?" question — so it is
    /// answered by asking the bridge something it can only answer while it is
    /// alive.
    /// </para>
    /// <para>
    /// One current-desktop query is enough because the notification registration
    /// is held by the same native bridge object that answers the query. A bridge
    /// that still answers is a bridge that still holds the registration; a
    /// bridge whose shell has gone away fails the query, which is exactly the
    /// condition worth detecting. The query also changes nothing, so asking it
    /// is free of consequences.
    /// </para>
    /// <para>
    /// A provider that cannot observe topology changes is not asked at all. In
    /// Limited Mode the capability does not exist to begin with, so an absent
    /// registration is the normal, working state rather than breakage — which is
    /// why the answer carries <c>IsObservable</c> alongside it.
    /// </para>
    /// </remarks>
    /// <param name="cancellationToken">Cancels the check.</param>
    /// <returns>What the check found.</returns>
    public async ValueTask<NativeRegistrationValidity> CheckAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            bool hooksLive = eventSource.IsRunning;
            bool isObservable = topologyProvider.Capabilities.CanObserveTopologyChanges;

            if (!isObservable)
            {
                return new NativeRegistrationValidity(
                    WindowHooksLive: hooksLive,
                    TopologyNotificationsLive: false,
                    IsObservable: false,
                    hooksLive
                        ? "Window tracking is still running. Desktop change " +
                          "notifications are not available in this mode, so there " +
                          "is nothing there to have lost."
                        : "Window tracking has stopped, so new windows would go " +
                          "unnoticed. Desktop change notifications are not " +
                          "available in this mode.");
            }

            DesktopTopologyProviderResult<Guid> probe = await topologyProvider
                .GetCurrentDesktopIdAsync(cancellationToken)
                .ConfigureAwait(false);
            bool notificationsLive = probe.IsSuccess;

            return new NativeRegistrationValidity(
                WindowHooksLive: hooksLive,
                TopologyNotificationsLive: notificationsLive,
                IsObservable: true,
                (hooksLive, notificationsLive) switch
                {
                    (true, true) =>
                        "Window tracking and desktop change notifications are both " +
                        "still working.",
                    (true, false) =>
                        "Window tracking is still running, but the desktop " +
                        "connection stopped answering, so desktop changes are no " +
                        "longer being reported.",
                    (false, true) =>
                        "Desktop change notifications are still working, but window " +
                        "tracking has stopped, so new windows would go unnoticed.",
                    _ =>
                        "Neither window tracking nor the desktop connection " +
                        "survived, so nothing is being observed.",
                });
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A check that could throw would turn "I do not know" into a crash.
            // Not knowing is a real answer here — Unknown never reads as intact,
            // so the caller rebuilds rather than assuming. Caller cancellation
            // is the one exception: shutdown must not turn an abandoned check
            // into a fresh registration pass.
            return NativeRegistrationValidity.Unknown(
                "DesktopShift could not tell whether its Windows registrations " +
                "are still valid. " + exception.Message);
        }
    }

    /// <summary>
    /// Drops the hooks taken against the shell that has gone away.
    /// </summary>
    /// <remarks>
    /// This cannot fail. Unhooking a hook whose shell is already gone is not an
    /// error — Windows has thrown the registration away, and saying so twice
    /// changes nothing — so a failure to unhook is swallowed rather than
    /// reported. The failure genuinely worth preventing is the opposite one:
    /// leaving a stale hook installed while taking a second one, which is why
    /// this runs before re-registration rather than instead of it.
    /// </remarks>
    /// <param name="cancellationToken">
    /// Ignored. Dropping registrations half-way is worse than either dropping
    /// them or not starting, and this is short enough that there is nothing to
    /// cancel.
    /// </param>
    /// <returns>A completed task.</returns>
    public ValueTask InvalidateAsync(CancellationToken cancellationToken = default)
    {
        _ = cancellationToken;

        try
        {
            restartableEventSource.Stop();
        }
        catch (Exception)
        {
            // Deliberately swallowed; see the remarks above.
        }

        return ValueTask.CompletedTask;
    }

    /// <summary>
    /// Takes the registrations again, after capability validation has succeeded.
    /// </summary>
    /// <param name="cancellationToken">Cancels the re-registration.</param>
    /// <returns>
    /// Success only when the hooks are genuinely running again, and a stable
    /// <c>recovery.</c> code naming the step that failed otherwise.
    /// </returns>
    public async ValueTask<NativeRegistrationResult> ReregisterAsync(
        CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            eventSource.Start();
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            // A shell that is still coming up refuses hooks. That is a reported
            // failure, not a thrown one: the app keeps running, says automatic
            // assignment is not working, and never retries Explorer.
            return NativeRegistrationResult.Failed(
                WindowHooksCode,
                "DesktopShift could not start watching windows again. " +
                exception.Message,
                exception.HResult);
        }

        if (topologyProvider.Capabilities.CanObserveTopologyChanges)
        {
            NativeRegistrationResult? failure =
                await TryStartTopologyNotificationsAsync(cancellationToken)
                    .ConfigureAwait(false);
            if (failure is not null)
            {
                return failure;
            }
        }

        // Success is claimed from the state Windows is actually in, not from the
        // fact that the calls returned without complaining. A source that
        // accepted Start and still holds no hooks is a failed recovery, and
        // reporting it as one is what stops the app insisting it is healthy
        // while nothing is being observed.
        return eventSource.IsRunning
            ? NativeRegistrationResult.Succeeded
            : NativeRegistrationResult.Failed(
                WindowHooksCode,
                "DesktopShift asked Windows for window notifications again and " +
                "did not get them.");
    }

    /// <summary>
    /// Re-registers topology notifications, returning the failure to report or
    /// null when it worked.
    /// </summary>
    private async ValueTask<NativeRegistrationResult?> TryStartTopologyNotificationsAsync(
        CancellationToken cancellationToken)
    {
        DesktopTopologyProviderResult result;
        try
        {
            result = await topologyProvider
                .StartTopologyNotificationsAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception exception)
        {
            return NativeRegistrationResult.Failed(
                TopologyNotificationsCode,
                "DesktopShift could not start listening for desktop changes " +
                "again. " + exception.Message,
                exception.HResult);
        }

        if (result.IsSuccess)
        {
            return null;
        }

        // The provider's own code names where inside the native bridge it went
        // wrong, which is worth keeping in the message. The code this result
        // carries stays the stable recovery one, so a reader filtering their
        // diagnostics for a failed recovery finds every failed recovery.
        return NativeRegistrationResult.Failed(
            TopologyNotificationsCode,
            result.Error is null
                ? "DesktopShift could not start listening for desktop changes again."
                : "DesktopShift could not start listening for desktop changes " +
                  $"again. {result.Error.Message} ({result.Error.Code})",
            result.Error?.HResult);
    }
}

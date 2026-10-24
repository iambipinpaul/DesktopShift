using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Assignments;

/// <summary>
/// Returns a window an earlier pin kept on every desktop to normal placement.
/// </summary>
/// <remarks>
/// <para>
/// The release is lazy by design. Disabling or deleting a pin rule never
/// enumerates windows: the pin is dropped on the window's next observed event,
/// which is also the moment the rule that now matches has to be applied. A rule
/// edit therefore never touches windows the user is not looking at, and it
/// costs nothing until the events that follow it.
/// </para>
/// <para>
/// The pin state is queried before anything is released, so a window that was
/// never pinned is never handed to the pin surface at all. A host that cannot
/// report pin state — Limited Mode, or a build whose pin layout was never
/// proved — is told the release succeeded, because no pin can be holding the
/// window there and the placement that follows must not be blocked by a
/// surface that does not exist. A window the Shell has no view for is treated
/// the same way. Any other refusal leaves the pin state unknown, and an unknown
/// pin state is reported rather than moved past: the window may still be on
/// every desktop, and moving it would leave it there while its rule says
/// otherwise.
/// </para>
/// </remarks>
internal static class WindowPinRelease
{
    /// <summary>
    /// Releases a held pin, if there is one.
    /// </summary>
    /// <param name="placement">The placement seam the pin lives on.</param>
    /// <param name="windowHandle">The root window handle to release.</param>
    /// <param name="cancellationToken">Abandons the release.</param>
    /// <returns>
    /// The release result, or success when no pin was held, when the host
    /// cannot report one, or when the window is one the Shell has no view for.
    /// </returns>
    public static async ValueTask<DesktopTopologyProviderResult>
        ReleaseHeldPinAsync(
            IWindowDesktopPlacementService placement,
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);

        DesktopTopologyProviderResult<bool> pinned = await QueryHeldPinAsync(
            placement,
            windowHandle,
            cancellationToken).ConfigureAwait(false);

        if (!pinned.IsSuccess)
        {
            return new DesktopTopologyProviderResult(
                pinned.Outcome,
                pinned.Error);
        }

        return pinned.Value != true
            ? DesktopTopologyProviderResult.Succeeded()
            : await placement
                .UnpinWindowAsync(windowHandle, cancellationToken)
                .ConfigureAwait(false);
    }

    /// <summary>
    /// Whether a pin currently holds the window on every desktop.
    /// </summary>
    /// <remarks>
    /// The three answers keep the shape the release needs: true means a pin
    /// holds the window, false means none does — including a host that cannot
    /// pin at all, and a window the Shell has no view for, because no pin can be
    /// holding the window there either — and a failure means the pin state could
    /// not be established, which is never read as "not pinned".
    /// </remarks>
    /// <param name="placement">The placement seam the pin lives on.</param>
    /// <param name="windowHandle">The root window handle to query.</param>
    /// <param name="cancellationToken">Abandons the query.</param>
    /// <returns>Whether a pin holds the window.</returns>
    public static async ValueTask<DesktopTopologyProviderResult<bool>>
        QueryHeldPinAsync(
            IWindowDesktopPlacementService placement,
            nint windowHandle,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(placement);

        DesktopTopologyProviderResult<bool> pinned = await placement
            .GetWindowPinnedAsync(windowHandle, cancellationToken)
            .ConfigureAwait(false);

        if (pinned.Outcome == DesktopTopologyResultOutcome.Unsupported)
        {
            return DesktopTopologyProviderResult<bool>.Succeeded(false);
        }

        return pinned.IsSuccess || !IsWindowNotTracked(pinned.Error)
            ? pinned
            : DesktopTopologyProviderResult<bool>.Succeeded(false);
    }

    /// <summary>
    /// Whether this event is one that repairs placement.
    /// </summary>
    /// <remarks>
    /// These are the events that release a pin a rule no longer holds. They are
    /// deliberately the same set the unmanaged sweep answers — which is why
    /// this defers to the sweep rather than repeating the list, so the two
    /// cannot drift apart. A foreground activation is not one of them:
    /// releasing a pin changes placement, and that event must never move a
    /// window out from under a click.
    /// </remarks>
    /// <param name="trigger">The window event being observed.</param>
    /// <returns>Whether a held pin is released on this event.</returns>
    public static bool IsRepairEvent(WindowEventKind trigger) =>
        UnmanagedWindowSweep.AnswersEvent(trigger);

    /// <summary>
    /// Whether a pin rule answers a window event of this kind.
    /// </summary>
    /// <remarks>
    /// This is the whole set a pin rule answers. Its stored triggers are not
    /// read, exactly as its stored target desktop is not read: the rule answers
    /// the events that keep a pin repaired on every desktop, plus the foreground
    /// activation that re-asserts a lost pin without moving the window. A pin
    /// rule that answered only its configured triggers would let a rule carried
    /// over from a move rule — or saved with none, which validation allows
    /// because the field is not read — name an application and never pin it.
    /// </remarks>
    /// <param name="trigger">The window event being observed.</param>
    /// <returns>Whether a pin rule answers the event.</returns>
    public static bool PinAnswersEvent(WindowEventKind trigger) =>
        IsRepairEvent(trigger) ||
        trigger == WindowEventKind.ForegroundActivated;

    private static bool IsWindowNotTracked(
        DesktopTopologyProviderError? error) =>
        error?.Code is
            "window_placement.window_not_tracked" or
            "window_placement.stale_window_handle";
}

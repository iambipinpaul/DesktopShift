namespace DesktopShift.Core.Tiling;

/// <summary>
/// Whether an assignment left the window somewhere tiling can place it.
/// </summary>
public enum TilingAssignmentDisposition
{
    /// <summary>
    /// The window moved to its target desktop, which may be the one on screen.
    /// It is eligible for a tile once that desktop is visible.
    /// </summary>
    PlacedOnTargetDesktop,

    /// <summary>
    /// The window already sat on its target desktop. If that desktop is the
    /// visible one, it is eligible right away.
    /// </summary>
    AlreadyInPlace,

    /// <summary>
    /// The move failed or did not happen. The window stays out of the tree
    /// until another event vouches for it.
    /// </summary>
    NotPlaced,
}

/// <param name="WindowHandle">The window the assignment finished for.</param>
/// <param name="TargetDesktopKey">
/// The configured desktop key the window was assigned to.
/// </param>
/// <param name="Disposition">Where the window ended up.</param>
public readonly record struct TilingAssignmentNotification(
    nint WindowHandle,
    string TargetDesktopKey,
    TilingAssignmentDisposition Disposition);

/// <summary>
/// The three pipeline moments tiling needs to hear about. Implementations run
/// on the observation pipeline thread and must return quickly; anything slow
/// is the coordinator's job behind its own queue.
/// </summary>
public interface ITilingTrigger
{
    /// <summary>
    /// Visibility or window state changed. The event vouches for its HWND, then
    /// the window is re-read so a hidden leaf is removed or an eligible window
    /// is admitted again.
    /// </summary>
    void NotifyWindowStateChanged(nint windowHandle);

    /// <summary>
    /// The user finished moving or sizing a window. The destination monitor it
    /// landed on wins; the owning layout is reconciled once.
    /// </summary>
    void NotifyMoveSizeEnded(nint windowHandle);

    /// <summary>
    /// A window closed. Its tile token is dropped and the layout closes over
    /// the gap.
    /// </summary>
    void NotifyWindowDestroyed(nint windowHandle);

    /// <summary>
    /// An assignment finished for a window. This is what admits new windows
    /// into the tree once they are known to live on a managed desktop.
    /// </summary>
    void NotifyAssignmentCompleted(in TilingAssignmentNotification notification);
}

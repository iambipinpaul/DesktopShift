using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tiling;

/// <summary>What happened when the adapter tried to read one window.</summary>
/// <remarks>
/// Elevated and protected windows can refuse every read. That refusal is a
/// normal answer here, not an error path: the window is skipped and reported,
/// and nothing in DesktopShift ever answers it with elevation, UIAccess, or
/// broader process rights.
/// </remarks>
public enum TilingReadStatus
{
    /// <summary>The read succeeded.</summary>
    Succeeded,

    /// <summary>The handle no longer names a window.</summary>
    WindowGone,

    /// <summary>The window refused the read.</summary>
    AccessDenied,

    /// <summary>
    /// The read failed for a reason that is neither of the above — a missing
    /// DWM attribute on this Windows build, for example.
    /// </summary>
    Unavailable,
}

/// <summary>Why DWM has cloaked a window.</summary>
[Flags]
public enum TilingCloakReason
{
    /// <summary>The window is not cloaked, or DWM did not report a reason.</summary>
    None = 0,

    /// <summary>The application cloaked its own window.</summary>
    Application = 1,

    /// <summary>The Windows shell cloaked the window.</summary>
    Shell = 2,

    /// <summary>The window inherited its cloak from an owner.</summary>
    Inherited = 4,
}

/// <summary>One window's placement-relevant state, read once, on demand.</summary>
/// <param name="WindowRectPixels">
/// The outer rectangle including invisible resize borders, screen coordinates.
/// </param>
/// <param name="VisibleFramePixels">
/// The rectangle the user sees, from DWM extended frame bounds. Tiling targets
/// this, not the outer rectangle.
/// </param>
/// <param name="IsVisible">Whether the window would paint at all.</param>
/// <param name="IsMinimized">Iconic windows are never placed.</param>
/// <param name="IsMaximized">
/// Maximized windows are skipped by policy until they return to normal state.
/// </param>
/// <param name="IsCloaked">
/// Cloaked windows live off-screen in the shell's keeping; moving them is
/// pointless while they stay cloaked.
/// </param>
/// <param name="MonitorHandle">
/// The HMONITOR of the monitor the window sits on, or zero when unknown.
/// </param>
/// <param name="DesktopId">
/// The virtual desktop GUID, or null when the documented manager refused to
/// say — which elevated windows do.
/// </param>
/// <param name="CloakReasons">
/// The DWM cloak reason flags. Shell cloaking is temporary during a virtual
/// desktop switch, so the coordinator can keep that window's BSP slot.
/// </param>
public sealed record TilingWindowState(
    TileRect WindowRectPixels,
    TileRect VisibleFramePixels,
    bool IsVisible,
    bool IsMinimized,
    bool IsMaximized,
    bool IsCloaked,
    nint MonitorHandle,
    Guid? DesktopId,
    TilingCloakReason CloakReasons = TilingCloakReason.None)
{
    /// <summary>Whether the Windows shell supplied the cloak.</summary>
    public bool IsShellCloaked =>
        (CloakReasons & TilingCloakReason.Shell) != 0;

    /// <summary>
    /// How much invisible border each edge adds outside the visible frame.
    /// A tile is expressed in visible-frame terms, so placing means growing
    /// the target rectangle by exactly these margins.
    /// </summary>
    public (int Left, int Top, int Right, int Bottom) FrameMargins =>
        (
            VisibleFramePixels.X - WindowRectPixels.X,
            VisibleFramePixels.Y - WindowRectPixels.Y,
            WindowRectPixels.X + WindowRectPixels.Width -
                (VisibleFramePixels.X + VisibleFramePixels.Width),
            WindowRectPixels.Y + WindowRectPixels.Height -
                (VisibleFramePixels.Y + VisibleFramePixels.Height));
}

/// <summary>The outcome of one read attempt, successful or not.</summary>
public sealed record TilingWindowReading(TilingReadStatus Status, TilingWindowState? State);

/// <summary>
/// One monitor as the current topology reports it.
/// </summary>
/// <param name="Handle">The HMONITOR, valid only right now.</param>
/// <param name="DeviceKey">
/// The runtime monitor identity, valid only for this topology. Never an
/// HMONITOR: those values are reused after display changes.
/// </param>
/// <param name="WorkAreaPixels">
/// The area outside taskbars and appbars, physical pixels.
/// </param>
/// <param name="MonitorBoundsPixels">
/// The monitor's full rectangle, physical pixels. The full-screen heuristic
/// measures against this, not against the work area.
/// </param>
/// <param name="DpiX">The effective DPI used to scale gap and floor units.</param>
public sealed record TilingMonitorInfo(
    nint Handle,
    string DeviceKey,
    TileRect WorkAreaPixels,
    TileRect MonitorBoundsPixels,
    uint DpiX);

/// <summary>
/// Reads monitors and their work areas for the current topology.
/// </summary>
/// <remarks>
/// Implementations rebuild their notion of "which monitors exist" whenever the
/// display topology changes; callers must treat every returned
/// <see cref="TilingMonitorInfo.DeviceKey"/> as valid only between rebuilds.
/// </remarks>
public interface ITilingMonitorCatalog
{
    /// <summary>All monitors right now, work areas included.</summary>
    IReadOnlyList<TilingMonitorInfo> ReadMonitors();
}

/// <summary>Reads one window's placement state on demand. No polling.</summary>
public interface ITilingWindowReader
{
    /// <summary>Reads everything placement needs about one window.</summary>
    TilingWindowReading Read(nint windowHandle);
}

/// <summary>Reads the window that currently owns keyboard focus.</summary>
public interface ITilingFocusReader
{
    /// <summary>The focused top-level window, or zero when none is available.</summary>
    nint ReadFocusedWindow();
}

/// <summary>
/// Resolves a window's application identity for classification.
/// </summary>
/// <remarks>
/// A seam over the identity resolver so the coordinator stays free of native
/// process reads. Resolution fails for elevated and protected windows; the
/// classifier treats that as "ignore", which is the safe answer.
/// </remarks>
public interface ITilingIdentitySource
{
    /// <summary>
    /// The window's identity, or null when it could not be resolved.
    /// </summary>
    ValueTask<WindowIdentity?> ResolveAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Commits one complete layout as one atomic deferred-position batch.
/// </summary>
public interface ITilingPlacementExecutor
{
    /// <summary>Applies every placement as few committed batches as possible.</summary>
    TilingBatchResult Apply(IReadOnlyList<TilingPlacementRequest> placements);
}

/// <summary>One requested move, in outer-window coordinates.</summary>
/// <param name="WindowHandle">The window to move.</param>
/// <param name="WindowRectPixels">
/// The outer rectangle to set, already grown by the frame margins so the
/// visible frame lands on the planned tile.
/// </param>
public sealed record TilingPlacementRequest(nint WindowHandle, TileRect WindowRectPixels);

/// <summary>How one committed-or-abandoned batch ended.</summary>
public enum TilingBatchOutcome
{
    /// <summary>EndDeferWindowPos reported success.</summary>
    Committed,

    /// <summary>BeginDeferWindowPos refused the batch outright.</summary>
    BeginFailed,

    /// <summary>
    /// DeferWindowPos failed part way through. The uncommitted batch was
    /// abandoned as Microsoft requires; any remaining placements were rebuilt
    /// into a new batch.
    /// </summary>
    DeferFailed,

    /// <summary>
    /// EndDeferWindowPos failed after every placement was queued. Windows does
    /// not say which HWND was at fault, so the whole batch is reported failed.
    /// </summary>
    EndFailed,
}

/// <summary>
/// One window that Windows rejected before a placement batch could commit.
/// </summary>
/// <param name="WindowHandle">The rejected top-level window.</param>
/// <param name="NativeErrorCode">
/// The Win32 error from DeferWindowPos, or zero when Windows supplied none.
/// </param>
public sealed record TilingPlacementRejection(
    nint WindowHandle,
    int NativeErrorCode);

/// <summary>Structured, title-free result of one Apply call.</summary>
public sealed record TilingBatchResult
{
    /// <summary>Creates the result of one placement batch.</summary>
    /// <param name="outcome">How the batching ended.</param>
    /// <param name="committedWindows">
    /// Every window whose position reached a successfully committed batch.
    /// </param>
    /// <param name="skippedWindows">
    /// Windows dropped because DeferWindowPos rejected them individually.
    /// </param>
    /// <param name="failedWindows">
    /// For EndFailed only: every window in the batch Windows could not commit.
    /// </param>
    /// <param name="placementRejections">
    /// Native error details for individually rejected windows.
    /// </param>
    public TilingBatchResult(
        TilingBatchOutcome outcome,
        IReadOnlyList<nint> committedWindows,
        IReadOnlyList<nint> skippedWindows,
        IReadOnlyList<nint> failedWindows,
        IReadOnlyList<TilingPlacementRejection>? placementRejections = null)
    {
        Outcome = outcome;
        CommittedWindows = committedWindows;
        SkippedWindows = skippedWindows;
        FailedWindows = failedWindows;
        PlacementRejections = placementRejections ?? [];
    }

    public TilingBatchOutcome Outcome { get; init; }

    public IReadOnlyList<nint> CommittedWindows { get; init; }

    public IReadOnlyList<nint> SkippedWindows { get; init; }

    public IReadOnlyList<nint> FailedWindows { get; init; }

    /// <summary>Native errors for individually rejected windows.</summary>
    public IReadOnlyList<TilingPlacementRejection> PlacementRejections { get; init; }

    /// <summary>A fully committed batch with nothing skipped.</summary>
    public static TilingBatchResult CommittedAll(IReadOnlyList<nint> windows) =>
        new(TilingBatchOutcome.Committed, windows, [], []);
}

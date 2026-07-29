using System.Runtime.InteropServices;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Windows.VirtualDesktops;
using DesktopShift.Windows.VirtualDesktops.NativeBridge;

namespace DesktopShift.Windows.Assignments;

public sealed class WindowsWindowDesktopPlacementService :
    IWindowDesktopPlacementService,
    IDisposable
{
    private const int UnexpectedResultHResult =
        unchecked((int)0x8000FFFF);
    private const int AccessDeniedHResult = unchecked((int)0x80070005);
    private const int WindowNotTrackedHResult = unchecked((int)0x8002802B);
    private const string ManagerActivationStage = "ManagerActivation";

    private readonly IWindowHandleApi windowApi;
    private readonly IDocumentedVirtualDesktopManagerApi desktopManager;
    private readonly IValidatedWindowDesktopMover desktopMover;
    private readonly bool ownsDesktopManager;
    private bool disposed;

    public WindowsWindowDesktopPlacementService(
        ValidatedVirtualDesktopTopologyProvider topologyProvider)
        : this(
            new WindowHandleApi(),
            new DocumentedVirtualDesktopManagerApi(),
            topologyProvider,
            ownsDesktopManager: true)
    {
    }

    internal WindowsWindowDesktopPlacementService(
        IWindowHandleApi windowApi,
        IDocumentedVirtualDesktopManagerApi desktopManager,
        IValidatedWindowDesktopMover desktopMover,
        bool ownsDesktopManager = false)
    {
        this.windowApi =
            windowApi ?? throw new ArgumentNullException(nameof(windowApi));
        this.desktopManager =
            desktopManager ?? throw new ArgumentNullException(nameof(desktopManager));
        this.desktopMover =
            desktopMover ?? throw new ArgumentNullException(nameof(desktopMover));
        this.ownsDesktopManager = ownsDesktopManager;
    }

    public ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        DesktopTopologyProviderResult<Guid>? invalidResult =
            ValidateWindow<Guid>(windowHandle);
        if (invalidResult is not null)
        {
            return ValueTask.FromResult(invalidResult);
        }

        DocumentedDesktopIdResult result =
            desktopManager.GetWindowDesktopId(windowHandle);
        if (!result.IsSuccess)
        {
            return ValueTask.FromResult(ClassifyQueryFailure(result));
        }

        if (result.DesktopId == Guid.Empty)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    "window_placement.invalid_desktop_result",
                    "Windows returned an empty virtual desktop identifier.",
                    UnexpectedResultHResult));
        }

        return ValueTask.FromResult(
            DesktopTopologyProviderResult<Guid>.Succeeded(result.DesktopId));
    }

    public ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
        nint windowHandle,
        Guid desktopId,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        if (desktopId == Guid.Empty)
        {
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    "window_placement.invalid_desktop_id",
                    "A non-empty target virtual desktop identifier is required."));
        }

        DesktopTopologyProviderResult? invalidResult =
            ValidateWindow(windowHandle);
        if (invalidResult is not null)
        {
            return ValueTask.FromResult(invalidResult);
        }

        // One attempt, then a structured refusal. Windows, not DesktopShift,
        // decides whether a window may leave its desktop, and re-issuing the
        // identical call cannot change that answer, so a refusal is reported
        // rather than retried.
        NativeBridgeResult result =
            desktopMover.MoveWindowToDesktop(windowHandle, desktopId);
        return ValueTask.FromResult(
            result.IsSuccess
                ? DesktopTopologyProviderResult.Succeeded()
                : ClassifyMoveFailure(windowHandle, result));
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (ownsDesktopManager)
        {
            desktopManager.Dispose();
        }
    }

    private DesktopTopologyProviderResult<T>? ValidateWindow<T>(
        nint windowHandle)
    {
        DesktopTopologyProviderError? error = GetWindowValidationError(windowHandle);
        return error is null
            ? null
            : DesktopTopologyProviderResult<T>.Failed(
                error.Code,
                error.Message,
                error.HResult,
                error.NativeErrorCode);
    }

    private DesktopTopologyProviderResult? ValidateWindow(nint windowHandle)
    {
        DesktopTopologyProviderError? error = GetWindowValidationError(windowHandle);
        return error is null
            ? null
            : DesktopTopologyProviderResult.Failed(
                error.Code,
                error.Message,
                error.HResult,
                error.NativeErrorCode);
    }

    private DesktopTopologyProviderError? GetWindowValidationError(
        nint windowHandle)
    {
        if (windowHandle == 0)
        {
            return new DesktopTopologyProviderError(
                "window_placement.invalid_window_handle",
                "A nonzero top-level window handle is required.");
        }

        if (!windowApi.IsWindow(windowHandle))
        {
            return new DesktopTopologyProviderError(
                "window_placement.stale_window_handle",
                "The top-level window handle is no longer live.");
        }

        nint rootWindow = windowApi.GetRootWindow(windowHandle);
        if (rootWindow == 0 || !windowApi.IsWindow(rootWindow))
        {
            return new DesktopTopologyProviderError(
                "window_placement.stale_window_handle",
                "The top-level window handle is no longer live.");
        }

        if (rootWindow != windowHandle)
        {
            return new DesktopTopologyProviderError(
                "window_placement.not_top_level_window",
                "The window handle does not identify a top-level window.");
        }

        return null;
    }

    /// <summary>
    /// Turns a refused move into a failure a reader of Activity can act on.
    /// </summary>
    /// <remarks>
    /// Only distinctions Windows actually states are drawn. An access denial is
    /// a defined <c>HRESULT</c>, and a window that stopped existing can be
    /// observed directly. Every other refusal keeps the generic code and
    /// carries the exact <c>HRESULT</c> the Shell bridge returned, because no other
    /// mapping from a refusal to a cause is contractual. A full-screen remote
    /// session lands here: it is a window Windows is managing itself, and the
    /// honest report is that the move was refused, with the code Windows gave.
    /// </remarks>
    /// <param name="windowHandle">The window whose move was refused.</param>
    /// <param name="result">The refusal the validated Shell bridge returned.</param>
    /// <returns>A failed result naming the most specific known cause.</returns>
    private DesktopTopologyProviderResult ClassifyMoveFailure(
        nint windowHandle,
        NativeBridgeResult result)
    {
        NativeBridgeError error = result.Error ??
            new NativeBridgeError(
                "native.window_move_failed",
                "WindowMove",
                "The native window move failed without structured error details.",
                UnexpectedResultHResult);

        if (string.Equals(
                error.Stage,
                "ProviderSelection",
                StringComparison.Ordinal) ||
            string.Equals(
                error.Code,
                "native.window_move_unavailable",
                StringComparison.Ordinal))
        {
            return DesktopTopologyProviderResult.Failed(
                "window_placement.move_unavailable",
                "DesktopShift could not attempt the window move because no validated native adapter is available. " +
                error.Message,
                error.HResult);
        }

        if (string.Equals(
            error.Code,
            "native.window_move_exception",
            StringComparison.Ordinal))
        {
            return DesktopTopologyProviderResult.Failed(
                "window_placement.move_exception",
                "DesktopShift could not complete the native window-move call. " +
                error.Message,
                error.HResult);
        }

        if (string.Equals(
            error.Stage,
            ManagerActivationStage,
            StringComparison.Ordinal))
        {
            return DesktopTopologyProviderResult.Failed(
                "window_placement.manager_activation_failed",
                "The virtual desktop manager could not be activated, so the window was not moved.",
                error.HResult);
        }

        if (error.HResult == AccessDeniedHResult)
        {
            return DesktopTopologyProviderResult.Failed(
                "window_placement.move_access_denied",
                "Windows denied the Shell request to move the window, so it was left where it is. " +
                PrivilegeBoundary.DeniedWindowExplanation,
                error.HResult);
        }

        if (!windowApi.IsWindow(windowHandle))
        {
            // The window closed underneath the move. An owned reconnection or
            // credential dialog dismissing itself does exactly this, and it is
            // not a failure the user can act on.
            return DesktopTopologyProviderResult.Failed(
                "window_placement.stale_window_handle",
                "The window closed before it could be moved to the requested virtual desktop.",
                error.HResult);
        }

        return DesktopTopologyProviderResult.Failed(
            "window_placement.move_failed",
            $"Windows refused to move the window to the requested virtual desktop. {error.Message}",
            error.HResult);
    }

    /// <summary>
    /// Turns a refused desktop query into a failure a reader of Activity can act
    /// on, using the same distinctions a refused move draws.
    /// </summary>
    /// <remarks>
    /// An elevated application's window is refused here first, before any move
    /// is attempted, so this is the path that most often carries the privilege
    /// boundary to the user. It is reported as its own code rather than folded
    /// into the generic query failure, because "denied" and "unknown" call for
    /// different responses.
    /// </remarks>
    /// <param name="result">The refusal the documented manager returned.</param>
    /// <returns>A failed result naming the most specific known cause.</returns>
    private static DesktopTopologyProviderResult<Guid> ClassifyQueryFailure(
        DocumentedDesktopIdResult result)
    {
        if (string.Equals(
            result.Stage,
            ManagerActivationStage,
            StringComparison.Ordinal))
        {
            return DesktopTopologyProviderResult<Guid>.Failed(
                "window_placement.manager_activation_failed",
                "The virtual desktop manager could not be activated, so the window's virtual desktop is unknown.",
                result.HResult);
        }

        if (result.HResult == AccessDeniedHResult)
        {
            return DesktopTopologyProviderResult<Guid>.Failed(
                "window_placement.query_access_denied",
                "Windows denied access to the window, so its virtual desktop could not be read and the window was left where it is. " +
                PrivilegeBoundary.DeniedWindowExplanation,
                result.HResult);
        }

        if (result.HResult == WindowNotTrackedHResult)
        {
            return DesktopTopologyProviderResult<Guid>.Failed(
                "window_placement.window_not_tracked",
                "Windows was not tracking this window on a virtual desktop.",
                result.HResult);
        }

        return DesktopTopologyProviderResult<Guid>.Failed(
            "window_placement.get_desktop_failed",
            "Windows could not determine the window's virtual desktop.",
            result.HResult);
    }
}

internal interface IWindowHandleApi
{
    bool IsWindow(nint windowHandle);

    nint GetRootWindow(nint windowHandle);
}

internal sealed class WindowHandleApi : IWindowHandleApi
{
    private const uint GetAncestorRoot = 2;

    public bool IsWindow(nint windowHandle) =>
        NativeMethods.IsWindow(windowHandle);

    public nint GetRootWindow(nint windowHandle) =>
        NativeMethods.GetAncestor(windowHandle, GetAncestorRoot);

    private static class NativeMethods
    {
        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool IsWindow(nint windowHandle);

        [DllImport("user32.dll")]
        internal static extern nint GetAncestor(
            nint windowHandle,
            uint flags);
    }
}

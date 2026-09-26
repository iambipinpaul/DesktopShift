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
    private const string ApplicationViewLookupStage = "ApplicationViewLookup";
    private const string WindowNotTrackedMessage =
        "Windows was not tracking this window on a virtual desktop.";

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
            // Windows says "I have not placed this window on a desktop yet" two
            // ways: the documented not-tracked HRESULT, and an S_OK carrying an
            // empty identifier. A window shown before the Shell has registered
            // it takes the second path. Both report the same code, so a caller
            // cannot read one of them as a refusal, and Activity never shows an
            // HRESULT that no Windows call returned.
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    "window_placement.window_not_tracked",
                    WindowNotTrackedMessage,
                    result.HResult));
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

    public ValueTask<DesktopTopologyProviderResult<bool>> GetWindowPinnedAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        DesktopTopologyProviderResult<bool>? invalidResult =
            ValidateWindow<bool>(windowHandle);
        if (invalidResult is not null)
        {
            return ValueTask.FromResult(invalidResult);
        }

        try
        {
            NativeBridgeResult<bool> result =
                desktopMover.IsWindowPinned(windowHandle);
            return ValueTask.FromResult(
                result.IsSuccess
                    ? DesktopTopologyProviderResult<bool>.Succeeded(result.Value)
                    : ClassifyPinQueryFailure(windowHandle, result));
        }
        catch (Exception exception)
        {
            return ValueTask.FromResult(PinFailed<bool>(exception));
        }
    }

    public ValueTask<DesktopTopologyProviderResult> PinWindowAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        DesktopTopologyProviderResult? invalidResult =
            ValidateWindow(windowHandle);
        if (invalidResult is not null)
        {
            return ValueTask.FromResult(invalidResult);
        }

        return ValueTask.FromResult(Pin(windowHandle));
    }

    public ValueTask<DesktopTopologyProviderResult> UnpinWindowAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(disposed, this);
        DesktopTopologyProviderResult? invalidResult =
            ValidateWindow(windowHandle);
        if (invalidResult is not null)
        {
            return ValueTask.FromResult(invalidResult);
        }

        return ValueTask.FromResult(Unpin(windowHandle));
    }

    /// <summary>
    /// Applies a pin, once, and reports what Windows said about it.
    /// </summary>
    /// <remarks>
    /// One attempt, then a structured answer — the same bargain a move makes.
    /// Whether a window may be shown on every desktop is Windows' answer to
    /// give, and re-issuing the identical call cannot change it.
    /// </remarks>
    private DesktopTopologyProviderResult Pin(nint windowHandle)
    {
        try
        {
            NativeBridgeResult result = desktopMover.PinWindow(windowHandle);
            return result.IsSuccess
                ? DesktopTopologyProviderResult.Succeeded()
                : ClassifyPinFailure(windowHandle, result);
        }
        catch (Exception exception)
        {
            return PinFailed(exception);
        }
    }

    /// <inheritdoc cref="Pin"/>
    private DesktopTopologyProviderResult Unpin(nint windowHandle)
    {
        try
        {
            NativeBridgeResult result = desktopMover.UnpinWindow(windowHandle);
            return result.IsSuccess
                ? DesktopTopologyProviderResult.Succeeded()
                : ClassifyPinFailure(windowHandle, result);
        }
        catch (Exception exception)
        {
            return PinFailed(exception);
        }
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
    /// The one refusal that is not a refusal is a window the Shell has not yet
    /// given an application view, which the native bridge reports under its own
    /// stage so it can be told apart from the move itself being rejected.
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

        if (string.Equals(
            error.Stage,
            ApplicationViewLookupStage,
            StringComparison.Ordinal))
        {
            // The Shell has no application view for this window yet, which is
            // the same answer the desktop query gives while a window is still
            // being registered, and so it is reported under the same code. A
            // window shown a few milliseconds before the Shell caught up is not
            // a refusal, and reporting it as one puts a failure in Activity for
            // a window the very next event places correctly.
            return DesktopTopologyProviderResult.Failed(
                "window_placement.window_not_tracked",
                "The Windows Shell had not registered this window yet, so it was left where it is.",
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
                WindowNotTrackedMessage,
                result.HResult);
        }

        return DesktopTopologyProviderResult<Guid>.Failed(
            "window_placement.get_desktop_failed",
            "Windows could not determine the window's virtual desktop.",
            result.HResult);
    }

    /// <summary>
    /// Turns a refused pin into the outcome and error that best name what
    /// happened, drawing the distinctions a refused move draws.
    /// </summary>
    /// <remarks>
    /// A pin has one distinction a move does not: the host may have no pin
    /// surface at all. That is not a refusal — nothing was attempted and
    /// nothing can be — so it is reported as unsupported, which is what lets
    /// assignment record a deliberate skip instead of a failure the user
    /// cannot act on.
    /// </remarks>
    /// <param name="windowHandle">The window whose pin was refused.</param>
    /// <param name="bridgeError">The refusal the pin seam returned.</param>
    /// <returns>The outcome to report and the error that explains it.</returns>
    private (DesktopTopologyResultOutcome Outcome, DesktopTopologyProviderError Error)
        DescribePinFailure(nint windowHandle, NativeBridgeError? bridgeError)
    {
        NativeBridgeError error = bridgeError ??
            new NativeBridgeError(
                "native.window_pin_failed",
                "WindowPin",
                "The native window pin failed without structured error details.",
                UnexpectedResultHResult);

        if (string.Equals(
            error.Code,
            "native.pin_unsupported",
            StringComparison.Ordinal))
        {
            return (
                DesktopTopologyResultOutcome.Unsupported,
                new DesktopTopologyProviderError(
                    "window_placement.pin_unsupported",
                    "DesktopShift could not keep the window on every desktop because this host has not proved a pinning surface. " +
                    error.Message));
        }

        if (string.Equals(
            error.Code,
            "native.window_pin_exception",
            StringComparison.Ordinal))
        {
            return (
                DesktopTopologyResultOutcome.Failed,
                new DesktopTopologyProviderError(
                    "window_placement.pin_exception",
                    "DesktopShift could not complete the native window pin call. " +
                    error.Message,
                    error.HResult));
        }

        if (string.Equals(
            error.Stage,
            ApplicationViewLookupStage,
            StringComparison.Ordinal))
        {
            // The Shell has no application view for this window yet, which is
            // the same answer the desktop query gives while a window is still
            // being registered, and so it is reported under the same code. A
            // window shown a few milliseconds before the Shell caught up is not
            // a refusal, and the next event pins it.
            return (
                DesktopTopologyResultOutcome.Failed,
                new DesktopTopologyProviderError(
                    "window_placement.window_not_tracked",
                    WindowNotTrackedMessage,
                    error.HResult));
        }

        if (!windowApi.IsWindow(windowHandle))
        {
            // The window closed underneath the pin, which is a race rather than
            // something the user can act on.
            return (
                DesktopTopologyResultOutcome.Failed,
                new DesktopTopologyProviderError(
                    "window_placement.stale_window_handle",
                    "The window closed before it could be kept on every virtual desktop.",
                    error.HResult));
        }

        return (
            DesktopTopologyResultOutcome.Failed,
            new DesktopTopologyProviderError(
                "window_placement.pin_failed",
                $"Windows refused to keep the window on every virtual desktop. {error.Message}",
                error.HResult));
    }

    private DesktopTopologyProviderResult ClassifyPinFailure(
        nint windowHandle,
        NativeBridgeResult result)
    {
        (DesktopTopologyResultOutcome outcome, DesktopTopologyProviderError error) =
            DescribePinFailure(windowHandle, result.Error);
        return new DesktopTopologyProviderResult(outcome, error);
    }

    private DesktopTopologyProviderResult<bool> ClassifyPinQueryFailure(
        nint windowHandle,
        NativeBridgeResult<bool> result)
    {
        (DesktopTopologyResultOutcome outcome, DesktopTopologyProviderError error) =
            DescribePinFailure(windowHandle, result.Error);
        return new DesktopTopologyProviderResult<bool>(outcome, default, error);
    }

    /// <summary>
    /// Reports a pin that threw locally rather than being refused.
    /// </summary>
    /// <remarks>
    /// A throw is its own code, because it says something a refusal does not:
    /// the host was asked and the call did not finish. It is reported here
    /// rather than left to surface as an unhandled exception so a caller sees
    /// the same shape whether Windows refused the pin or DesktopShift's own
    /// seam failed to make the call.
    /// </remarks>
    private static DesktopTopologyProviderResult PinFailed(
        Exception exception) =>
        DesktopTopologyProviderResult.Failed(
            "window_placement.pin_exception",
            "DesktopShift could not complete the native window pin call. " +
            exception.Message,
            exception.HResult);

    /// <inheritdoc cref="PinFailed(Exception)"/>
    private static DesktopTopologyProviderResult<T> PinFailed<T>(
        Exception exception) =>
        DesktopTopologyProviderResult<T>.Failed(
            "window_placement.pin_exception",
            "DesktopShift could not complete the native window pin call. " +
            exception.Message,
            exception.HResult);
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

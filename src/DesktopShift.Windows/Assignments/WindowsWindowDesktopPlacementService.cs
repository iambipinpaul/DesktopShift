using System.Runtime.InteropServices;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;

namespace DesktopShift.Windows.Assignments;

public sealed class WindowsWindowDesktopPlacementService :
    IWindowDesktopPlacementService,
    IDisposable
{
    private const int UnexpectedResultHResult =
        unchecked((int)0x8000FFFF);

    private readonly IWindowHandleApi windowApi;
    private readonly IDocumentedVirtualDesktopManagerApi desktopManager;
    private readonly bool ownsDesktopManager;
    private bool disposed;

    public WindowsWindowDesktopPlacementService()
        : this(
            new WindowHandleApi(),
            new DocumentedVirtualDesktopManagerApi(),
            ownsDesktopManager: true)
    {
    }

    internal WindowsWindowDesktopPlacementService(
        IWindowHandleApi windowApi,
        IDocumentedVirtualDesktopManagerApi desktopManager,
        bool ownsDesktopManager = false)
    {
        this.windowApi =
            windowApi ?? throw new ArgumentNullException(nameof(windowApi));
        this.desktopManager =
            desktopManager ?? throw new ArgumentNullException(nameof(desktopManager));
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
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Failed(
                    GetFailureCode(result.Stage, "get_desktop_failed"),
                    "Windows could not determine the window's virtual desktop.",
                    result.HResult));
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

        DocumentedDesktopOperationResult result =
            desktopManager.MoveWindowToDesktop(windowHandle, desktopId);
        return result.IsSuccess
            ? ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded())
            : ValueTask.FromResult(
                DesktopTopologyProviderResult.Failed(
                    GetFailureCode(result.Stage, "move_failed"),
                    "Windows could not move the window to the requested virtual desktop.",
                    result.HResult));
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
        if (rootWindow == 0 || !windowApi.IsWindow(windowHandle))
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

    private static string GetFailureCode(string stage, string operation) =>
        string.Equals(stage, "ManagerActivation", StringComparison.Ordinal)
            ? "window_placement.manager_activation_failed"
            : $"window_placement.{operation}";
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

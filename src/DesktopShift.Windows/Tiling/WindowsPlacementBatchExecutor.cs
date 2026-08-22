using System.Runtime.InteropServices;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Windows.Tiling;

/// <summary>
/// Moves windows through exactly one deferred-position batch per layout.
/// </summary>
/// <remarks>
/// <para>
/// The whole visible layout is queued first, then committed once, with
/// <c>SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER</c> so nothing about
/// focus or Z order changes: tiling rearranges rectangles and nothing else.
/// </para>
/// <para>
/// Failure follows Microsoft's contract for
/// <see href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-deferwindowpos">DeferWindowPos</see>:
/// when one window's call fails the batch handle is dead and must never be
/// handed to EndDeferWindowPos, so the uncommitted batch is abandoned, the
/// failed window is skipped and reported, the remaining placements are rebuilt
/// into a fresh batch, and that one is committed. A failure of
/// <see href="https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-enddeferwindowpos">EndDeferWindowPos</see>
/// names no individual window, so the entire batch is reported failed.
/// </para>
/// </remarks>
public sealed class WindowsPlacementBatchExecutor : ITilingPlacementExecutor
{
    private readonly ITilingDeferApi deferApi;

    public WindowsPlacementBatchExecutor()
        : this(new TilingDeferNativeApi())
    {
    }

    internal WindowsPlacementBatchExecutor(ITilingDeferApi deferApi)
    {
        this.deferApi =
            deferApi ?? throw new ArgumentNullException(nameof(deferApi));
    }

    /// <inheritdoc />
    /// <remarks>
    /// Never moves focus, never changes Z order, never activates. A window
    /// whose individual DeferWindowPos call fails is reported in
    /// <see cref="TilingBatchResult.SkippedWindows"/>; everything else still
    /// lands.
    /// </remarks>
    public TilingBatchResult Apply(IReadOnlyList<TilingPlacementRequest> placements)
    {
        ArgumentNullException.ThrowIfNull(placements);

        List<nint> committed = [];
        List<nint> skipped = [];
        List<TilingPlacementRejection> rejections = [];
        List<TilingPlacementRequest> pending = [.. placements];

        while (pending.Count > 0)
        {
            nint handle = deferApi.Begin(pending.Count);
            if (handle == 0)
            {
                return Failed(
                    TilingBatchOutcome.BeginFailed,
                    committed,
                    skipped,
                    pending);
            }

            TilingPlacementRequest? rejected = null;
            foreach (TilingPlacementRequest request in pending)
            {
                nint updatedHandle = deferApi.Defer(handle, request);
                if (updatedHandle == 0)
                {
                    rejected = request;
                    break;
                }

                handle = updatedHandle;
            }

            if (rejected is null)
            {
                bool ended = deferApi.End(handle);
                if (!ended)
                {
                    return Failed(
                        TilingBatchOutcome.EndFailed,
                        committed,
                        skipped,
                        pending);
                }

                // One committed batch covers every placement it contained;
                // earlier rebuilds already added theirs.
                committed.AddRange(pending.Select(static request => request.WindowHandle));
                pending.Clear();
                break;
            }

            // Abandon, skip, report, rebuild, commit again — the only path
            // Microsoft leaves open after a mid-batch DeferWindowPos failure.
            skipped.Add(rejected.WindowHandle);
            rejections.Add(new TilingPlacementRejection(
                rejected.WindowHandle,
                deferApi.LastErrorCode));
            pending.Remove(rejected);
        }

        return new TilingBatchResult(
            skipped.Count == 0
                ? TilingBatchOutcome.Committed
                : TilingBatchOutcome.DeferFailed,
            committed,
            skipped,
            [],
            rejections);
    }

    private static TilingBatchResult Failed(
        TilingBatchOutcome outcome,
        List<nint> committed,
        List<nint> skipped,
        List<TilingPlacementRequest> unplaced) =>
        new(
            outcome,
            committed,
            skipped,
            unplaced.Select(static request => request.WindowHandle).ToArray());

    internal interface ITilingDeferApi
    {
        nint Begin(int capacity);

        nint Defer(nint batchHandle, TilingPlacementRequest request);

        int LastErrorCode { get; }

        bool End(nint batchHandle);
    }

    internal sealed class TilingDeferNativeApi : ITilingDeferApi
    {
        // No activate, no Z-order change of any kind — the flags that make a
        // tiling pass invisible to focus and stacking:
        // https://learn.microsoft.com/windows/win32/api/winuser/nf-winuser-setwindowpos
        private const uint SwpNoactivate = 0x0010;
        private const uint SwpNozorder = 0x0004;
        private const uint SwpNoownerzorder = 0x0200;
        private const uint PlacementFlags =
            SwpNoactivate | SwpNozorder | SwpNoownerzorder;

        public nint Begin(int capacity) => NativeMethods.BeginDeferWindowPos(capacity);

        public nint Defer(nint batchHandle, TilingPlacementRequest request)
        {
            ArgumentNullException.ThrowIfNull(request);
            Marshal.SetLastPInvokeError(0);
            return NativeMethods.DeferWindowPos(
                batchHandle,
                request.WindowHandle,
                0,
                request.WindowRectPixels.X,
                request.WindowRectPixels.Y,
                request.WindowRectPixels.Width,
                request.WindowRectPixels.Height,
                PlacementFlags);
        }

        public int LastErrorCode => Marshal.GetLastPInvokeError();

        public bool End(nint batchHandle)
        {
            Marshal.SetLastPInvokeError(0);
            return NativeMethods.EndDeferWindowPos(batchHandle);
        }

        private static class NativeMethods
        {
            [DllImport("user32.dll", SetLastError = true)]
            internal static extern nint BeginDeferWindowPos(int count);

            [DllImport("user32.dll", SetLastError = true)]
            internal static extern nint DeferWindowPos(
                nint batchHandle,
                nint windowHandle,
                nint insertAfter,
                int x,
                int y,
                int width,
                int height,
                uint flags);

            [DllImport("user32.dll", SetLastError = true)]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool EndDeferWindowPos(nint batchHandle);
        }
    }
}

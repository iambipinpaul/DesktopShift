using DesktopShift.Core.Compatibility;

namespace DesktopShift.Core.Assignments;

public interface IWindowDesktopPlacementService
{
    ValueTask<DesktopTopologyProviderResult<Guid>> GetWindowDesktopIdAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default);

    ValueTask<DesktopTopologyProviderResult> MoveWindowToDesktopAsync(
        nint windowHandle,
        Guid desktopId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Whether the window is currently shown on every virtual desktop.
    /// </summary>
    /// <remarks>
    /// A host that has not proved its pinning surface answers with
    /// <see cref="DesktopTopologyResultOutcome.Unsupported"/> rather than a
    /// result that would be read as "not pinned". The default answers that way
    /// for the same reason: a placement service written before pinning existed
    /// cannot know, and "I do not implement this" must never pass for "not
    /// pinned".
    /// </remarks>
    /// <param name="windowHandle">The root window handle to query.</param>
    /// <param name="cancellationToken">Abandons the query.</param>
    /// <returns>Whether a pin currently holds the window.</returns>
    ValueTask<DesktopTopologyProviderResult<bool>> GetWindowPinnedAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(
            DesktopTopologyProviderResult<bool>.Unsupported(
                "window_placement.pin_unsupported",
                "This host cannot report whether a window is shown on every desktop."));

    /// <summary>
    /// Keeps the window visible on every virtual desktop.
    /// </summary>
    /// <param name="windowHandle">The root window handle to pin.</param>
    /// <param name="cancellationToken">Abandons the pin.</param>
    /// <returns>Whether the pin was applied.</returns>
    ValueTask<DesktopTopologyProviderResult> PinWindowAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DesktopTopologyProviderResult.Unsupported(
            "window_placement.pin_unsupported",
            "This host cannot pin a window to every desktop."));

    /// <summary>
    /// Returns a pinned window to normal placement on one desktop.
    /// </summary>
    /// <param name="windowHandle">The root window handle to release.</param>
    /// <param name="cancellationToken">Abandons the release.</param>
    /// <returns>Whether the pin was released.</returns>
    ValueTask<DesktopTopologyProviderResult> UnpinWindowAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(DesktopTopologyProviderResult.Unsupported(
            "window_placement.pin_unsupported",
            "This host cannot release a pinned window."));
}

public interface ITopLevelWindowEnumerator
{
    IReadOnlyList<nint> Enumerate();
}

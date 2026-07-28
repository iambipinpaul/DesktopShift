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
}

public interface ITopLevelWindowEnumerator
{
    IReadOnlyList<nint> Enumerate();
}

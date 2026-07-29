using DesktopShift.Core.Compatibility;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// Finds the Windows desktop at position 0 — the one Task View calls
/// "Desktop 1" — which is where windows no rule names are swept.
/// </summary>
/// <remarks>
/// <para>
/// Position 0 is deliberately not a Managed Desktop. A Managed Desktop can come
/// back from reconciliation missing, ambiguous, or limited, and a fallback that
/// can fail to resolve is not a fallback. Windows refuses to delete the last
/// virtual desktop, so a desktop at position 0 always exists.
/// </para>
/// <para>
/// It is located by position and never by name. It may be a desktop the user
/// named themselves long before installing DesktopShift, so nothing here renames
/// it and nothing adds it to the Managed Desktop catalog.
/// </para>
/// </remarks>
public interface IFirstDesktopLocator
{
    /// <summary>
    /// Reads the runtime identifier of the desktop at position 0.
    /// </summary>
    /// <param name="cancellationToken">Cancels the inventory read.</param>
    /// <returns>The desktop identifier, or why it could not be read.</returns>
    ValueTask<DesktopTopologyProviderResult<Guid>> GetFirstDesktopIdAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Reads position 0 out of the topology inventory each time it is asked.
/// </summary>
/// <remarks>
/// The answer is deliberately not cached. Desktops are created, deleted, and
/// reordered by the user at any moment, and a cached identifier that has since
/// been deleted would send windows nowhere — silently, and only for the users
/// who rearrange their desktops most.
/// </remarks>
/// <param name="topologyProvider">The provider that owns the inventory.</param>
public sealed class FirstDesktopLocator(
    IDesktopTopologyProvider topologyProvider) : IFirstDesktopLocator
{
    /// <inheritdoc />
    public async ValueTask<DesktopTopologyProviderResult<Guid>>
        GetFirstDesktopIdAsync(CancellationToken cancellationToken = default)
    {
        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>
            inventory = await topologyProvider
                .EnumerateDesktopsAsync(cancellationToken)
                .ConfigureAwait(false);

        if (!inventory.IsSuccess)
        {
            return new DesktopTopologyProviderResult<Guid>(
                inventory.Outcome,
                Guid.Empty,
                inventory.Error ?? new DesktopTopologyProviderError(
                    "first_desktop.inventory_failed",
                    "The virtual desktops could not be enumerated."));
        }

        VirtualDesktopDescriptor? first = inventory.Value?
            .OrderBy(static desktop => desktop.Position)
            .FirstOrDefault();

        // Ordering rather than looking for Position 0 exactly: a provider that
        // numbered its inventory from one would otherwise report no first
        // desktop at all, and refusing to sweep is worse than sweeping to
        // whichever desktop comes first.
        return first is null || first.Id == Guid.Empty
            ? DesktopTopologyProviderResult<Guid>.Failed(
                "first_desktop.not_found",
                "Windows reported no virtual desktop at the first position.")
            : DesktopTopologyProviderResult<Guid>.Succeeded(first.Id);
    }
}

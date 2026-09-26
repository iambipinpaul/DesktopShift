using System.Collections.Immutable;

namespace DesktopShift.Core.Compatibility;

/// <param name="CanRenameDesktop">
/// Whether this provider may name a runtime desktop, which needs the extended
/// shell-manager layout. It trails the required members and defaults to false
/// so a provider which has not proved that layout is described as unable to
/// name, rather than silently assumed able.
/// </param>
/// <param name="CanReorderDesktop">
/// Whether this provider may move an existing runtime desktop to a new Task
/// View index. Like naming, reordering requires the extended shell-manager
/// layout to be proved before the capability is enabled.
/// </param>
/// <param name="CanPinWindow">
/// Whether this provider may keep a window visible on every virtual desktop.
/// Pinning lives on its own private Shell surface, so it is proved by its own
/// read-only probe and defaults to false: a provider that has not proved it
/// reports pinning as unavailable rather than assuming it works.
/// </param>
public sealed record VirtualDesktopCapabilities(
    bool CanGetWindowDesktopId,
    bool CanMoveWindowToDesktop,
    bool CanEnumerateDesktops,
    bool CanGetCurrentDesktop,
    bool CanCreateDesktop,
    bool CanSwitchDesktop,
    bool CanObserveTopologyChanges,
    bool CanRenameDesktop = false,
    bool CanReorderDesktop = false,
    bool CanPinWindow = false)
{
    public static VirtualDesktopCapabilities DocumentedLimited { get; } = new(
        CanGetWindowDesktopId: true,
        CanMoveWindowToDesktop: false,
        CanEnumerateDesktops: false,
        CanGetCurrentDesktop: false,
        CanCreateDesktop: false,
        CanSwitchDesktop: false,
        CanObserveTopologyChanges: false,
        CanRenameDesktop: false,
        CanReorderDesktop: false,
        CanPinWindow: false);

    public bool HasPrivateTopologyCapabilities =>
        CanEnumerateDesktops ||
        CanGetCurrentDesktop ||
        CanCreateDesktop ||
        CanSwitchDesktop ||
        CanObserveTopologyChanges ||
        CanRenameDesktop ||
        CanReorderDesktop ||
        CanPinWindow;

    public ImmutableArray<string> AvailableCapabilityNames
    {
        get
        {
            ImmutableArray<string>.Builder names = ImmutableArray.CreateBuilder<string>();

            AddIfAvailable(names, CanGetWindowDesktopId, "Get a window's desktop ID");
            AddIfAvailable(names, CanMoveWindowToDesktop, "Move a window to a known desktop");
            AddIfAvailable(names, CanEnumerateDesktops, "Enumerate desktops");
            AddIfAvailable(names, CanGetCurrentDesktop, "Get the current desktop");
            AddIfAvailable(names, CanCreateDesktop, "Create desktops");
            AddIfAvailable(names, CanSwitchDesktop, "Switch desktops");
            AddIfAvailable(names, CanObserveTopologyChanges, "Observe topology changes");
            AddIfAvailable(names, CanRenameDesktop, "Name desktops");
            AddIfAvailable(names, CanReorderDesktop, "Reorder desktops");
            AddIfAvailable(names, CanPinWindow, "Keep a window on every desktop");

            return names.ToImmutable();
        }
    }

    private static void AddIfAvailable(
        ImmutableArray<string>.Builder names,
        bool isAvailable,
        string name)
    {
        if (isAvailable)
        {
            names.Add(name);
        }
    }
}

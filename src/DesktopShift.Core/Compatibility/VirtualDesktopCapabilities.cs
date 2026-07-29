using System.Collections.Immutable;

namespace DesktopShift.Core.Compatibility;

/// <param name="CanRenameDesktop">
/// Whether this provider may name a runtime desktop, which needs shell manager
/// slots well past the ones every other capability lives in. It trails the
/// required members and defaults to false so that a provider which has not
/// proved that layout is described as unable to name, rather than silently
/// assumed able.
/// </param>
public sealed record VirtualDesktopCapabilities(
    bool CanGetWindowDesktopId,
    bool CanMoveWindowToDesktop,
    bool CanEnumerateDesktops,
    bool CanGetCurrentDesktop,
    bool CanCreateDesktop,
    bool CanSwitchDesktop,
    bool CanObserveTopologyChanges,
    bool CanRenameDesktop = false)
{
    public static VirtualDesktopCapabilities DocumentedLimited { get; } = new(
        CanGetWindowDesktopId: true,
        CanMoveWindowToDesktop: false,
        CanEnumerateDesktops: false,
        CanGetCurrentDesktop: false,
        CanCreateDesktop: false,
        CanSwitchDesktop: false,
        CanObserveTopologyChanges: false,
        CanRenameDesktop: false);

    public bool HasPrivateTopologyCapabilities =>
        CanEnumerateDesktops ||
        CanGetCurrentDesktop ||
        CanCreateDesktop ||
        CanSwitchDesktop ||
        CanObserveTopologyChanges ||
        CanRenameDesktop;

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

using System.Collections.Immutable;

namespace DesktopShift.Windows.VirtualDesktops.NativeBridge;

internal sealed record NativeDesktopSnapshot(
    ImmutableArray<NativeDesktopDescriptor> Desktops,
    Guid CurrentDesktopId);

internal sealed record NativeDesktopDescriptor(
    Guid Id,
    string? DisplayName,
    int Position);

internal sealed record NativeBridgeError(
    string Code,
    string Stage,
    string Message,
    int HResult);

internal sealed record NativeBridgeResult(
    NativeBridgeError? Error = null)
{
    public bool IsSuccess => Error is null;

    public static NativeBridgeResult Succeeded { get; } = new();

    public static NativeBridgeResult Failed(NativeBridgeError error) => new(error);
}

internal sealed record NativeBridgeResult<T>(
    T? Value,
    NativeBridgeError? Error = null)
{
    public bool IsSuccess => Error is null && Value is not null;

    public static NativeBridgeResult<T> Succeeded(T value) => new(value);

    public static NativeBridgeResult<T> Failed(NativeBridgeError error) => new(default, error);
}

internal interface INativeVirtualDesktopBridge : IDisposable
{
    NativeBridgeResult Validate();

    NativeBridgeResult<NativeDesktopSnapshot> ReadSnapshot();

    NativeBridgeResult<Guid> CreateDesktop();

    NativeBridgeResult SwitchDesktop(Guid desktopId);

    NativeBridgeResult MoveWindowToDesktop(nint windowHandle, Guid desktopId);

    /// <summary>
    /// Proves the shell manager lays its vtable out where this build family
    /// expects, using a read-only lookup that changes nothing.
    /// </summary>
    /// <remarks>
    /// Naming a desktop needs a slot well past the read-only prefix every other
    /// operation lives in, and a shifted layout would land that call on a
    /// neighbour. This is how the layout is established by behaviour instead of
    /// by assumption. A failure here means naming stays unavailable; it says
    /// nothing about enumeration, creation, switching or window moves.
    /// </remarks>
    NativeBridgeResult ProbeDesktopLookup();

    /// <summary>
    /// Names an existing runtime desktop.
    /// </summary>
    /// <remarks>
    /// There is deliberately no counterpart for removing or reordering a
    /// desktop. Naming is the only desktop-targeted mutation the bridge offers.
    /// </remarks>
    NativeBridgeResult SetDesktopName(Guid desktopId, string name);

    NativeBridgeResult StartNotifications(Action<string> onTopologyChanged);
}

internal interface INativeVirtualDesktopBridgeFactory
{
    NativeBridgeResult<INativeVirtualDesktopBridge> TryCreate(int windowsBuild);
}

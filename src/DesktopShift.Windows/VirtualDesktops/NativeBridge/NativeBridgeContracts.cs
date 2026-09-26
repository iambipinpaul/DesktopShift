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

    NativeBridgeResult MoveDesktop(Guid desktopId, int targetPosition) =>
        NativeBridgeResult.Failed(
            new NativeBridgeError(
                "native.reorder_unsupported",
                "DesktopReorder",
                "This bridge cannot reorder virtual desktops.",
                unchecked((int)0x80004001)));

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
    /// Removing a desktop remains deliberately unavailable. Reordering is a
    /// separate capability guarded by the same extended-layout probe.
    /// </remarks>
    NativeBridgeResult SetDesktopName(Guid desktopId, string name);

    /// <summary>
    /// Whether the Shell currently shows the window on every virtual desktop.
    /// </summary>
    /// <remarks>
    /// The read-only half of the pin surface, and the only pin slot reached
    /// before that surface has been proved.
    /// </remarks>
    NativeBridgeResult<bool> IsWindowPinned(nint windowHandle) =>
        NativeBridgeResult<bool>.Failed(UnsupportedPin());

    /// <summary>
    /// Keeps the window visible on every virtual desktop.
    /// </summary>
    NativeBridgeResult PinWindow(nint windowHandle) =>
        NativeBridgeResult.Failed(UnsupportedPin());

    /// <summary>
    /// Returns a pinned window to normal placement on one desktop.
    /// </summary>
    NativeBridgeResult UnpinWindow(nint windowHandle) =>
        NativeBridgeResult.Failed(UnsupportedPin());

    /// <summary>
    /// Proves the pinned-apps surface lays its vtable out where this build
    /// family expects, using a read-only query that changes nothing.
    /// </summary>
    /// <remarks>
    /// Pinning lives on its own private interface with its own vtable, so it is
    /// established separately from the manager layout. A failure here means
    /// pinning stays unavailable; it says nothing about enumeration, creation,
    /// switching, window moves, naming or reordering.
    /// </remarks>
    NativeBridgeResult ProbeWindowPin() =>
        NativeBridgeResult.Failed(UnsupportedPin());

    NativeBridgeResult StartNotifications(Action<string> onTopologyChanged);

    /// <summary>
    /// The refusal every pin default above answers with.
    /// </summary>
    /// <remarks>
    /// A bridge that does not carry the pin surface keeps compiling by
    /// inheriting these defaults, and such a bridge reports pinning as
    /// unsupported rather than as a pin that failed. That is the same answer a
    /// Windows build which cannot pin gives, so a caller has one thing to
    /// understand instead of two.
    /// </remarks>
    private static NativeBridgeError UnsupportedPin() =>
        new(
            "native.pin_unsupported",
            "WindowPin",
            "This bridge cannot keep windows on every virtual desktop.",
            unchecked((int)0x80004001));
}

internal interface INativeVirtualDesktopBridgeFactory
{
    NativeBridgeResult<INativeVirtualDesktopBridge> TryCreate(int windowsBuild);
}

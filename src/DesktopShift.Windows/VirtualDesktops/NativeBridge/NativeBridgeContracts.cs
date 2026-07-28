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

    NativeBridgeResult StartNotifications(Action<string> onTopologyChanged);
}

internal interface INativeVirtualDesktopBridgeFactory
{
    NativeBridgeResult<INativeVirtualDesktopBridge> TryCreate(int windowsBuild);
}

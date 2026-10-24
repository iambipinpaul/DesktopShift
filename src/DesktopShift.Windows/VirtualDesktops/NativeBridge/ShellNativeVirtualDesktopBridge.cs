using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace DesktopShift.Windows.VirtualDesktops.NativeBridge;

internal sealed class ShellNativeVirtualDesktopBridgeFactory :
    INativeVirtualDesktopBridgeFactory
{
    public NativeBridgeResult<INativeVirtualDesktopBridge> TryCreate(int windowsBuild)
    {
        try
        {
            int result = NativeMethods.DesktopShiftNative_CreateAdapter(
                checked((uint)windowsBuild),
                out nint adapter,
                out NativeMethods.NativeError error);
            if (result < 0 || adapter == 0)
            {
                return NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                    ToError(error, result, "native.activation_failed"));
            }

            return NativeBridgeResult<INativeVirtualDesktopBridge>.Succeeded(
                new ShellNativeVirtualDesktopBridge(adapter));
        }
        catch (DllNotFoundException exception)
        {
            return MissingNativeDependency(exception);
        }
        catch (BadImageFormatException exception)
        {
            return MissingNativeDependency(exception);
        }
        catch (EntryPointNotFoundException exception)
        {
            return MissingNativeDependency(exception);
        }
        catch (OverflowException exception)
        {
            return NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
                new NativeBridgeError(
                    "native.invalid_build",
                    "BuildSelection",
                    exception.Message,
                    exception.HResult));
        }
    }

    private static NativeBridgeResult<INativeVirtualDesktopBridge> MissingNativeDependency(
        Exception exception) =>
        NativeBridgeResult<INativeVirtualDesktopBridge>.Failed(
            new NativeBridgeError(
                "native.bridge_unavailable",
                "NativeBridgeLoad",
                $"The {RuntimeInformation.ProcessArchitecture} DesktopShift native bridge could not be loaded.",
                exception.HResult));

    internal static NativeBridgeError ToError(
        NativeMethods.NativeError error,
        int fallbackHResult,
        string fallbackCode)
    {
        string stage = Enum.IsDefined(error.Stage)
            ? error.Stage.ToString()
            : $"UnknownStage{(uint)error.Stage}";
        string message = string.IsNullOrWhiteSpace(error.Message)
            ? "The native virtual-desktop operation failed."
            : error.Message;
        int hResult = error.HResult < 0 ? error.HResult : fallbackHResult;
        string code = error.Stage == NativeMethods.NativeStage.None
            ? fallbackCode
            : $"native.{ToSnakeCase(stage)}";

        return new NativeBridgeError(code, stage, message, hResult);
    }

    private static string ToSnakeCase(string value)
    {
        IEnumerable<char> characters = value.SelectMany(
            static (character, index) =>
                index > 0 && char.IsUpper(character)
                    ? new[] { '_', char.ToLowerInvariant(character) }
                    : new[] { char.ToLowerInvariant(character) });
        return new string(characters.ToArray());
    }
}

internal sealed class ShellNativeVirtualDesktopBridge :
    INativeVirtualDesktopBridge
{
    private readonly object syncRoot = new();
    private nint adapter;
    private NativeMethods.TopologyCallback? callback;
    private Action<string>? topologyChanged;

    public ShellNativeVirtualDesktopBridge(nint adapter)
    {
        if (adapter == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(adapter));
        }

        this.adapter = adapter;
    }

    public NativeBridgeResult Validate()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_ValidateAdapter(
                adapter,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.validation_failed"));
        }
    }

    public NativeBridgeResult<NativeDesktopSnapshot> ReadSnapshot()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_RefreshSnapshot(
                adapter,
                out NativeMethods.NativeError error);
            if (result < 0)
            {
                return FailedSnapshot(error, result);
            }

            result = NativeMethods.DesktopShiftNative_GetDesktopCount(
                adapter,
                out uint count,
                out error);
            if (result < 0)
            {
                return FailedSnapshot(error, result);
            }

            ImmutableArray<NativeDesktopDescriptor>.Builder desktops =
                ImmutableArray.CreateBuilder<NativeDesktopDescriptor>(checked((int)count));
            for (uint index = 0; index < count; index++)
            {
                result = NativeMethods.DesktopShiftNative_GetDesktop(
                    adapter,
                    index,
                    out NativeMethods.NativeDesktop desktop,
                    out error);
                if (result < 0)
                {
                    return FailedSnapshot(error, result);
                }

                desktops.Add(
                    new NativeDesktopDescriptor(
                        desktop.Id,
                        string.IsNullOrWhiteSpace(desktop.DisplayName)
                            ? null
                            : desktop.DisplayName,
                        checked((int)desktop.Position)));
            }

            result = NativeMethods.DesktopShiftNative_GetCurrentDesktopId(
                adapter,
                out Guid currentDesktopId,
                out error);
            return result >= 0
                ? NativeBridgeResult<NativeDesktopSnapshot>.Succeeded(
                    new NativeDesktopSnapshot(desktops.ToImmutable(), currentDesktopId))
                : FailedSnapshot(error, result);
        }
    }

    public NativeBridgeResult<Guid> CreateDesktop()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_CreateDesktop(
                adapter,
                out Guid desktopId,
                out NativeMethods.NativeError error);
            return result >= 0 && desktopId != Guid.Empty
                ? NativeBridgeResult<Guid>.Succeeded(desktopId)
                : NativeBridgeResult<Guid>.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.desktop_creation_failed"));
        }
    }

    public NativeBridgeResult SwitchDesktop(Guid desktopId)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_SwitchDesktop(
                adapter,
                in desktopId,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.desktop_switch_failed"));
        }
    }

    public NativeBridgeResult MoveWindowToDesktop(
        nint windowHandle,
        Guid desktopId)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_MoveWindowToDesktop(
                adapter,
                windowHandle,
                in desktopId,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.window_move_failed"));
        }
    }

    public NativeBridgeResult ProbeDesktopLookup()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_ProbeDesktopLookup(
                adapter,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.layout_probe_failed"));
        }
    }

    public NativeBridgeResult MoveDesktop(Guid desktopId, int targetPosition)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(targetPosition);

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_MoveDesktop(
                adapter,
                in desktopId,
                checked((uint)targetPosition),
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.reorder_failed"));
        }
    }

    public NativeBridgeResult SetDesktopName(Guid desktopId, string name)
    {
        ArgumentNullException.ThrowIfNull(name);

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_SetDesktopName(
                adapter,
                in desktopId,
                name,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.desktop_rename_failed"));
        }
    }

    public NativeBridgeResult<bool> IsWindowPinned(nint windowHandle)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_IsWindowPinned(
                adapter,
                windowHandle,
                out int isPinned,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult<bool>.Succeeded(isPinned != 0)
                : NativeBridgeResult<bool>.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.window_pin_query_failed"));
        }
    }

    public NativeBridgeResult PinWindow(nint windowHandle)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_PinWindow(
                adapter,
                windowHandle,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.window_pin_failed"));
        }
    }

    public NativeBridgeResult UnpinWindow(nint windowHandle)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_UnpinWindow(
                adapter,
                windowHandle,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.window_unpin_failed"));
        }
    }

    public NativeBridgeResult ProbeWindowPin()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            int result = NativeMethods.DesktopShiftNative_ProbeWindowPin(
                adapter,
                out NativeMethods.NativeError error);
            return result >= 0
                ? NativeBridgeResult.Succeeded
                : NativeBridgeResult.Failed(
                    ShellNativeVirtualDesktopBridgeFactory.ToError(
                        error,
                        result,
                        "native.window_pin_probe_failed"));
        }
    }

    public NativeBridgeResult StartNotifications(Action<string> onTopologyChanged)
    {
        ArgumentNullException.ThrowIfNull(onTopologyChanged);

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(adapter == 0, this);
            Volatile.Write(ref topologyChanged, onTopologyChanged);
            callback ??= OnNativeTopologyChanged;
            int result = NativeMethods.DesktopShiftNative_StartNotifications(
                adapter,
                callback,
                0,
                out NativeMethods.NativeError error);
            if (result >= 0)
            {
                return NativeBridgeResult.Succeeded;
            }

            Volatile.Write(ref topologyChanged, null);
            return NativeBridgeResult.Failed(
                ShellNativeVirtualDesktopBridgeFactory.ToError(
                    error,
                    result,
                    "native.notification_registration_failed"));
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (adapter == 0)
            {
                return;
            }

            Volatile.Write(ref topologyChanged, null);
            NativeMethods.DesktopShiftNative_StopNotifications(adapter);
            NativeMethods.DesktopShiftNative_DestroyAdapter(adapter);
            adapter = 0;
            callback = null;
        }
    }

    private static NativeBridgeResult<NativeDesktopSnapshot> FailedSnapshot(
        NativeMethods.NativeError error,
        int result) =>
        NativeBridgeResult<NativeDesktopSnapshot>.Failed(
            ShellNativeVirtualDesktopBridgeFactory.ToError(
                error,
                result,
                "native.snapshot_failed"));

    private void OnNativeTopologyChanged(
        NativeMethods.NativeTopologyReason reason,
        nint context)
    {
        _ = context;
        Action<string>? handler = Volatile.Read(ref topologyChanged);

        if (handler is not null)
        {
            ThreadPool.UnsafeQueueUserWorkItem(
                static state => state.Handler(state.Reason),
                new TopologyWorkItem(handler, reason.ToString()),
                preferLocal: false);
        }
    }

    private sealed record TopologyWorkItem(Action<string> Handler, string Reason);
}

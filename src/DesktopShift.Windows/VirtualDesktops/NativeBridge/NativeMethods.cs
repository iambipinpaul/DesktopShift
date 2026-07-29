using System.Runtime.InteropServices;

namespace DesktopShift.Windows.VirtualDesktops.NativeBridge;

internal static partial class NativeMethods
{
    internal const string LibraryName = "DesktopShift.NativeBridge.dll";

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    internal delegate void TopologyCallback(
        NativeTopologyReason reason,
        nint context);

    internal enum NativeStage : uint
    {
        None = 0,
        BuildSelection = 1,
        ComInitialization = 2,
        ShellActivation = 3,
        ManagerActivation = 4,
        Enumeration = 5,
        CurrentDesktop = 6,
        BehaviorValidation = 7,
        NotificationActivation = 8,
        NotificationRegistration = 9,
        DesktopCreation = 10,
        DesktopSwitch = 11,
        ApplicationViewActivation = 12,
        WindowMove = 13,
    }

    internal enum NativeTopologyReason : uint
    {
        Created = 1,
        Destroyed = 2,
        Moved = 3,
        NameChanged = 4,
        CurrentChanged = 5,
        Switched = 6,
        RemoteConnected = 7,
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeError
    {
        internal int HResult;
        internal NativeStage Stage;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 256)]
        internal string Message;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    internal struct NativeDesktop
    {
        internal Guid Id;

        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)]
        internal string DisplayName;

        internal uint Position;
        internal int IsCurrent;
    }

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_CreateAdapter(
        uint windowsBuild,
        out nint adapter,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_ValidateAdapter(
        nint adapter,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_RefreshSnapshot(
        nint adapter,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_GetDesktopCount(
        nint adapter,
        out uint count,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_GetDesktop(
        nint adapter,
        uint index,
        out NativeDesktop desktop,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_GetCurrentDesktopId(
        nint adapter,
        out Guid desktopId,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_CreateDesktop(
        nint adapter,
        out Guid desktopId,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_SwitchDesktop(
        nint adapter,
        in Guid desktopId,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_MoveWindowToDesktop(
        nint adapter,
        nint windowHandle,
        in Guid desktopId,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern int DesktopShiftNative_StartNotifications(
        nint adapter,
        TopologyCallback callback,
        nint context,
        out NativeError error);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void DesktopShiftNative_StopNotifications(nint adapter);

    [DllImport(LibraryName, CallingConvention = CallingConvention.StdCall)]
    internal static extern void DesktopShiftNative_DestroyAdapter(nint adapter);
}

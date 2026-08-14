using System.Runtime.InteropServices;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>The window procedure held by the native hotkey window class.</summary>
public delegate nint WindowsHotkeyWindowProcedure(
    nint windowHandle,
    uint message,
    nint wParam,
    nint lParam);

/// <summary>
/// The User32 boundary required to own a <c>RegisterHotKey</c> message window.
/// </summary>
/// <remarks>
/// Tests replace this entire seam. Normal automated tests therefore exercise
/// translation, message delivery, failure reporting, and cleanup without ever
/// claiming a key combination from the machine running them.
/// </remarks>
public interface IWindowsHotkeyNativeApi
{
    uint CurrentThreadId { get; }

    bool RegisterWindowClass(
        string className,
        WindowsHotkeyWindowProcedure procedure);

    nint CreateMessageOnlyWindow(string className);

    bool RegisterHotKey(
        nint windowHandle,
        int registrationId,
        uint modifiers,
        uint virtualKey);

    bool UnregisterHotKey(nint windowHandle, int registrationId);

    bool DestroyWindow(nint windowHandle);

    bool UnregisterWindowClass(string className);

    nint DefWindowProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam);

    int GetLastError();
}

/// <summary>The production User32 implementation of the hotkey native seam.</summary>
internal sealed class User32WindowsHotkeyNativeApi : IWindowsHotkeyNativeApi
{
    private static readonly nint MessageOnlyWindowParent = new(-3);
    private readonly nint instanceHandle = NativeMethods.GetModuleHandle(null);

    public uint CurrentThreadId => NativeMethods.GetCurrentThreadId();

    public bool RegisterWindowClass(
        string className,
        WindowsHotkeyWindowProcedure procedure)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(className);
        ArgumentNullException.ThrowIfNull(procedure);

        NativeMethods.WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(procedure),
            InstanceHandle = instanceHandle,
            ClassName = className,
        };

        return NativeMethods.RegisterClassEx(ref windowClass) != 0;
    }

    public nint CreateMessageOnlyWindow(string className) =>
        NativeMethods.CreateWindowEx(
            dwExStyle: 0,
            lpClassName: className,
            lpWindowName: "DesktopShift desktop switching shortcuts",
            dwStyle: 0,
            x: 0,
            y: 0,
            nWidth: 0,
            nHeight: 0,
            hWndParent: MessageOnlyWindowParent,
            hMenu: 0,
            hInstance: instanceHandle,
            lpParam: 0);

    public bool RegisterHotKey(
        nint windowHandle,
        int registrationId,
        uint modifiers,
        uint virtualKey) =>
        NativeMethods.RegisterHotKey(
            windowHandle,
            registrationId,
            modifiers,
            virtualKey);

    public bool UnregisterHotKey(nint windowHandle, int registrationId) =>
        NativeMethods.UnregisterHotKey(windowHandle, registrationId);

    public bool DestroyWindow(nint windowHandle) =>
        NativeMethods.DestroyWindow(windowHandle);

    public bool UnregisterWindowClass(string className) =>
        NativeMethods.UnregisterClass(className, instanceHandle);

    public nint DefWindowProc(
        nint windowHandle,
        uint message,
        nint wParam,
        nint lParam) =>
        NativeMethods.DefWindowProc(windowHandle, message, wParam, lParam);

    public int GetLastError() => Marshal.GetLastWin32Error();

    private static class NativeMethods
    {
        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        internal struct WindowClassEx
        {
            public uint Size;
            public uint Style;
            public nint WindowProcedure;
            public int ClassExtraBytes;
            public int WindowExtraBytes;
            public nint InstanceHandle;
            public nint IconHandle;
            public nint CursorHandle;
            public nint BackgroundBrush;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string? MenuName;

            [MarshalAs(UnmanagedType.LPWStr)]
            public string ClassName;

            public nint SmallIconHandle;
        }

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "RegisterClassExW",
            SetLastError = true)]
        internal static extern ushort RegisterClassEx(
            ref WindowClassEx windowClass);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "CreateWindowExW",
            SetLastError = true)]
        internal static extern nint CreateWindowEx(
            uint dwExStyle,
            string lpClassName,
            string lpWindowName,
            uint dwStyle,
            int x,
            int y,
            int nWidth,
            int nHeight,
            nint hWndParent,
            nint hMenu,
            nint hInstance,
            nint lpParam);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool RegisterHotKey(
            nint windowHandle,
            int registrationId,
            uint modifiers,
            uint virtualKey);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterHotKey(
            nint windowHandle,
            int registrationId);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool DestroyWindow(nint windowHandle);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "UnregisterClassW",
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClass(
            string className,
            nint instanceHandle);

        [DllImport(
            "user32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport(
            "kernel32.dll",
            CharSet = CharSet.Unicode,
            EntryPoint = "GetModuleHandleW")]
        internal static extern nint GetModuleHandle(string? moduleName);

        [DllImport("kernel32.dll")]
        internal static extern uint GetCurrentThreadId();
    }
}

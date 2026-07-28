using System.ComponentModel;
using System.Runtime.InteropServices;

namespace DesktopShift.Windows.Tests.Assignments;

internal sealed class TestTopLevelWindow : IDisposable
{
    private const uint OverlappedWindowStyle = 0x00CF0000;
    private const uint VisibleWindowStyle = 0x10000000;
    private const uint PopupWindowStyle = 0x80000000;

    // Keeps a controlled window out of the user's way: it is never activated
    // and never appears in the task switcher, while still being a plain visible
    // top-level window as far as ownership and classification are concerned.
    private const uint NoActivateExtendedStyle = 0x08000000;

    private const uint CloseMessage = 0x0010;
    private const uint DestroyMessage = 0x0002;

    private readonly ManualResetEventSlim ready = new();
    private readonly Thread thread;
    private readonly WindowProcedure windowProcedure;
    private readonly string className;
    private readonly nint ownerHandle;
    private readonly uint style;
    private readonly uint extendedStyle;
    private Exception? startupException;
    private nint windowHandle;
    private bool disposed;

    /// <summary>
    /// Creates a controlled top-level window.
    /// </summary>
    /// <param name="windowClassName">
    /// The window class to register, so a test can reproduce the class a real
    /// application reports. Defaults to a unique per-instance class.
    /// </param>
    /// <param name="owner">
    /// The window that owns this one. A nonzero owner makes this an owned
    /// window rather than a child, which is the shape a dialog takes.
    /// </param>
    /// <param name="preventActivation">
    /// Keeps the window from taking focus from whatever the machine running the
    /// test is doing. Left off by default so the existing native placement
    /// contract keeps testing an ordinary window.
    /// </param>
    /// <param name="ownedAsPopup">
    /// Whether an owned window carries <c>WS_POPUP</c>. The two shapes are read
    /// differently by the ancestor API, so both have to be reproducible.
    /// </param>
    public TestTopLevelWindow(
        string? windowClassName = null,
        nint owner = 0,
        bool preventActivation = false,
        bool ownedAsPopup = true)
    {
        className = windowClassName ??
            $"DesktopShift.Contract.{Guid.NewGuid():N}";
        ownerHandle = owner;
        style = owner != 0 && ownedAsPopup
            ? PopupWindowStyle | OverlappedWindowStyle | VisibleWindowStyle
            : OverlappedWindowStyle | VisibleWindowStyle;
        extendedStyle = preventActivation ? NoActivateExtendedStyle : 0;
        windowProcedure = OnWindowMessage;
        thread = new Thread(ThreadMain)
        {
            IsBackground = true,
            Name = "DesktopShift test-owned window",
        };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        ready.Wait();

        if (startupException is not null)
        {
            throw new InvalidOperationException(
                "Could not start the test-owned window thread.",
                startupException);
        }
    }

    public nint Handle => windowHandle;

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        if (windowHandle != 0 &&
            !NativeMethods.PostMessage(windowHandle, CloseMessage, 0, 0))
        {
            throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "Could not close the test-owned window.");
        }

        thread.Join();
        ready.Dispose();
    }

    private void ThreadMain()
    {
        ushort classAtom = 0;
        nint module = NativeMethods.GetModuleHandle(null);
        try
        {
            WindowClass windowClass = new()
            {
                Size = checked((uint)Marshal.SizeOf<WindowClass>()),
                WindowProcedure = windowProcedure,
                Instance = module,
                ClassName = className,
            };
            classAtom = NativeMethods.RegisterClassEx(in windowClass);
            if (classAtom == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not register the test-owned window class.");
            }

            windowHandle = NativeMethods.CreateWindowEx(
                extendedStyle,
                className,
                "DesktopShift assignment contract",
                style,
                100,
                100,
                160,
                90,
                ownerHandle,
                0,
                module,
                0);
            if (windowHandle == 0)
            {
                throw new Win32Exception(
                    Marshal.GetLastWin32Error(),
                    "Could not create the test-owned top-level window.");
            }

            ready.Set();
            while (NativeMethods.GetMessage(out Message message, 0, 0, 0) > 0)
            {
                NativeMethods.TranslateMessage(in message);
                NativeMethods.DispatchMessage(in message);
            }
        }
        catch (Exception exception)
        {
            startupException = exception;
            ready.Set();
        }
        finally
        {
            windowHandle = 0;
            if (classAtom != 0)
            {
                NativeMethods.UnregisterClass(className, module);
            }
        }
    }

    private nint OnWindowMessage(
        nint window,
        uint message,
        nint wordParameter,
        nint longParameter)
    {
        if (message == DestroyMessage)
        {
            NativeMethods.PostQuitMessage(0);
            return 0;
        }

        return NativeMethods.DefWindowProc(
            window,
            message,
            wordParameter,
            longParameter);
    }

    [UnmanagedFunctionPointer(CallingConvention.Winapi)]
    private delegate nint WindowProcedure(
        nint window,
        uint message,
        nint wordParameter,
        nint longParameter);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct WindowClass
    {
        internal uint Size;
        internal uint Style;
        internal WindowProcedure WindowProcedure;
        internal int ClassExtraBytes;
        internal int WindowExtraBytes;
        internal nint Instance;
        internal nint Icon;
        internal nint Cursor;
        internal nint BackgroundBrush;
        internal string? MenuName;
        internal string ClassName;
        internal nint SmallIcon;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct Message
    {
        internal nint Window;
        internal uint Id;
        internal nuint WordParameter;
        internal nint LongParameter;
        internal uint Time;
        internal int PointX;
        internal int PointY;
        internal uint Private;
    }

    private static class NativeMethods
    {
        [DllImport(
            "user32.dll",
            EntryPoint = "RegisterClassExW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern ushort RegisterClassEx(in WindowClass windowClass);

        [DllImport(
            "user32.dll",
            EntryPoint = "UnregisterClassW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClass(
            string className,
            nint instance);

        [DllImport(
            "user32.dll",
            EntryPoint = "CreateWindowExW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern nint CreateWindowEx(
            uint extendedStyle,
            string className,
            string windowName,
            uint style,
            int x,
            int y,
            int width,
            int height,
            nint parent,
            nint menu,
            nint instance,
            nint parameter);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint window,
            uint message,
            nint wordParameter,
            nint longParameter);

        [DllImport(
            "user32.dll",
            EntryPoint = "GetMessageW",
            SetLastError = true)]
        internal static extern int GetMessage(
            out Message message,
            nint window,
            uint minimumMessage,
            uint maximumMessage);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(in Message message);

        [DllImport(
            "user32.dll",
            EntryPoint = "DispatchMessageW")]
        internal static extern nint DispatchMessage(in Message message);

        [DllImport(
            "user32.dll",
            EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(
            nint window,
            uint message,
            nint wordParameter,
            nint longParameter);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport(
            "kernel32.dll",
            EntryPoint = "GetModuleHandleW",
            CharSet = CharSet.Unicode,
            SetLastError = true)]
        internal static extern nint GetModuleHandle(string? moduleName);
    }
}

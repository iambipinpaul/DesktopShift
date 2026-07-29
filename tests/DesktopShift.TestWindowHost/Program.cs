using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DesktopShift.TestWindowHost;

internal static class Program
{
    private const string ExitCommand = "EXIT";

    public static int Main(string[] args)
    {
        if (args.Length != 1 || !Guid.TryParse(args[0], out Guid nonce))
        {
            Console.Error.WriteLine("A single handshake nonce is required.");
            return 2;
        }

        try
        {
            using HostedWindow window = new(nonce);
            WindowHandshake handshake = new(
                nonce,
                Environment.ProcessId,
                window.Handle.ToInt64());
            Console.Out.WriteLine(JsonSerializer.Serialize(handshake));
            Console.Out.Flush();

            string? command = Console.In.ReadLine();
            return string.Equals(
                command,
                $"{ExitCommand} {nonce:D}",
                StringComparison.Ordinal)
                ? 0
                : 3;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private sealed record WindowHandshake(
        Guid Nonce,
        int ProcessId,
        long WindowHandle);

    private sealed class HostedWindow : IDisposable
    {
        private const uint OverlappedWindowStyle = 0x00CF0000;
        private const int ShowWithoutActivation = 4;
        private const uint CloseMessage = 0x0010;
        private const uint DestroyMessage = 0x0002;

        private static readonly TimeSpan StartupTimeout = TimeSpan.FromSeconds(10);
        private static readonly TimeSpan ShutdownTimeout = TimeSpan.FromSeconds(5);

        private readonly ManualResetEventSlim ready = new();
        private readonly WindowProcedure windowProcedure;
        private readonly Thread thread;
        private readonly string className;
        private Exception? startupException;
        private nint windowHandle;
        private bool disposed;

        internal HostedWindow(Guid nonce)
        {
            className = $"DesktopShift.SecondProcess.{nonce:N}";
            windowProcedure = OnWindowMessage;
            thread = new Thread(ThreadMain)
            {
                IsBackground = true,
                Name = "DesktopShift isolated test window",
            };
            thread.SetApartmentState(ApartmentState.STA);
            thread.Start();

            if (!ready.Wait(StartupTimeout))
            {
                throw new TimeoutException(
                    "The isolated test window did not become ready.");
            }

            if (startupException is not null)
            {
                throw new InvalidOperationException(
                    "The isolated test window could not be created.",
                    startupException);
            }
        }

        internal nint Handle => windowHandle;

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            if (windowHandle != 0)
            {
                _ = NativeMethods.PostMessage(windowHandle, CloseMessage, 0, 0);
            }

            if (!thread.Join(ShutdownTimeout))
            {
                throw new TimeoutException(
                    "The isolated test window did not shut down.");
            }

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
                        "Could not register the isolated window class.");
                }

                // Keep the window ordinary enough for Shell to assign it a
                // virtual-desktop view. WS_EX_NOACTIVATE prevents that
                // assignment on current Windows builds, so creation starts
                // hidden and ShowWindow suppresses activation instead.
                windowHandle = NativeMethods.CreateWindowEx(
                    0,
                    className,
                    "DesktopShift isolated placement contract",
                    OverlappedWindowStyle,
                    -32000,
                    -32000,
                    160,
                    90,
                    0,
                    0,
                    module,
                    0);
                if (windowHandle == 0)
                {
                    throw new Win32Exception(
                        Marshal.GetLastWin32Error(),
                        "Could not create the isolated top-level window.");
                }

                _ = NativeMethods.ShowWindow(
                    windowHandle,
                    ShowWithoutActivation);
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

        private static nint OnWindowMessage(
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

            [DllImport("user32.dll")]
            [return: MarshalAs(UnmanagedType.Bool)]
            internal static extern bool ShowWindow(
                nint windowHandle,
                int commandShow);

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

            [DllImport("user32.dll", EntryPoint = "DispatchMessageW")]
            internal static extern nint DispatchMessage(in Message message);

            [DllImport("user32.dll", EntryPoint = "DefWindowProcW")]
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
}

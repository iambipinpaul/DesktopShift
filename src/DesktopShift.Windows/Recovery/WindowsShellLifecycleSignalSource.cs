using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopShift.Core.Recovery;

namespace DesktopShift.Windows.Recovery;

/// <summary>
/// Reports Explorer restarts, resumes from sleep, and display changes by
/// listening for the messages Windows broadcasts when they happen.
/// </summary>
/// <remarks>
/// <para>
/// The listener owns a hidden top-level window, and the fact that it is
/// top-level rather than message-only is the entire point of the type. All three
/// notifications it needs — the registered <c>TaskbarCreated</c> message,
/// <c>WM_DISPLAYCHANGE</c>, and <c>WM_POWERBROADCAST</c> — are broadcasts, and
/// Windows delivers broadcasts to top-level windows only. A message-only window
/// receives nothing of the sort, so a listener built on one would sit silently
/// through every disruption it exists to notice. This is the same reason
/// <c>ShellNotifyIconHost</c> gives for its own hidden window, and it is worth
/// repeating here because "message-only window" is the obvious-looking choice
/// for a window with no user interface.
/// </para>
/// <para>
/// The window is hidden and marked as a tool window, so it never appears to the
/// user, never appears in the taskbar, and never appears in the window switcher.
/// It renders nothing and owns no user interface.
/// </para>
/// <para>
/// The listener owns the thread its window lives on, and that ownership is the
/// second point of the type. A window belongs to the thread that created it, and
/// only that thread's message pump will ever deliver these broadcasts — so a
/// window created on a thread that does not pump receives nothing, silently and
/// forever. <see cref="Start"/> is called from <c>IHostedService.StartAsync</c>,
/// which runs on whichever thread the generic host's start sequence happens to
/// be on by then: any hosted service registered earlier that awaits real I/O
/// moves every later one onto the thread pool, where nothing pumps. Rather than
/// make correctness depend on registration order, this type starts a dedicated
/// background thread, creates the window on it, and runs the pump there for the
/// window's whole life.
/// </para>
/// <para>
/// That also decides how teardown works. <c>DestroyWindow</c> only succeeds when
/// called from the owning thread, so <see cref="Dispose"/> posts
/// <c>WM_CLOSE</c> to the window and joins the pump thread instead of destroying
/// the window where it stands.
/// </para>
/// <para>
/// This type observes and nothing more. It never restarts Explorer, never
/// suspends or resumes the machine, never changes a display setting, and never
/// raises a signal Windows did not actually deliver.
/// </para>
/// </remarks>
public sealed class WindowsShellLifecycleSignalSource : IShellLifecycleSignalSource
{
    private const string WindowClassNamePrefix = "DesktopShift.ShellLifecycleListener.";
    private const string WindowName = "DesktopShift shell lifecycle listener";

    /// <summary>
    /// The <c>TRUE</c> a window procedure returns for
    /// <c>WM_POWERBROADCAST</c>, which is what the documented contract asks for.
    /// </summary>
    private const nint PowerBroadcastHandled = 1;

    /// <summary>The zero a window procedure returns for a message it handled.</summary>
    private const nint MessageHandled = 0;

    /// <summary>How long <see cref="Dispose"/> waits for the pump thread to end.</summary>
    /// <remarks>
    /// The thread is a background thread, so a pump that somehow refuses to end
    /// cannot keep the process alive. This bound exists so that shutting the
    /// listener down cannot block the host's stop sequence either.
    /// </remarks>
    private static readonly TimeSpan PumpThreadJoinTimeout = TimeSpan.FromSeconds(5);

    private readonly NativeMethods.WindowProcedure windowProcedure;
    private readonly string windowClassName;
    private readonly uint taskbarCreatedMessage;
    private readonly nint instanceHandle;
    private readonly object syncRoot = new();
    private readonly ManualResetEventSlim pumpReady = new(false);
    private Thread? pumpThread;
    private Exception? pumpStartFailure;
    private nint windowHandle;
    private long signalsRaised;
    private long callbackFailures;
    private bool isWindowClassRegistered;
    private bool isDisposed;

    public WindowsShellLifecycleSignalSource()
    {
        // RegisterClassEx keeps the function pointer, not the delegate object.
        // Holding the delegate in a field for the window's whole lifetime is
        // what stops the garbage collector reclaiming it out from under Windows,
        // and it is the same reason WindowsWinEventSource holds its callback.
        windowProcedure = OnWindowMessage;
        windowClassName = WindowClassNamePrefix + Guid.NewGuid().ToString("N");
        instanceHandle = NativeMethods.GetModuleHandle(null);

        // The id is allocated once and reused for the object's whole life.
        // Asking again later would return the same id, but asking once keeps the
        // window procedure free of calls it does not need to make.
        taskbarCreatedMessage = NativeMethods.RegisterWindowMessage("TaskbarCreated");
    }

    /// <summary>
    /// The per-instance native class identity. Exposed internally so the
    /// lifetime invariant can be pinned without registering a real window.
    /// </summary>
    internal string WindowClassName => windowClassName;

    /// <inheritdoc />
    public event EventHandler<ShellLifecycleSignalEventArgs>? SignalRaised;

    /// <summary>Whether the hidden window exists and is receiving messages.</summary>
    public bool IsListening
    {
        get
        {
            lock (syncRoot)
            {
                return windowHandle != 0;
            }
        }
    }

    /// <summary>
    /// What this listener has seen, for the hosted layer to report.
    /// </summary>
    public ShellLifecycleSignalSourceSnapshot Snapshot => new(
        IsListening,
        Interlocked.Read(ref signalsRaised),
        Interlocked.Read(ref callbackFailures));

    /// <inheritdoc />
    /// <remarks>
    /// Returns once the window exists and its pump is running, so a caller that
    /// sees this return knows the listener is live rather than merely requested.
    /// A failure on the pump thread is re-thrown here for the same reason: the
    /// caller that asked to start listening is the one that has to hear that it
    /// did not.
    /// </remarks>
    public void Start()
    {
        Thread thread;
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);
            if (pumpThread is not null)
            {
                return;
            }

            thread = new Thread(RunPumpThread)
            {
                // Background, so a pump still draining cannot hold the process
                // open; STA because this thread owns a window for its lifetime.
                IsBackground = true,
                Name = WindowName,
            };
            thread.SetApartmentState(ApartmentState.STA);
            pumpThread = thread;
        }

        thread.Start();
        pumpReady.Wait();

        Exception? failure;
        lock (syncRoot)
        {
            failure = pumpStartFailure;
            if (failure is not null)
            {
                // A failed start must leave the object startable again rather
                // than stuck holding a thread that already ended.
                pumpThread = null;
                pumpStartFailure = null;
                pumpReady.Reset();
            }
        }

        if (failure is not null)
        {
            UnregisterWindowClass();
            throw failure;
        }
    }

    /// <summary>
    /// Destroys the hidden window and ends the thread that pumps it.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The window is closed by posting to it rather than by destroying it here.
    /// <c>DestroyWindow</c> fails when it is not called from the thread that owns
    /// the window, and this runs on whichever thread the host stops on, so
    /// posting <c>WM_CLOSE</c> is what makes teardown work from anywhere: the
    /// pump thread destroys its own window, the window procedure turns the
    /// resulting <c>WM_DESTROY</c> into a quit, and the pump ends.
    /// </para>
    /// <para>
    /// Every listener has its own class because the class holds this instance's
    /// window-procedure function pointer. Reusing one constant class would make
    /// a later listener call the first, potentially collected instance after a
    /// test host or app lifetime recreated the service. The class is unregistered
    /// after its only window is destroyed — which is why the join comes first —
    /// so Windows stops retaining that function pointer before this object can be
    /// collected.
    /// </para>
    /// </remarks>
    public void Dispose()
    {
        nint handle;
        Thread? thread;
        lock (syncRoot)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            handle = windowHandle;
            thread = pumpThread;
            pumpThread = null;
        }

        SignalRaised = null;

        if (handle != 0)
        {
            _ = NativeMethods.PostMessage(
                handle,
                NativeMethods.WmClose,
                0,
                0);
        }

        _ = thread?.Join(PumpThreadJoinTimeout);
        UnregisterWindowClass();
        pumpReady.Dispose();
    }

    /// <summary>
    /// Owns the window for its whole life: creates it, reports the outcome to
    /// <see cref="Start"/>, then pumps until the window is destroyed.
    /// </summary>
    private void RunPumpThread()
    {
        try
        {
            RegisterWindowClass();
            nint handle = CreateHiddenWindow();
            lock (syncRoot)
            {
                windowHandle = handle;
            }
        }
        catch (Exception exception)
        {
            lock (syncRoot)
            {
                pumpStartFailure = exception;
            }

            pumpReady.Set();
            return;
        }

        pumpReady.Set();

        try
        {
            RunMessageLoop();
        }
        finally
        {
            lock (syncRoot)
            {
                windowHandle = 0;
            }
        }
    }

    /// <summary>
    /// The pump itself, which ends only when the window is destroyed.
    /// </summary>
    /// <remarks>
    /// <c>GetMessage</c> answers three ways and all three end the loop or
    /// continue it: zero is the <c>WM_QUIT</c> the window procedure posts on
    /// <c>WM_DESTROY</c>, and -1 is an error against a handle that is already
    /// gone. Neither is worth throwing over on a thread nobody can catch on.
    /// </remarks>
    private static void RunMessageLoop()
    {
        while (true)
        {
            int available = NativeMethods.GetMessage(
                out NativeMethods.Message message,
                0,
                0,
                0);
            if (available is 0 or -1)
            {
                return;
            }

            _ = NativeMethods.TranslateMessage(ref message);
            _ = NativeMethods.DispatchMessage(ref message);
        }
    }

    private void UnregisterWindowClass()
    {
        if (!isWindowClassRegistered)
        {
            return;
        }

        _ = NativeMethods.UnregisterClass(windowClassName, instanceHandle);
        isWindowClassRegistered = false;
    }

    private void RegisterWindowClass()
    {
        if (isWindowClassRegistered)
        {
            return;
        }

        NativeMethods.WindowClassEx windowClass = new()
        {
            Size = (uint)Marshal.SizeOf<NativeMethods.WindowClassEx>(),
            WindowProcedure = Marshal.GetFunctionPointerForDelegate(windowProcedure),
            InstanceHandle = instanceHandle,
            ClassName = windowClassName,
        };

        if (NativeMethods.RegisterClassEx(ref windowClass) != 0)
        {
            isWindowClassRegistered = true;
            return;
        }

        throw new Win32Exception(
            Marshal.GetLastWin32Error(),
            "DesktopShift could not register its shell lifecycle window class.");
    }

    private nint CreateHiddenWindow()
    {
        nint handle = NativeMethods.CreateWindowEx(
            dwExStyle: NativeMethods.WsExToolWindow,
            lpClassName: windowClassName,
            lpWindowName: WindowName,
            dwStyle: NativeMethods.WsPopup,
            x: 0,
            y: 0,
            nWidth: 0,
            nHeight: 0,
            hWndParent: 0,
            hMenu: 0,
            hInstance: instanceHandle,
            lpParam: 0);

        return handle != 0
            ? handle
            : throw new Win32Exception(
                Marshal.GetLastWin32Error(),
                "DesktopShift could not create its shell lifecycle window.");
    }

    /// <summary>
    /// Turns one window message into at most one signal and returns.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This runs on the message pump, so it does no COM, no process access, no
    /// disk I/O, no logging, and no waiting — not even on a lock. Deciding what
    /// a message means is a pure function; raising the event hands the decision
    /// to the coalescer, and everything expensive happens on the other side of
    /// that handoff. The window procedure also re-enters during
    /// <c>CreateWindowEx</c> and <c>DestroyWindow</c>, so a lock taken here
    /// would be a lock taken while <see cref="Start"/> or <see cref="Dispose"/>
    /// already holds it.
    /// </para>
    /// <para>
    /// No managed exception may escape into Windows, so a handler that throws is
    /// counted and swallowed exactly as
    /// <c>WindowsWinEventSource.OnWinEvent</c> counts and swallows one. A
    /// listener that crashed the message pump because a subscriber threw would
    /// take down the very thing recovery exists to keep running.
    /// </para>
    /// </remarks>
    private nint OnWindowMessage(
        nint handle,
        uint message,
        nint wParam,
        nint lParam)
    {
        // Destruction is what ends the pump. Posting the quit here rather than
        // from Dispose keeps it on the thread whose loop has to see it, and
        // keeps it correct whether the window went away because Dispose asked
        // or because Windows tore it down first.
        if (message == NativeMethods.WmDestroy)
        {
            NativeMethods.PostQuitMessage(0);
            return MessageHandled;
        }

        // Windows documents that a window handling WM_POWERBROADCAST returns
        // TRUE, and it says so for every power notification rather than only for
        // the two that mean "awake". The answer is therefore decided from the
        // message alone, before anything can throw.
        bool isPowerBroadcast = message == WindowsShellLifecycleMessages.PowerBroadcast;

        try
        {
            if (WindowsShellLifecycleMessages.TryMap(
                message,
                wParam,
                taskbarCreatedMessage,
                out ShellLifecycleSignal signal))
            {
                Interlocked.Increment(ref signalsRaised);
                SignalRaised?.Invoke(this, new ShellLifecycleSignalEventArgs(signal));
                return isPowerBroadcast ? PowerBroadcastHandled : MessageHandled;
            }
        }
        catch (Exception)
        {
            Interlocked.Increment(ref callbackFailures);
            return isPowerBroadcast ? PowerBroadcastHandled : MessageHandled;
        }

        return isPowerBroadcast
            ? PowerBroadcastHandled
            : NativeMethods.DefWindowProc(handle, message, wParam, lParam);
    }

    private static class NativeMethods
    {
        internal const uint WsPopup = 0x80000000;
        internal const uint WsExToolWindow = 0x00000080;
        internal const uint WmDestroy = 0x0002;
        internal const uint WmClose = 0x0010;

        internal delegate nint WindowProcedure(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [StructLayout(LayoutKind.Sequential)]
        internal struct Point
        {
            public int X;
            public int Y;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct Message
        {
            public nint WindowHandle;
            public uint Value;
            public nint WParam;
            public nint LParam;
            public uint Time;
            public Point Cursor;
        }

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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterClassExW", SetLastError = true)]
        internal static extern ushort RegisterClassEx(ref WindowClassEx windowClass);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "CreateWindowExW", SetLastError = true)]
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

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "UnregisterClassW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnregisterClass(
            string className,
            nint instanceHandle);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DefWindowProcW")]
        internal static extern nint DefWindowProc(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetMessageW", SetLastError = true)]
        internal static extern int GetMessage(
            out Message message,
            nint windowHandle,
            uint filterMinimum,
            uint filterMaximum);

        [DllImport("user32.dll")]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool TranslateMessage(ref Message message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "DispatchMessageW")]
        internal static extern nint DispatchMessage(ref Message message);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll")]
        internal static extern void PostQuitMessage(int exitCode);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
        internal static extern uint RegisterWindowMessage(string message);

        [DllImport("kernel32.dll", CharSet = CharSet.Unicode, EntryPoint = "GetModuleHandleW")]
        internal static extern nint GetModuleHandle(string? moduleName);
    }
}

/// <summary>
/// What a shell lifecycle listener has seen, mirroring
/// <c>WinEventSourceSnapshot</c> so the hosted layer reports both the same way.
/// </summary>
/// <param name="IsListening">Whether the hidden window currently exists.</param>
/// <param name="SignalsRaised">
/// How many disruptions were reported. This counts mapped lifecycle signals,
/// not every native message: the later user-interaction resume broadcast is
/// normalized away before this count is incremented, while overlapping repeated
/// signals are folded by the recovery service.
/// </param>
/// <param name="CallbackFailures">
/// How many exceptions were swallowed at the native boundary. Anything above
/// zero means a subscriber threw, which is worth reporting because the message
/// pump cannot.
/// </param>
public sealed record ShellLifecycleSignalSourceSnapshot(
    bool IsListening,
    long SignalsRaised,
    long CallbackFailures);

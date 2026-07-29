using System.Runtime.InteropServices;
using DesktopShift.Core.Recovery;
using DesktopShift.Windows.Recovery;

namespace DesktopShift.Windows.Tests.Recovery;

[TestClass]
public sealed class WindowsShellLifecycleSignalSourceTests
{
    private static readonly TimeSpan SignalTimeout = TimeSpan.FromSeconds(5);

    [TestMethod]
    public void SeparateListeners_NeverReuseAnInstanceBoundWindowClass()
    {
        using WindowsShellLifecycleSignalSource first = new();
        using WindowsShellLifecycleSignalSource second = new();

        Assert.AreNotEqual(first.WindowClassName, second.WindowClassName);
        StringAssert.StartsWith(
            first.WindowClassName,
            "DesktopShift.ShellLifecycleListener.");
        StringAssert.StartsWith(
            second.WindowClassName,
            "DesktopShift.ShellLifecycleListener.");
    }

    /// <summary>
    /// The regression that matters. A listener started from a thread with no
    /// message pump used to create its window there and then never hear a
    /// single broadcast, so every Explorer restart went unnoticed and the app
    /// stayed in Limited Mode until it was restarted by hand.
    /// </summary>
    /// <remarks>
    /// Starting from the thread pool is exactly what the generic host does once
    /// any hosted service registered earlier awaits real I/O, so this is the
    /// real caller rather than a contrived one. The message is posted to this
    /// listener's own window by handle — never broadcast — so the test cannot
    /// touch any other window on the machine.
    /// </remarks>
    [TestMethod]
    public void ListenerStartedOffAPumplessThread_StillReceivesBroadcastMessages()
    {
        using WindowsShellLifecycleSignalSource source = new();
        using ManualResetEventSlim raised = new(false);
        ShellLifecycleSignal? observed = null;
        source.SignalRaised += (_, args) =>
        {
            observed = args.Signal;
            raised.Set();
        };

        Task.Run(source.Start).GetAwaiter().GetResult();

        Assert.IsTrue(source.IsListening);
        nint window = NativeMethods.FindWindow(source.WindowClassName, null);
        Assert.AreNotEqual(0, window);

        Assert.IsTrue(
            NativeMethods.PostMessage(
                window,
                NativeMethods.RegisterWindowMessage("TaskbarCreated"),
                0,
                0));

        Assert.IsTrue(raised.Wait(SignalTimeout));
        Assert.AreEqual(ShellLifecycleSignal.ExplorerRestarted, observed);
        Assert.AreEqual(1, source.Snapshot.SignalsRaised);
        Assert.AreEqual(0, source.Snapshot.CallbackFailures);
    }

    [TestMethod]
    public void Listener_ReportsDisplayChangesOnTheSamePump()
    {
        using WindowsShellLifecycleSignalSource source = new();
        using ManualResetEventSlim raised = new(false);
        ShellLifecycleSignal? observed = null;
        source.SignalRaised += (_, args) =>
        {
            observed = args.Signal;
            raised.Set();
        };

        Task.Run(source.Start).GetAwaiter().GetResult();
        nint window = NativeMethods.FindWindow(source.WindowClassName, null);

        Assert.IsTrue(
            NativeMethods.PostMessage(
                window,
                WindowsShellLifecycleMessages.DisplayChange,
                0,
                0));

        Assert.IsTrue(raised.Wait(SignalTimeout));
        Assert.AreEqual(ShellLifecycleSignal.DisplayChanged, observed);
    }

    /// <summary>
    /// Teardown has to work from a thread that does not own the window, because
    /// the host stops on whatever thread it stops on. <c>DestroyWindow</c> fails
    /// silently when called from the wrong thread, which would leak the window,
    /// the class, and the pump thread for the life of the process.
    /// </summary>
    [TestMethod]
    public void Dispose_FromAThreadThatDoesNotOwnTheWindow_TearsTheListenerDown()
    {
        WindowsShellLifecycleSignalSource source = new();
        Task.Run(source.Start).GetAwaiter().GetResult();
        string className = source.WindowClassName;
        Assert.AreNotEqual(0, NativeMethods.FindWindow(className, null));

        source.Dispose();

        Assert.IsFalse(source.IsListening);
        Assert.AreEqual(0, NativeMethods.FindWindow(className, null));
    }

    [TestMethod]
    public void Start_IsIdempotentAndDisposeRemainsSafeToRepeat()
    {
        WindowsShellLifecycleSignalSource source = new();

        source.Start();
        source.Start();

        Assert.IsTrue(source.IsListening);
        source.Dispose();
        source.Dispose();
        Assert.IsFalse(source.IsListening);
    }

    [TestMethod]
    public void Start_AfterDispose_Throws()
    {
        WindowsShellLifecycleSignalSource source = new();
        source.Dispose();

        _ = Assert.ThrowsExactly<ObjectDisposedException>(source.Start);
    }

    private static class NativeMethods
    {
        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "FindWindowW", SetLastError = true)]
        internal static extern nint FindWindow(string? className, string? windowName);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "PostMessageW", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool PostMessage(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam);

        [DllImport("user32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegisterWindowMessageW")]
        internal static extern uint RegisterWindowMessage(string message);
    }
}

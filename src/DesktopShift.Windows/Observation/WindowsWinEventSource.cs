using System.ComponentModel;
using System.Runtime.InteropServices;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;

namespace DesktopShift.Windows.Observation;

public sealed class WindowsWinEventSource : IWindowEventSource, IRestartableWindowEventSource
{
    public const uint EventSystemForeground = 0x0003;
    public const uint EventObjectCreate = 0x8000;
    public const uint EventObjectDestroy = 0x8001;
    public const uint EventObjectShow = 0x8002;
    public const uint EventObjectCloaked = 0x8017;
    public const uint EventObjectUncloaked = 0x8018;

    private const int ObjectIdWindow = 0;
    private const int ChildIdSelf = 0;
    private const uint OutOfContext = 0x0000;
    private const uint SkipOwnProcess = 0x0002;

    private static readonly (uint EventId, WindowEventKind Kind)[] Registrations =
    [
        (EventObjectCreate, WindowEventKind.Created),
        (EventObjectShow, WindowEventKind.Shown),
        (EventSystemForeground, WindowEventKind.ForegroundActivated),
        (EventObjectDestroy, WindowEventKind.Destroyed),
        (EventObjectCloaked, WindowEventKind.Cloaked),
        (EventObjectUncloaked, WindowEventKind.Uncloaked),
    ];

    private readonly object syncRoot = new();
    private readonly IWindowEventQueue queue;
    private readonly IWinEventHookApi nativeApi;
    private readonly TimeProvider timeProvider;
    private readonly WinEventCallback callback;
    private readonly List<nint> hooks = [];
    private long sequence;
    private long published;
    private long queueRejected;
    private long callbackFailures;
    private bool disposed;

    public WindowsWinEventSource(
        IWindowEventQueue queue,
        IWinEventHookApi? nativeApi = null,
        TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(queue);

        this.queue = queue;
        this.nativeApi = nativeApi ?? new WinEventHookApi();
        this.timeProvider = timeProvider ?? TimeProvider.System;

        // SetWinEventHook keeps the function pointer, not the delegate object. Keeping this
        // field alive for the complete hook lifetime prevents delegate collection.
        callback = OnWinEvent;
    }

    public bool IsRunning
    {
        get
        {
            lock (syncRoot)
            {
                return hooks.Count > 0;
            }
        }
    }

    public WinEventSourceSnapshot Snapshot => new(
        IsRunning,
        Interlocked.Read(ref published),
        Interlocked.Read(ref queueRejected),
        Interlocked.Read(ref callbackFailures));

    public void Start()
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (hooks.Count > 0)
            {
                return;
            }

            try
            {
                foreach ((uint eventId, _) in Registrations)
                {
                    nint hook = nativeApi.SetHook(
                        eventId,
                        eventId,
                        callback,
                        OutOfContext | SkipOwnProcess);
                    if (hook == 0)
                    {
                        throw new Win32Exception(
                            Marshal.GetLastWin32Error(),
                            $"SetWinEventHook failed for event 0x{eventId:X4}.");
                    }

                    hooks.Add(hook);
                }
            }
            catch
            {
                UnhookAll();
                throw;
            }
        }
    }

    /// <summary>
    /// Drops every hook while leaving this source able to <see cref="Start"/>
    /// again.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Restarting Explorer invalidates every hook that was registered against
    /// the shell that just went away, and the only way to get working hooks back
    /// is to unhook and hook again. That has to be possible without tearing the
    /// source down, because the bounded queue, the observation processor, and
    /// the hosted service that owns them hold state a shell restart has no
    /// reason to discard — the sequence counter, events already queued, and the
    /// activity history a user may be reading at that moment.
    /// </para>
    /// <para>
    /// Stopping therefore clears the hooks and nothing else. It deliberately
    /// does not set <c>disposed</c>, which is the difference between this and
    /// <see cref="Dispose"/>. Stopping a source that never started, was already
    /// stopped, or has been disposed does nothing at all rather than throwing:
    /// recovery is frequently reacting to a shell that has already vanished, and
    /// turning that race into an exception would fail a recovery pass over a
    /// state that is exactly what was being asked for.
    /// </para>
    /// </remarks>
    public void Stop()
    {
        lock (syncRoot)
        {
            UnhookAll();
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            UnhookAll();
        }
    }

    private void OnWinEvent(
        nint hook,
        uint eventId,
        nint windowHandle,
        int objectId,
        int childId,
        uint eventThread,
        uint eventTime)
    {
        try
        {
            // Native callbacks deliberately do no process access, COM, disk I/O,
            // waiting, or logging. The bounded queue is the only handoff.
            if (windowHandle == 0 ||
                objectId != ObjectIdWindow ||
                childId != ChildIdSelf ||
                !TryGetEventKind(eventId, out WindowEventKind kind))
            {
                return;
            }

            // Both clocks are read here, at the only moment that is genuinely
            // receipt: the wall clock for the Activity view and for coalescing,
            // and the monotonic reading so event-to-move latency starts where the
            // user's wait started rather than where the processor got round to it.
            bool accepted = queue.TryPublish(
                new WindowEvent(
                    Interlocked.Increment(ref sequence),
                    kind,
                    windowHandle,
                    timeProvider.GetUtcNow(),
                    timeProvider.GetTimestamp()));
            if (accepted)
            {
                Interlocked.Increment(ref published);
            }
            else
            {
                Interlocked.Increment(ref queueRejected);
            }
        }
        catch (Exception)
        {
            // No managed exception may escape an unmanaged WinEvent callback.
            Interlocked.Increment(ref callbackFailures);
        }
    }

    private static bool TryGetEventKind(
        uint eventId,
        out WindowEventKind kind)
    {
        foreach ((uint id, WindowEventKind candidate) in Registrations)
        {
            if (eventId == id)
            {
                kind = candidate;
                return true;
            }
        }

        kind = default;
        return false;
    }

    private void UnhookAll()
    {
        foreach (nint hook in hooks)
        {
            nativeApi.Unhook(hook);
        }

        hooks.Clear();
    }
}

public sealed record WinEventSourceSnapshot(
    bool IsRunning,
    long Published,
    long QueueRejected,
    long CallbackFailures);

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
public delegate void WinEventCallback(
    nint hook,
    uint eventId,
    nint windowHandle,
    int objectId,
    int childId,
    uint eventThread,
    uint eventTime);

public interface IWinEventHookApi
{
    nint SetHook(
        uint eventMin,
        uint eventMax,
        WinEventCallback callback,
        uint flags);

    bool Unhook(nint hook);
}

public sealed class WinEventHookApi : IWinEventHookApi
{
    public nint SetHook(
        uint eventMin,
        uint eventMax,
        WinEventCallback callback,
        uint flags) =>
        NativeMethods.SetWinEventHook(
            eventMin,
            eventMax,
            0,
            callback,
            0,
            0,
            flags);

    public bool Unhook(nint hook) => NativeMethods.UnhookWinEvent(hook);

    private static class NativeMethods
    {
        [DllImport("user32.dll", SetLastError = true)]
        internal static extern nint SetWinEventHook(
            uint eventMin,
            uint eventMax,
            nint eventHookModule,
            WinEventCallback callback,
            uint processId,
            uint threadId,
            uint flags);

        [DllImport("user32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        internal static extern bool UnhookWinEvent(nint hook);
    }
}

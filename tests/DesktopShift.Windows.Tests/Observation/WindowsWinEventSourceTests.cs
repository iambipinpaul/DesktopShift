using System.Runtime.CompilerServices;
using DesktopShift.Core.Observation;
using DesktopShift.Windows.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowsWinEventSourceTests
{
    [TestMethod]
    public async Task Start_RegistersOnlyExactRequiredEvents_AndDisposeUnhooksAll()
    {
        BoundedWindowEventQueue queue = new(8);
        RecordingHookApi nativeApi = new();
        WindowsWinEventSource source = new(queue, nativeApi);

        source.Start();
        source.Start();

        CollectionAssert.AreEquivalent(
            new uint[]
            {
                WindowsWinEventSource.EventObjectCreate,
                WindowsWinEventSource.EventObjectShow,
                WindowsWinEventSource.EventSystemForeground,
                WindowsWinEventSource.EventObjectDestroy,
            },
            nativeApi.Registrations.Select(static item => item.Minimum).ToArray());
        Assert.IsTrue(
            nativeApi.Registrations.All(
                static item => item.Minimum == item.Maximum));
        Assert.AreEqual(4, nativeApi.Registrations.Count);
        Assert.IsTrue(source.IsRunning);

        foreach (HookRegistration registration in nativeApi.Registrations)
        {
            registration.Callback(
                registration.Handle,
                registration.Minimum,
                (nint)42,
                0,
                0,
                0,
                0);
        }

        // Child/object callbacks are rejected before the queue.
        nativeApi.Registrations[0].Callback(
            nativeApi.Registrations[0].Handle,
            WindowsWinEventSource.EventObjectCreate,
            (nint)42,
            -4,
            0,
            0,
            0);

        source.Dispose();
        source.Dispose();
        Assert.IsFalse(source.IsRunning);
        CollectionAssert.AreEquivalent(
            nativeApi.Registrations
                .Select(static item => item.Handle)
                .ToArray(),
            nativeApi.Unhooked.ToArray());

        queue.Complete();
        List<WindowEvent> events = [];
        await foreach (WindowEvent item in queue.ReadAllAsync())
        {
            events.Add(item);
        }

        Assert.AreEqual(4, events.Count);
        Assert.AreEqual(4L, source.Snapshot.Published);
        Assert.AreEqual(0L, source.Snapshot.CallbackFailures);
        CollectionAssert.AreEquivalent(
            new[]
            {
                WindowEventKind.Created,
                WindowEventKind.Shown,
                WindowEventKind.ForegroundActivated,
                WindowEventKind.Destroyed,
            },
            events.Select(static item => item.Kind).ToArray());
    }

    [TestMethod]
    public void Callback_ClockOrQueueThrows_ContainsFailureAtNativeBoundary()
    {
        RecordingHookApi queueHookApi = new();
        using WindowsWinEventSource throwingQueueSource = new(
            new ThrowingWindowEventQueue(),
            queueHookApi);
        throwingQueueSource.Start();

        queueHookApi.Registrations[0].Callback(
            queueHookApi.Registrations[0].Handle,
            WindowsWinEventSource.EventObjectCreate,
            (nint)42,
            0,
            0,
            0,
            0);

        Assert.AreEqual(
            1L,
            throwingQueueSource.Snapshot.CallbackFailures);

        RecordingHookApi clockHookApi = new();
        using WindowsWinEventSource throwingClockSource = new(
            new BoundedWindowEventQueue(1),
            clockHookApi,
            new ThrowingTimeProvider());
        throwingClockSource.Start();

        clockHookApi.Registrations[0].Callback(
            clockHookApi.Registrations[0].Handle,
            WindowsWinEventSource.EventObjectCreate,
            (nint)42,
            0,
            0,
            0,
            0);

        Assert.AreEqual(
            1L,
            throwingClockSource.Snapshot.CallbackFailures);
        Assert.AreEqual(0L, throwingClockSource.Snapshot.Published);
    }

    private sealed class RecordingHookApi : IWinEventHookApi
    {
        public List<HookRegistration> Registrations { get; } = [];

        public List<nint> Unhooked { get; } = [];

        public nint SetHook(
            uint eventMin,
            uint eventMax,
            WinEventCallback callback,
            uint flags)
        {
            nint handle = (nint)(Registrations.Count + 1);
            Registrations.Add(
                new HookRegistration(
                    handle,
                    eventMin,
                    eventMax,
                    callback));
            return handle;
        }

        public bool Unhook(nint hook)
        {
            Unhooked.Add(hook);
            return true;
        }
    }

    private sealed record HookRegistration(
        nint Handle,
        uint Minimum,
        uint Maximum,
        WinEventCallback Callback);

    private sealed class ThrowingWindowEventQueue : IWindowEventQueue
    {
        public WindowEventQueueSnapshot Snapshot =>
            new(1, 0, 0, 0, 0);

        public bool TryPublish(WindowEvent windowEvent) =>
            throw new InvalidOperationException("queue failed");

        public async IAsyncEnumerable<WindowEvent> ReadAllAsync(
            [EnumeratorCancellation]
            CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void Complete()
        {
        }
    }

    private sealed class ThrowingTimeProvider : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() =>
            throw new InvalidOperationException("clock failed");
    }
}

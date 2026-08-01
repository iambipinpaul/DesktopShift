using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;
using DesktopShift.Windows.Observation;

namespace DesktopShift.Windows.Tests.Recovery;

/// <summary>
/// The restart behaviour an Explorer restart depends on, proved against a fake
/// hook API.
/// </summary>
/// <remarks>
/// No real WinEvent hook is installed and no real shell is restarted. The fake
/// records what was asked for and what was released, which is the only thing
/// worth asserting: recovery's contract is that every hook taken against the old
/// shell is released and a fresh set is taken, and both halves are visible as
/// handles rather than as machine state.
/// </remarks>
[TestClass]
public sealed class WindowsWinEventSourceRestartTests
{
    private const int ExpectedHookCount = 6;

    [TestMethod]
    public void TheWindowEventSource_AdvertisesItselfAsRestartable()
    {
        // Recovery composition refuses a source that cannot be restarted, so
        // the shipping source has to keep saying that it can be.
        using WindowsWinEventSource source = new(
            new BoundedWindowEventQueue(8),
            new RecordingHookApi());

        Assert.IsInstanceOfType<IRestartableWindowEventSource>(source);
    }

    [TestMethod]
    public void Stop_UnhooksEveryHookAndLeavesTheSourceStopped()
    {
        RecordingHookApi nativeApi = new();
        using WindowsWinEventSource source = new(
            new BoundedWindowEventQueue(8),
            nativeApi);
        source.Start();
        nint[] taken = nativeApi.Registrations
            .Select(static registration => registration.Handle)
            .ToArray();

        source.Stop();

        Assert.AreEqual(ExpectedHookCount, taken.Length);
        Assert.IsFalse(source.IsRunning);
        CollectionAssert.AreEquivalent(
            taken,
            nativeApi.Unhooked.ToArray(),
            "A hook taken against the previous shell was left installed.");
    }

    [TestMethod]
    public void StartAfterStop_TakesTheHooksAgainWithoutANewSource()
    {
        RecordingHookApi nativeApi = new();
        using WindowsWinEventSource source = new(
            new BoundedWindowEventQueue(8),
            nativeApi);
        source.Start();
        nint[] first = nativeApi.Registrations
            .Select(static registration => registration.Handle)
            .ToArray();
        source.Stop();

        source.Start();

        Assert.IsTrue(source.IsRunning);
        Assert.AreEqual(ExpectedHookCount * 2, nativeApi.Registrations.Count);

        // Fresh handles, not the old ones handed back. The point of the restart
        // is that the second set was registered against the shell that is
        // running now.
        nint[] second = nativeApi.Registrations
            .Select(static registration => registration.Handle)
            .Skip(ExpectedHookCount)
            .ToArray();
        Assert.IsEmpty(second.Intersect(first).ToArray());

        // The same six events, asked for again exactly once each.
        CollectionAssert.AreEquivalent(
            new uint[]
            {
                WindowsWinEventSource.EventObjectCreate,
                WindowsWinEventSource.EventObjectShow,
                WindowsWinEventSource.EventSystemForeground,
                WindowsWinEventSource.EventObjectDestroy,
                WindowsWinEventSource.EventObjectCloaked,
                WindowsWinEventSource.EventObjectUncloaked,
            },
            nativeApi.Registrations
                .Skip(ExpectedHookCount)
                .Select(static registration => registration.Minimum)
                .ToArray());
    }

    [TestMethod]
    public void Stop_CalledTwice_ChangesNothingTheSecondTime()
    {
        RecordingHookApi nativeApi = new();
        using WindowsWinEventSource source = new(
            new BoundedWindowEventQueue(8),
            nativeApi);
        source.Start();
        source.Stop();
        int afterFirstStop = nativeApi.Unhooked.Count;

        source.Stop();

        Assert.AreEqual(ExpectedHookCount, afterFirstStop);
        Assert.AreEqual(afterFirstStop, nativeApi.Unhooked.Count);
        Assert.IsFalse(source.IsRunning);
    }

    [TestMethod]
    public void Stop_AfterDispose_IsHarmless()
    {
        // Recovery can easily be answering a shell that went away during
        // shutdown. Stopping a source that is already gone has to be a no-op
        // rather than a thrown exception, or a harmless race becomes a failed
        // recovery pass.
        RecordingHookApi nativeApi = new();
        WindowsWinEventSource source = new(
            new BoundedWindowEventQueue(8),
            nativeApi);
        source.Start();
        source.Dispose();
        int afterDispose = nativeApi.Unhooked.Count;

        source.Stop();
        source.Stop();

        Assert.AreEqual(ExpectedHookCount, afterDispose);
        Assert.AreEqual(afterDispose, nativeApi.Unhooked.Count);
        Assert.IsFalse(source.IsRunning);
    }

    [TestMethod]
    public void Stop_BeforeAnythingWasEverStarted_IsHarmless()
    {
        RecordingHookApi nativeApi = new();
        using WindowsWinEventSource source = new(
            new BoundedWindowEventQueue(8),
            nativeApi);

        source.Stop();

        Assert.IsFalse(source.IsRunning);
        Assert.IsEmpty(nativeApi.Unhooked);
        Assert.IsEmpty(nativeApi.Registrations);
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
            _ = flags;

            // Handles are never reused, so a test can tell a fresh registration
            // from a stale one that was handed back.
            nint handle = (nint)(Registrations.Count + 1);
            Registrations.Add(
                new HookRegistration(handle, eventMin, eventMax, callback));
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
}

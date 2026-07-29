using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Core.Tests.Hotkeys;

[TestClass]
public sealed class HotkeyCommandDispatcherTests
{
    [TestMethod]
    public async Task Dispatch_RoutesEveryActionToExactlyOneCommand()
    {
        List<HotkeyAction> calls = [];
        HotkeyCommandDispatcher dispatcher = CreateDispatcher(calls);

        foreach (HotkeyAction action in Enum.GetValues<HotkeyAction>())
        {
            await dispatcher.DispatchAsync(action);
        }

        CollectionAssert.AreEqual(Enum.GetValues<HotkeyAction>(), calls);
    }

    [TestMethod]
    public async Task Dispatch_DropsASecondPressOfTheSameRunningAction()
    {
        TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int callCount = 0;
        HotkeyCommandDispatcher dispatcher = new(new HotkeyCommands(
            async _ =>
            {
                callCount++;
                await release.Task;
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask));

        Task first = dispatcher.DispatchAsync(HotkeyAction.ReassignAllWindows);
        Task duplicate = dispatcher.DispatchAsync(HotkeyAction.ReassignAllWindows);

        Assert.AreEqual(1, callCount);
        Assert.IsTrue(duplicate.IsCompletedSuccessfully);
        release.SetResult();
        await first;
        await dispatcher.DispatchAsync(HotkeyAction.ReassignAllWindows);
        Assert.AreEqual(2, callCount);
    }

    [TestMethod]
    public async Task Dispatch_AllowsDifferentActionsToRunConcurrently()
    {
        TaskCompletionSource release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        int running = 0;
        int maximumRunning = 0;
        async Task Command(CancellationToken _)
        {
            int current = Interlocked.Increment(ref running);
            maximumRunning = Math.Max(maximumRunning, current);
            await release.Task;
            Interlocked.Decrement(ref running);
        }

        HotkeyCommandDispatcher dispatcher = new(new HotkeyCommands(
            Command,
            Command,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask));

        Task all = dispatcher.DispatchAsync(HotkeyAction.ReassignAllWindows);
        Task foreground =
            dispatcher.DispatchAsync(HotkeyAction.ReassignForegroundWindow);

        Assert.AreEqual(2, maximumRunning);
        release.SetResult();
        await Task.WhenAll(all, foreground);
    }

    [TestMethod]
    public async Task Dispatch_AFaultOrCancellationDoesNotLeaveTheActionRunning()
    {
        int faultCalls = 0;
        HotkeyCommandDispatcher faulting = new(new HotkeyCommands(
            _ =>
            {
                faultCalls++;
                return Task.FromException(new InvalidOperationException("failed"));
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask));

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faulting.DispatchAsync(HotkeyAction.ReassignAllWindows));
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            faulting.DispatchAsync(HotkeyAction.ReassignAllWindows));
        Assert.AreEqual(2, faultCalls);

        int canceledCalls = 0;
        HotkeyCommandDispatcher canceling = new(new HotkeyCommands(
            _ =>
            {
                canceledCalls++;
                return Task.FromCanceled(new CancellationToken(canceled: true));
            },
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            canceling.DispatchAsync(HotkeyAction.ReassignAllWindows));
        await Assert.ThrowsAsync<TaskCanceledException>(() =>
            canceling.DispatchAsync(HotkeyAction.ReassignAllWindows));
        Assert.AreEqual(2, canceledCalls);
    }

    [TestMethod]
    public async Task HandleInvoked_DoesNotThrowAndLeavesFailuresVisibleThroughPending()
    {
        HotkeyCommandDispatcher dispatcher = new(new HotkeyCommands(
            static _ => throw new InvalidOperationException("synchronous"),
            static _ => Task.FromException(new InvalidOperationException("asynchronous")),
            static _ => Task.CompletedTask,
            static _ => Task.CompletedTask));

        dispatcher.HandleInvoked(
            this,
            new HotkeyInvokedEventArgs(HotkeyAction.ReassignAllWindows));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.Pending);
        dispatcher.HandleInvoked(
            this,
            new HotkeyInvokedEventArgs(HotkeyAction.ReassignForegroundWindow));
        await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await dispatcher.Pending);

        Assert.IsTrue(dispatcher.Pending.IsFaulted);
    }

    [TestMethod]
    public void Dispatch_RejectsAnUndefinedAction()
    {
        HotkeyCommandDispatcher dispatcher = CreateDispatcher([]);

        Assert.Throws<ArgumentOutOfRangeException>(() =>
            dispatcher.DispatchAsync((HotkeyAction)999));
    }

    private static HotkeyCommandDispatcher CreateDispatcher(
        ICollection<HotkeyAction> calls) =>
        new(new HotkeyCommands(
            cancellationToken => Record(
                calls,
                HotkeyAction.ReassignAllWindows,
                cancellationToken),
            cancellationToken => Record(
                calls,
                HotkeyAction.ReassignForegroundWindow,
                cancellationToken),
            cancellationToken => Record(
                calls,
                HotkeyAction.TogglePause,
                cancellationToken),
            cancellationToken => Record(
                calls,
                HotkeyAction.OpenDesktopShift,
                cancellationToken)));

    private static Task Record(
        ICollection<HotkeyAction> calls,
        HotkeyAction action,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        calls.Add(action);
        return Task.CompletedTask;
    }
}

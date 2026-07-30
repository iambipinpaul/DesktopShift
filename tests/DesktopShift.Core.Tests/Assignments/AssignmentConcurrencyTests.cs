using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Assignments;

/// <summary>
/// What happens when assignments for the same window, and for different
/// windows, arrive at once.
/// </summary>
/// <remarks>
/// The pump drains one event at a time, but three other paths reach the same
/// service concurrently: a held open-window assignment firing from a timer, the
/// Reassign All command walking every window from the UI, and the reassign
/// hotkey. These pin down that two of them landing on one window take turns, and
/// that landing on different windows costs nothing.
/// </remarks>
[TestClass]
public sealed class AssignmentConcurrencyTests
{
    private static readonly Guid TargetDesktopId = Guid.NewGuid();
    private static readonly Guid OtherDesktopId = Guid.NewGuid();

    [TestMethod]
    public async Task SameWindow_ConcurrentAssignments_NeverOverlap()
    {
        GatedPlacementService placement = new();
        PerWindowAssignmentGate gate = new();
        WindowAssignmentService service = CreateService(placement, gate);

        Task<WindowAssignmentActivity>[] assignments =
        [
            .. Enumerable
                .Range(0, 8)
                .Select(_ => service.AssignAsync(
                    CreateRequest((nint)500)).AsTask()),
        ];

        // Every assignment for one window has to queue behind the one in front
        // of it, so releasing one call at a time is enough to drain them all.
        for (int index = 0; index < assignments.Length; index++)
        {
            await placement.ReleaseOneAsync();
        }

        await Task.WhenAll(assignments);

        Assert.AreEqual(1, placement.MaxConcurrentCalls);
        Assert.AreEqual(0, placement.OverlappingWindows);
        Assert.AreEqual(0, gate.TrackedWindowCount);
    }

    [TestMethod]
    public async Task DifferentWindows_ConcurrentAssignments_ProgressTogether()
    {
        GatedPlacementService placement = new();
        PerWindowAssignmentGate gate = new();
        WindowAssignmentService service = CreateService(placement, gate);

        Task<WindowAssignmentActivity> first =
            service.AssignAsync(CreateRequest((nint)601)).AsTask();
        Task<WindowAssignmentActivity> second =
            service.AssignAsync(CreateRequest((nint)602)).AsTask();

        // Neither completes until the other is also inside the placement
        // service. A gate that serialized every window rather than each window
        // would deadlock here rather than fail an assertion.
        await placement.WaitForConcurrentCallsAsync(2);
        await placement.ReleaseOneAsync();
        await placement.ReleaseOneAsync();
        await Task.WhenAll(first, second);

        Assert.AreEqual(2, placement.MaxConcurrentCalls);
        Assert.AreEqual(0, gate.TrackedWindowCount);
    }

    [TestMethod]
    public async Task AbandonedWaiter_LeavesTheWindowUsableForTheNextAssignment()
    {
        GatedPlacementService placement = new();
        PerWindowAssignmentGate gate = new();
        WindowAssignmentService service = CreateService(placement, gate);

        Task<WindowAssignmentActivity> holder =
            service.AssignAsync(CreateRequest((nint)700)).AsTask();
        await placement.WaitForConcurrentCallsAsync(1);

        using CancellationTokenSource cancellation = new();
        Task<WindowAssignmentActivity> abandoned = service
            .AssignAsync(CreateRequest((nint)700), cancellation.Token)
            .AsTask();
        cancellation.Cancel();

        OperationCanceledException? abandonment = null;
        try
        {
            _ = await abandoned;
        }
        catch (OperationCanceledException exception)
        {
            abandonment = exception;
        }

        Assert.IsNotNull(abandonment);

        await placement.ReleaseOneAsync();
        await holder;

        // The abandoned waiter never entered, so it owed the gate nothing. If
        // it had released anyway, this assignment would find a window it is not
        // alone on; if it had left its waiter counted, the gate would still be
        // tracking the window at the end.
        Task<WindowAssignmentActivity> later =
            service.AssignAsync(CreateRequest((nint)700)).AsTask();
        await placement.ReleaseOneAsync();

        Assert.AreEqual(
            WindowAssignmentOutcome.Skipped,
            (await later.WaitAsync(TimeSpan.FromSeconds(5))).Outcome);
        Assert.AreEqual(1, placement.MaxConcurrentCalls);
        Assert.AreEqual(0, gate.TrackedWindowCount);
    }

    /// <summary>
    /// An assignment that blows up still hands the window back.
    /// </summary>
    /// <remarks>
    /// A gate held by a failed assignment would stall that one window forever,
    /// and it would do it silently — every later event for it would simply never
    /// be answered.
    /// </remarks>
    [TestMethod]
    public async Task Gate_ReleasesTheWindowWhenAnAssignmentThrows()
    {
        PerWindowAssignmentGate gate = new();
        WindowAssignmentService service = new(
            new StubPlacementService(
                DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId)),
            new BoundReconciliationService(),
            new ThrowingActivitySink(),
            TimeProvider.System,
            switchCoordinator: null,
            suppression: null,
            activationTracker: null,
            firstDesktopLocator: null,
            gate);

        await Assert.ThrowsExactlyAsync<InvalidOperationException>(
            () => service.AssignAsync(CreateRequest((nint)800)).AsTask());

        Assert.AreEqual(0, gate.TrackedWindowCount);
    }

    /// <summary>
    /// Shutdown that lands between the move and the switch records the move
    /// rather than losing it.
    /// </summary>
    /// <remarks>
    /// The window is somewhere new by this point. Unwinding would leave the user
    /// with a window that had been moved and an Activity list that never
    /// mentioned it.
    /// </remarks>
    [TestMethod]
    public async Task SwitchCancelledAfterTheMove_RecordsTheMoveItAlreadyMade()
    {
        BoundedWindowAssignmentActivityStore activities = new();
        using CancellationTokenSource cancellation = new();
        WindowAssignmentService service = new(
            new StubPlacementService(
                DesktopTopologyProviderResult<Guid>.Succeeded(OtherDesktopId)),
            new BoundReconciliationService(),
            activities,
            TimeProvider.System,
            new CancellingSwitchCoordinator(cancellation));

        WindowAssignmentActivity result = await service.AssignAsync(
            CreateRequest((nint)900, WindowEventKind.ForegroundActivated),
            cancellation.Token);

        Assert.AreEqual(WindowAssignmentOutcome.Succeeded, result.Outcome);
        Assert.AreEqual(WindowMoveOutcome.Succeeded, result.MoveOutcome);
        Assert.AreEqual(DesktopSwitchOutcome.NotRequested, result.SwitchOutcome);
        Assert.AreEqual(
            DesktopSwitchDecisionReason.SwitchCancelled,
            result.SwitchDecisionReason);
        Assert.IsNull(result.Error);
        Assert.HasCount(1, activities.Snapshot);
    }

    /// <summary>
    /// Cancellation before anything has been committed still unwinds, because
    /// there is nothing to report and shutdown asked to stop.
    /// </summary>
    [TestMethod]
    public async Task CancellationBeforeTheMove_UnwindsWithoutRecording()
    {
        BoundedWindowAssignmentActivityStore activities = new();
        using CancellationTokenSource cancellation = new();
        WindowAssignmentService service = new(
            new StubPlacementService(
                DesktopTopologyProviderResult<Guid>.Succeeded(TargetDesktopId)),
            new BoundReconciliationService(),
            activities,
            TimeProvider.System,
            new CancellingSwitchCoordinator(cancellation));

        await Assert.ThrowsExactlyAsync<OperationCanceledException>(
            () => service
                .AssignAsync(
                    CreateRequest((nint)901, WindowEventKind.ForegroundActivated),
                    cancellation.Token)
                .AsTask());

        Assert.IsEmpty(activities.Snapshot);
    }

    [TestMethod]
    public async Task Gate_UnderABurstOfMixedWindows_TracksNothingAtRest()
    {
        PerWindowAssignmentGate gate = new();
        int overlaps = 0;
        int[] inFlight = new int[16];

        Task[] workers =
        [
            .. Enumerable
                .Range(0, 64)
                .Select(worker => Task.Run(
                    async () =>
                    {
                        for (int index = 0; index < 100; index++)
                        {
                            int slot = (worker + index) % inFlight.Length;
                            using PerWindowAssignmentGate.Lease lease =
                                await gate.AcquireAsync((nint)slot);
                            if (Interlocked.Increment(
                                ref inFlight[slot]) != 1)
                            {
                                Interlocked.Increment(ref overlaps);
                            }

                            await Task.Yield();
                            Interlocked.Decrement(ref inFlight[slot]);
                        }
                    })),
        ];

        await Task.WhenAll(workers);

        Assert.AreEqual(0, overlaps);
        Assert.AreEqual(0, gate.TrackedWindowCount);
    }

    private static WindowAssignmentService CreateService(
        IWindowDesktopPlacementService placement,
        PerWindowAssignmentGate gate) =>
        new(
            placement,
            new BoundReconciliationService(),
            new BoundedWindowAssignmentActivityStore(),
            TimeProvider.System,
            switchCoordinator: null,
            suppression: null,
            activationTracker: null,
            firstDesktopLocator: null,
            gate);

    private static WindowAssignmentRequest CreateRequest(
        nint windowHandle,
        WindowEventKind trigger = WindowEventKind.Shown) =>
        new(
            trigger,
            windowHandle,
            CreateRule(),
            new WindowSafeIdentity(
                "Code.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                "Chrome_WidgetWin_1"));

    private static WindowObservationRule CreateRule() =>
        new(
            "vscode",
            "Visual Studio Code",
            IsEnabled: true,
            "code",
            [ApplicationRuleTrigger.WindowShown],
            DesktopSwitchPolicy.OnForegroundActivation,
            WindowMatchCriteria.ForProcessNames(["Code.exe"]),
            Order: 0);

    /// <summary>
    /// A placement service that holds every desktop query open until the test
    /// lets it go, and reports how many were ever held at once.
    /// </summary>
    private sealed class GatedPlacementService : IWindowDesktopPlacementService
    {
        private readonly object syncRoot = new();
        private readonly Queue<TaskCompletionSource> waiting = [];
        private readonly List<nint> active = [];
        private int concurrentCalls;

        public int MaxConcurrentCalls { get; private set; }

        public int OverlappingWindows { get; private set; }

        public async ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default)
        {
            TaskCompletionSource release =
                new(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (syncRoot)
            {
                if (active.Contains(windowHandle))
                {
                    OverlappingWindows++;
                }

                active.Add(windowHandle);
                concurrentCalls++;
                MaxConcurrentCalls = Math.Max(
                    MaxConcurrentCalls,
                    concurrentCalls);
                waiting.Enqueue(release);
            }

            try
            {
                await release.Task.WaitAsync(cancellationToken);
            }
            finally
            {
                lock (syncRoot)
                {
                    active.Remove(windowHandle);
                    concurrentCalls--;
                }
            }

            return DesktopTopologyProviderResult<Guid>.Succeeded(
                TargetDesktopId);
        }

        public ValueTask<DesktopTopologyProviderResult>
            MoveWindowToDesktopAsync(
                nint windowHandle,
                Guid desktopId,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public async Task WaitForConcurrentCallsAsync(int expected)
        {
            DateTimeOffset deadline =
                DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                lock (syncRoot)
                {
                    if (concurrentCalls >= expected)
                    {
                        return;
                    }
                }

                await Task.Delay(5);
            }

            Assert.Fail(
                $"Only reached {concurrentCalls} concurrent calls, wanted {expected}.");
        }

        public async Task ReleaseOneAsync()
        {
            DateTimeOffset deadline = DateTimeOffset.UtcNow.AddSeconds(5);
            while (DateTimeOffset.UtcNow < deadline)
            {
                TaskCompletionSource? release = null;
                lock (syncRoot)
                {
                    if (waiting.Count > 0)
                    {
                        release = waiting.Dequeue();
                    }
                }

                if (release is not null)
                {
                    release.SetResult();
                    return;
                }

                await Task.Delay(5);
            }

            Assert.Fail("No assignment ever reached the placement service.");
        }
    }

    private sealed class ThrowingActivitySink : IWindowAssignmentActivitySink
    {
        public ValueTask RecordAsync(
            WindowAssignmentActivity activity,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("Activity sink failure.");
    }

    private sealed class StubPlacementService(
        DesktopTopologyProviderResult<Guid> currentDesktopResult) :
        IWindowDesktopPlacementService
    {
        public ValueTask<DesktopTopologyProviderResult<Guid>>
            GetWindowDesktopIdAsync(
                nint windowHandle,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(currentDesktopResult);

        public ValueTask<DesktopTopologyProviderResult>
            MoveWindowToDesktopAsync(
                nint windowHandle,
                Guid desktopId,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());
    }

    /// <summary>
    /// Stands in for shutdown arriving while the switch is being attempted.
    /// </summary>
    private sealed class CancellingSwitchCoordinator(
        CancellationTokenSource cancellation) : IDesktopSwitchCoordinator
    {
        public ValueTask<DesktopSwitchResult> ApplyAsync(
            DesktopSwitchRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellation.Cancel();
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new DesktopSwitchResult(
                    DesktopSwitchOutcome.Succeeded,
                    DesktopSwitchDecisionReason.PolicyApproved,
                    TimeSpan.Zero));
        }
    }

    private sealed class BoundReconciliationService :
        IManagedDesktopReconciliationService
    {
        public ManagedDesktopReconciliationSnapshot Current { get; } = new(
            DateTimeOffset.UtcNow,
            ManagedDesktopReconciliationTrigger.Startup,
            "test.full",
            DesktopTopologyProviderMode.Full,
            ManagedDesktopReconciliationOutcome.Succeeded,
            [new ManagedDesktopRuntimeMapping(
                "code",
                "Code",
                PreferredOrder: 0,
                RecreateWhenMissing: true,
                TargetDesktopId,
                "Code",
                RuntimePosition: 0,
                ManagedDesktopMappingStatus.ReusedPersistedBinding,
                [],
                "test.bound",
                "Bound")],
            []);

        public event EventHandler<ManagedDesktopReconciliationChangedEventArgs>?
            Changed
        {
            add { }
            remove { }
        }

        public Task<ManagedDesktopReconciliationSnapshot> ReconcileAsync(
            ManagedDesktopReconciliationTrigger trigger,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(Current);
    }
}

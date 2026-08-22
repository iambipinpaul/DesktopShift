using DesktopShift.Core.Tiling;
using DesktopShift.Windows.Tiling;

namespace DesktopShift.Windows.Tests.Tiling;

[TestClass]
public sealed class WindowsPlacementBatchExecutorTests
{
    [TestMethod]
    public void Apply_QueuesTheWholeLayoutInOneBatchAndCommitsOnce()
    {
        FakeDeferApi deferApi = new();
        WindowsPlacementBatchExecutor executor = new(deferApi);
        IReadOnlyList<TilingPlacementRequest> placements =
            MakePlacements(3);

        TilingBatchResult result = executor.Apply(placements);

        Assert.AreEqual(TilingBatchOutcome.Committed, result.Outcome);
        Assert.HasCount(1, deferApi.BegunBatches);
        CollectionAssert.AreEqual(
            placements.Select(static request => request.WindowHandle).ToArray(),
            result.CommittedWindows.ToArray());
        Assert.IsEmpty(result.SkippedWindows);
        Assert.IsEmpty(result.FailedWindows);
    }

    [TestMethod]
    public void Apply_UsesFlagsThatPreserveFocusAndZOrder()
    {
        FakeDeferApi deferApi = new();
        WindowsPlacementBatchExecutor executor = new(deferApi);

        _ = executor.Apply(MakePlacements(1));

        // SWP_NOACTIVATE | SWP_NOZORDER | SWP_NOOWNERZORDER = 0x0214.
        Assert.AreEqual(0x0214u, deferApi.LastFlags);
    }

    [TestMethod]
    public void Apply_NeverEndsAnAbandonedBatchAndRebuildsAroundTheFailure()
    {
        nint failingWindow = 0x2002;
        FakeDeferApi deferApi = new();
        deferApi.FailDeferFor(failingWindow);
        WindowsPlacementBatchExecutor executor = new(deferApi);

        TilingBatchResult result = executor.Apply(MakePlacements(3));

        Assert.AreEqual(TilingBatchOutcome.DeferFailed, result.Outcome);
        CollectionAssert.AreEqual(new[] { failingWindow }, result.SkippedWindows.ToArray());

        // The rejected batch was never handed to EndDeferWindowPos, exactly as
        // Microsoft requires; only the rebuilt batch was committed.
        Assert.HasCount(2, deferApi.BegunBatches);
        Assert.HasCount(1, deferApi.EndedBatches);
        Assert.AreEqual(deferApi.BegunBatches[1], deferApi.EndedBatches[0]);

        // Every placement except the rejected one landed.
        CollectionAssert.AreEqual(
            new nint[] { 0x2001, 0x2003 },
            result.CommittedWindows.ToArray());
    }

    [TestMethod]
    public void Apply_WhenEveryWindowIsRejectedReportsOnlySkips()
    {
        FakeDeferApi deferApi = new();
        deferApi.FailDeferFor(0x2001);
        deferApi.FailDeferFor(0x2002);
        WindowsPlacementBatchExecutor executor = new(deferApi);

        TilingBatchResult result = executor.Apply(MakePlacements(2));

        Assert.AreEqual(TilingBatchOutcome.DeferFailed, result.Outcome);
        Assert.IsEmpty(result.CommittedWindows);
        Assert.HasCount(2, result.SkippedWindows);
        Assert.IsEmpty(result.FailedWindows);
    }

    [TestMethod]
    public void Apply_EndFailureReportsTheWholeBatch()
    {
        FakeDeferApi deferApi = new();
        deferApi.FailEnd = true;
        WindowsPlacementBatchExecutor executor = new(deferApi);

        TilingBatchResult result = executor.Apply(MakePlacements(3));

        Assert.AreEqual(TilingBatchOutcome.EndFailed, result.Outcome);
        Assert.IsEmpty(result.CommittedWindows);

        // Windows identifies no individual culprit after an End failure, so
        // every window in the batch is named as failed.
        CollectionAssert.AreEqual(
            new nint[] { 0x2001, 0x2002, 0x2003 },
            result.FailedWindows.ToArray());
    }

    [TestMethod]
    public void Apply_BeginFailureReportsTheWholeBatch()
    {
        FakeDeferApi deferApi = new();
        deferApi.BeginFails = true;
        WindowsPlacementBatchExecutor executor = new(deferApi);

        TilingBatchResult result = executor.Apply(MakePlacements(2));

        Assert.AreEqual(TilingBatchOutcome.BeginFailed, result.Outcome);
        Assert.IsEmpty(result.CommittedWindows);
        CollectionAssert.AreEqual(
            new nint[] { 0x2001, 0x2002 },
            result.FailedWindows.ToArray());
    }

    [TestMethod]
    public void Apply_AccessDeniedRejectionCarriesItsNativeError()
    {
        nint inaccessibleWindow = 0x2002;
        FakeDeferApi deferApi = new();
        deferApi.FailDeferFor(inaccessibleWindow, nativeErrorCode: 5);
        WindowsPlacementBatchExecutor executor = new(deferApi);

        TilingBatchResult result = executor.Apply(MakePlacements(2));

        Assert.HasCount(1, result.PlacementRejections);
        TilingPlacementRejection rejection = result.PlacementRejections[0];
        Assert.AreEqual(inaccessibleWindow, rejection.WindowHandle);
        Assert.AreEqual(5, rejection.NativeErrorCode);
    }

    [TestMethod]
    public void Apply_ForwardsEachUpdatedBatchHandleToTheNextNativeCall()
    {
        FakeDeferApi deferApi = new() { ReplaceHandleAfterEachDefer = true };
        WindowsPlacementBatchExecutor executor = new(deferApi);

        TilingBatchResult result = executor.Apply(MakePlacements(3));

        Assert.AreEqual(TilingBatchOutcome.Committed, result.Outcome);
        CollectionAssert.AreEqual(
            new nint[] { -1, -101, -102 },
            deferApi.ReceivedDeferHandles.ToArray());
        CollectionAssert.AreEqual(
            new nint[] { -103 },
            deferApi.EndedBatches.ToArray());
    }

    private static IReadOnlyList<TilingPlacementRequest> MakePlacements(int count)
    {
        List<TilingPlacementRequest> placements = [];
        for (int index = 0; index < count; index++)
        {
            placements.Add(new TilingPlacementRequest(
                0x2001 + index,
                new TileRect(index * 100, 0, 100, 200)));
        }

        return placements;
    }

    private sealed class FakeDeferApi
        : WindowsPlacementBatchExecutor.ITilingDeferApi
    {
        private readonly Dictionary<nint, int> rejectedWindows = [];

        public List<nint> BegunBatches { get; } = [];

        public List<nint> EndedBatches { get; } = [];

        public uint LastFlags { get; private set; }

        public bool BeginFails { get; set; }

        public bool FailEnd { get; set; }

        public bool ReplaceHandleAfterEachDefer { get; set; }

        public List<nint> ReceivedDeferHandles { get; } = [];

        public void FailDeferFor(
            nint windowHandle,
            int nativeErrorCode = 0) =>
            rejectedWindows[windowHandle] = nativeErrorCode;

        public nint Begin(int capacity)
        {
            if (BeginFails)
            {
                return 0;
            }

            nint handle = -(BegunBatches.Count + 1);
            BegunBatches.Add(handle);
            return handle;
        }

        public nint Defer(nint batchHandle, TilingPlacementRequest request)
        {
            LastFlags = 0x0214;
            ReceivedDeferHandles.Add(batchHandle);
            if (rejectedWindows.TryGetValue(
                request.WindowHandle,
                out int errorCode))
            {
                LastErrorCode = errorCode;
                return 0;
            }

            return ReplaceHandleAfterEachDefer
                ? -100 - ReceivedDeferHandles.Count
                : batchHandle;
        }

        public int LastErrorCode { get; private set; }

        public bool End(nint batchHandle)
        {
            if (FailEnd)
            {
                return false;
            }

            EndedBatches.Add(batchHandle);
            return true;
        }
    }
}

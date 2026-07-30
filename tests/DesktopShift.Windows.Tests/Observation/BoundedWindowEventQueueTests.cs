using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class BoundedWindowEventQueueTests
{
    [TestMethod]
    public async Task TryPublish_WhenCapacityIsExhausted_DropsWithoutBlocking()
    {
        BoundedWindowEventQueue queue = new(capacity: 2);

        Assert.IsTrue(queue.TryPublish(CreateEvent(1)));
        Assert.IsTrue(queue.TryPublish(CreateEvent(2)));
        Assert.IsFalse(queue.TryPublish(CreateEvent(3)));

        WindowEventQueueSnapshot pressure = queue.Snapshot;
        Assert.AreEqual(2L, pressure.Accepted);
        Assert.AreEqual(1L, pressure.Dropped);
        Assert.AreEqual(2, pressure.CurrentDepth);

        queue.Complete();
        List<WindowEvent> read = [];
        await foreach (WindowEvent item in queue.ReadAllAsync())
        {
            read.Add(item);
        }

        CollectionAssert.AreEqual(
            new long[] { 1, 2 },
            read.Select(static item => item.Sequence).ToArray());
        Assert.AreEqual(0, queue.Snapshot.CurrentDepth);
        Assert.AreEqual(2L, queue.Snapshot.Read);
    }

    /// <summary>
    /// The saturation policy keeps what is already queued and refuses what is
    /// arriving, so the events that survive a burst are the earliest ones.
    /// </summary>
    [TestMethod]
    public async Task TryPublish_WhenFull_KeepsTheQueuedEventsAndRefusesTheNew()
    {
        BoundedWindowEventQueue queue = new(capacity: 3);

        for (long sequence = 1; sequence <= 3; sequence++)
        {
            Assert.IsTrue(queue.TryPublish(CreateEvent(sequence)));
        }

        for (long sequence = 4; sequence <= 10; sequence++)
        {
            Assert.IsFalse(queue.TryPublish(CreateEvent(sequence)));
        }

        queue.Complete();
        List<long> survivors = [];
        await foreach (WindowEvent item in queue.ReadAllAsync())
        {
            survivors.Add(item.Sequence);
        }

        CollectionAssert.AreEqual(new long[] { 1, 2, 3 }, survivors.ToArray());
    }

    /// <summary>
    /// A full queue answers the publisher rather than parking it.
    /// </summary>
    /// <remarks>
    /// <see cref="BoundedWindowEventQueue.TryPublish"/> runs inside an unmanaged
    /// WinEvent callback, so waiting there would stall the window Windows is
    /// reporting about. This drives the refusal from a thread with no
    /// synchronization context and asserts it completed on the same thread it
    /// started on, which no awaited handoff could do.
    /// </remarks>
    [TestMethod]
    public void TryPublish_WhenFull_ReturnsOnTheCallingThreadWithoutWaiting()
    {
        BoundedWindowEventQueue queue = new(capacity: 1);
        Assert.IsTrue(queue.TryPublish(CreateEvent(1)));

        int callingThread = Environment.CurrentManagedThreadId;
        bool accepted = queue.TryPublish(CreateEvent(2));

        Assert.IsFalse(accepted);
        Assert.AreEqual(callingThread, Environment.CurrentManagedThreadId);
        Assert.AreEqual(1L, queue.Snapshot.Dropped);
    }

    /// <summary>
    /// One burst of refusals counts as one episode, and a queue that recovers
    /// and fills again counts as two.
    /// </summary>
    /// <remarks>
    /// The raw drop count says how much was lost; the episode count is what
    /// separates a single storm from a queue that is chronically too small.
    /// </remarks>
    [TestMethod]
    public async Task Snapshot_CountsOneSaturationEpisodePerRunOfDrops()
    {
        BoundedWindowEventQueue queue = new(capacity: 1);

        Assert.IsTrue(queue.TryPublish(CreateEvent(1)));
        for (long sequence = 2; sequence <= 6; sequence++)
        {
            Assert.IsFalse(queue.TryPublish(CreateEvent(sequence)));
        }

        Assert.AreEqual(5L, queue.Snapshot.Dropped);
        Assert.AreEqual(1L, queue.Snapshot.SaturationEpisodes);

        // Drain, publish again, and fill again: a second, separate storm.
        await using IAsyncEnumerator<WindowEvent> reader =
            queue.ReadAllAsync().GetAsyncEnumerator();
        Assert.IsTrue(await reader.MoveNextAsync());
        Assert.IsTrue(queue.TryPublish(CreateEvent(7)));
        Assert.IsFalse(queue.TryPublish(CreateEvent(8)));

        Assert.AreEqual(6L, queue.Snapshot.Dropped);
        Assert.AreEqual(2L, queue.Snapshot.SaturationEpisodes);

        queue.Complete();
    }

    /// <summary>
    /// A queue that never fills reports no episodes at all, so a nonzero count
    /// always means events were genuinely lost.
    /// </summary>
    [TestMethod]
    public void Snapshot_WithNoDrops_ReportsNoSaturation()
    {
        BoundedWindowEventQueue queue = new(capacity: 4);

        Assert.IsTrue(queue.TryPublish(CreateEvent(1)));
        Assert.IsTrue(queue.TryPublish(CreateEvent(2)));

        WindowEventQueueSnapshot snapshot = queue.Snapshot;
        Assert.AreEqual(0L, snapshot.Dropped);
        Assert.AreEqual(0L, snapshot.SaturationEpisodes);
        Assert.AreEqual(2L, snapshot.Accepted);
    }

    [TestMethod]
    public async Task Snapshot_UnderConcurrentPressure_DepthStaysWithinBounds()
    {
        const int capacity = 32;
        BoundedWindowEventQueue queue = new(capacity);
        int invalidSnapshots = 0;
        int sampling = 1;

        Task consumer = Task.Run(
            async () =>
            {
                await foreach (WindowEvent _ in queue.ReadAllAsync())
                {
                    await Task.Yield();
                }
            });
        Task sampler = Task.Run(
            () =>
            {
                while (Volatile.Read(ref sampling) != 0)
                {
                    int depth = queue.Snapshot.CurrentDepth;
                    if (depth < 0 || depth > capacity)
                    {
                        Interlocked.Increment(ref invalidSnapshots);
                    }
                }
            });
        Task[] producers = Enumerable
            .Range(0, 4)
            .Select(
                producer => Task.Run(
                    () =>
                    {
                        for (int index = 0; index < 5_000; index++)
                        {
                            queue.TryPublish(
                                CreateEvent(
                                    (producer * 5_000L) + index + 1));
                        }
                    }))
            .ToArray();

        await Task.WhenAll(producers);
        queue.Complete();
        await consumer;
        Volatile.Write(ref sampling, 0);
        await sampler;

        Assert.AreEqual(0, invalidSnapshots);
        WindowEventQueueSnapshot final = queue.Snapshot;
        Assert.AreEqual(0, final.CurrentDepth);
        Assert.AreEqual(final.Accepted, final.Read);

        // Nothing is lost twice and nothing is lost silently: every event a
        // producer offered was either taken or counted as dropped.
        Assert.AreEqual(4L * 5_000, final.Accepted + final.Dropped);
        Assert.IsTrue(final.SaturationEpisodes <= final.Dropped);
    }

    private static WindowEvent CreateEvent(long sequence) =>
        new(
            sequence,
            WindowEventKind.Shown,
            (nint)sequence,
            DateTimeOffset.UnixEpoch);
}

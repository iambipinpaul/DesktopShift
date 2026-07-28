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
        Assert.AreEqual(0, queue.Snapshot.CurrentDepth);
        Assert.AreEqual(queue.Snapshot.Accepted, queue.Snapshot.Read);
    }

    private static WindowEvent CreateEvent(long sequence) =>
        new(
            sequence,
            WindowEventKind.Shown,
            (nint)sequence,
            DateTimeOffset.UnixEpoch);
}

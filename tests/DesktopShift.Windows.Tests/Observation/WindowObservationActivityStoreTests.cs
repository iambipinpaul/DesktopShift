using DesktopShift.Core.Observation;

namespace DesktopShift.Windows.Tests.Observation;

[TestClass]
public sealed class WindowObservationActivityStoreTests
{
    [TestMethod]
    public async Task Record_BoundsSnapshotAndPublishesTheNewSafeRecord()
    {
        BoundedWindowObservationActivityStore store = new(capacity: 2);
        List<long> published = [];
        store.ActivityRecorded += (_, eventArgs) =>
            published.Add(eventArgs.Activity.EventSequence);

        await store.RecordAsync(CreateActivity(1));
        await store.RecordAsync(CreateActivity(2));
        await store.RecordAsync(CreateActivity(3));

        CollectionAssert.AreEqual(
            new long[] { 2, 3 },
            store.Snapshot
                .Select(static item => item.EventSequence)
                .ToArray());
        CollectionAssert.AreEqual(
            new long[] { 1, 2, 3 },
            published.ToArray());
    }

    private static WindowObservationActivity CreateActivity(long sequence) =>
        new(
            DateTimeOffset.UnixEpoch,
            sequence,
            WindowEventKind.Shown,
            (nint)sequence,
            WindowObservationOutcome.Skipped,
            WindowSkipReason.NoMatchingRule,
            null,
            null,
            new WindowSafeIdentity(
                "Code.exe",
                null,
                null,
                "ApplicationWindow"));
}

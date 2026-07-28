using System.Collections.Immutable;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class ActivityJournalTests
{
    [TestMethod]
    public void Journal_KeepsOnlyTheMostRecentRecordsWithinItsCapacity()
    {
        BoundedActivityJournal journal = new(TimeProvider.System, capacity: 3);

        for (int index = 0; index < 10; index++)
        {
            journal.Record(Record(index));
        }

        Assert.HasCount(3, journal.Snapshot);
        CollectionAssert.AreEqual(
            new[] { "code-7", "code-8", "code-9" },
            journal.Snapshot.Select(static record => record.ResultCode).ToArray());
    }

    [TestMethod]
    public void Journal_PublishesEachRecordedEvent()
    {
        BoundedActivityJournal journal = new(TimeProvider.System);
        List<ActivityRecord> observed = [];
        journal.RecordAdded += (_, args) => observed.Add(args.Record);

        journal.Record(Record(1));
        journal.Record([Record(2), Record(3)]);

        Assert.HasCount(3, observed);
    }

    [TestMethod]
    public void DefaultBatch_IsRecordedAsNothing()
    {
        BoundedActivityJournal journal = new(TimeProvider.System);

        journal.Record(default(ImmutableArray<ActivityRecord>));

        Assert.IsEmpty(journal.Snapshot);
    }

    [TestMethod]
    public void ThrowingSubscriber_CannotBreakRecording()
    {
        BoundedActivityJournal journal = new(TimeProvider.System);
        journal.RecordAdded += static (_, _) =>
            throw new InvalidOperationException("Subscriber failure.");

        journal.Record(Record(1));

        Assert.HasCount(1, journal.Snapshot);
    }

    [TestMethod]
    public void Clear_EmptiesTheJournalAndAnnouncesIt()
    {
        BoundedActivityJournal journal = new(TimeProvider.System);
        journal.Record(Record(1));
        int clearedCount = 0;
        journal.Cleared += (_, _) => clearedCount++;

        journal.Clear();

        Assert.IsEmpty(journal.Snapshot);
        Assert.AreEqual(1, clearedCount);
    }

    [TestMethod]
    public void Session_IsStampedOnceFromTheClock()
    {
        Guid sessionId = Guid.NewGuid();
        FakeTimeProvider time = new(
            new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero));
        BoundedActivityJournal journal = new(time, sessionId);

        Assert.AreEqual(sessionId, journal.SessionId);
        Assert.AreEqual(
            new DateTimeOffset(2026, 7, 28, 8, 0, 0, TimeSpan.Zero),
            journal.SessionStartedAtUtc);
    }

    [TestMethod]
    public void NonPositiveCapacity_IsRejected()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            static () => new BoundedActivityJournal(
                TimeProvider.System,
                capacity: 0));
    }

    private static ActivityRecord Record(int index) =>
        new(
            Guid.NewGuid(),
            Guid.Empty,
            DateTimeOffset.UnixEpoch.AddMinutes(index),
            ActivityEventSource.Observation,
            WindowEventKind.Created,
            ActivityResult.Succeeded,
            $"code-{index}",
            "Summary.",
            "Code.exe",
            new WindowSafeIdentity("Code.exe", null, null, "CodeWindow"),
            "vscode",
            "code");

    private sealed class FakeTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

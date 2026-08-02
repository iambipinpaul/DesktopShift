using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class ActivityRecordingTests
{
    [TestMethod]
    public void Controller_StartsOff_AndGatesEveryJournalWriteShape()
    {
        ActivityRecordingController controller = new();
        BoundedActivityJournal retained = new(TimeProvider.System);
        RecordingActivityJournal journal = new(retained, controller);
        ActivityRecord record = CreateRecord();

        journal.Record(record);
        journal.Record([record]);
        Assert.IsFalse(controller.IsEnabled);
        Assert.IsEmpty(journal.Snapshot);

        controller.SetEnabled(true);
        journal.Record(record);
        journal.Record([record]);
        Assert.HasCount(2, journal.Snapshot);

        controller.SetEnabled(false);
        journal.Clear();
        Assert.IsEmpty(journal.Snapshot);
    }

    [TestMethod]
    public void RollingWriter_StopsNewWritesButStillAllowsExplicitClear()
    {
        ActivityRecordingController controller = new();
        CapturingLogWriter retained = new();
        RecordingDiagnosticLogWriter writer = new(retained, controller);

        writer.Write(CreateRecord());
        Assert.AreEqual(0, retained.WriteCount);

        controller.SetEnabled(true);
        writer.Write(CreateRecord());
        Assert.AreEqual(1, retained.WriteCount);

        controller.SetEnabled(false);
        writer.Clear();
        Assert.AreEqual(1, retained.ClearCount);
    }

    private static ActivityRecord CreateRecord() =>
        new(
            Guid.NewGuid(),
            Guid.NewGuid(),
            DateTimeOffset.UtcNow,
            ActivityEventSource.Observation,
            WindowEventKind.Created,
            ActivityResult.Skipped,
            "test.skipped",
            "Test activity.",
            null,
            null,
            null,
            null);

    private sealed class CapturingLogWriter : IDiagnosticLogWriter
    {
        public int WriteCount { get; private set; }

        public int ClearCount { get; private set; }

        public void Write(ActivityRecord record)
        {
            ArgumentNullException.ThrowIfNull(record);
            WriteCount++;
        }

        public void Clear() => ClearCount++;
    }
}

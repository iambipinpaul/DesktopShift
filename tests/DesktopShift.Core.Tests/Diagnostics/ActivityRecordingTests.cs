using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Tiling;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class ActivityRecordingTests
{
    [TestMethod]
    public void DiagnosticsRecorder_WritesTilingDenialToJournalAndLog()
    {
        BoundedActivityJournal journal = new(TimeProvider.System);
        CapturingLogWriter log = new();
        ActivityDiagnosticsRecorder recorder = new(journal, log);

        recorder.Record(new TilingPlacementDeniedEventArgs(
            DateTimeOffset.UtcNow,
            0x1234,
            new WindowSafeIdentity(
                "AdminTool.exe",
                PackageFamilyName: null,
                AppUserModelId: null,
                WindowClass: "AdminWindow"),
            nativeErrorCode: 5));

        Assert.HasCount(1, journal.Snapshot);
        Assert.AreEqual(ActivityEventSource.Tiling, journal.Snapshot[0].Source);
        Assert.AreEqual(1, log.WriteCount);
    }

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

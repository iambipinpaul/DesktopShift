using System.Collections.Immutable;

namespace DesktopShift.Core.Diagnostics;

/// <summary>
/// Controls whether DesktopShift retains routine activity in memory or writes
/// it to the rolling activity log.
/// </summary>
/// <remarks>
/// Crash reporting is deliberately outside this boundary. Turning routine
/// activity off must not make a startup failure impossible to diagnose.
/// </remarks>
public interface IActivityRecordingController
{
    bool IsEnabled { get; }

    void SetEnabled(bool isEnabled);
}

/// <summary>The process-wide, thread-safe activity-recording preference.</summary>
public sealed class ActivityRecordingController : IActivityRecordingController
{
    private int isEnabled;

    public bool IsEnabled => Volatile.Read(ref isEnabled) != 0;

    public void SetEnabled(bool enabled) =>
        Volatile.Write(ref isEnabled, enabled ? 1 : 0);
}

/// <summary>
/// Applies the recording preference at the journal boundary, including to
/// recovery and topology producers that write directly to the journal.
/// </summary>
public sealed class RecordingActivityJournal(
    IActivityJournal inner,
    IActivityRecordingController recording) : IActivityJournal
{
    public Guid SessionId => inner.SessionId;

    public DateTimeOffset SessionStartedAtUtc => inner.SessionStartedAtUtc;

    public IReadOnlyList<ActivityRecord> Snapshot => inner.Snapshot;

    public event EventHandler<ActivityRecordedEventArgs>? RecordAdded
    {
        add => inner.RecordAdded += value;
        remove => inner.RecordAdded -= value;
    }

    public event EventHandler<EventArgs>? Cleared
    {
        add => inner.Cleared += value;
        remove => inner.Cleared -= value;
    }

    public void Record(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (recording.IsEnabled)
        {
            inner.Record(record);
        }
    }

    public void Record(ImmutableArray<ActivityRecord> records)
    {
        if (recording.IsEnabled)
        {
            inner.Record(records);
        }
    }

    // Clearing is always allowed. Turning recording off stops future writes;
    // it never silently changes the user's decision about retained history.
    public void Clear() => inner.Clear();
}

/// <summary>Applies the same preference to rolling activity-log writes.</summary>
public sealed class RecordingDiagnosticLogWriter(
    IDiagnosticLogWriter inner,
    IActivityRecordingController recording) : IDiagnosticLogWriter
{
    public void Write(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        if (recording.IsEnabled)
        {
            inner.Write(record);
        }
    }

    public void Clear() => inner.Clear();
}

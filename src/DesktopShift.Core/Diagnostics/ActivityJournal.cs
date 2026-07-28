using System.Collections.Immutable;

namespace DesktopShift.Core.Diagnostics;

public sealed class ActivityRecordedEventArgs : EventArgs
{
    public ActivityRecordedEventArgs(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);
        Record = record;
    }

    public ActivityRecord Record { get; }
}

/// <summary>
/// The readable side of the activity journal.
/// </summary>
public interface IActivityJournalProjection
{
    /// <summary>Identifies this run of the application.</summary>
    Guid SessionId { get; }

    /// <summary>When this run of the application started recording.</summary>
    DateTimeOffset SessionStartedAtUtc { get; }

    /// <summary>
    /// The retained records, oldest first. Bounded by the journal's capacity.
    /// </summary>
    IReadOnlyList<ActivityRecord> Snapshot { get; }

    event EventHandler<ActivityRecordedEventArgs>? RecordAdded;

    event EventHandler<EventArgs>? Cleared;
}

/// <summary>
/// The writable side of the activity journal.
/// </summary>
public interface IActivityJournal : IActivityJournalProjection
{
    void Record(ActivityRecord record);

    void Record(ImmutableArray<ActivityRecord> records);

    /// <summary>
    /// Drops every retained record. This clears the application's own in-memory
    /// store only; it never touches anything the user owns.
    /// </summary>
    void Clear();
}

/// <summary>
/// A fixed-capacity in-memory journal of privacy-safe activity events.
/// </summary>
/// <remarks>
/// The cap is explicit rather than emergent: the Activity view is a bounded
/// window onto recent behavior, not an archive, and an unbounded journal in a
/// process that runs all day is a leak.
/// </remarks>
public sealed class BoundedActivityJournal : IActivityJournal
{
    public const int DefaultCapacity = 2000;

    private readonly object syncRoot = new();
    private readonly Queue<ActivityRecord> records;
    private readonly int capacity;

    public BoundedActivityJournal(
        TimeProvider timeProvider,
        int capacity = DefaultCapacity)
        : this(timeProvider, Guid.NewGuid(), capacity)
    {
    }

    public BoundedActivityJournal(
        TimeProvider timeProvider,
        Guid sessionId,
        int capacity = DefaultCapacity)
    {
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(capacity);

        this.capacity = capacity;
        records = new Queue<ActivityRecord>(capacity);
        SessionId = sessionId;
        SessionStartedAtUtc = timeProvider.GetUtcNow();
    }

    public Guid SessionId { get; }

    public DateTimeOffset SessionStartedAtUtc { get; }

    public int Capacity => capacity;

    public IReadOnlyList<ActivityRecord> Snapshot
    {
        get
        {
            lock (syncRoot)
            {
                return records.ToArray();
            }
        }
    }

    public event EventHandler<ActivityRecordedEventArgs>? RecordAdded;

    public event EventHandler<EventArgs>? Cleared;

    public void Record(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        lock (syncRoot)
        {
            if (records.Count == capacity)
            {
                _ = records.Dequeue();
            }

            records.Enqueue(record);
        }

        Publish(record);
    }

    public void Record(ImmutableArray<ActivityRecord> batch)
    {
        foreach (ActivityRecord record in batch.IsDefault ? [] : batch)
        {
            Record(record);
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            records.Clear();
        }

        Cleared?.Invoke(this, EventArgs.Empty);
    }

    private void Publish(ActivityRecord record)
    {
        EventHandler<ActivityRecordedEventArgs>? handlers = RecordAdded;
        if (handlers is null)
        {
            return;
        }

        ActivityRecordedEventArgs args = new(record);
        foreach (EventHandler<ActivityRecordedEventArgs> handler in handlers
            .GetInvocationList()
            .Cast<EventHandler<ActivityRecordedEventArgs>>())
        {
            try
            {
                handler(this, args);
            }
            catch
            {
                // A journal subscriber cannot invalidate an assignment.
            }
        }
    }
}

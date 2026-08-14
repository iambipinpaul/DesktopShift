namespace DesktopShift.Core.Assignments;

/// <summary>
/// Lets one assignment at a time run for any given window, while assignments for
/// unrelated windows carry on together.
/// </summary>
/// <remarks>
/// <para>
/// Window events arrive on one queue and are drained one at a time, so it is
/// tempting to assume assignments never overlap. Three paths break that
/// assumption, and all three are ordinary use rather than edge cases: a held
/// open-window assignment runs from a timer when its grace period ends, the
/// Reassign All command walks every top-level window from the UI, and the
/// manual reassignment sends a foreground window through on whichever thread
/// pressed it.
/// </para>
/// <para>
/// Two of those landing on the same window is what this prevents. Each
/// assignment reads where the window currently is and then moves it, and two
/// readers interleaved both see the old desktop: the second move is issued
/// against a placement that is already stale, and Activity records two
/// assignments that disagree about where the window came from. Worse, whichever
/// finishes last decides the desktop, so which one wins depends on timing rather
/// than on which event the user caused.
/// </para>
/// <para>
/// Serializing per window rather than globally is the point. A global gate would
/// make the Reassign All command — a hundred windows, each a round trip through
/// COM — stall every window that opened while it ran. Different handles never
/// contend here, so that batch and a newly opened window make progress at the
/// same time.
/// </para>
/// <para>
/// Waiting is asynchronous throughout. Nothing here blocks a thread, which
/// matters because callers include the UI thread and a hosted worker, and
/// because an assignment holds this gate across the desktop switch gate — a
/// blocking wait on either would turn an ordinary queue into a stalled desktop.
/// The two gates are always taken in that order, per-window first, so no cycle
/// exists to deadlock on.
/// </para>
/// <para>
/// A window's entry lives only while somebody holds or awaits it, so a machine
/// that has opened and closed a hundred thousand windows tracks none of them.
/// </para>
/// </remarks>
public sealed class PerWindowAssignmentGate
{
    private readonly object syncRoot = new();
    private readonly Dictionary<nint, Entry> entries = [];

    /// <summary>
    /// How many windows currently have an assignment running or waiting.
    /// </summary>
    /// <remarks>
    /// Zero whenever nothing is in flight, which is what makes it worth
    /// asserting on: a nonzero count at rest would mean a lease was never
    /// released.
    /// </remarks>
    public int TrackedWindowCount
    {
        get
        {
            lock (syncRoot)
            {
                return entries.Count;
            }
        }
    }

    /// <summary>
    /// Waits until no other assignment is running for this window.
    /// </summary>
    /// <param name="windowHandle">The window being assigned.</param>
    /// <param name="cancellationToken">Abandons the wait.</param>
    /// <returns>
    /// A lease that must be disposed once the assignment is done. Disposing it
    /// twice is harmless; never disposing it stalls every later assignment for
    /// that one window.
    /// </returns>
    /// <exception cref="OperationCanceledException">
    /// The wait was abandoned. Nothing was acquired, so there is nothing to
    /// release.
    /// </exception>
    public async ValueTask<Lease> AcquireAsync(
        nint windowHandle,
        CancellationToken cancellationToken = default)
    {
        Entry entry;
        lock (syncRoot)
        {
            if (!entries.TryGetValue(windowHandle, out Entry? existing))
            {
                existing = new Entry();
                entries[windowHandle] = existing;
            }

            // Counted before the wait, not after it. The count is what keeps the
            // entry alive, so a waiter that has not reached the semaphore yet
            // still has to be visible — otherwise the holder's release would
            // dispose the semaphore out from under it.
            existing.Waiters++;
            entry = existing;
        }

        try
        {
            await entry.Gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            Release(windowHandle, entry, holdsGate: false);
            throw;
        }

        return new Lease(this, windowHandle, entry);
    }

    private void Release(nint windowHandle, Entry entry, bool holdsGate)
    {
        if (holdsGate)
        {
            entry.Gate.Release();
        }

        lock (syncRoot)
        {
            entry.Waiters--;
            if (entry.Waiters > 0)
            {
                return;
            }

            // Nobody holds it and nobody is waiting on it, so the entry cannot
            // be reached again and the semaphore can go. A later assignment for
            // the same window builds a fresh one.
            if (entries.TryGetValue(windowHandle, out Entry? current) &&
                ReferenceEquals(current, entry))
            {
                entries.Remove(windowHandle);
            }

            entry.Gate.Dispose();
        }
    }

    internal sealed class Entry
    {
        public SemaphoreSlim Gate { get; } = new(1, 1);

        public int Waiters { get; set; }
    }

    /// <summary>
    /// The right to assign one window, held until it is disposed.
    /// </summary>
    /// <remarks>
    /// A class rather than a struct, so that a copy cannot release the gate a
    /// second time. One allocation per assignment is nothing next to the COM
    /// round trips the assignment it guards is about to make, and releasing
    /// twice would let two assignments into the same window — the exact thing
    /// this type exists to stop.
    /// </remarks>
    public sealed class Lease : IDisposable
    {
        private readonly PerWindowAssignmentGate owner;
        private readonly nint windowHandle;
        private readonly Entry entry;
        private int released;

        internal Lease(
            PerWindowAssignmentGate owner,
            nint windowHandle,
            Entry entry)
        {
            this.owner = owner;
            this.windowHandle = windowHandle;
            this.entry = entry;
        }

        public void Dispose()
        {
            if (Interlocked.Exchange(ref released, 1) != 0)
            {
                return;
            }

            owner.Release(windowHandle, entry, holdsGate: true);
        }
    }
}

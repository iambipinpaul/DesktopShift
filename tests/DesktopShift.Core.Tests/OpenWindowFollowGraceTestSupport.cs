using DesktopShift.Core.Observation;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests;

internal static class OpenWindowFollowGraceTestDefaults
{
    /// <summary>
    /// Registers a zero grace period, so an opened window is assigned inline.
    /// </summary>
    /// <remarks>
    /// For host tests whose subject is what an assignment decides rather than
    /// when it runs. The wait that lets a foreground activation overtake a
    /// newly opened window has tests of its own that drive the release; leaving
    /// it switched on here would add a scheduler to pump to every unrelated
    /// assertion without making any of them prove more.
    /// </remarks>
    /// <param name="services">The host's service collection.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AssignOpenedWindowsInline(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        return services.AddSingleton(new OpenWindowFollowGrace(TimeSpan.Zero));
    }
}

/// <summary>
/// An <see cref="IOpenWindowFollowScheduler"/> that only fires when told to.
/// </summary>
/// <remarks>
/// The grace period exists to resolve a race — whether a newly opened window
/// takes the foreground before it is moved away. A test that waited on a real
/// timer would be re-running that race rather than deciding it, so this hands
/// the test the clock instead: a held assignment stays held until
/// <see cref="ReleaseAllAsync"/> asks for it.
/// </remarks>
internal sealed class ManualOpenWindowFollowScheduler : IOpenWindowFollowScheduler
{
    private readonly object syncRoot = new();
    private readonly List<Entry> pending = [];

    /// <summary>How many assignments are still waiting on an activation.</summary>
    public int PendingCount
    {
        get
        {
            lock (syncRoot)
            {
                return pending.Count(static entry => !entry.Cancelled);
            }
        }
    }

    public IDisposable Schedule(TimeSpan dueTime, Func<Task> callback)
    {
        ArgumentNullException.ThrowIfNull(callback);

        Entry entry = new(callback);
        lock (syncRoot)
        {
            pending.Add(entry);
        }

        return entry;
    }

    /// <summary>
    /// Runs every assignment still waiting, oldest first, and awaits each.
    /// </summary>
    /// <remarks>
    /// Awaiting is the point. In the running application a released assignment
    /// is unobserved, so a test that did not await one would assert against a
    /// move that had not happened yet.
    /// </remarks>
    /// <returns>How many assignments ran.</returns>
    public async Task<int> ReleaseAllAsync()
    {
        Entry[] due;
        lock (syncRoot)
        {
            due = [.. pending.Where(static entry => !entry.Cancelled)];
            pending.Clear();
        }

        foreach (Entry entry in due)
        {
            await entry.Callback().ConfigureAwait(false);
        }

        return due.Length;
    }

    private sealed class Entry(Func<Task> callback) : IDisposable
    {
        public Func<Task> Callback { get; } = callback;

        public bool Cancelled { get; private set; }

        public void Dispose() => Cancelled = true;
    }
}

/// <summary>
/// Keeps the observation activities the processor records.
/// </summary>
/// <remarks>
/// A held assignment records its observation when it is released rather than
/// returning it to the caller, so a test that only reads what
/// <see cref="WindowObservationProcessor.ProcessAsync"/> returned would see the
/// provisional row and none of the assignment. Reading the sink sees what the
/// Activity view would.
/// </remarks>
internal sealed class CapturingWindowObservationActivitySink :
    IWindowObservationActivitySink
{
    private readonly object syncRoot = new();
    private readonly List<WindowObservationActivity> recorded = [];

    public IReadOnlyList<WindowObservationActivity> Recorded
    {
        get
        {
            lock (syncRoot)
            {
                return [.. recorded];
            }
        }
    }

    public WindowObservationActivity? Last
    {
        get
        {
            lock (syncRoot)
            {
                return recorded.Count == 0 ? null : recorded[^1];
            }
        }
    }

    public ValueTask RecordAsync(
        WindowObservationActivity activity,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(activity);
        lock (syncRoot)
        {
            recorded.Add(activity);
        }

        return ValueTask.CompletedTask;
    }
}

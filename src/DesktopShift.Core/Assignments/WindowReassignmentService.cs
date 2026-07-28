using System.Collections.Immutable;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Assignments;

public sealed class WindowReassignmentService(
    ITopLevelWindowEnumerator windowEnumerator,
    WindowObservationProcessor observationProcessor,
    TimeProvider timeProvider) : IWindowReassignmentService
{
    private readonly SemaphoreSlim startupGate = new(1, 1);
    private WindowReassignmentBatchResult? startupResult;
    private long sequence;

    public async Task<WindowReassignmentBatchResult> ReconcileStartupAsync(
        CancellationToken cancellationToken = default)
    {
        await startupGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            startupResult ??= await RunAsync(
                WindowEventKind.StartupReconciliation,
                cancellationToken).ConfigureAwait(false);
            return startupResult;
        }
        finally
        {
            startupGate.Release();
        }
    }

    public Task<WindowReassignmentBatchResult> ReassignAllAsync(
        CancellationToken cancellationToken = default) =>
        RunAsync(WindowEventKind.ManualReassignment, cancellationToken);

    private async Task<WindowReassignmentBatchResult> RunAsync(
        WindowEventKind trigger,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Guid correlationId = Guid.NewGuid();
        DateTimeOffset startedAtUtc = timeProvider.GetUtcNow();
        long startedTimestamp = timeProvider.GetTimestamp();
        IReadOnlyList<nint> windows = windowEnumerator.Enumerate();
        ImmutableArray<WindowAssignmentActivity>.Builder assignments =
            ImmutableArray.CreateBuilder<WindowAssignmentActivity>();

        foreach (nint windowHandle in windows)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WindowObservationActivity observation =
                await observationProcessor.ProcessAsync(
                    new WindowEvent(
                        Interlocked.Increment(ref sequence),
                        trigger,
                        windowHandle,
                        timeProvider.GetUtcNow()),
                    cancellationToken).ConfigureAwait(false);

            if (observation.Assignment is not null)
            {
                assignments.Add(observation.Assignment);
            }
        }

        return new WindowReassignmentBatchResult(
            correlationId,
            startedAtUtc,
            timeProvider.GetElapsedTime(startedTimestamp),
            trigger,
            windows.Count,
            assignments.ToImmutable());
    }
}

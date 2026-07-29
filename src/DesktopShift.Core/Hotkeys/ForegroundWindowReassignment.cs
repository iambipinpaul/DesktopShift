using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// Sends the current foreground window through the ordinary observation and
/// assignment pipeline.
/// </summary>
public sealed class ForegroundWindowReassignment(
    IForegroundWindowProvider foregroundWindowProvider,
    WindowObservationProcessor observationProcessor,
    TimeProvider timeProvider) : IForegroundWindowReassignment
{
    private long _sequence;

    public async Task<ForegroundReassignmentResult> ReassignForegroundWindowAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        nint windowHandle = foregroundWindowProvider.GetForegroundWindow();
        if (windowHandle == 0)
        {
            return new ForegroundReassignmentResult(false, 0);
        }

        _ = await observationProcessor.ProcessAsync(
            new WindowEvent(
                Interlocked.Increment(ref _sequence),
                WindowEventKind.ManualReassignment,
                windowHandle,
                timeProvider.GetUtcNow()),
            cancellationToken).ConfigureAwait(false);

        return new ForegroundReassignmentResult(true, windowHandle);
    }
}

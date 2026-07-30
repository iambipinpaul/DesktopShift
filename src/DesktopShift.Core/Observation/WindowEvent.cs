namespace DesktopShift.Core.Observation;

public enum WindowEventKind
{
    Created,
    Shown,
    ForegroundActivated,
    Destroyed,
    StartupReconciliation,
    ManualReassignment,
}

/// <param name="Sequence">The monotonic order the event was received in.</param>
/// <param name="Kind">What Windows said happened to the window.</param>
/// <param name="WindowHandle">The window it happened to.</param>
/// <param name="ObservedAt">
/// The wall-clock moment of receipt, which is what the Activity view shows and
/// what coalescing compares two events by.
/// </param>
/// <param name="ReceivedTimestamp">
/// The monotonic reading taken at the same instant as
/// <paramref name="ObservedAt"/>, so event-to-move latency can be measured
/// without depending on a wall clock that a time-zone change or an NTP
/// correction can move underneath it.
/// </param>
/// <remarks>
/// <paramref name="ReceivedTimestamp"/> defaults to zero, which reads as "not
/// stamped". Latency for an unstamped event is measured from the moment the
/// processor picked it up instead, which loses the queue wait but is never wrong
/// by the distance between two unrelated clocks. Events raised by a startup
/// reconciliation or a manual Reassign All are unstamped on purpose: they were
/// never waiting in the queue, so there is no queue wait to attribute to them.
/// </remarks>
public readonly record struct WindowEvent(
    long Sequence,
    WindowEventKind Kind,
    nint WindowHandle,
    DateTimeOffset ObservedAt,
    long ReceivedTimestamp = 0);

public interface IWindowEventSource : IDisposable
{
    bool IsRunning { get; }

    void Start();
}

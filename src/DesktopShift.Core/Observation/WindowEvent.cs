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

public readonly record struct WindowEvent(
    long Sequence,
    WindowEventKind Kind,
    nint WindowHandle,
    DateTimeOffset ObservedAt);

public interface IWindowEventSource : IDisposable
{
    bool IsRunning { get; }

    void Start();
}

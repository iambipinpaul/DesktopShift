namespace DesktopShift.Core.Hosting;

public sealed class AutomaticAssignmentPauseChangedEventArgs : EventArgs
{
    public AutomaticAssignmentPauseChangedEventArgs(bool isPaused)
    {
        IsPaused = isPaused;
    }

    public bool IsPaused { get; }
}

/// <summary>
/// Controls whether observed window events are allowed to drive automatic
/// assignment.
/// </summary>
/// <remarks>
/// The switch sits above the observation processor, not inside it. Pausing has
/// to silence the event-driven feed only: an explicit reassignment reaches the
/// processor through <c>IWindowReassignmentService</c> without passing the
/// event queue, so it keeps working while automatic assignment is paused. That
/// is the whole point of a pause a user can trust — the application stops
/// acting on its own but still does exactly what it is told.
/// </remarks>
public interface IAutomaticAssignmentPauseController
{
    bool IsPaused { get; }

    event EventHandler<AutomaticAssignmentPauseChangedEventArgs>? PauseStateChanged;

    void Pause();

    void Resume();

    /// <summary>
    /// Flips the switch and returns the resulting paused state.
    /// </summary>
    bool TogglePause();
}

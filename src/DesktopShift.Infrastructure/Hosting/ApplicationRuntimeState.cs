using DesktopShift.Core.Hosting;

namespace DesktopShift.Infrastructure.Hosting;

internal sealed class ApplicationRuntimeState :
    IApplicationRuntimeState,
    IAutomaticAssignmentPauseController
{
    private readonly object _syncRoot = new();
    private DateTimeOffset? _startedAtUtc;
    private bool _isPaused;

    public bool IsRunning
    {
        get
        {
            lock (_syncRoot)
            {
                return _startedAtUtc is not null;
            }
        }
    }

    public DateTimeOffset? StartedAtUtc
    {
        get
        {
            lock (_syncRoot)
            {
                return _startedAtUtc;
            }
        }
    }

    public bool IsPaused
    {
        get
        {
            lock (_syncRoot)
            {
                return _isPaused;
            }
        }
    }

    public event EventHandler<AutomaticAssignmentPauseChangedEventArgs>? PauseStateChanged;

    public void MarkStarted(DateTimeOffset startedAtUtc)
    {
        lock (_syncRoot)
        {
            _startedAtUtc = startedAtUtc;
        }
    }

    public void MarkStopped()
    {
        lock (_syncRoot)
        {
            _startedAtUtc = null;
        }
    }

    public void Pause() => SetPaused(true);

    public void Resume() => SetPaused(false);

    public bool TogglePause()
    {
        bool requested;
        lock (_syncRoot)
        {
            requested = !_isPaused;
        }

        SetPaused(requested);
        return requested;
    }

    private void SetPaused(bool isPaused)
    {
        lock (_syncRoot)
        {
            if (_isPaused == isPaused)
            {
                return;
            }

            _isPaused = isPaused;
        }

        // Raised outside the lock so a subscriber that redraws the tray menu
        // cannot deadlock against a concurrent state read.
        PauseStateChanged?.Invoke(this, new AutomaticAssignmentPauseChangedEventArgs(isPaused));
    }
}

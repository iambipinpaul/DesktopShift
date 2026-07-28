using DesktopShift.Core.Hosting;

namespace DesktopShift.Infrastructure.Hosting;

internal sealed class ApplicationRuntimeState : IApplicationRuntimeState
{
    private readonly object _syncRoot = new();
    private DateTimeOffset? _startedAtUtc;

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
}

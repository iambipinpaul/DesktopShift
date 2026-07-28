namespace DesktopShift.Core.Hosting;

/// <summary>
/// Read-only lifecycle state for services and shell status surfaces.
/// </summary>
public interface IApplicationRuntimeState
{
    bool IsRunning { get; }

    DateTimeOffset? StartedAtUtc { get; }
}

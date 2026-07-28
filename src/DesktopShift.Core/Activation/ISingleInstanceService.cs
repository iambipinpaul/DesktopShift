namespace DesktopShift.Core.Activation;

/// <summary>
/// Provides the platform-specific single-instance registration and activation bridge.
/// </summary>
public interface ISingleInstanceService : IAsyncDisposable
{
    bool IsCurrent { get; }

    event EventHandler? ActivationRequested;

    Task RedirectActivationToCurrentAsync(CancellationToken cancellationToken = default);
}

namespace DesktopShift.Core.Activation;

/// <summary>
/// Routes startup activation to either the current process or the registered primary process.
/// </summary>
public sealed class SingleInstanceCoordinator : IAsyncDisposable
{
    private readonly ISingleInstanceService _singleInstanceService;
    private bool _isDisposed;

    public SingleInstanceCoordinator(ISingleInstanceService singleInstanceService)
    {
        ArgumentNullException.ThrowIfNull(singleInstanceService);

        _singleInstanceService = singleInstanceService;
        _singleInstanceService.ActivationRequested += OnActivationRequested;
    }

    public event EventHandler? ActivationRequested;

    public async Task<ActivationRoute> RouteStartupActivationAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_isDisposed, this);

        if (_singleInstanceService.IsCurrent)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
            return ActivationRoute.PrimaryInstance;
        }

        await _singleInstanceService
            .RedirectActivationToCurrentAsync(cancellationToken)
            .ConfigureAwait(false);

        return ActivationRoute.RedirectedToPrimaryInstance;
    }

    public async ValueTask DisposeAsync()
    {
        if (_isDisposed)
        {
            return;
        }

        _isDisposed = true;
        _singleInstanceService.ActivationRequested -= OnActivationRequested;
        await _singleInstanceService.DisposeAsync().ConfigureAwait(false);
    }

    private void OnActivationRequested(object? sender, EventArgs eventArgs)
    {
        if (!_isDisposed)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

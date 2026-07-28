using DesktopShift.Core;
using DesktopShift.Core.Activation;
using Microsoft.Windows.AppLifecycle;

namespace DesktopShift.Windows.Activation;

/// <summary>
/// Coordinates process activation through the Windows App SDK app-lifecycle broker.
/// </summary>
public sealed class SingleInstanceService : ISingleInstanceService
{
    private readonly AppInstance _currentInstance;
    private readonly AppInstance _registeredInstance;
    private int _disposed;

    public SingleInstanceService()
    {
        _currentInstance = AppInstance.GetCurrent();
        _registeredInstance = AppInstance.FindOrRegisterForKey(ProductInfo.SingleInstanceKey);
        IsCurrent = _registeredInstance.IsCurrent;

        if (IsCurrent)
        {
            _registeredInstance.Activated += OnActivated;
        }
    }

    public event EventHandler? ActivationRequested;

    public bool IsCurrent { get; }

    public async Task RedirectActivationToCurrentAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);
        cancellationToken.ThrowIfCancellationRequested();

        if (IsCurrent)
        {
            return;
        }

        var activationArguments = _currentInstance.GetActivatedEventArgs();
        await _registeredInstance
            .RedirectActivationToAsync(activationArguments)
            .AsTask(cancellationToken)
            .ConfigureAwait(false);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return ValueTask.CompletedTask;
        }

        if (IsCurrent)
        {
            _registeredInstance.Activated -= OnActivated;
            _registeredInstance.UnregisterKey();
        }

        ActivationRequested = null;
        return ValueTask.CompletedTask;
    }

    private void OnActivated(object? sender, AppActivationArguments args)
    {
        if (Volatile.Read(ref _disposed) == 0)
        {
            ActivationRequested?.Invoke(this, EventArgs.Empty);
        }
    }
}

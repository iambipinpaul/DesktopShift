using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.ManagedDesktops;

internal sealed class ManagedDesktopReconciliationHostedService(
    IConfigurationService configurationService,
    ICompatibilityCoordinator compatibilityCoordinator,
    IDesktopTopologyProvider topologyProvider,
    IManagedDesktopReconciliationService reconciliationService) :
    IHostedService,
    IDisposable
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task pendingReconciliation = Task.CompletedTask;
    private bool started;
    private bool disposed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (started)
            {
                return;
            }

            started = true;
            compatibilityCoordinator.StatusChanged += OnCompatibilityChanged;
            topologyProvider.TopologyChanged += OnTopologyChanged;
        }

        _ = await configurationService.LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        _ = await reconciliationService.ReconcileAsync(
            ManagedDesktopReconciliationTrigger.Startup,
            cancellationToken).ConfigureAwait(false);
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pending;
        lock (syncRoot)
        {
            if (!started)
            {
                return;
            }

            started = false;
            compatibilityCoordinator.StatusChanged -= OnCompatibilityChanged;
            topologyProvider.TopologyChanged -= OnTopologyChanged;
            lifetimeCancellation.Cancel();
            pending = pendingReconciliation;
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    public void Dispose()
    {
        if (disposed)
        {
            return;
        }

        disposed = true;
        compatibilityCoordinator.StatusChanged -= OnCompatibilityChanged;
        topologyProvider.TopologyChanged -= OnTopologyChanged;
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
    }

    private void OnCompatibilityChanged(
        object? sender,
        CompatibilityStatusChangedEventArgs args)
    {
        if (args.Status.LastTest.IsSuccessful)
        {
            QueueReconciliation(
                ManagedDesktopReconciliationTrigger.CompatibilityChanged);
        }
    }

    private void OnTopologyChanged(
        object? sender,
        DesktopTopologyChangedEventArgs args)
    {
        _ = args;
        QueueReconciliation(ManagedDesktopReconciliationTrigger.TopologyChanged);
    }

    private void QueueReconciliation(ManagedDesktopReconciliationTrigger trigger)
    {
        lock (syncRoot)
        {
            if (!started || lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            pendingReconciliation = pendingReconciliation
                .ContinueWith(
                    _ => reconciliationService.ReconcileAsync(
                        trigger,
                        lifetimeCancellation.Token),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }
}

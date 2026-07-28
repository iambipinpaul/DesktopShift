using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.ManagedDesktops;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Assignments;

internal sealed class WindowAssignmentReadinessHostedService(
    ICompatibilityCoordinator compatibilityCoordinator,
    IManagedDesktopReconciliationService reconciliationService,
    IWindowReassignmentService reassignmentService,
    IHostApplicationLifetime applicationLifetime) :
    IHostedService,
    IDisposable
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private Task pending = Task.CompletedTask;
    private bool started;
    private bool startupQueued;
    private bool disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            if (started)
            {
                return Task.CompletedTask;
            }

            started = true;
            compatibilityCoordinator.StatusChanged += OnCompatibilityChanged;
            reconciliationService.Changed += OnReconciliationChanged;
            QueueStartupIfReady();
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task pendingToAwait;
        lock (syncRoot)
        {
            if (!started)
            {
                return;
            }

            started = false;
            compatibilityCoordinator.StatusChanged -= OnCompatibilityChanged;
            reconciliationService.Changed -= OnReconciliationChanged;
            lifetimeCancellation.Cancel();
            pendingToAwait = pending;
        }

        try
        {
            await pendingToAwait.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (
            lifetimeCancellation.IsCancellationRequested)
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
        reconciliationService.Changed -= OnReconciliationChanged;
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
    }

    private void OnCompatibilityChanged(
        object? sender,
        CompatibilityStatusChangedEventArgs args)
    {
        _ = args;
        lock (syncRoot)
        {
            QueueStartupIfReady();
        }
    }

    private void OnReconciliationChanged(
        object? sender,
        ManagedDesktopReconciliationChangedEventArgs args)
    {
        _ = args;
        lock (syncRoot)
        {
            QueueStartupIfReady();
        }
    }

    private void QueueStartupIfReady()
    {
        if (!started ||
            startupQueued ||
            lifetimeCancellation.IsCancellationRequested ||
            !compatibilityCoordinator.Current.LastTest.IsSuccessful ||
            !reconciliationService.Current.Mappings.Any(
                static mapping => mapping.IsBound))
        {
            return;
        }

        startupQueued = true;
        pending = reassignmentService.ReconcileStartupAsync(
            lifetimeCancellation.Token);
        _ = pending.ContinueWith(
            ObserveStartupCompletion,
            CancellationToken.None,
            TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);
    }

    private void ObserveStartupCompletion(Task completed)
    {
        if (!completed.IsFaulted)
        {
            return;
        }

        // Observe immediately and ask the Generic Host to stop. StopAsync awaits
        // the same task and propagates the original startup assignment failure.
        _ = completed.Exception;
        applicationLifetime.StopApplication();
    }
}

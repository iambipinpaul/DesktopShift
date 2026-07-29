using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.ManagedDesktops;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.ManagedDesktops;

/// <summary>
/// Drives managed-desktop reconciliation from application lifetime, the
/// compatibility check, and Windows virtual-desktop topology notifications.
/// </summary>
/// <remarks>
/// Topology notifications arrive in bursts and off a native callback thread, so
/// they are queued onto a single continuation chain. One notification is handled
/// to completion before the next one starts, which is what lets the recovery
/// pass reason about "what changed since last time" at all.
/// </remarks>
internal sealed class ManagedDesktopReconciliationHostedService(
    IConfigurationService configurationService,
    ICompatibilityCoordinator compatibilityCoordinator,
    IDesktopTopologyProvider topologyProvider,
    IManagedDesktopReconciliationService reconciliationService,
    IManagedDesktopTopologyRecoveryService topologyRecoveryService,
    IManagedDesktopNamingService? namingService = null,
    IActivityJournal? activityJournal = null) :
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
            if (namingService is not null)
            {
                reconciliationService.Changed += OnReconciled;
            }
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
            reconciliationService.Changed -= OnReconciled;
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
        reconciliationService.Changed -= OnReconciled;
        lifetimeCancellation.Cancel();
        lifetimeCancellation.Dispose();
    }

    /// <summary>
    /// Queues a naming pass behind the reconciliation that produced the
    /// snapshot.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Naming runs after reconciliation rather than inside it, and on the same
    /// serialized chain, so a pass always names bindings that have finished
    /// being decided. Writing a name raises a Windows name-changed notification
    /// which reconciles again — that converges, because the second pass sees the
    /// name it just wrote and does nothing.
    /// </para>
    /// <para>
    /// A naming failure is swallowed here on purpose. Nothing downstream of
    /// reconciliation may be made conditional on a cosmetic label, so a pass
    /// that throws must not break the chain that assigns windows.
    /// </para>
    /// </remarks>
    private void OnReconciled(
        object? sender,
        ManagedDesktopReconciliationChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        if (namingService is null)
        {
            return;
        }

        ManagedDesktopReconciliationSnapshot snapshot = args.Snapshot;
        Queue(async token =>
        {
            try
            {
                ManagedDesktopNamingPass pass = await namingService
                    .ApplyAsync(snapshot, token)
                    .ConfigureAwait(false);
                activityJournal?.Record(
                    ActivityRecordFactory.FromNaming(pass, activityJournal.SessionId));
            }
            catch (Exception exception) when (
                exception is not OperationCanceledException ||
                !token.IsCancellationRequested)
            {
            }
        });
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

    /// <summary>
    /// Hands a topology notification to the recovery pass rather than
    /// reconciling directly.
    /// </summary>
    /// <remarks>
    /// The pass does more than reconcile: it bounds recreation, notes reorders,
    /// and decides whether a single window reconciliation is warranted. Calling
    /// the reconciliation service straight from here would skip all three.
    /// </remarks>
    private void OnTopologyChanged(
        object? sender,
        DesktopTopologyChangedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        string reason = args.Reason;
        Queue(token => topologyRecoveryService.HandleTopologyChangedAsync(reason, token));
    }

    private void QueueReconciliation(ManagedDesktopReconciliationTrigger trigger) =>
        Queue(token => reconciliationService.ReconcileAsync(trigger, token));

    private void Queue(Func<CancellationToken, Task> work)
    {
        lock (syncRoot)
        {
            if (!started || lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            pendingReconciliation = pendingReconciliation
                .ContinueWith(
                    _ => work(lifetimeCancellation.Token),
                    CancellationToken.None,
                    TaskContinuationOptions.ExecuteSynchronously,
                    TaskScheduler.Default)
                .Unwrap();
        }
    }
}

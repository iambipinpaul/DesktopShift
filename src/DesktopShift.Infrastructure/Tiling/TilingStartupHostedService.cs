using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Recovery;
using DesktopShift.Core.Tiling;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Tiling;

/// <summary>Rebuilds the in-memory BSP trees from current windows at startup.</summary>
internal sealed class TilingStartupHostedService(
    TilingCoordinator coordinator,
    ICompatibilityCoordinator? compatibilityCoordinator = null,
    IShellLifecycleSignalSource? signalSource = null)
    : IHostedService, IDisposable
{
    private bool subscribed;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (signalSource is not null && !subscribed)
        {
            signalSource.SignalRaised += OnSignalRaised;
            subscribed = true;
        }

        if (coordinator.IsEnabled &&
            compatibilityCoordinator?.Current.LastTest.Outcome ==
                CompatibilityTestOutcome.NotRun)
        {
            _ = await compatibilityCoordinator
                .RunCompatibilityTestAsync(cancellationToken)
                .ConfigureAwait(false);
        }

        await coordinator
            .ReconcileAllWindowsAsync(cancellationToken)
            .ConfigureAwait(false);
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        Unsubscribe();
        return Task.CompletedTask;
    }

    public void Dispose() => Unsubscribe();

    private void OnSignalRaised(
        object? sender,
        ShellLifecycleSignalEventArgs args)
    {
        if (args.Signal is
            ShellLifecycleSignal.DisplayChanged or
            ShellLifecycleSignal.SessionResumed)
        {
            coordinator.RequestReconcile();
        }
    }

    private void Unsubscribe()
    {
        if (signalSource is not null && subscribed)
        {
            signalSource.SignalRaised -= OnSignalRaised;
            subscribed = false;
        }
    }
}

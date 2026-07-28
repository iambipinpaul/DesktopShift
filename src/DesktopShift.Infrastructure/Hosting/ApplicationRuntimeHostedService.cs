using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Hosting;

internal sealed class ApplicationRuntimeHostedService(
    ApplicationRuntimeState runtimeState,
    TimeProvider timeProvider) : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        runtimeState.MarkStarted(timeProvider.GetUtcNow());
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        runtimeState.MarkStopped();
        return Task.CompletedTask;
    }
}

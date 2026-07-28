using DesktopShift.Core.Observation;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Observation;

internal sealed class WindowObservationHostedService(
    IWindowEventSource eventSource,
    IWindowEventQueue eventQueue,
    WindowObservationProcessor processor,
    IHostApplicationLifetime applicationLifetime) :
    IHostedService,
    IDisposable
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource workerCancellation = new();
    private Task? worker;
    private bool stopped;
    private bool disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(disposed, this);
            if (worker is not null)
            {
                return Task.CompletedTask;
            }

            eventSource.Start();
            worker = processor.RunAsync(
                eventQueue,
                workerCancellation.Token);
            _ = worker.ContinueWith(
                ObserveWorkerCompletion,
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }

        return Task.CompletedTask;
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? workerToAwait;

        lock (syncRoot)
        {
            if (stopped)
            {
                return;
            }

            stopped = true;

            // Unhook first so no native callback can race queue completion.
            eventSource.Dispose();
            eventQueue.Complete();
            workerToAwait = worker;
        }

        if (workerToAwait is null)
        {
            return;
        }

        try
        {
            await workerToAwait
                .WaitAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            workerCancellation.Cancel();
            try
            {
                await workerToAwait.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (workerCancellation.IsCancellationRequested)
            {
            }
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            stopped = true;
            eventSource.Dispose();
            eventQueue.Complete();
            workerCancellation.Cancel();
        }

        workerCancellation.Dispose();
    }

    private void ObserveWorkerCompletion(Task completedWorker)
    {
        if (!completedWorker.IsFaulted)
        {
            return;
        }

        // Observe the exception immediately and ask the Generic Host to shut down.
        // StopAsync awaits the same task and propagates the original failure.
        _ = completedWorker.Exception;
        applicationLifetime.StopApplication();
    }
}

using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Recovery;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Recovery;

/// <summary>
/// Drives shell recovery from the lifecycle signals the platform reports.
/// </summary>
/// <remarks>
/// <para>
/// Signals arrive off a window procedure on the UI thread. The platform source
/// normalizes the documented pair of resume broadcasts into one wake signal,
/// while genuinely repeated shell or display notifications can still overlap.
/// The handler therefore queues and returns immediately,
/// exactly as managed-desktop reconciliation does with topology notifications,
/// so nothing a recovery pass does can stall the message pump. Folding the burst
/// into one pass is <see cref="IShellRecoveryService"/>'s job. Calls must remain
/// independently runnable here so the service can observe a duplicate while its
/// per-signal pass is active.
/// </para>
/// <para>
/// The signal source is optional. A host that registers no platform source —
/// every test host, and any host that is not Windows — starts, subscribes to
/// nothing, and never recovers. That is the honest outcome rather than a defect:
/// nothing on such a host can raise a lifecycle signal, so refusing to start
/// would report a problem that does not exist.
/// </para>
/// <para>
/// Nothing here restarts Explorer, suspends or resumes the machine, or changes a
/// display setting. This service only answers those things happening to it.
/// </para>
/// </remarks>
internal sealed class ShellRecoveryHostedService(
    IShellLifecycleSignalSource? signalSource,
    IShellRecoveryService recoveryService,
    IActivityJournal? activityJournal,
    IDiagnosticLogWriter? diagnosticLog) :
    IHostedService,
    IDisposable
{
    private readonly object syncRoot = new();
    private readonly CancellationTokenSource lifetimeCancellation = new();
    private readonly HashSet<Task> pendingRecoveries = [];
    private Task invocationTail = Task.CompletedTask;
    private bool started;
    private bool disposed;

    public Task StartAsync(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this);
        cancellationToken.ThrowIfCancellationRequested();

        IShellLifecycleSignalSource? source = signalSource;
        if (source is null)
        {
            return Task.CompletedTask;
        }

        lock (syncRoot)
        {
            if (started)
            {
                return Task.CompletedTask;
            }

            started = true;
            source.SignalRaised += OnSignalRaised;
        }

        // Started outside the lock. Beginning to listen registers real platform
        // notifications, and holding the queue's lock across that call would let
        // an unrelated signal wait on work that has nothing to do with it.
        try
        {
            source.Start();
        }
        catch
        {
            // A failed listener start must not leave the failed hosted service
            // subscribed. Hosts dispose after a start failure, but cleaning up
            // here also makes a direct retry obey Start's idempotent contract.
            lock (syncRoot)
            {
                started = false;
                Unsubscribe();
            }

            throw;
        }

        return Task.CompletedTask;
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
            Unsubscribe();
            lifetimeCancellation.Cancel();
            pending = Task.WhenAll(pendingRecoveries);
        }

        try
        {
            await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
            when (lifetimeCancellation.IsCancellationRequested)
        {
        }
    }

    /// <summary>
    /// Stops listening and abandons any pass still queued.
    /// </summary>
    /// <remarks>
    /// The signal source is deliberately not disposed here. The container
    /// created it and the container owns its lifetime; disposing a shared
    /// singleton from a consumer would tear down listening for anyone else
    /// holding it.
    /// </remarks>
    public void Dispose()
    {
        lock (syncRoot)
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            started = false;
            Unsubscribe();
            lifetimeCancellation.Cancel();
        }

        lifetimeCancellation.Dispose();
    }

    private void Unsubscribe()
    {
        if (signalSource is not null)
        {
            signalSource.SignalRaised -= OnSignalRaised;
        }
    }

    /// <summary>
    /// Hands one raw notification to the recovery pass without waiting for it.
    /// </summary>
    /// <remarks>
    /// This runs on the thread that pumps the user's window messages. A pass
    /// that took a COM call to a shell that has just died could block for
    /// seconds, so the only correct thing to do here is queue and return.
    /// </remarks>
    private void OnSignalRaised(
        object? sender,
        ShellLifecycleSignalEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);
        ShellLifecycleSignal signal = args.Signal;
        Queue(token => RecoverAndLogAsync(signal, token));
    }

    private async Task RecoverAndLogAsync(
        ShellLifecycleSignal signal,
        CancellationToken cancellationToken)
    {
        ShellRecoveryResult result = await recoveryService
            .RecoverAsync(signal, cancellationToken)
            .ConfigureAwait(false);

        if (result.CoalescedSignalCount == 0 ||
            activityJournal is null ||
            diagnosticLog is null)
        {
            return;
        }

        // ShellRecoveryService already publishes these records to the journal.
        // The hosted boundary owns only the missing durable half of diagnostics:
        // writing the exact same projection to the rolling structured log.
        foreach (ActivityRecord record in ActivityRecordFactory.FromShellRecovery(
            result,
            activityJournal.SessionId))
        {
            diagnosticLog.Write(record);
        }
    }

    private void Queue(Func<CancellationToken, Task> work)
    {
        lock (syncRoot)
        {
            if (disposed ||
                !started ||
                lifetimeCancellation.IsCancellationRequested)
            {
                return;
            }

            CancellationToken token = lifetimeCancellation.Token;

            // Preserve notification order only until RecoverAsync has been
            // invoked. It claims its per-signal coalescer before returning its
            // Task, so the next notification can then enter while the previous
            // pass is still active. Waiting for completion here would make every
            // duplicate wait until the claim had been released and would defeat
            // the service-level burst rule.
            Task previousInvocation = invocationTail;
            TaskCompletionSource invocationStarted = new(
                TaskCreationOptions.RunContinuationsAsynchronously);
            invocationTail = invocationStarted.Task;
            Task pending = Task.Run(
                () => RunAsync(
                    previousInvocation,
                    invocationStarted,
                    work,
                    token),
                CancellationToken.None);
            _ = pendingRecoveries.Add(pending);
            _ = pending.ContinueWith(
                completed => RemoveCompleted(completed),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private void RemoveCompleted(Task completed)
    {
        lock (syncRoot)
        {
            _ = pendingRecoveries.Remove(completed);
        }
    }

    /// <summary>
    /// Runs one pass and observes whatever it throws.
    /// </summary>
    /// <remarks>
    /// <see cref="IShellRecoveryService.RecoverAsync"/> is specified never to
    /// throw except on cancellation: a failed pass is reported as
    /// <see cref="ShellRecoveryOutcome.Failed"/>, not raised. This catch is belt
    /// and braces against an implementation that breaks that promise, and it is
    /// worth having for two reasons. A fault left on the chain would be an
    /// unobserved task exception, and the same fault would be re-raised out of
    /// <see cref="StopAsync"/> and fail an otherwise clean shutdown. Neither is
    /// an acceptable way to learn that recovery went wrong — recovery exists to
    /// keep the app running, so a recovery that fails must leave it running.
    /// </remarks>
    private static async Task RunAsync(
        Task previousInvocation,
        TaskCompletionSource invocationStarted,
        Func<CancellationToken, Task> work,
        CancellationToken cancellationToken)
    {
        try
        {
            await previousInvocation.ConfigureAwait(false);

            Task recovery;
            try
            {
                recovery = work(cancellationToken);
            }
            finally
            {
                // Release the next notification after the service has had the
                // chance to claim its coalescer, even when a broken
                // implementation throws before returning a Task.
                invocationStarted.TrySetResult();
            }

            await recovery.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Includes the cancellation thrown when the host is stopping.
        }
    }
}

using System.Collections.Immutable;

namespace DesktopShift.Core.Hosting;

/// <summary>
/// One named teardown action in the application shutdown order.
/// </summary>
public sealed record ShutdownStep(
    string Name,
    Func<CancellationToken, ValueTask> DisposeAsync);

public sealed record ShutdownStepFailure(string Name, Exception Exception);

public sealed record ShutdownReport(
    ImmutableArray<string> CompletedSteps,
    ImmutableArray<ShutdownStepFailure> Failures)
{
    public bool IsClean => Failures.IsEmpty;
}

/// <summary>
/// Runs the application's teardown steps in a fixed order, exactly once.
/// </summary>
/// <remarks>
/// <para>
/// Exit has to reach every owned resource — WinEvent hooks, the topology
/// provider registration, the notification-area icon, and the hosted services —
/// and the order matters. Input surfaces are torn down first so nothing can
/// raise a command into services that are already stopping, and the host is
/// stopped and disposed last so hosted services observe a normal shutdown.
/// </para>
/// <para>
/// A failing step never stops the sequence. A notification-area icon that
/// refuses to delete must not strand the WinEvent hooks or leave the process
/// alive; the failure is collected and reported instead.
/// </para>
/// <para>
/// Exit can arrive from the notification area and from the main window at the
/// same time, so the sequence runs once and every later caller awaits the same
/// report.
/// </para>
/// </remarks>
public sealed class ApplicationShutdownSequence
{
    private readonly ImmutableArray<ShutdownStep> steps;
    private readonly object syncRoot = new();
    private Task<ShutdownReport>? run;

    public ApplicationShutdownSequence(IEnumerable<ShutdownStep> steps)
    {
        ArgumentNullException.ThrowIfNull(steps);

        this.steps = [.. steps];

        foreach (ShutdownStep step in this.steps)
        {
            ArgumentNullException.ThrowIfNull(step);
            ArgumentException.ThrowIfNullOrWhiteSpace(step.Name);
        }
    }

    public bool HasRun
    {
        get
        {
            lock (syncRoot)
            {
                return run is not null;
            }
        }
    }

    public Task<ShutdownReport> RunAsync(CancellationToken cancellationToken = default)
    {
        lock (syncRoot)
        {
            return run ??= RunCoreAsync(cancellationToken);
        }
    }

    private async Task<ShutdownReport> RunCoreAsync(CancellationToken cancellationToken)
    {
        ImmutableArray<string>.Builder completed =
            ImmutableArray.CreateBuilder<string>(steps.Length);
        ImmutableArray<ShutdownStepFailure>.Builder failures =
            ImmutableArray.CreateBuilder<ShutdownStepFailure>();

        foreach (ShutdownStep step in steps)
        {
            try
            {
                await step.DisposeAsync(cancellationToken).ConfigureAwait(false);
                completed.Add(step.Name);
            }
            catch (Exception exception)
            {
                failures.Add(new ShutdownStepFailure(step.Name, exception));
            }
        }

        return new ShutdownReport(completed.ToImmutable(), failures.ToImmutable());
    }
}

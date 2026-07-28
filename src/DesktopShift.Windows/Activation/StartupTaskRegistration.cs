using DesktopShift.Core.Hosting;
using Windows.ApplicationModel;

namespace DesktopShift.Windows.Activation;

/// <summary>
/// The per-user start-with-Windows registration for the packaged application.
/// </summary>
/// <remarks>
/// <para>
/// DesktopShift ships as a packaged per-user application, so the supported
/// registration is the <c>windows.startupTask</c> package extension rather than
/// a hand-written <c>Run</c> registry value. The package extension is what the
/// Startup apps page in Windows Settings and Task Manager show and control, and
/// it is removed with the package instead of outliving it.
/// </para>
/// <para>
/// Every state Windows reports back is preserved. Once a user disables the task
/// outside the application, <see cref="StartupTask.RequestEnableAsync"/> returns
/// <c>DisabledByUser</c> without prompting and the toggle must say so rather
/// than silently snapping back.
/// </para>
/// </remarks>
public sealed class StartupTaskRegistration : IStartupRegistration
{
    /// <summary>
    /// Matches the <c>TaskId</c> of the <c>windows.startupTask</c> extension
    /// declared in the package manifest.
    /// </summary>
    public const string TaskId = "DesktopShiftStartupTask";

    public async ValueTask<StartupRegistrationState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StartupTask? task = await TryGetTaskAsync(cancellationToken).ConfigureAwait(false);
        return task is null
            ? StartupRegistrationState.Unavailable
            : Map(task.State);
    }

    public async ValueTask<StartupRegistrationState> SetEnabledAsync(
        bool isEnabled,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();

        StartupTask? task = await TryGetTaskAsync(cancellationToken).ConfigureAwait(false);
        if (task is null)
        {
            return StartupRegistrationState.Unavailable;
        }

        try
        {
            if (isEnabled)
            {
                StartupTaskState requested = await task
                    .RequestEnableAsync()
                    .AsTask(cancellationToken)
                    .ConfigureAwait(false);
                return Map(requested);
            }

            task.Disable();
            return Map(task.State);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            return StartupRegistrationState.Unavailable;
        }
    }

    private static async Task<StartupTask?> TryGetTaskAsync(
        CancellationToken cancellationToken)
    {
        try
        {
            return await StartupTask
                .GetAsync(TaskId)
                .AsTask(cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            // An unpackaged or side-loaded-without-the-extension run has no
            // startup task at all. That is reported, never thrown, because the
            // shell still has to open with a coherent settings page.
            return null;
        }
    }

    private static StartupRegistrationState Map(StartupTaskState state) =>
        state switch
        {
            StartupTaskState.Enabled or StartupTaskState.EnabledByPolicy =>
                StartupRegistrationState.Enabled,
            StartupTaskState.DisabledByUser => StartupRegistrationState.DisabledByUser,
            StartupTaskState.DisabledByPolicy => StartupRegistrationState.DisabledByPolicy,
            _ => StartupRegistrationState.Disabled,
        };
}

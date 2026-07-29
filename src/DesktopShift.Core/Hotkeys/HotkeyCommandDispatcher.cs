namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// The work each shortcut runs, supplied by whoever owns it.
/// </summary>
/// <remarks>
/// Delegates rather than services on purpose. Two of the four commands belong
/// to the shell — restoring a window and flipping a switch it draws — and one
/// belongs to the assignment pipeline. Naming those types here would drag the
/// whole application graph into a class whose only job is to pick one of four
/// things to do.
/// </remarks>
/// <param name="ReassignAllWindowsAsync">Runs the manual reassignment batch.</param>
/// <param name="ReassignForegroundWindowAsync">
/// Reassigns only the window that currently has the foreground.
/// </param>
/// <param name="TogglePauseAsync">Flips automatic assignment.</param>
/// <param name="OpenShellAsync">Restores and focuses the shell window.</param>
public sealed record HotkeyCommands(
    Func<CancellationToken, Task> ReassignAllWindowsAsync,
    Func<CancellationToken, Task> ReassignForegroundWindowAsync,
    Func<CancellationToken, Task> TogglePauseAsync,
    Func<CancellationToken, Task> OpenShellAsync);

/// <summary>
/// Turns a pressed shortcut into the one piece of work it stands for.
/// </summary>
/// <remarks>
/// <para>
/// Kept apart from <see cref="GlobalHotkeyCoordinator"/> so the routing can be
/// proved without registering anything: the coordinator decides which chords are
/// live, this decides what a live chord does, and neither needs the other to be
/// tested.
/// </para>
/// <para>
/// A second press of an action already running is dropped rather than queued. A
/// user leaning on Reassign all windows would otherwise stack whole batches
/// behind each other and keep moving windows long after they let go.
/// </para>
/// </remarks>
public sealed class HotkeyCommandDispatcher
{
    private readonly HotkeyCommands _commands;
    private readonly object _syncRoot = new();
    private readonly HashSet<HotkeyAction> _running = [];
    private Task _pending = Task.CompletedTask;

    public HotkeyCommandDispatcher(HotkeyCommands commands)
    {
        ArgumentNullException.ThrowIfNull(commands);
        ArgumentNullException.ThrowIfNull(commands.ReassignAllWindowsAsync);
        ArgumentNullException.ThrowIfNull(commands.ReassignForegroundWindowAsync);
        ArgumentNullException.ThrowIfNull(commands.TogglePauseAsync);
        ArgumentNullException.ThrowIfNull(commands.OpenShellAsync);

        _commands = commands;
    }

    /// <summary>
    /// The work started by the most recent press, so a caller — a test in
    /// particular — can await what a key press fires and forgets.
    /// </summary>
    public Task Pending
    {
        get
        {
            lock (_syncRoot)
            {
                return _pending;
            }
        }
    }

    /// <summary>
    /// Runs the work bound to <paramref name="action"/>, or nothing at all when
    /// that action is already running.
    /// </summary>
    public Task DispatchAsync(
        HotkeyAction action,
        CancellationToken cancellationToken = default)
    {
        Func<CancellationToken, Task> command = Resolve(action);

        lock (_syncRoot)
        {
            if (!_running.Add(action))
            {
                return Task.CompletedTask;
            }
        }

        Task work = RunAsync(action, command, cancellationToken);

        lock (_syncRoot)
        {
            _pending = work;
        }

        return work;
    }

    /// <summary>
    /// The <see cref="IGlobalHotkeyCoordinator.Invoked"/> handler, shaped so the
    /// shell can subscribe this dispatcher directly.
    /// </summary>
    /// <remarks>
    /// A key press has no caller to hand an exception to, and an unobserved
    /// faulted task would carry the failure to the finalizer thread instead.
    /// The failure is swallowed here and left visible through
    /// <see cref="Pending"/>.
    /// </remarks>
    public void HandleInvoked(object? sender, HotkeyInvokedEventArgs args)
    {
        ArgumentNullException.ThrowIfNull(args);

        Task work;
        try
        {
            work = DispatchAsync(args.Action);
        }
        catch (Exception exception)
        {
            work = Task.FromException(exception);
        }

        lock (_syncRoot)
        {
            _pending = work;
        }

        _ = ObserveAsync(work);
    }

    private Func<CancellationToken, Task> Resolve(HotkeyAction action) =>
        action switch
        {
            HotkeyAction.ReassignAllWindows => _commands.ReassignAllWindowsAsync,
            HotkeyAction.ReassignForegroundWindow =>
                _commands.ReassignForegroundWindowAsync,
            HotkeyAction.TogglePause => _commands.TogglePauseAsync,
            HotkeyAction.OpenDesktopShift => _commands.OpenShellAsync,
            _ => throw new ArgumentOutOfRangeException(
                nameof(action),
                action,
                "The hotkey action is not defined."),
        };

    private async Task RunAsync(
        HotkeyAction action,
        Func<CancellationToken, Task> command,
        CancellationToken cancellationToken)
    {
        try
        {
            await command(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_syncRoot)
            {
                _running.Remove(action);
            }
        }
    }

    private static async Task ObserveAsync(Task work)
    {
        try
        {
            await work.ConfigureAwait(false);
        }
        catch (Exception)
        {
            // A shortcut has no caller to report to. The command itself is
            // responsible for telling the user anything worth knowing.
        }
    }
}

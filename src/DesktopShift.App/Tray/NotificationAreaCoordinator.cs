using System;
using System.Threading;
using System.Threading.Tasks;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;

namespace DesktopShift.App.Tray;

/// <summary>
/// Runs DesktopShift as a quiet notification-area utility.
/// </summary>
/// <remarks>
/// <para>
/// Owns everything the tray needs to decide: which menu to show, what each
/// command does, whether closing the window hides or exits, and which events
/// deserve a notification. It holds no WinUI type, so the whole of that
/// behavior can be driven in tests through <see cref="ITrayIconHost"/> and
/// <see cref="IShellWindowSurface"/>.
/// </para>
/// <para>
/// The single <see cref="IShellWindowSurface"/> it is given is the only shell
/// window there will ever be. Hiding and restoring move that one surface, which
/// is what stops a restore from building a second window over a still-running
/// first one.
/// </para>
/// </remarks>
public sealed class NotificationAreaCoordinator : IAsyncDisposable
{
    /// <summary>
    /// The destination the recent-error entry opens.
    /// </summary>
    public const string RecentIssueDestinationKey = "activity";

    private readonly ITrayIconHost _trayIconHost;
    private readonly IShellWindowSurface _shellWindow;
    private readonly IAutomaticAssignmentPauseController _pauseController;
    private readonly IWindowAssignmentActivityProjection _assignmentActivity;
    private readonly ICompatibilityCoordinator _compatibilityCoordinator;
    private readonly WindowReassignmentCommand _reassignmentCommand;
    private TrayNotificationPreferences _notificationPreferences;
    private readonly Func<CancellationToken, Task> _exitAsync;
    private readonly object _syncRoot = new();
    private TrayMenuModel _currentMenu;
    private Task _pendingCommand = Task.CompletedTask;
    private bool _isReassignmentRunning;
    private bool _isDisposed;

    public NotificationAreaCoordinator(
        ITrayIconHost trayIconHost,
        IShellWindowSurface shellWindow,
        IAutomaticAssignmentPauseController pauseController,
        IWindowReassignmentService reassignmentService,
        IWindowAssignmentActivityProjection assignmentActivity,
        ICompatibilityCoordinator compatibilityCoordinator,
        Func<CancellationToken, Task> exitAsync,
        TrayNotificationPreferences? notificationPreferences = null)
    {
        ArgumentNullException.ThrowIfNull(trayIconHost);
        ArgumentNullException.ThrowIfNull(shellWindow);
        ArgumentNullException.ThrowIfNull(pauseController);
        ArgumentNullException.ThrowIfNull(reassignmentService);
        ArgumentNullException.ThrowIfNull(assignmentActivity);
        ArgumentNullException.ThrowIfNull(compatibilityCoordinator);
        ArgumentNullException.ThrowIfNull(exitAsync);

        _trayIconHost = trayIconHost;
        _shellWindow = shellWindow;
        _pauseController = pauseController;
        _assignmentActivity = assignmentActivity;
        _compatibilityCoordinator = compatibilityCoordinator;
        _reassignmentCommand = new WindowReassignmentCommand(reassignmentService);
        _notificationPreferences = notificationPreferences ?? TrayNotificationPreferences.Default;
        _exitAsync = exitAsync;
        _currentMenu = BuildMenu();

        _trayIconHost.CommandInvoked += OnTrayCommandInvoked;
        _pauseController.PauseStateChanged += OnPauseStateChanged;
        _assignmentActivity.ActivityRecorded += OnAssignmentActivityRecorded;
        _compatibilityCoordinator.StatusChanged += OnCompatibilityStatusChanged;
    }

    public TrayMenuModel CurrentMenu
    {
        get
        {
            lock (_syncRoot)
            {
                return _currentMenu;
            }
        }
    }

    /// <summary>
    /// The command started by the most recent notification-area click, so a
    /// caller — a test in particular — can await work the shell itself fires
    /// and forgets.
    /// </summary>
    public Task PendingCommand
    {
        get
        {
            lock (_syncRoot)
            {
                return _pendingCommand;
            }
        }
    }

    public bool IsPaused => _pauseController.IsPaused;

    /// <summary>
    /// Applies the persisted notification switches without recreating the tray
    /// icon or its event subscriptions.
    /// </summary>
    public void UpdateNotificationPreferences(
        TrayNotificationPreferences notificationPreferences)
    {
        ArgumentNullException.ThrowIfNull(notificationPreferences);

        lock (_syncRoot)
        {
            ObjectDisposedException.ThrowIf(_isDisposed, this);
            _notificationPreferences = notificationPreferences;
        }
    }

    /// <summary>
    /// Places the icon in the notification area and applies the launch
    /// disposition.
    /// </summary>
    public void Start(ShellLaunchDisposition disposition)
    {
        RefreshMenu();

        if (disposition == ShellLaunchDisposition.ShowWindow)
        {
            _shellWindow.ShowAndActivate();
            return;
        }

        // The icon is already showing, so a start-minimized launch has a visible
        // way back in and does not need the window at all.
        _shellWindow.Hide();
    }

    /// <summary>
    /// Handles a request to activate the already-running instance.
    /// </summary>
    public void HandleActivationRequested() => _shellWindow.ShowAndActivate();

    /// <summary>
    /// Decides what closing the main window means, and carries the decision out
    /// for the hide case.
    /// </summary>
    /// <remarks>
    /// Exiting is left to the caller: the window close handler has to let the
    /// close proceed before the shutdown sequence tears the host down.
    /// </remarks>
    public ShellCloseDisposition HandleWindowClosing(BehaviorSettings behavior)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        ShellCloseDisposition disposition = ShellLifetimePolicy.ResolveClose(behavior);
        if (disposition == ShellCloseDisposition.HideToNotificationArea)
        {
            _shellWindow.Hide();
            RefreshMenu();
        }

        return disposition;
    }

    public async Task ExecuteAsync(
        TrayCommand command,
        CancellationToken cancellationToken = default)
    {
        switch (command)
        {
            case TrayCommand.Open:
                _shellWindow.ShowAndActivate();
                break;

            case TrayCommand.ShowRecentIssue:
                _shellWindow.ShowAndActivate();
                _shellWindow.NavigateTo(RecentIssueDestinationKey);
                break;

            case TrayCommand.TogglePause:
                _ = _pauseController.TogglePause();
                break;

            case TrayCommand.ReassignAll:
                await ReassignAllAsync(cancellationToken).ConfigureAwait(false);
                break;

            case TrayCommand.Exit:
                await _exitAsync(cancellationToken).ConfigureAwait(false);
                break;

            default:
                throw new ArgumentOutOfRangeException(
                    nameof(command),
                    command,
                    "The notification-area command is not defined.");
        }

        RefreshMenu();
    }

    public ValueTask DisposeAsync()
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return ValueTask.CompletedTask;
            }

            _isDisposed = true;
        }

        _trayIconHost.CommandInvoked -= OnTrayCommandInvoked;
        _pauseController.PauseStateChanged -= OnPauseStateChanged;
        _assignmentActivity.ActivityRecorded -= OnAssignmentActivityRecorded;
        _compatibilityCoordinator.StatusChanged -= OnCompatibilityStatusChanged;
        _trayIconHost.Dispose();

        return ValueTask.CompletedTask;
    }

    private async Task ReassignAllAsync(CancellationToken cancellationToken)
    {
        lock (_syncRoot)
        {
            if (_isReassignmentRunning)
            {
                return;
            }

            _isReassignmentRunning = true;
        }

        RefreshMenu();

        try
        {
            // Deliberately not gated on the pause state. Pausing stops
            // DesktopShift acting on its own; it never refuses an instruction.
            _ = await _reassignmentCommand
                .ExecuteAsync(cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            lock (_syncRoot)
            {
                _isReassignmentRunning = false;
            }

            RefreshMenu();
        }
    }

    private void OnTrayCommandInvoked(object? sender, TrayCommandEventArgs args)
    {
        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _pendingCommand = RunCommandAsync(args.Command);
        }
    }

    /// <summary>
    /// Runs a command the user picked from the menu, reporting rather than
    /// propagating a failure.
    /// </summary>
    /// <remarks>
    /// A menu click has no caller to hand an exception to, and an unobserved
    /// faulted task would swallow it entirely. A command the user asked for is
    /// always worth reporting, so this path ignores the notification
    /// preferences, which govern unattended events.
    /// </remarks>
    private async Task RunCommandAsync(TrayCommand command)
    {
        try
        {
            await ExecuteAsync(command).ConfigureAwait(false);
        }
        catch (Exception exception)
        {
            _trayIconHost.Notify(
                new TrayNotification(
                    "DesktopShift could not complete that action",
                    exception.Message,
                    TrayNotificationSeverity.Error));
        }
    }

    private void OnPauseStateChanged(
        object? sender,
        AutomaticAssignmentPauseChangedEventArgs args)
    {
        _ = args;
        RefreshMenu();
    }

    private void OnAssignmentActivityRecorded(
        object? sender,
        WindowAssignmentActivityRecordedEventArgs args)
    {
        TrayNotificationPreferences preferences;
        lock (_syncRoot)
        {
            preferences = _notificationPreferences;
        }

        TrayNotification? notification = TrayNotificationPolicy.ForAssignment(
            args.Activity,
            preferences);
        if (notification is not null)
        {
            _trayIconHost.Notify(notification);
        }

        RefreshMenu();
    }

    private void OnCompatibilityStatusChanged(
        object? sender,
        CompatibilityStatusChangedEventArgs args)
    {
        TrayNotificationPreferences preferences;
        lock (_syncRoot)
        {
            preferences = _notificationPreferences;
        }

        TrayNotification? notification = TrayNotificationPolicy.ForCompatibility(
            args.Status,
            preferences);
        if (notification is not null)
        {
            _trayIconHost.Notify(notification);
        }

        RefreshMenu();
    }

    private void RefreshMenu()
    {
        TrayMenuModel menu = BuildMenu();

        lock (_syncRoot)
        {
            if (_isDisposed)
            {
                return;
            }

            _currentMenu = menu;
        }

        _trayIconHost.Show(menu);
    }

    private TrayMenuModel BuildMenu()
    {
        bool isReassignmentRunning;
        lock (_syncRoot)
        {
            isReassignmentRunning = _isReassignmentRunning;
        }

        RecentIssue? recentIssue = RecentIssueProjection.Project(
            _assignmentActivity.Snapshot,
            _compatibilityCoordinator.Current);

        return TrayMenuPresentation.Create(
            _pauseController.IsPaused,
            isReassignmentRunning,
            recentIssue);
    }
}

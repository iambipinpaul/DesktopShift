using System.Diagnostics;
using System.IO;
using DesktopShift.App.Tray;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Activation;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Hotkeys;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;
using DesktopShift.Infrastructure.Configuration;
using DesktopShift.Infrastructure.Hosting;
using DesktopShift.Windows.Activation;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.Hotkeys;
using DesktopShift.Windows.Observation;
using DesktopShift.Windows.Recovery;
using DesktopShift.Windows.VirtualDesktops;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;

namespace DesktopShift.App;

public partial class App : Application
{
    private readonly IHost _host;
    private DispatcherQueue? _dispatcherQueue;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private NotificationAreaCoordinator? _notificationArea;
    private IGlobalHotkeyCoordinator? _hotkeyCoordinator;
    private HotkeyCommandDispatcher? _hotkeyDispatcher;
    private IDesktopSwitchHotkeyCoordinator? _desktopSwitchHotkeyCoordinator;
    private IDesktopSwitchShortcutService? _desktopSwitchService;
    private Task _desktopSwitchWork = Task.CompletedTask;
    private ApplicationShutdownSequence? _shutdownSequence;
    private MainWindow? _window;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();

        _host = DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<ISingleInstanceService, SingleInstanceService>();
            services.AddSingleton<SingleInstanceCoordinator>();

            // Registered after AddDesktopShiftFoundation, so this replaces the
            // in-memory default the foundation installs for tests.
            services.AddSingleton<IStartupRegistration, StartupTaskRegistration>();
            services.AddDesktopShiftConfigurationExchange();
            services.AddSingleton<
                IGlobalHotkeyRegistrar,
                WindowsGlobalHotkeyRegistrar>();
            services.AddSingleton<
                IDesktopSwitchHotkeyRegistrar,
                WindowsDesktopSwitchHotkeyRegistrar>();
            services.AddSingleton<
                IForegroundWindowProvider,
                WindowsForegroundWindowProvider>();
            services.AddSingleton<IWindowsBuildInfoProvider, EnvironmentWindowsBuildInfoProvider>();
            services.AddSingleton<ValidatedVirtualDesktopTopologyProvider>();
            services.AddSingleton<IDesktopTopologyProvider>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<
                        ValidatedVirtualDesktopTopologyProvider>());
            services.AddSingleton<ICompatibilityCoordinator, CompatibilityCoordinator>();
            services.AddSingleton<
                IWindowDesktopPlacementService,
                WindowsWindowDesktopPlacementService>();
            services.AddSingleton<
                ITopLevelWindowEnumerator,
                WindowsTopLevelWindowEnumerator>();
            services.AddSingleton<IWindowClassifier, WindowsWindowClassifier>();
            services.AddSingleton<IWindowIdentityResolver, WindowsProcessIdentityResolver>();
            services.AddSingleton<IApplicationIconReader, WindowsApplicationIconReader>();
            services.AddSingleton<
                IRunningApplicationInventory,
                RunningApplicationInventory>();
            // One instance behind both interfaces on purpose: the hooks the
            // recovery set drops and retakes have to be the very hooks window
            // observation is feeding from, or recovery would rebuild a source
            // nobody is listening to.
            services.AddSingleton<WindowsWinEventSource>();
            services.AddSingleton<IWindowEventSource>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<WindowsWinEventSource>());
            services.AddSingleton<IRestartableWindowEventSource>(
                static serviceProvider =>
                    serviceProvider.GetRequiredService<WindowsWinEventSource>());
            services.AddSingleton<
                INativeRegistrationSet,
                WindowsNativeRegistrationSet>();
            services.AddSingleton<
                IShellLifecycleSignalSource,
                WindowsShellLifecycleSignalSource>();
            services.AddDesktopShiftObservation();
            services.AddDesktopShiftManagedDesktopReconciliation();
            services.AddDesktopShiftShellRecovery();
            services.AddSingleton<
                IManagedDesktopMaintenanceService,
                ManagedDesktopMaintenanceService>();
            services.AddDesktopShiftAssignments();
            services.AddDesktopShiftDiagnostics(useFileSystemLogStore: true);
            services.AddSingleton<MainWindow>();
        });

        UnhandledException += OnUnhandledException;
    }

    protected override async void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
            _singleInstanceCoordinator =
                _host.Services.GetRequiredService<SingleInstanceCoordinator>();

            ActivationRoute route = await _singleInstanceCoordinator
                .RouteStartupActivationAsync()
                .ConfigureAwait(true);

            if (route is ActivationRoute.RedirectedToPrimaryInstance)
            {
                await ShutdownAsync().ConfigureAwait(true);
                Exit();
                return;
            }

            _singleInstanceCoordinator.ActivationRequested += OnActivationRequested;

            ConfigurationState startupConfiguration = await _host.Services
                .GetRequiredService<IConfigurationService>()
                .LoadAsync()
                .ConfigureAwait(true);
            BehaviorSettings startupBehavior =
                BehaviorSettingsCommand.ResolveBehavior(startupConfiguration);
            _host.Services
                .GetRequiredService<IThemePreferenceService>()
                .SetTheme(startupBehavior.Theme);
            IAutomaticAssignmentPauseController startupPause =
                _host.Services.GetRequiredService<
                    IAutomaticAssignmentPauseController>();
            if (startupBehavior.StartAssignmentPaused)
            {
                startupPause.Pause();
            }
            else
            {
                startupPause.Resume();
            }

            await _host.StartAsync().ConfigureAwait(true);
            await StartNotificationAreaAsync(
                    startupConfiguration,
                    IsAutomaticStartupActivation())
                .ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"DesktopShift failed to launch: {exception}");
            await ShutdownAsync().ConfigureAwait(true);
            Exit();
        }
    }

    private async Task StartNotificationAreaAsync(
        ConfigurationState configuration,
        bool isAutomaticStartup)
    {
        MainWindow window = CreateMainWindow();
        BehaviorSettings behavior =
            BehaviorSettingsCommand.ResolveBehavior(configuration);
        IAutomaticAssignmentPauseController pauseController =
            _host.Services.GetRequiredService<
                IAutomaticAssignmentPauseController>();

        NotificationAreaCoordinator notificationArea = new(
            new ShellNotifyIconHost(),
            new MainWindowShellSurface(window),
            pauseController,
            _host.Services.GetRequiredService<IWindowReassignmentService>(),
            _host.Services.GetRequiredService<IWindowAssignmentActivityProjection>(),
            _host.Services.GetRequiredService<ICompatibilityCoordinator>(),
            ExitFromNotificationAreaAsync,
            new TrayNotificationPreferences(
                behavior.NotifyOnAssignmentFailure,
                behavior.NotifyOnCompatibilityWarning));
        _notificationArea = notificationArea;
        window.CloseRequestHandler = notificationArea.HandleWindowClosing;
        window.AcceptedBehaviorHandler = accepted =>
            notificationArea.UpdateNotificationPreferences(
                new TrayNotificationPreferences(
                    accepted.NotifyOnAssignmentFailure,
                    accepted.NotifyOnCompatibilityWarning));
        _shutdownSequence = CreateShutdownSequence(notificationArea);
        ShellLaunchDisposition disposition = ShellLifetimePolicy.ResolveLaunch(
            behavior,
            isFirstRunComplete: !configuration.IsFirstRun,
            isAutomaticStartup);

        notificationArea.Start(disposition);
        StartHotkeys(behavior);

        if (disposition == ShellLaunchDisposition.StayInNotificationArea)
        {
            // The shell normally runs the compatibility test when its content
            // loads. A launch that never shows the window has to run it here,
            // or nothing would ever prove the provider works and startup
            // reconciliation would wait forever.
            _ = await _host.Services
                .GetRequiredService<ICompatibilityCoordinator>()
                .RunCompatibilityTestAsync()
                .ConfigureAwait(true);
        }
    }

    private static bool IsAutomaticStartupActivation()
    {
        try
        {
            return AppInstance.GetCurrent().GetActivatedEventArgs().Kind ==
                ExtendedActivationKind.StartupTask;
        }
        catch (Exception)
        {
            // If activation metadata is unavailable, showing the shell is the
            // safe behavior. A user-initiated click must never look like a
            // failed launch merely because startup context could not be read.
            return false;
        }
    }

    private void StartHotkeys(BehaviorSettings behavior)
    {
        IWindowReassignmentService reassignment =
            _host.Services.GetRequiredService<IWindowReassignmentService>();
        IForegroundWindowReassignment foreground =
            _host.Services.GetRequiredService<IForegroundWindowReassignment>();
        IAutomaticAssignmentPauseController pause =
            _host.Services.GetRequiredService<
                IAutomaticAssignmentPauseController>();

        _hotkeyDispatcher = new HotkeyCommandDispatcher(
            new HotkeyCommands(
                async cancellationToken =>
                {
                    _ = await reassignment
                        .ReassignAllAsync(cancellationToken)
                        .ConfigureAwait(false);
                },
                async cancellationToken =>
                {
                    _ = await foreground
                        .ReassignForegroundWindowAsync(cancellationToken)
                        .ConfigureAwait(false);
                },
                cancellationToken =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    _ = pause.TogglePause();
                    return Task.CompletedTask;
                },
                OpenShellFromHotkeyAsync));

        _hotkeyCoordinator =
            _host.Services.GetRequiredService<IGlobalHotkeyCoordinator>();
        _hotkeyCoordinator.Invoked += _hotkeyDispatcher.HandleInvoked;
        _ = _hotkeyCoordinator.Apply(behavior.ToHotkeySettings());

        StartDesktopSwitchHotkeys(behavior);
    }

    /// <summary>
    /// Restores the saved desktop-switching profile, so the combinations a user
    /// chose are claimed again at sign-in without being asked for twice.
    /// </summary>
    private void StartDesktopSwitchHotkeys(BehaviorSettings behavior)
    {
        _desktopSwitchService =
            _host.Services.GetRequiredService<IDesktopSwitchShortcutService>();
        _desktopSwitchHotkeyCoordinator =
            _host.Services.GetRequiredService<IDesktopSwitchHotkeyCoordinator>();
        _desktopSwitchHotkeyCoordinator.Invoked += OnDesktopSwitchHotkeyInvoked;
        _ = _desktopSwitchHotkeyCoordinator.Apply(
            behavior.ToDesktopSwitchShortcutSettings());
    }

    /// <summary>
    /// Runs one desktop-switching press.
    /// </summary>
    /// <remarks>
    /// A key press has no caller to hand an exception to, so the work is started
    /// and observed here rather than awaited. The switch itself is serialized by
    /// <see cref="DesktopSwitchShortcutService"/>, so leaning on a digit does not
    /// stack transitions.
    /// </remarks>
    private void OnDesktopSwitchHotkeyInvoked(
        object? sender,
        DesktopSwitchHotkeyInvokedEventArgs args)
    {
        _desktopSwitchWork = SwitchDesktopAsync(args.DesktopOrdinal);
    }

    private async Task SwitchDesktopAsync(int desktopOrdinal)
    {
        IDesktopSwitchShortcutService? service = _desktopSwitchService;
        if (service is null)
        {
            return;
        }

        try
        {
            DesktopSwitchShortcutResult result = await service
                .SwitchToDesktopAsync(desktopOrdinal)
                .ConfigureAwait(true);
            if (result.ShouldNotify)
            {
                NotifyDesktopSwitch(result);
            }
        }
        catch (Exception exception)
        {
            Debug.WriteLine(
                $"DesktopShift could not switch to desktop {desktopOrdinal}: {exception}");
        }
    }

    /// <summary>
    /// Tells the user why a shortcut they pressed did nothing visible.
    /// </summary>
    /// <remarks>
    /// A desktop that does not exist is the case this is really for. Pressing
    /// Ctrl+Alt+7 with four desktops open is otherwise indistinguishable from the
    /// shortcut being broken, and a user who cannot tell those apart will go
    /// looking for a bug that is not there.
    /// </remarks>
    private void NotifyDesktopSwitch(DesktopSwitchShortcutResult result)
    {
        NotificationAreaCoordinator? notificationArea = _notificationArea;
        if (notificationArea is null || result.Message is null)
        {
            return;
        }

        TrayNotification notification = new(
            result.Outcome == DesktopSwitchShortcutOutcome.DesktopMissing
                ? $"There is no Desktop {result.DesktopOrdinal}"
                : "DesktopShift could not switch desktops",
            result.Message,
            result.Outcome == DesktopSwitchShortcutOutcome.DesktopMissing
                ? TrayNotificationSeverity.Information
                : TrayNotificationSeverity.Warning);

        if (_dispatcherQueue?.HasThreadAccess is true)
        {
            notificationArea.Notify(notification);
            return;
        }

        _ = _dispatcherQueue?.TryEnqueue(
            () => notificationArea.Notify(notification));
    }

    private Task OpenShellFromHotkeyAsync(CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (_dispatcherQueue?.HasThreadAccess is true)
        {
            _notificationArea?.HandleActivationRequested();
            return Task.CompletedTask;
        }

        if (_dispatcherQueue?.TryEnqueue(
            () => _notificationArea?.HandleActivationRequested()) is true)
        {
            return Task.CompletedTask;
        }

        return Task.FromException(
            new InvalidOperationException(
                "DesktopShift could not return to its UI thread."));
    }

    private MainWindow CreateMainWindow()
    {
        _window ??= _host.Services.GetRequiredService<MainWindow>();
        _window.Closed -= OnMainWindowClosed;
        _window.Closed += OnMainWindowClosed;
        return _window;
    }

    private ApplicationShutdownSequence CreateShutdownSequence(
        NotificationAreaCoordinator notificationArea)
    {
        SingleInstanceCoordinator? singleInstance = _singleInstanceCoordinator;

        // Ordered so nothing can raise a command into services that are already
        // stopping: the notification area first, then the activation
        // registration, then the hosted services, and finally the container that
        // owns the WinEvent hooks and the topology provider registration.
        return new ApplicationShutdownSequence(
        [
            new ShutdownStep(
                "global hotkeys",
                _ =>
                {
                    if (_hotkeyCoordinator is not null)
                    {
                        if (_hotkeyDispatcher is not null)
                        {
                            _hotkeyCoordinator.Invoked -=
                                _hotkeyDispatcher.HandleInvoked;
                        }

                        _hotkeyCoordinator.Dispose();
                    }

                    return ValueTask.CompletedTask;
                }),
            // Its own step rather than a second statement in the one above: a
            // failure releasing the four command shortcuts must not leave ten
            // desktop combinations claimed by a process that is exiting.
            new ShutdownStep(
                "desktop switching shortcuts",
                _ =>
                {
                    if (_desktopSwitchHotkeyCoordinator is not null)
                    {
                        _desktopSwitchHotkeyCoordinator.Invoked -=
                            OnDesktopSwitchHotkeyInvoked;
                        _desktopSwitchHotkeyCoordinator.Dispose();
                    }

                    return ValueTask.CompletedTask;
                }),
            new ShutdownStep(
                "notification area",
                async _ => await notificationArea.DisposeAsync().ConfigureAwait(false)),
            new ShutdownStep(
                "single instance registration",
                async _ =>
                {
                    if (singleInstance is not null)
                    {
                        singleInstance.ActivationRequested -= OnActivationRequested;
                        await singleInstance.DisposeAsync().ConfigureAwait(false);
                    }
                }),
            new ShutdownStep(
                "hosted services",
                async _ => await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(false)),
            new ShutdownStep(
                "host container",
                _ =>
                {
                    _host.Dispose();
                    return ValueTask.CompletedTask;
                }),
        ]);
    }

    private void OnActivationRequested(object? sender, EventArgs args)
    {
        if (_dispatcherQueue?.HasThreadAccess is true)
        {
            _notificationArea?.HandleActivationRequested();
            return;
        }

        _ = _dispatcherQueue?.TryEnqueue(
            () => _notificationArea?.HandleActivationRequested());
    }

    private async Task ExitFromNotificationAreaAsync(CancellationToken cancellationToken)
    {
        _ = cancellationToken;
        await ShutdownAsync().ConfigureAwait(true);
        Exit();
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
        // Reached only when close-to-tray is off; otherwise the close is
        // cancelled before it gets here and the window is hidden instead.
        await ShutdownAsync().ConfigureAwait(true);
        Exit();
    }

    private async Task ShutdownAsync()
    {
        if (Interlocked.Exchange(ref _shutdownStarted, 1) != 0)
        {
            return;
        }

        UnhandledException -= OnUnhandledException;

        if (_window is not null)
        {
            _window.Closed -= OnMainWindowClosed;
            _window.CloseRequestHandler = null;
            _window.AcceptedBehaviorHandler = null;
        }

        if (_shutdownSequence is not null)
        {
            ShutdownReport report = await _shutdownSequence
                .RunAsync()
                .ConfigureAwait(true);
            foreach (ShutdownStepFailure failure in report.Failures)
            {
                Debug.WriteLine(
                    $"DesktopShift could not tear down {failure.Name}: {failure.Exception}");
            }

            return;
        }

        // A launch that failed before the notification area existed still has to
        // release the host and the activation registration.
        if (_singleInstanceCoordinator is not null)
        {
            _singleInstanceCoordinator.ActivationRequested -= OnActivationRequested;
            await _singleInstanceCoordinator.DisposeAsync().ConfigureAwait(true);
        }

        await _host.StopAsync(TimeSpan.FromSeconds(5)).ConfigureAwait(true);
        _host.Dispose();
    }

    /// <summary>
    /// Records a UI exception and keeps DesktopShift running.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Two deliberate changes from simply logging. First, the exception is
    /// written to a file: <see cref="Debug.WriteLine"/> reaches a debugger and
    /// nothing else, so a crash on a user's machine left no evidence at all and
    /// the only symptom was the window disappearing.
    /// </para>
    /// <para>
    /// Second, <c>Handled</c> is set. Without it any exception reaching here
    /// terminates the process, which for a notification-area utility means the
    /// icon vanishes with no message and automatic assignment silently stops. A
    /// failed button click should cost the user that click, not the session.
    /// The failure is still recorded, so this hides nothing — it only declines
    /// to treat every UI fault as fatal.
    /// </para>
    /// </remarks>
    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        Debug.WriteLine($"Unhandled DesktopShift UI exception: {args.Exception}");
        RecordCrash(args.Exception);
        args.Handled = true;
    }

    /// <summary>
    /// Appends an exception to a crash file beside the configuration.
    /// </summary>
    /// <remarks>
    /// Written with its own file handling rather than through the diagnostic
    /// log: the rolling log carries redacted, structured activity records, and a
    /// stack trace is neither. This is also the one path that has to keep
    /// working when the rest of the application is already in an unknown state,
    /// so it depends on nothing but the file system and swallows its own
    /// failures.
    /// </remarks>
    private static void RecordCrash(Exception exception)
    {
        try
        {
            string directory = Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                "DesktopShift");
            _ = Directory.CreateDirectory(directory);
            File.AppendAllText(
                Path.Combine(directory, "crash.log"),
                $"""

                ===== {DateTimeOffset.UtcNow:O} =====
                {exception}

                """);
        }
        catch (Exception)
        {
            // Nothing useful is left to do. Failing to record a crash must not
            // become a second crash.
        }
    }
}

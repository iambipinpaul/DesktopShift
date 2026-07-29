using System.Diagnostics;
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

namespace DesktopShift.App;

public partial class App : Application
{
    private readonly IHost _host;
    private DispatcherQueue? _dispatcherQueue;
    private SingleInstanceCoordinator? _singleInstanceCoordinator;
    private NotificationAreaCoordinator? _notificationArea;
    private IGlobalHotkeyCoordinator? _hotkeyCoordinator;
    private HotkeyCommandDispatcher? _hotkeyDispatcher;
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
            await StartNotificationAreaAsync(startupConfiguration)
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
        ConfigurationState configuration)
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
            isFirstRunComplete: !configuration.IsFirstRun);

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

    private static void OnUnhandledException(
        object sender,
        Microsoft.UI.Xaml.UnhandledExceptionEventArgs args)
    {
        Debug.WriteLine($"Unhandled DesktopShift UI exception: {args.Exception}");
    }
}

using System.Diagnostics;
using DesktopShift.App.Tray;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Activation;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;
using DesktopShift.Infrastructure.Hosting;
using DesktopShift.Windows.Activation;
using DesktopShift.Windows.Assignments;
using DesktopShift.Windows.Compatibility;
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
            services.AddSingleton<IWindowsBuildInfoProvider, EnvironmentWindowsBuildInfoProvider>();
            services.AddSingleton<IDesktopTopologyProvider, ValidatedVirtualDesktopTopologyProvider>();
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

            await _host.StartAsync().ConfigureAwait(true);
            await StartNotificationAreaAsync().ConfigureAwait(true);
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"DesktopShift failed to launch: {exception}");
            await ShutdownAsync().ConfigureAwait(true);
            Exit();
        }
    }

    private async Task StartNotificationAreaAsync()
    {
        MainWindow window = CreateMainWindow();
        NotificationAreaCoordinator notificationArea = new(
            new ShellNotifyIconHost(),
            new MainWindowShellSurface(window),
            _host.Services.GetRequiredService<IAutomaticAssignmentPauseController>(),
            _host.Services.GetRequiredService<IWindowReassignmentService>(),
            _host.Services.GetRequiredService<IWindowAssignmentActivityProjection>(),
            _host.Services.GetRequiredService<ICompatibilityCoordinator>(),
            ExitFromNotificationAreaAsync);
        _notificationArea = notificationArea;
        window.CloseRequestHandler = notificationArea.HandleWindowClosing;
        _shutdownSequence = CreateShutdownSequence(notificationArea);

        ConfigurationState configuration = await _host.Services
            .GetRequiredService<IConfigurationService>()
            .LoadAsync()
            .ConfigureAwait(true);
        ShellLaunchDisposition disposition = ShellLifetimePolicy.ResolveLaunch(
            BehaviorSettingsCommand.ResolveBehavior(configuration),
            isFirstRunComplete: !configuration.IsFirstRun);

        notificationArea.Start(disposition);

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

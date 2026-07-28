using System.Diagnostics;
using DesktopShift.Core.Activation;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Hosting;
using DesktopShift.Windows.Activation;
using DesktopShift.Windows.Compatibility;
using DesktopShift.Windows.Observation;
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
    private MainWindow? _window;
    private int _shutdownStarted;

    public App()
    {
        InitializeComponent();

        _host = DesktopShiftHost.Create(services =>
        {
            services.AddSingleton<ISingleInstanceService, SingleInstanceService>();
            services.AddSingleton<SingleInstanceCoordinator>();
            services.AddSingleton<IWindowsBuildInfoProvider, EnvironmentWindowsBuildInfoProvider>();
            services.AddSingleton<IDesktopTopologyProvider, ValidatedVirtualDesktopTopologyProvider>();
            services.AddSingleton<ICompatibilityCoordinator, CompatibilityCoordinator>();
            services.AddDesktopShiftManagedDesktopReconciliation();
            services.AddSingleton<IWindowClassifier, WindowsWindowClassifier>();
            services.AddSingleton<IWindowIdentityResolver, WindowsProcessIdentityResolver>();
            services.AddSingleton<IWindowEventSource, WindowsWinEventSource>();
            services.AddDesktopShiftObservation();
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
            ShowMainWindow();
        }
        catch (Exception exception)
        {
            Debug.WriteLine($"DesktopShift failed to launch: {exception}");
            await ShutdownAsync().ConfigureAwait(true);
            Exit();
        }
    }

    private void ShowMainWindow()
    {
        _window ??= _host.Services.GetRequiredService<MainWindow>();
        _window.Closed -= OnMainWindowClosed;
        _window.Closed += OnMainWindowClosed;
        _window.AppWindow.Show();
        _window.Show();
    }

    private void OnActivationRequested(object? sender, EventArgs args)
    {
        if (_dispatcherQueue?.HasThreadAccess is true)
        {
            ShowMainWindow();
            return;
        }

        _ = _dispatcherQueue?.TryEnqueue(ShowMainWindow);
    }

    private async void OnMainWindowClosed(object sender, WindowEventArgs args)
    {
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

        if (_singleInstanceCoordinator is not null)
        {
            _singleInstanceCoordinator.ActivationRequested -= OnActivationRequested;
        }

        if (_window is not null)
        {
            _window.Closed -= OnMainWindowClosed;
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

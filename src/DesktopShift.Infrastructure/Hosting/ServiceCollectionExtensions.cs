using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Appearance;
using DesktopShift.Infrastructure.Assignments;
using DesktopShift.Infrastructure.Configuration;
using DesktopShift.Infrastructure.Diagnostics;
using DesktopShift.Infrastructure.ManagedDesktops;
using DesktopShift.Infrastructure.Observation;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace DesktopShift.Infrastructure.Hosting;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddDesktopShiftFoundation(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IThemePreferenceService, InMemoryThemePreferenceService>();
        services.TryAddSingleton<
            IConfigurationStoragePath,
            LocalAppDataConfigurationStoragePath>();
        services.TryAddSingleton<JsonConfigurationFileStore>();
        services.TryAddSingleton<IConfigurationService, ConfigurationService>();
        services.TryAddSingleton<IFirstRunService, FirstRunService>();
        services.TryAddSingleton<
            IOverviewConfigurationProjection,
            OverviewConfigurationProjection>();
        services.TryAddSingleton<ApplicationRuntimeState>();
        services.TryAddSingleton<IApplicationRuntimeState>(
            static serviceProvider => serviceProvider.GetRequiredService<ApplicationRuntimeState>());
        services.TryAddSingleton<IAutomaticAssignmentPauseController>(
            static serviceProvider => serviceProvider.GetRequiredService<ApplicationRuntimeState>());

        // The in-memory registration is the safe default: it is what every test
        // host gets, so no test can write the machine's real login state. The
        // packaged application registers the platform implementation after this
        // call, and the later registration is the one resolved.
        services.TryAddSingleton<IStartupRegistration, InMemoryStartupRegistration>();
        services.AddHostedService<ApplicationRuntimeHostedService>();

        return services;
    }

    public static IServiceCollection AddDesktopShiftObservation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<BoundedWindowEventQueue>();
        services.TryAddSingleton<IWindowEventQueue>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<BoundedWindowEventQueue>());

        services.TryAddSingleton<BoundedWindowObservationActivityStore>();
        services.TryAddSingleton<IWindowObservationActivitySink>(
            static serviceProvider => serviceProvider.GetRequiredService<
                BoundedWindowObservationActivityStore>());
        services.TryAddSingleton<IWindowObservationActivityProjection>(
            static serviceProvider => serviceProvider.GetRequiredService<
                BoundedWindowObservationActivityStore>());

        services.TryAddSingleton<
            IWindowRuleSource,
            ActiveConfigurationWindowRuleSource>();
        services.TryAddSingleton<WindowRuleMatcher>();
        services.TryAddSingleton<WindowEventCoalescer>();
        services.TryAddSingleton<WindowObservationProcessor>();
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                WindowObservationHostedService>());

        return services;
    }

    /// <summary>
    /// Registers semantic managed-desktop reconciliation and the topology
    /// recovery that keeps it correct when the user edits their desktops.
    /// </summary>
    /// <remarks>
    /// The window pass and the activity journal are resolved optionally. A test
    /// host that wants mapping behavior alone registers this without the
    /// assignment pipeline or diagnostics, and recovery degrades to reconciling
    /// mappings rather than refusing to start.
    /// </remarks>
    public static IServiceCollection AddDesktopShiftManagedDesktopReconciliation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<
            IManagedDesktopBindingStore,
            JsonManagedDesktopBindingStore>();
        services.TryAddSingleton(
            static _ => ManagedDesktopRecreationCooldownOptions.Default);
        services.TryAddSingleton<ManagedDesktopRecreationCooldown>();
        services.TryAddSingleton<IManagedDesktopRecreationGate>(
            static serviceProvider => serviceProvider.GetRequiredService<
                ManagedDesktopRecreationCooldown>());
        services.TryAddSingleton<ManagedDesktopReconciliationService>();
        services.TryAddSingleton<IManagedDesktopReconciliationService>(
            static serviceProvider => serviceProvider.GetRequiredService<
                ManagedDesktopReconciliationService>());
        services.TryAddSingleton<IManagedDesktopTopologyRecoveryService>(
            static serviceProvider => new ManagedDesktopTopologyRecoveryService(
                serviceProvider.GetRequiredService<IManagedDesktopReconciliationService>(),
                serviceProvider.GetRequiredService<IManagedDesktopRecreationGate>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<IWindowReassignmentService>(),
                serviceProvider.GetService<IActivityJournal>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                ManagedDesktopReconciliationHostedService>());

        return services;
    }

    public static IServiceCollection AddDesktopShiftAssignments(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<BoundedWindowAssignmentActivityStore>();
        services.TryAddSingleton<BoundedForegroundSwitchSuppression>();
        services.TryAddSingleton<IForegroundSwitchSuppression>(
            static serviceProvider => serviceProvider.GetRequiredService<
                BoundedForegroundSwitchSuppression>());
        services.TryAddSingleton<BoundedNewWindowActivationTracker>();
        services.TryAddSingleton<INewWindowActivationTracker>(
            static serviceProvider => serviceProvider.GetRequiredService<
                BoundedNewWindowActivationTracker>());
        if (services.Any(
            static descriptor =>
                descriptor.ServiceType == typeof(IDesktopTopologyProvider)))
        {
            services.TryAddSingleton<DesktopSwitchCoordinator>();
            services.TryAddSingleton<IDesktopSwitchCoordinator>(
                static serviceProvider => serviceProvider.GetRequiredService<
                    DesktopSwitchCoordinator>());
        }
        services.TryAddSingleton<IWindowAssignmentActivitySink>(
            static serviceProvider => serviceProvider.GetRequiredService<
                BoundedWindowAssignmentActivityStore>());
        services.TryAddSingleton<IWindowAssignmentActivityProjection>(
            static serviceProvider => serviceProvider.GetRequiredService<
                BoundedWindowAssignmentActivityStore>());
        services.TryAddSingleton<WindowAssignmentService>();
        services.TryAddSingleton<IWindowAssignmentService>(
            static serviceProvider => serviceProvider.GetRequiredService<
                WindowAssignmentService>());
        services.TryAddSingleton<WindowReassignmentService>();
        services.TryAddSingleton<IWindowReassignmentService>(
            static serviceProvider => serviceProvider.GetRequiredService<
                WindowReassignmentService>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                WindowAssignmentReadinessHostedService>());

        return services;
    }

    /// <summary>
    /// Registers the bounded activity journal, the rolling log, and the
    /// user-initiated diagnostic actions the Activity view offers.
    /// </summary>
    /// <remarks>
    /// The in-memory log store is the safe default, for the same reason the
    /// in-memory startup registration is: it is what every test host gets, so
    /// no test can write to the machine's real log directory. The packaged
    /// application asks for the file-system store explicitly.
    /// </remarks>
    /// <param name="services">The collection to register into.</param>
    /// <param name="useFileSystemLogStore">
    /// <c>true</c> to write rolling logs to the application's own folder under
    /// local application data.
    /// </param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDesktopShiftDiagnostics(
        this IServiceCollection services,
        bool useFileSystemLogStore = false)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<
            IDiagnosticLogLocation,
            LocalAppDataDiagnosticLogLocation>();
        if (useFileSystemLogStore)
        {
            services.TryAddSingleton<
                IDiagnosticLogStore,
                FileSystemDiagnosticLogStore>();
        }
        else
        {
            services.TryAddSingleton<IDiagnosticLogStore>(
                static _ => new InMemoryDiagnosticLogStore());
        }

        services.TryAddSingleton(
            static _ => RollingDiagnosticLogOptions.Default);
        services.TryAddSingleton(
            static serviceProvider => new RollingDiagnosticLog(
                serviceProvider.GetRequiredService<IDiagnosticLogStore>(),
                serviceProvider.GetRequiredService<RollingDiagnosticLogOptions>()));
        services.TryAddSingleton<IDiagnosticLogWriter>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<RollingDiagnosticLog>());
        services.TryAddSingleton(
            static serviceProvider => new BoundedActivityJournal(
                serviceProvider.GetRequiredService<TimeProvider>()));
        services.TryAddSingleton<IActivityJournal>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<BoundedActivityJournal>());
        services.TryAddSingleton<IActivityJournalProjection>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<BoundedActivityJournal>());
        services.TryAddSingleton<ActivityDiagnosticsRecorder>();
        services.TryAddSingleton<DiagnosticsCoordinator>();
        services.TryAddSingleton<IDiagnosticsCoordinator>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<DiagnosticsCoordinator>());
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<
                IHostedService,
                DiagnosticActivityHostedService>());

        return services;
    }
}

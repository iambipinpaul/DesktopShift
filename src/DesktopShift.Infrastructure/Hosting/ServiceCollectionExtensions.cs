using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.Hotkeys;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Recovery;
using DesktopShift.Infrastructure.Appearance;
using DesktopShift.Infrastructure.Assignments;
using DesktopShift.Infrastructure.Configuration;
using DesktopShift.Infrastructure.Diagnostics;
using DesktopShift.Infrastructure.Hotkeys;
using DesktopShift.Infrastructure.ManagedDesktops;
using DesktopShift.Infrastructure.Observation;
using DesktopShift.Infrastructure.Recovery;
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
        services.TryAddSingleton<
            IGlobalHotkeyRegistrar,
            InMemoryGlobalHotkeyRegistrar>();
        services.TryAddSingleton<GlobalHotkeyCoordinator>();
        services.TryAddSingleton<IGlobalHotkeyCoordinator>(
            static serviceProvider =>
                serviceProvider.GetRequiredService<GlobalHotkeyCoordinator>());

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
        services.TryAddSingleton(
            static _ => ManagedDesktopNamingOptions.Default);
        services.TryAddSingleton<
            IManagedDesktopNamingService,
            ManagedDesktopNamingService>();
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

    /// <summary>
    /// Registers the recovery pass that restores automatic assignment after
    /// Explorer restarts, the machine resumes, or the displays change.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Call this after
    /// <see cref="AddDesktopShiftManagedDesktopReconciliation"/>. A recovery
    /// pass ends by driving the topology recovery service, so the reconciliation
    /// registration has to be in place for this one to resolve.
    /// </para>
    /// <para>
    /// Neither <see cref="INativeRegistrationSet"/> nor
    /// <see cref="IShellLifecycleSignalSource"/> is registered here, and neither
    /// gets a default. Both are platform types: one owns real WinEvent hooks and
    /// a real topology notification registration, the other listens for real
    /// Windows messages. A default would mean every test host quietly hooking
    /// the machine it runs on. The packaged application supplies them.
    /// </para>
    /// <para>
    /// The signal source is therefore resolved with <c>GetService</c> rather
    /// than <c>GetRequiredService</c>, which is why the hosted service takes it
    /// as nullable. A host that registers no platform source starts cleanly and
    /// simply never recovers — nothing there can raise a lifecycle signal, so
    /// there is nothing to answer, and turning "no platform" into "will not
    /// start" would be the worse failure. The activity journal is optional for
    /// the same reason it is optional in reconciliation: a host without
    /// diagnostics still recovers, it just does so unrecorded.
    /// </para>
    /// <para>
    /// <see cref="INativeRegistrationSet"/> stays required. A recovery service
    /// without one could neither check nor retake anything, so it would be a
    /// recovery that silently does not recover, and that is worth failing over.
    /// </para>
    /// </remarks>
    /// <param name="services">The collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDesktopShiftShellRecovery(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IShellRecoveryService>(
            static serviceProvider => new ShellRecoveryService(
                serviceProvider.GetRequiredService<INativeRegistrationSet>(),
                serviceProvider.GetRequiredService<ICompatibilityCoordinator>(),
                serviceProvider.GetRequiredService<
                    IManagedDesktopTopologyRecoveryService>(),
                serviceProvider.GetRequiredService<TimeProvider>(),
                serviceProvider.GetService<IActivityJournal>()));
        services.TryAddEnumerable(
            ServiceDescriptor.Singleton<IHostedService, ShellRecoveryHostedService>(
                static serviceProvider => new ShellRecoveryHostedService(
                    serviceProvider.GetService<IShellLifecycleSignalSource>(),
                    serviceProvider.GetRequiredService<IShellRecoveryService>(),
                    serviceProvider.GetService<IActivityJournal>(),
                    serviceProvider.GetService<IDiagnosticLogWriter>())));

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
        if (services.Any(
            static descriptor =>
                descriptor.ServiceType == typeof(IForegroundWindowProvider)))
        {
            services.TryAddSingleton<ForegroundWindowReassignment>();
            services.TryAddSingleton<IForegroundWindowReassignment>(
                static serviceProvider => serviceProvider.GetRequiredService<
                    ForegroundWindowReassignment>());
        }
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

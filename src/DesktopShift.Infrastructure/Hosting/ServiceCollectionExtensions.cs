using DesktopShift.Core.Appearance;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Infrastructure.Appearance;
using DesktopShift.Infrastructure.Assignments;
using DesktopShift.Infrastructure.Configuration;
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

    public static IServiceCollection AddDesktopShiftManagedDesktopReconciliation(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<
            IManagedDesktopBindingStore,
            JsonManagedDesktopBindingStore>();
        services.TryAddSingleton<ManagedDesktopReconciliationService>();
        services.TryAddSingleton<IManagedDesktopReconciliationService>(
            static serviceProvider => serviceProvider.GetRequiredService<
                ManagedDesktopReconciliationService>());
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
}

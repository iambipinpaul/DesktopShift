using DesktopShift.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace DesktopShift.Infrastructure.Configuration;

/// <summary>
/// Registers configuration export and import.
/// </summary>
/// <remarks>
/// Kept out of the foundation registration because exchange is a user-initiated
/// feature, not something the application needs to run. A host that only wants
/// configuration loaded — a test host, or a future headless mode — should not
/// have to take a dependency on the profile directory of whoever is signed in.
/// </remarks>
public static class ConfigurationExchangeServiceCollectionExtensions
{
    /// <summary>
    /// Adds the exchange service and the profile-directory seam it needs.
    /// </summary>
    /// <remarks>
    /// Every registration is a try-add, so a caller that registered its own
    /// <see cref="IUserProfilePath"/> beforehand keeps it. That is how an export
    /// is proved to rewrite profile paths without the test depending on the
    /// machine it runs on.
    /// </remarks>
    /// <param name="services">The collection to register into.</param>
    /// <returns>The same collection, for chaining.</returns>
    public static IServiceCollection AddDesktopShiftConfigurationExchange(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton(TimeProvider.System);
        services.TryAddSingleton<IUserProfilePath, EnvironmentUserProfilePath>();
        services.TryAddSingleton<
            IConfigurationExchangeService,
            ConfigurationExchangeService>();

        return services;
    }
}

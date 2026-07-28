using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal sealed class OverviewConfigurationProjection : IOverviewConfigurationProjection
{
    private readonly IConfigurationService _configurationService;

    public OverviewConfigurationProjection(
        IConfigurationService configurationService)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        _configurationService = configurationService;
    }

    public ConfigurationOverview GetSnapshot()
    {
        ConfigurationDocument? active =
            _configurationService.CurrentState.Active;

        return active is null
            ? new ConfigurationOverview(0, 0)
            : new ConfigurationOverview(
                active.ManagedDesktops.Length,
                active.ApplicationRules.Count(static rule => rule.IsEnabled));
    }
}

using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Infrastructure.Observation;

internal sealed class ActiveConfigurationWindowRuleSource(
    IConfigurationService configurationService) : IWindowRuleSource
{
    public IReadOnlyList<WindowObservationRule> GetRules()
    {
        ConfigurationDocument? active =
            configurationService.CurrentState.Active;
        if (active is null)
        {
            return [];
        }

        return new ConfigurationWindowRuleSource(() => active).GetRules();
    }
}

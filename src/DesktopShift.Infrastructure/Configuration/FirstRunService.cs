using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal sealed class FirstRunService : IFirstRunService
{
    private readonly IConfigurationService _configurationService;

    public FirstRunService(IConfigurationService configurationService)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        _configurationService = configurationService;
    }

    public async Task<FirstRunState> GetStateAsync(
        CancellationToken cancellationToken = default)
    {
        ConfigurationState state = await _configurationService.LoadAsync(
            cancellationToken);
        return ToFirstRunState(state);
    }

    public async Task<FirstRunCompletionResult> CompleteAsync(
        ConfigurationDocument candidate,
        bool startWithWindows,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        ConfigurationDocument candidateWithStartupChoice =
            candidate with
            {
                Behavior = candidate.Behavior with
                {
                    StartWithWindows = startWithWindows,
                },
            };

        ConfigurationSaveResult result =
            await _configurationService.SaveCandidateAsync(
                candidateWithStartupChoice,
                cancellationToken);
        FirstRunState state = ToFirstRunState(result.State);

        return new FirstRunCompletionResult(
            result.Accepted,
            state,
            result.State.Issues);
    }

    private static FirstRunState ToFirstRunState(ConfigurationState state)
    {
        return new FirstRunState(
            IsCompleted: !state.IsFirstRun,
            state.Candidate,
            state.Candidate.Behavior.StartWithWindows,
            state.Issues);
    }
}

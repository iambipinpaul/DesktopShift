using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;

namespace DesktopShift.App.ViewModels;

public sealed record BehaviorSettingsPresentation(
    bool StartWithWindows,
    bool StartMinimized,
    bool CloseToTray,
    string StartupStateDescription,
    bool IsStartupToggleEnabled);

/// <summary>
/// Reads and applies the quiet-utility behavior settings.
/// </summary>
/// <remarks>
/// Start-with-Windows is the one setting that lives in two places: the
/// configuration document records what the user asked for, and Windows records
/// what is actually registered. The two can disagree — a user can turn the
/// entry off in Task Manager, and no application can turn it back on — so the
/// registration is always read back after a write and the presentation reports
/// the registration, not the request.
/// </remarks>
public sealed class BehaviorSettingsCommand
{
    private readonly IConfigurationService _configurationService;
    private readonly IStartupRegistration _startupRegistration;

    public BehaviorSettingsCommand(
        IConfigurationService configurationService,
        IStartupRegistration startupRegistration)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(startupRegistration);

        _configurationService = configurationService;
        _startupRegistration = startupRegistration;
    }

    public async Task<BehaviorSettingsPresentation> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ConfigurationState state = await _configurationService
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        StartupRegistrationState startupState = await _startupRegistration
            .GetStateAsync(cancellationToken)
            .ConfigureAwait(false);

        return Present(ResolveBehavior(state), startupState);
    }

    public async Task<BehaviorSettingsPresentation> ApplyAsync(
        BehaviorSettings behavior,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        ConfigurationState state = _configurationService.CurrentState;
        ConfigurationDocument source = state.Active ?? state.Candidate;
        ConfigurationSaveResult result = await _configurationService
            .SaveCandidateAsync(source with { Behavior = behavior }, cancellationToken)
            .ConfigureAwait(false);

        StartupRegistrationState startupState = await _startupRegistration
            .SetEnabledAsync(behavior.StartWithWindows, cancellationToken)
            .ConfigureAwait(false);

        return Present(ResolveBehavior(result.State), startupState);
    }

    /// <summary>
    /// The behavior the shell should act on.
    /// </summary>
    /// <remarks>
    /// The accepted document wins. An unaccepted candidate has not passed
    /// validation, and letting it decide whether closing the window exits the
    /// process would act on settings the user never successfully saved.
    /// </remarks>
    public static BehaviorSettings ResolveBehavior(ConfigurationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return (state.Active ?? state.Candidate).Behavior;
    }

    private static BehaviorSettingsPresentation Present(
        BehaviorSettings behavior,
        StartupRegistrationState startupState) =>
        new(
            startupState.IsEnabled(),
            behavior.StartMinimized,
            behavior.CloseToTray,
            startupState.Describe(),
            !startupState.IsLocked());
}

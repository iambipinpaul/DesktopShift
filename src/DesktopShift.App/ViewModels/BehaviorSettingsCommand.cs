using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;

namespace DesktopShift.App.ViewModels;

public sealed record BehaviorSettingsPresentation(
    BehaviorSettings Behavior,
    BehaviorSettings ActiveBehavior,
    bool Accepted,
    IReadOnlyList<ConfigurationValidationIssue> Issues,
    string StartupStateDescription,
    bool IsStartupToggleEnabled)
{
    public bool StartWithWindows => Behavior.StartWithWindows;

    public bool StartMinimized => Behavior.StartMinimized;

    public bool CloseToTray => Behavior.CloseToTray;

    public bool RecordLocalActivity => Behavior.RecordLocalActivity;
}

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

        return Present(
            state.Candidate.Behavior ?? ResolveBehavior(state),
            ResolveBehavior(state),
            state.Issues.IsEmpty,
            state.Issues,
            startupState,
            startupFailure: null);
    }

    public async Task<BehaviorSettingsPresentation> ApplyAsync(
        BehaviorSettings behavior,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(behavior);

        ConfigurationState state = _configurationService.CurrentState;
        // Settings edits continue from the retained candidate. Starting from
        // the active document would discard every invalid imported entry as
        // soon as the user corrected the first field.
        ConfigurationDocument source = state.Candidate;
        ConfigurationSaveResult result = await _configurationService
            .SaveCandidateAsync(source with { Behavior = behavior }, cancellationToken)
            .ConfigureAwait(false);

        StartupRegistrationState startupState;
        string? startupFailure = null;
        try
        {
            startupState = result.Accepted
                ? await _startupRegistration
                    .SetEnabledAsync(behavior.StartWithWindows, cancellationToken)
                    .ConfigureAwait(false)
                : await _startupRegistration
                    .GetStateAsync(cancellationToken)
                    .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception exception)
        {
            // Configuration is already accepted at this point. Keep that fact
            // visible and let the other live settings apply; startup
            // registration is an external Windows side effect that can be
            // retried independently.
            startupState = StartupRegistrationState.Unavailable;
            startupFailure = exception.Message;
        }

        return Present(
            result.State.Candidate.Behavior ?? ResolveBehavior(result.State),
            ResolveBehavior(result.State),
            result.Accepted,
            result.State.Issues,
            startupState,
            startupFailure);
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
        return state.Active?.Behavior ??
            state.Candidate.Behavior ??
            ConfigurationDefaults.Create().Behavior;
    }

    private static BehaviorSettingsPresentation Present(
        BehaviorSettings candidate,
        BehaviorSettings active,
        bool accepted,
        IReadOnlyList<ConfigurationValidationIssue> issues,
        StartupRegistrationState startupState,
        string? startupFailure) =>
        new(
            candidate with { StartWithWindows = startupState.IsEnabled() },
            active,
            accepted,
            issues,
            startupFailure is null
                ? startupState.Describe()
                : $"Configuration was saved, but Windows startup registration failed: {startupFailure}",
            !startupState.IsLocked());
}

using DesktopShift.Core.Configuration;

namespace DesktopShift.App.ViewModels;

/// <summary>The tiling settings shown after a load or save.</summary>
public sealed record TilingSettingsPresentation(
    TilingSettings Settings,
    TilingSettings ActiveSettings,
    bool Accepted,
    IReadOnlyList<ConfigurationValidationIssue> Issues);

/// <summary>Reads and persists the native window-tiling settings.</summary>
public sealed class TilingSettingsCommand
{
    private readonly IConfigurationService configurationService;

    public TilingSettingsCommand(IConfigurationService configurationService)
    {
        this.configurationService = configurationService ??
            throw new ArgumentNullException(nameof(configurationService));
    }

    public async Task<TilingSettingsPresentation> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        ConfigurationState state = await configurationService
            .LoadAsync(cancellationToken)
            .ConfigureAwait(false);
        return Present(state, state.Issues.IsEmpty);
    }

    public async Task<TilingSettingsPresentation> ApplyAsync(
        TilingSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);

        ConfigurationDocument source = configurationService.CurrentState.Candidate;
        ConfigurationSaveResult result = await configurationService
            .SaveCandidateAsync(source with { Tiling = settings }, cancellationToken)
            .ConfigureAwait(false);
        return Present(result.State, result.Accepted);
    }

    /// <summary>Gets the settings that the running tiler is allowed to use.</summary>
    public static TilingSettings ResolveActive(ConfigurationState state)
    {
        ArgumentNullException.ThrowIfNull(state);
        return (state.Active ?? state.Candidate).Tiling;
    }

    /// <summary>
    /// Changes the on/off policy for one stable managed desktop key.
    /// </summary>
    public static TilingSettings SetManagedDesktopEnabled(
        TilingSettings settings,
        string semanticKey,
        bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(semanticKey);

        HashSet<string> disabled = settings.DisabledManagedDesktopKeys
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (isEnabled)
        {
            _ = disabled.Remove(semanticKey);
        }
        else
        {
            _ = disabled.Add(semanticKey.Trim());
        }

        return settings with
        {
            DisabledManagedDesktopKeys =
            [.. disabled.OrderBy(static key => key, StringComparer.OrdinalIgnoreCase)],
        };
    }

    private static TilingSettingsPresentation Present(
        ConfigurationState state,
        bool accepted) =>
        new(
            state.Candidate.Tiling,
            ResolveActive(state),
            accepted,
            state.Issues);
}

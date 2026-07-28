using System.Collections.Immutable;
using System.Text.Json;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal sealed class ConfigurationService : IConfigurationService
{
    private readonly JsonConfigurationFileStore _fileStore;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConfigurationState _currentState;

    public ConfigurationService(
        JsonConfigurationFileStore fileStore,
        TimeProvider timeProvider)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _fileStore = fileStore;
        _timeProvider = timeProvider;
        _currentState = CreateInitialState();
    }

    public ConfigurationState CurrentState => Volatile.Read(ref _currentState);

    public async Task<ConfigurationState> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken);
        try
        {
            ConfigurationDocument? candidate = null;
            ConfigurationDocument? active = null;
            ImmutableArray<ConfigurationValidationIssue>.Builder loadIssues =
                ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();

            try
            {
                candidate = await _fileStore.TryReadAsync(
                    _fileStore.CandidatePath,
                    cancellationToken);
            }
            catch (JsonException exception)
            {
                loadIssues.Add(UnreadableIssue(
                    ConfigurationValidationCode.CandidateUnreadable,
                    "$",
                    $"The candidate configuration could not be read: {exception.Message}"));
            }

            try
            {
                active = await ReadValidSnapshotAsync(
                    _fileStore.ActivePath,
                    cancellationToken);
            }
            catch (JsonException exception)
            {
                loadIssues.Add(UnreadableIssue(
                    ConfigurationValidationCode.ActiveSnapshotUnreadable,
                    "$",
                    $"The active configuration could not be read: {exception.Message}"));
            }

            if (active is null)
            {
                try
                {
                    active = await ReadValidSnapshotAsync(
                        _fileStore.LastValidPath,
                        cancellationToken);
                    if (active is not null)
                    {
                        loadIssues.Add(new ConfigurationValidationIssue(
                            ConfigurationValidationCode.RecoveredLastValidSnapshot,
                            "The active configuration was recovered from the last-valid snapshot.",
                            "$",
                            ConfigurationEntryKind.Document));
                    }
                }
                catch (JsonException exception)
                {
                    loadIssues.Add(UnreadableIssue(
                        ConfigurationValidationCode.ActiveSnapshotUnreadable,
                        "$",
                        $"The last-valid configuration could not be read: {exception.Message}"));
                }
            }

            candidate ??= active ?? ConfigurationDefaults.Create();
            ImmutableArray<ConfigurationValidationIssue> candidateIssues =
                ConfigurationValidator.Validate(candidate);
            loadIssues.AddRange(candidateIssues);

            ConfigurationState state = new(
                candidate,
                active,
                loadIssues.ToImmutable(),
                _timeProvider.GetUtcNow());
            Volatile.Write(ref _currentState, state);
            return state;
        }
        finally
        {
            _gate.Release();
        }
    }

    public async Task<ConfigurationSaveResult> SaveCandidateAsync(
        ConfigurationDocument candidate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        await _gate.WaitAsync(cancellationToken);
        try
        {
            ImmutableArray<ConfigurationValidationIssue> issues =
                ConfigurationValidator.Validate(candidate);

            await _fileStore.WriteCandidateAsync(candidate, cancellationToken);

            ConfigurationDocument? active = CurrentState.Active;
            bool accepted = issues.IsEmpty;
            if (accepted)
            {
                await _fileStore.WriteActiveAsync(candidate, cancellationToken);
                active = candidate;
            }

            ConfigurationState state = new(
                candidate,
                active,
                issues,
                _timeProvider.GetUtcNow());
            Volatile.Write(ref _currentState, state);

            return new ConfigurationSaveResult(accepted, state);
        }
        finally
        {
            _gate.Release();
        }
    }

    private async Task<ConfigurationDocument?> ReadValidSnapshotAsync(
        string path,
        CancellationToken cancellationToken)
    {
        ConfigurationDocument? document = await _fileStore.TryReadAsync(
            path,
            cancellationToken);
        if (document is null)
        {
            return null;
        }

        return ConfigurationValidator.Validate(document).IsEmpty
            ? document
            : null;
    }

    private ConfigurationState CreateInitialState()
    {
        return new ConfigurationState(
            ConfigurationDefaults.Create(),
            Active: null,
            [],
            _timeProvider.GetUtcNow());
    }

    private static ConfigurationValidationIssue UnreadableIssue(
        ConfigurationValidationCode code,
        string path,
        string message)
    {
        return new ConfigurationValidationIssue(
            code,
            message,
            path,
            ConfigurationEntryKind.Document);
    }
}

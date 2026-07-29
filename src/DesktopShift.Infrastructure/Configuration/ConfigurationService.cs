using System.Collections.Immutable;
using System.Text.Json;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal sealed class ConfigurationService : IConfigurationService
{
    private readonly JsonConfigurationFileStore _fileStore;
    private readonly TimeProvider _timeProvider;
    private readonly ConfigurationSchema _schema;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private ConfigurationState _currentState;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="fileStore">The three-file store on disk.</param>
    /// <param name="timeProvider">The clock every observation is stamped with.</param>
    /// <param name="schema">
    /// The schema every document is read against. Optional, defaulting to
    /// <see cref="ConfigurationSchema.Current"/>, so the shipped behavior needs
    /// no registration while a test can still register its own schema and drive
    /// the real migration pipeline.
    /// </param>
    public ConfigurationService(
        JsonConfigurationFileStore fileStore,
        TimeProvider timeProvider,
        ConfigurationSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(fileStore);
        ArgumentNullException.ThrowIfNull(timeProvider);

        _fileStore = fileStore;
        _timeProvider = timeProvider;
        _schema = schema ?? ConfigurationSchema.Current;
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
                // Migration runs inside the read, so it happens on this path as
                // well as the two snapshot paths below. A migration that ran only
                // on import would leave the one document nobody chose to import —
                // the one already on disk — unreadable after an upgrade.
                ConfigurationReadResult read = await _fileStore.TryReadAsync(
                    _fileStore.CandidatePath,
                    cancellationToken);
                loadIssues.AddRange(read.Migration.Issues);
                candidate = read.Document;
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
                    loadIssues,
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
                        loadIssues,
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
                ConfigurationValidator.Validate(candidate, _schema.CurrentVersion);
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
                ConfigurationValidator.Validate(candidate, _schema.CurrentVersion);

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
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        CancellationToken cancellationToken)
    {
        ConfigurationReadResult read = await _fileStore.TryReadAsync(
            path,
            cancellationToken);
        issues.AddRange(read.Migration.Issues);
        if (read.Document is null)
        {
            return null;
        }

        return ConfigurationValidator
            .Validate(read.Document, _schema.CurrentVersion)
            .IsEmpty
            ? read.Document
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

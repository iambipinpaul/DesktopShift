using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

public interface IConfigurationService
{
    ConfigurationState CurrentState { get; }

    Task<ConfigurationState> LoadAsync(CancellationToken cancellationToken = default);

    Task<ConfigurationSaveResult> SaveCandidateAsync(
        ConfigurationDocument candidate,
        CancellationToken cancellationToken = default);
}

public interface IFirstRunService
{
    Task<FirstRunState> GetStateAsync(CancellationToken cancellationToken = default);

    Task<FirstRunCompletionResult> CompleteAsync(
        ConfigurationDocument candidate,
        bool startWithWindows,
        CancellationToken cancellationToken = default);
}

public interface IOverviewConfigurationProjection
{
    ConfigurationOverview GetSnapshot();
}

public interface IConfigurationStoragePath
{
    string DirectoryPath { get; }
}

public sealed record ConfigurationState(
    ConfigurationDocument Candidate,
    ConfigurationDocument? Active,
    ImmutableArray<ConfigurationValidationIssue> Issues,
    DateTimeOffset ObservedAtUtc)
{
    public bool IsFirstRun => Active is null;
}

public sealed record ConfigurationSaveResult(
    bool Accepted,
    ConfigurationState State);

public sealed record FirstRunState(
    bool IsCompleted,
    ConfigurationDocument Candidate,
    bool StartWithWindows,
    ImmutableArray<ConfigurationValidationIssue> Issues);

public sealed record FirstRunCompletionResult(
    bool Accepted,
    FirstRunState State,
    ImmutableArray<ConfigurationValidationIssue> Issues);

public sealed record ConfigurationOverview(
    int ManagedDesktopCount,
    int EnabledRuleCount);

public sealed record ConfigurationValidationIssue(
    ConfigurationValidationCode Code,
    string Message,
    string Path,
    ConfigurationEntryKind EntryKind,
    string? EntryId = null);

public enum ConfigurationEntryKind
{
    Document,
    ManagedDesktop,
    ApplicationRule,
    Behavior,
}

public enum ConfigurationValidationCode
{
    UnsupportedSchemaVersion,
    RequiredValue,
    DuplicateDesktopSemanticKey,
    DuplicateRuleId,
    UnknownDesktopReference,
    MissingProcessName,
    MissingTrigger,
    CandidateUnreadable,
    ActiveSnapshotUnreadable,
    RecoveredLastValidSnapshot,
}

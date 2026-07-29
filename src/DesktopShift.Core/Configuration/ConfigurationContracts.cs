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

    /// <summary>
    /// A global shortcut binding. Separate from <see cref="Behavior"/> because
    /// the Settings page shows a hotkey problem against the row that carries it,
    /// and a whole-document kind could not say which row.
    /// </summary>
    Hotkey,
}

public enum ConfigurationValidationCode
{
    UnsupportedSchemaVersion,
    RequiredValue,
    DuplicateDesktopSemanticKey,
    DuplicateRuleId,
    UnknownDesktopReference,
    MissingApplicationIdentity,
    MissingTrigger,
    CandidateUnreadable,
    ActiveSnapshotUnreadable,
    RecoveredLastValidSnapshot,

    /// <summary>
    /// An identity is shaped so that no running window could ever report it —
    /// a process name carrying a directory separator, a relative executable
    /// path, a package name without its publisher half. The matcher compares
    /// every identity verbatim, so such a value is not a narrow rule, it is a
    /// rule that can never match.
    /// </summary>
    InvalidIdentityPattern,

    /// <summary>
    /// A switch policy cannot be reached by the triggers the rule declares. A
    /// desktop switch is only ever considered on a foreground activation, so a
    /// rule that does not observe one can never switch, whatever its policy says.
    /// </summary>
    UnreachableSwitchPolicy,

    /// <summary>
    /// A document declares a schema version this build knows about, but no
    /// ordered chain of migrations reaches the current version from it. The
    /// document is reported rather than guessed at: a half-migrated document
    /// would be worse than one that plainly refused to load.
    /// </summary>
    MigrationUnavailable,

    /// <summary>
    /// A migration ran and threw. The document is left exactly as it arrived and
    /// nothing is replaced.
    /// </summary>
    MigrationFailed,

    /// <summary>
    /// An imported file could not be read as a configuration document at all —
    /// malformed JSON, or JSON that is not an object.
    /// </summary>
    ImportDocumentUnreadable,

    /// <summary>Two enabled shortcuts claim the same key combination.</summary>
    HotkeyConflict,

    /// <summary>
    /// A shortcut is turned on with no modifier. Windows would register a bare
    /// key and take it away from every application on the machine.
    /// </summary>
    HotkeyMissingModifier,

    /// <summary>A shortcut is turned on with no key chosen.</summary>
    HotkeyUnassigned,

    /// <summary>
    /// Windows refused to hand a combination over, almost always because another
    /// application already owns it. Reported after the fact by the registrar,
    /// because nothing but Windows knows the answer.
    /// </summary>
    HotkeyRegistrationFailed,

    /// <summary>The same shortcut action is bound more than once.</summary>
    DuplicateHotkeyAction,

    /// <summary>
    /// A hand-edited shortcut contains an action, modifier bit, or key value
    /// that this build does not understand.
    /// </summary>
    InvalidHotkeyValue,

    /// <summary>
    /// Global shortcuts are enabled, but one of the four supported actions has
    /// no binding in the document.
    /// </summary>
    MissingHotkeyAction,

    /// <summary>
    /// The desktop switching shortcuts carry no modifier. Windows would register
    /// ten bare digits and take the number row away from every application on
    /// the machine.
    /// </summary>
    DesktopSwitchShortcutMissingModifier,

    /// <summary>
    /// Windows refused one or more desktop switching combinations. Reported
    /// after the fact by the registrar, because nothing but Windows knows
    /// whether the shell claimed a combination first.
    /// </summary>
    DesktopSwitchShortcutRegistrationFailed,

    /// <summary>
    /// A hand-edited document names a desktop switching profile or modifier bit
    /// this build does not understand.
    /// </summary>
    InvalidDesktopSwitchShortcutValue,
}

/// <summary>
/// The schema this build reads, and the ordered migrations that reach it.
/// </summary>
/// <remarks>
/// <para>
/// Injected rather than read from a constant so the migration pipeline can be
/// exercised for real. With one shipped schema version there is nothing to
/// migrate, and a pipeline that is never run is a pipeline that does not work;
/// tests supply their own version and migrations and drive the same code the
/// application does.
/// </para>
/// <para>
/// <see cref="Current"/> is what the application uses, and the constructor
/// parameter that carries this defaults to it, so no registration is required
/// for the shipped behavior.
/// </para>
/// </remarks>
/// <param name="CurrentVersion">The version a document must reach to be usable.</param>
/// <param name="Migrations">
/// Every step this build can apply. Order in the collection is irrelevant — the
/// pipeline chains them by version.
/// </param>
public sealed record ConfigurationSchema(
    int CurrentVersion,
    ImmutableArray<IConfigurationMigration> Migrations)
{
    /// <summary>
    /// The shipped schema. There are no migrations: every member added since
    /// version 1 trails the required ones and reads as its default when absent,
    /// so a version 1 document on disk is already a current document.
    /// </summary>
    public static ConfigurationSchema Current { get; } =
        new(ConfigurationDefaults.CurrentSchemaVersion, []);

    /// <summary>Migrations, normalized away from a default array.</summary>
    public ImmutableArray<IConfigurationMigration> Migrations { get; init; } =
        Migrations.IsDefault ? [] : Migrations;
}

/// <summary>
/// Moves configuration between DesktopShift and a file the user owns.
/// </summary>
/// <remarks>
/// Import deliberately goes through the same candidate/active split as every
/// other edit. An imported document that fails validation is kept as the
/// candidate so every entry stays on screen to be corrected, and the last valid
/// snapshot goes on running the application in the meantime. Nothing about
/// import is allowed to be a second, quieter way of replacing configuration.
/// </remarks>
public interface IConfigurationExchangeService
{
    /// <summary>
    /// Writes the active configuration — or the candidate, when nothing has
    /// been accepted yet — as portable JSON.
    /// </summary>
    /// <param name="destination">The writable stream to fill.</param>
    /// <param name="cancellationToken">Cancels the export.</param>
    /// <returns>What was written.</returns>
    Task<ConfigurationExportResult> ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Reads a file, migrates it, and offers it as the candidate.
    /// </summary>
    /// <param name="source">The readable stream to import.</param>
    /// <param name="cancellationToken">Cancels the import.</param>
    /// <returns>
    /// What happened, including every issue the candidate still carries.
    /// </returns>
    Task<ConfigurationImportResult> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default);
}

/// <summary>What an export wrote.</summary>
/// <param name="SchemaVersion">The version stamped into the file.</param>
/// <param name="ManagedDesktopCount">How many desktops travelled.</param>
/// <param name="ApplicationRuleCount">How many rules travelled.</param>
/// <param name="HotkeyCount">How many shortcut bindings travelled.</param>
/// <param name="ByteCount">The size of the written document.</param>
public sealed record ConfigurationExportResult(
    int SchemaVersion,
    int ManagedDesktopCount,
    int ApplicationRuleCount,
    int HotkeyCount,
    long ByteCount);

/// <summary>What an import produced.</summary>
/// <param name="Accepted">
/// Whether the imported document became the active configuration. When false
/// the previous active snapshot is still the one running the application.
/// </param>
/// <param name="State">
/// The configuration state after the import, or null when the file could not be
/// read at all and nothing was offered as a candidate.
/// </param>
/// <param name="Issues">
/// Everything wrong with the imported document. Every entry that produced one is
/// still present in <paramref name="State"/> so it can be corrected in place.
/// </param>
/// <param name="SourceSchemaVersion">The version the file declared.</param>
/// <param name="AppliedMigrations">
/// The migration steps that ran, in the order they ran, so a report can say what
/// was changed on the way in.
/// </param>
public sealed record ConfigurationImportResult(
    bool Accepted,
    ConfigurationState? State,
    ImmutableArray<ConfigurationValidationIssue> Issues,
    int SourceSchemaVersion,
    ImmutableArray<ConfigurationMigrationStep> AppliedMigrations)
{
    /// <summary>Issues, normalized away from a default array.</summary>
    public ImmutableArray<ConfigurationValidationIssue> Issues { get; init; } =
        Issues.IsDefault ? [] : Issues;

    /// <summary>Applied steps, normalized away from a default array.</summary>
    public ImmutableArray<ConfigurationMigrationStep> AppliedMigrations { get; init; } =
        AppliedMigrations.IsDefault ? [] : AppliedMigrations;
}

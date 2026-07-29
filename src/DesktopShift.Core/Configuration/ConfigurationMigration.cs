using System.Collections.Immutable;
using System.Text.Json.Nodes;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// One step from one schema version to the next.
/// </summary>
/// <remarks>
/// <para>
/// A migration works on the raw JSON, not on
/// <see cref="ConfigurationDocument"/>. That is the whole point: the shape a
/// migration has to read is the <em>old</em> shape, and the typed record only
/// ever describes the current one. A pipeline that took typed documents could
/// only migrate documents that already deserialize, which is precisely the set
/// that needs no migration.
/// </para>
/// <para>
/// A migration never has to stamp the new version itself — the pipeline does
/// that after each step, so a step cannot leave the document claiming a version
/// it no longer has.
/// </para>
/// </remarks>
public interface IConfigurationMigration
{
    /// <summary>The version this step reads.</summary>
    int FromVersion { get; }

    /// <summary>The version this step produces. Must exceed <see cref="FromVersion"/>.</summary>
    int ToVersion { get; }

    /// <summary>
    /// What the step changes, phrased for a user reading an import report.
    /// </summary>
    string Description { get; }

    /// <summary>
    /// Rewrites the document. The input may be mutated and returned, or a new
    /// object returned in its place.
    /// </summary>
    JsonObject Apply(JsonObject document);
}

/// <summary>One migration that actually ran.</summary>
public sealed record ConfigurationMigrationStep(
    int FromVersion,
    int ToVersion,
    string Description)
{
    /// <summary>The step as an import report shows it.</summary>
    public string Describe() =>
        $"Schema {FromVersion} → {ToVersion}: {Description}";
}

/// <summary>
/// What the pipeline made of a document.
/// </summary>
/// <param name="Document">
/// The migrated document, or null when it could not be brought to the current
/// version. Null is load-bearing: a document that could not be migrated must
/// never reach validation, because validation would report the symptoms of a
/// half-understood file instead of the one real problem.
/// </param>
/// <param name="SourceVersion">
/// The version the document declared, or zero when it declared none.
/// </param>
/// <param name="AppliedSteps">The steps that ran, in the order they ran.</param>
/// <param name="Issues">Why it stopped, when it stopped.</param>
public sealed record ConfigurationMigrationResult(
    JsonObject? Document,
    int SourceVersion,
    ImmutableArray<ConfigurationMigrationStep> AppliedSteps,
    ImmutableArray<ConfigurationValidationIssue> Issues)
{
    /// <summary>Whether a usable current-version document came out.</summary>
    public bool Succeeded => Document is not null;

    /// <summary>Applied steps, normalized away from a default array.</summary>
    public ImmutableArray<ConfigurationMigrationStep> AppliedSteps { get; init; } =
        AppliedSteps.IsDefault ? [] : AppliedSteps;

    /// <summary>Issues, normalized away from a default array.</summary>
    public ImmutableArray<ConfigurationValidationIssue> Issues { get; init; } =
        Issues.IsDefault ? [] : Issues;
}

/// <summary>
/// Brings a document up to the current schema version before anything else
/// looks at it.
/// </summary>
/// <remarks>
/// <para>
/// Runs ahead of validation and ahead of every configuration replacement, on
/// every path that reads a document: the candidate file, the active snapshot,
/// the last-valid snapshot, and an imported file. A migration that ran only on
/// import would leave the one document nobody chose to import — the one already
/// on disk — permanently unreadable after an upgrade.
/// </para>
/// <para>
/// Steps are chained by version, not by their order in the collection, so a
/// registration order mistake cannot silently skip a step.
/// </para>
/// </remarks>
public static class ConfigurationMigrationPipeline
{
    /// <summary>The property every document states its version in.</summary>
    public const string SchemaVersionPropertyName = "schemaVersion";

    private const string SchemaVersionPath = "$.schemaVersion";

    /// <summary>
    /// Reads the declared schema version.
    /// </summary>
    /// <param name="document">The raw document.</param>
    /// <returns>The declared version, or zero when it is absent or not a number.</returns>
    public static int ReadSchemaVersion(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (!document.TryGetPropertyValue(SchemaVersionPropertyName, out JsonNode? node) ||
            node is not JsonValue value ||
            !value.TryGetValue(out int version))
        {
            return 0;
        }

        return version;
    }

    /// <summary>
    /// Applies every step needed to reach <see cref="ConfigurationSchema.CurrentVersion"/>.
    /// </summary>
    /// <param name="document">The raw document, as read.</param>
    /// <param name="schema">The version to reach and the steps available.</param>
    /// <returns>The migrated document, or the reason it could not be produced.</returns>
    /// <exception cref="ArgumentException">
    /// Two migrations claim the same <see cref="IConfigurationMigration.FromVersion"/>,
    /// or a migration does not move the version forward. Both are build-time
    /// mistakes in the migration set, not anything a document can cause.
    /// </exception>
    public static ConfigurationMigrationResult Migrate(
        JsonObject document,
        ConfigurationSchema schema)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(schema);

        Dictionary<int, IConfigurationMigration> steps = IndexSteps(schema);
        int sourceVersion = ReadSchemaVersion(document);

        if (sourceVersion <= 0)
        {
            return Failed(
                sourceVersion,
                ConfigurationValidationCode.UnsupportedSchemaVersion,
                $"The document does not declare a usable '{SchemaVersionPropertyName}', so there is no way to know which shape it is in.");
        }

        if (sourceVersion > schema.CurrentVersion)
        {
            // A newer document cannot be read backwards. Saying so is the only
            // honest answer; discarding the unknown parts would quietly destroy
            // configuration written by a build the user still has.
            return Failed(
                sourceVersion,
                ConfigurationValidationCode.UnsupportedSchemaVersion,
                $"Schema version {sourceVersion} was written by a newer version of DesktopShift. This build reads up to version {schema.CurrentVersion}.");
        }

        // A migration is allowed to mutate the object it receives. Work on a
        // clone so a failed step can never leave the caller's import candidate
        // half rewritten.
        JsonObject migrated = (JsonObject)document.DeepClone();
        ImmutableArray<ConfigurationMigrationStep>.Builder applied =
            ImmutableArray.CreateBuilder<ConfigurationMigrationStep>();
        int version = sourceVersion;

        while (version < schema.CurrentVersion)
        {
            if (!steps.TryGetValue(version, out IConfigurationMigration? step))
            {
                return new ConfigurationMigrationResult(
                    null,
                    sourceVersion,
                    applied.ToImmutable(),
                    [
                        Issue(
                            ConfigurationValidationCode.MigrationUnavailable,
                            $"No migration is available from schema version {version} to version {schema.CurrentVersion}."),
                    ]);
            }

            if (step.ToVersion > schema.CurrentVersion)
            {
                return new ConfigurationMigrationResult(
                    null,
                    sourceVersion,
                    applied.ToImmutable(),
                    [
                        Issue(
                            ConfigurationValidationCode.MigrationUnavailable,
                            $"Migration schema version {step.FromVersion} to version {step.ToVersion} overshoots the current version {schema.CurrentVersion}."),
                    ]);
            }

            try
            {
                migrated = step.Apply(migrated) ??
                    throw new InvalidOperationException(
                        "The migration returned no document.");
            }
            catch (Exception exception)
            {
                // A throwing migration leaves the document exactly as it
                // arrived. Nothing is replaced, so the last valid configuration
                // keeps running the application.
                return new ConfigurationMigrationResult(
                    null,
                    sourceVersion,
                    applied.ToImmutable(),
                    [
                        Issue(
                            ConfigurationValidationCode.MigrationFailed,
                            $"Migrating schema version {step.FromVersion} to version {step.ToVersion} failed: {exception.Message}"),
                    ]);
            }

            // Stamped here rather than inside the step, so a step cannot leave
            // the document claiming a version it no longer has.
            migrated[SchemaVersionPropertyName] = step.ToVersion;
            applied.Add(
                new ConfigurationMigrationStep(
                    step.FromVersion,
                    step.ToVersion,
                    step.Description));
            version = step.ToVersion;
        }

        return new ConfigurationMigrationResult(
            migrated,
            sourceVersion,
            applied.ToImmutable(),
            []);
    }

    private static Dictionary<int, IConfigurationMigration> IndexSteps(
        ConfigurationSchema schema)
    {
        Dictionary<int, IConfigurationMigration> steps = [];

        foreach (IConfigurationMigration migration in schema.Migrations)
        {
            if (migration.ToVersion <= migration.FromVersion)
            {
                throw new ArgumentException(
                    $"Migration '{migration.GetType().Name}' does not move the schema forward: {migration.FromVersion} → {migration.ToVersion}.",
                    nameof(schema));
            }

            if (!steps.TryAdd(migration.FromVersion, migration))
            {
                throw new ArgumentException(
                    $"Two migrations both start at schema version {migration.FromVersion}, so the chain is ambiguous.",
                    nameof(schema));
            }
        }

        return steps;
    }

    private static ConfigurationMigrationResult Failed(
        int sourceVersion,
        ConfigurationValidationCode code,
        string message) =>
        new(null, sourceVersion, [], [Issue(code, message)]);

    private static ConfigurationValidationIssue Issue(
        ConfigurationValidationCode code,
        string message) =>
        new(code, message, SchemaVersionPath, ConfigurationEntryKind.Document);
}

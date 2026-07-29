using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

/// <summary>
/// What one read of a configuration file produced.
/// </summary>
/// <remarks>
/// The migration result travels with the document rather than being swallowed,
/// because "this file could not be brought to the current schema" is the single
/// most useful thing a load can report and it is invisible in a null document.
/// </remarks>
/// <param name="Document">
/// The migrated, deserialized document, or null when the file was absent or
/// could not be migrated.
/// </param>
/// <param name="Migration">
/// What the pipeline made of the file. Carries no issues when the file was
/// absent, which is how a first run is told apart from a failure.
/// </param>
internal sealed record ConfigurationReadResult(
    ConfigurationDocument? Document,
    ConfigurationMigrationResult Migration)
{
    /// <summary>A file that is not there. Not a failure, and not an issue.</summary>
    public static ConfigurationReadResult Absent { get; } =
        new(null, new ConfigurationMigrationResult(null, 0, [], []));
}

internal sealed class JsonConfigurationFileStore
{
    private const string CandidateFileName = "configuration.candidate.json";
    private const string ActiveFileName = "configuration.json";
    private const string LastValidFileName = "configuration.last-valid.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IConfigurationStoragePath _storagePath;
    private readonly ConfigurationSchema _schema;

    /// <summary>
    /// Creates the store.
    /// </summary>
    /// <param name="storagePath">Where the three files live.</param>
    /// <param name="schema">
    /// The schema every read is brought up to. Optional so the shipped schema
    /// needs no registration, and so a test can substitute its own by
    /// registering a <see cref="ConfigurationSchema"/> before the container is
    /// built.
    /// </param>
    public JsonConfigurationFileStore(
        IConfigurationStoragePath storagePath,
        ConfigurationSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(storagePath);
        _storagePath = storagePath;
        _schema = schema ?? ConfigurationSchema.Current;
    }

    /// <summary>The serializer options every configuration file is written with.</summary>
    /// <remarks>
    /// Shared so an exported file reads exactly like <c>configuration.json</c>:
    /// indented, camelCase, enums as names. A user who opens one and then the
    /// other must not have to learn two spellings of the same document.
    /// </remarks>
    public static JsonSerializerOptions DocumentSerializerOptions => SerializerOptions;

    public string CandidatePath => GetPath(CandidateFileName);

    public string ActivePath => GetPath(ActiveFileName);

    public string LastValidPath => GetPath(LastValidFileName);

    /// <summary>
    /// Reads one configuration file, migrating it before anything typed sees it.
    /// </summary>
    /// <remarks>
    /// The raw JSON becomes a <see cref="JsonObject"/> first and only then a
    /// <see cref="ConfigurationDocument"/>. Deserializing first would make the
    /// migration pipeline unreachable for exactly the documents that need it: an
    /// old shape that no longer deserializes would be reported as corrupt rather
    /// than migrated.
    /// </remarks>
    /// <param name="path">The file to read.</param>
    /// <param name="cancellationToken">Cancels the read.</param>
    /// <returns>The document, or why it could not be produced.</returns>
    /// <exception cref="JsonException">The file is not readable as JSON.</exception>
    public async Task<ConfigurationReadResult> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return ConfigurationReadResult.Absent;
        }

        JsonNode? node;
        await using (FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan))
        {
            node = await JsonNode.ParseAsync(
                stream,
                nodeOptions: null,
                documentOptions: default,
                cancellationToken);
        }

        return Read(node);
    }

    private ConfigurationReadResult Read(JsonNode? node)
    {
        if (node is null)
        {
            return ConfigurationReadResult.Absent;
        }

        if (node is not JsonObject document)
        {
            throw new JsonException(
                "A configuration document must be a JSON object.");
        }

        ConfigurationMigrationResult migration =
            ConfigurationMigrationPipeline.Migrate(document, _schema);
        if (migration.Document is null)
        {
            return new ConfigurationReadResult(null, migration);
        }

        return new ConfigurationReadResult(
            migration.Document.Deserialize<ConfigurationDocument>(SerializerOptions),
            migration);
    }

    public Task WriteCandidateAsync(
        ConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        return WriteAtomicallyAsync(
            CandidatePath,
            backupPath: null,
            document,
            cancellationToken);
    }

    public async Task WriteActiveAsync(
        ConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        bool activeExisted = File.Exists(ActivePath);

        await WriteAtomicallyAsync(
            ActivePath,
            activeExisted ? LastValidPath : null,
            document,
            cancellationToken);

        if (!activeExisted)
        {
            await WriteAtomicallyAsync(
                LastValidPath,
                backupPath: null,
                document,
                cancellationToken);
        }
    }

    private async Task WriteAtomicallyAsync(
        string targetPath,
        string? backupPath,
        ConfigurationDocument document,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_storagePath.DirectoryPath);
        string temporaryPath = $"{targetPath}.tmp";

        try
        {
            await using (FileStream stream = new(
                temporaryPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.None,
                bufferSize: 4096,
                FileOptions.Asynchronous | FileOptions.WriteThrough))
            {
                await JsonSerializer.SerializeAsync(
                    stream,
                    document,
                    SerializerOptions,
                    cancellationToken);
                await stream.FlushAsync(cancellationToken);
            }

            cancellationToken.ThrowIfCancellationRequested();

            if (File.Exists(targetPath))
            {
                File.Replace(temporaryPath, targetPath, backupPath, ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, targetPath);
            }
        }
        finally
        {
            if (File.Exists(temporaryPath))
            {
                File.Delete(temporaryPath);
            }
        }
    }

    private string GetPath(string fileName)
    {
        return Path.Combine(_storagePath.DirectoryPath, fileName);
    }

    private static JsonSerializerOptions CreateSerializerOptions()
    {
        JsonSerializerOptions options = new(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }
}

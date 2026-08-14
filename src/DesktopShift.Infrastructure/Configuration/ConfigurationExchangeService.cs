using System.Collections.Immutable;
using System.Text.Json;
using System.Text.Json.Nodes;
using DesktopShift.Core;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

/// <summary>
/// Writes and reads the file a user carries their configuration around in.
/// </summary>
/// <remarks>
/// <para>
/// Import goes through <see cref="IConfigurationService.SaveCandidateAsync"/>
/// and nothing else. That is the whole design: an imported document that fails
/// validation becomes the candidate, so every bad entry stays on screen to be
/// corrected, and the last accepted snapshot goes on running the application
/// meanwhile. A second, quieter activation path would be a way to make invalid
/// configuration active by choosing the right menu item.
/// </para>
/// <para>
/// Nothing machine-bound is written. The one member that could be — an
/// executable path under the signed-in user's profile — is rewritten by
/// <see cref="ConfigurationPortability"/>. The runtime bindings between a
/// Managed Desktop and the virtual desktop GUID it resolved to live in their own
/// store and are not part of <see cref="ConfigurationDocument"/> at all, so an
/// export cannot leak one however the machine is set up.
/// </para>
/// </remarks>
internal sealed class ConfigurationExchangeService : IConfigurationExchangeService
{
    private readonly IConfigurationService _configurationService;
    private readonly TimeProvider _timeProvider;
    private readonly IUserProfilePath _userProfilePath;
    private readonly ConfigurationSchema _schema;

    /// <summary>
    /// Creates the service.
    /// </summary>
    /// <param name="configurationService">The one path configuration changes take.</param>
    /// <param name="timeProvider">The clock the envelope is stamped with.</param>
    /// <param name="userProfilePath">This machine's profile directory.</param>
    /// <param name="schema">
    /// The schema an imported file is migrated to. Optional, defaulting to
    /// <see cref="ConfigurationSchema.Current"/>, for the same reason it is
    /// optional on <see cref="ConfigurationService"/>.
    /// </param>
    public ConfigurationExchangeService(
        IConfigurationService configurationService,
        TimeProvider timeProvider,
        IUserProfilePath userProfilePath,
        ConfigurationSchema? schema = null)
    {
        ArgumentNullException.ThrowIfNull(configurationService);
        ArgumentNullException.ThrowIfNull(timeProvider);
        ArgumentNullException.ThrowIfNull(userProfilePath);

        _configurationService = configurationService;
        _timeProvider = timeProvider;
        _userProfilePath = userProfilePath;
        _schema = schema ?? ConfigurationSchema.Current;
    }

    public async Task<ConfigurationExportResult> ExportAsync(
        Stream destination,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(destination);

        ConfigurationState state = _configurationService.CurrentState;

        // The active document is what the application is actually running, so it
        // is what a user means by "my configuration". Falling back to the
        // candidate covers the one case where there is no active document at
        // all — a first run that has not been completed — where exporting
        // nothing would be worse than exporting what is on screen.
        ConfigurationDocument document = state.Active ?? state.Candidate;
        ConfigurationDocument portable = ConfigurationPortability.ToPortable(
            document,
            _userProfilePath.DirectoryPath);

        PortableConfigurationEnvelope envelope = new(
            ConfigurationPortability.FormatVersion,
            ProductInfo.ApplicationName,
            _timeProvider.GetUtcNow(),
            portable);

        byte[] payload = JsonSerializer.SerializeToUtf8Bytes(
            envelope,
            JsonConfigurationFileStore.DocumentSerializerOptions);
        await destination.WriteAsync(payload, cancellationToken);
        await destination.FlushAsync(cancellationToken);

        return new ConfigurationExportResult(
            portable.SchemaVersion,
            Count(portable.ManagedDesktops),
            Count(portable.ApplicationRules),
            payload.LongLength);
    }

    public async Task<ConfigurationImportResult> ImportAsync(
        Stream source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);

        JsonNode? node;
        try
        {
            node = await JsonNode.ParseAsync(
                source,
                nodeOptions: null,
                documentOptions: default,
                cancellationToken);
        }
        catch (JsonException exception)
        {
            return Unreadable($"The file is not valid JSON: {exception.Message}");
        }

        if (node is not JsonObject root)
        {
            return Unreadable(
                "The file does not contain a JSON object, so it is not a DesktopShift configuration.");
        }

        // Both shapes are accepted: the envelope an export writes, and a bare
        // configuration.json copied straight out of the application folder. A
        // user who has the second one and is told to produce the first would
        // have to hand-edit JSON to recover their own settings.
        JsonObject? configuration = root["configuration"] as JsonObject;
        if (configuration is not null)
        {
            if (!TryReadInt(root, "formatVersion", out int formatVersion) ||
                formatVersion != ConfigurationPortability.FormatVersion)
            {
                return Unreadable(
                    $"The portable configuration format is not supported. Expected format version {ConfigurationPortability.FormatVersion}.");
            }

            string? application =
                root["application"] is JsonValue applicationValue &&
                applicationValue.TryGetValue(out string? parsedApplication)
                    ? parsedApplication
                    : null;
            if (!string.Equals(
                application,
                ProductInfo.ApplicationName,
                StringComparison.Ordinal))
            {
                return Unreadable(
                    $"The portable file was not written by {ProductInfo.ApplicationName}.");
            }
        }

        if (configuration is null &&
            root.ContainsKey(ConfigurationMigrationPipeline.SchemaVersionPropertyName))
        {
            configuration = root;
        }

        if (configuration is null)
        {
            return Unreadable(
                "The file carries neither a 'configuration' object nor a 'schemaVersion', so it is not a DesktopShift configuration.");
        }

        ConfigurationMigrationResult migration =
            ConfigurationMigrationPipeline.Migrate(configuration, _schema);
        if (migration.Document is null)
        {
            // Nothing is offered as a candidate. A document that could not be
            // brought to the current schema would be reported as a list of
            // symptoms by the validator instead of the one real problem.
            return new ConfigurationImportResult(
                Accepted: false,
                State: null,
                migration.Issues,
                migration.SourceVersion,
                migration.AppliedSteps);
        }

        ConfigurationDocument? imported;
        try
        {
            imported = migration.Document.Deserialize<ConfigurationDocument>(
                JsonConfigurationFileStore.DocumentSerializerOptions);
        }
        catch (JsonException exception)
        {
            return Unreadable(
                $"The configuration could not be read: {exception.Message}");
        }

        if (imported is null)
        {
            return Unreadable("The file contains no configuration.");
        }

        ConfigurationSaveResult save =
            await _configurationService.SaveCandidateAsync(
                ConfigurationPortability.FromPortable(
                    imported,
                    _userProfilePath.DirectoryPath),
                cancellationToken);

        return new ConfigurationImportResult(
            save.Accepted,
            save.State,
            save.State.Issues,
            migration.SourceVersion,
            migration.AppliedSteps);
    }

    private static int Count<T>(ImmutableArray<T> values) =>
        values.IsDefault ? 0 : values.Length;

    private static bool TryReadInt(
        JsonObject document,
        string propertyName,
        out int value)
    {
        value = default;
        return document[propertyName] is JsonValue jsonValue &&
            jsonValue.TryGetValue(out value);
    }

    private static ConfigurationImportResult Unreadable(string message) =>
        new(
            Accepted: false,
            State: null,
            [
                new ConfigurationValidationIssue(
                    ConfigurationValidationCode.ImportDocumentUnreadable,
                    message,
                    "$",
                    ConfigurationEntryKind.Document),
            ],
            SourceSchemaVersion: 0,
            []);
}

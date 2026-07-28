using System.Collections.Immutable;
using System.Text.Json;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.Infrastructure.ManagedDesktops;

internal sealed class JsonManagedDesktopBindingStore(
    IConfigurationStoragePath storagePath) : IManagedDesktopBindingStore
{
    private const int CurrentSchemaVersion = 1;
    private const string FileName = "managed-desktop-bindings.json";
    private static readonly JsonSerializerOptions SerializerOptions =
        new(JsonSerializerDefaults.General)
        {
            PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
            WriteIndented = true,
        };

    private readonly string path = Path.Combine(
        (storagePath ?? throw new ArgumentNullException(nameof(storagePath))).DirectoryPath,
        FileName);

    public async Task<IReadOnlyList<ManagedDesktopBinding>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(path))
        {
            return [];
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);
        ManagedDesktopBindingDocument? document =
            await JsonSerializer.DeserializeAsync<ManagedDesktopBindingDocument>(
                stream,
                SerializerOptions,
                cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            throw new InvalidDataException(
                "Managed desktop binding metadata is empty.");
        }

        if (document.SchemaVersion != CurrentSchemaVersion)
        {
            throw new InvalidDataException(
                $"Managed desktop binding metadata schema {document.SchemaVersion} is unsupported.");
        }

        return document.Bindings;
    }

    public async Task SaveAsync(
        IReadOnlyCollection<ManagedDesktopBinding> bindings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(bindings);

        string? directory = Path.GetDirectoryName(path);
        if (directory is null)
        {
            throw new InvalidOperationException(
                "The managed desktop binding metadata path has no parent directory.");
        }

        Directory.CreateDirectory(directory);
        string temporaryPath = $"{path}.tmp";
        ManagedDesktopBindingDocument document = new(
            CurrentSchemaVersion,
            bindings
                .OrderBy(static item => item.SemanticKey, StringComparer.OrdinalIgnoreCase)
                .ToImmutableArray());

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
                    cancellationToken).ConfigureAwait(false);
                await stream.FlushAsync(cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            if (File.Exists(path))
            {
                File.Replace(
                    temporaryPath,
                    path,
                    destinationBackupFileName: null,
                    ignoreMetadataErrors: true);
            }
            else
            {
                File.Move(temporaryPath, path);
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

    private sealed record ManagedDesktopBindingDocument(
        int SchemaVersion,
        ImmutableArray<ManagedDesktopBinding> Bindings);
}

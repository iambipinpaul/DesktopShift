using System.Text.Json;
using System.Text.Json.Serialization;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal sealed class JsonConfigurationFileStore
{
    private const string CandidateFileName = "configuration.candidate.json";
    private const string ActiveFileName = "configuration.json";
    private const string LastValidFileName = "configuration.last-valid.json";

    private static readonly JsonSerializerOptions SerializerOptions = CreateSerializerOptions();
    private readonly IConfigurationStoragePath _storagePath;

    public JsonConfigurationFileStore(IConfigurationStoragePath storagePath)
    {
        ArgumentNullException.ThrowIfNull(storagePath);
        _storagePath = storagePath;
    }

    public string CandidatePath => GetPath(CandidateFileName);

    public string ActivePath => GetPath(ActiveFileName);

    public string LastValidPath => GetPath(LastValidFileName);

    public async Task<ConfigurationDocument?> TryReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            return null;
        }

        await using FileStream stream = new(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: 4096,
            FileOptions.Asynchronous | FileOptions.SequentialScan);

        return await JsonSerializer.DeserializeAsync<ConfigurationDocument>(
            stream,
            SerializerOptions,
            cancellationToken);
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

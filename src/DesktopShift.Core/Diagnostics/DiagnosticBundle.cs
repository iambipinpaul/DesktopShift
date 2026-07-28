using System.Collections.Immutable;
using System.Globalization;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopShift.Core.Diagnostics;

/// <summary>One text file carried inside a diagnostic bundle.</summary>
public sealed record DiagnosticBundleFile(string Name, string Content);

/// <summary>
/// What the bundle says about the machine it came from.
/// </summary>
/// <remarks>
/// Deliberately coarse. A maintainer needs the Windows build and the process
/// architecture to read a failure; nothing here identifies the person using the
/// machine.
/// </remarks>
public sealed record DiagnosticEnvironmentSummary(
    string OperatingSystem,
    string ProcessArchitecture,
    string RuntimeVersion);

/// <summary>The bundle's self-description.</summary>
public sealed record DiagnosticBundleManifest(
    string Product,
    string Version,
    DateTimeOffset CreatedAtUtc,
    Guid SessionId,
    DateTimeOffset SessionStartedAtUtc,
    DiagnosticEnvironmentSummary Environment,
    string LogLocation,
    long MaxLogFileSizeBytes,
    int MaxLogFileCount,
    int ActivityRecordCount,
    int LogFileCount,
    string PrivacyNotice);

/// <summary>Everything a bundle is built from.</summary>
public sealed record DiagnosticBundleContents(
    DiagnosticBundleManifest Manifest,
    ImmutableArray<ActivityRecord> Activity,
    ImmutableArray<DiagnosticBundleFile> Logs);

/// <summary>What an export produced.</summary>
public sealed record DiagnosticBundleSummary(
    int ActivityRecordCount,
    int LogFileCount,
    long ByteCount,
    string? FilePath = null,
    string? DisplayPath = null);

/// <summary>
/// Writes a diagnostic bundle as a zip archive.
/// </summary>
/// <remarks>
/// <para>
/// The bundle is produced and nothing else happens to it: it is not opened, not
/// uploaded, and not shared. Where it goes afterwards is the user's decision.
/// </para>
/// <para>
/// Every string the writer emits passes through
/// <see cref="DiagnosticRedaction"/>, which is defence in depth rather than the
/// primary control — the records themselves already carry only privacy-safe
/// identity.
/// </para>
/// </remarks>
public static class DiagnosticBundleWriter
{
    public const string ManifestEntryName = "manifest.json";
    public const string ActivityEntryName = "activity.json";
    public const string LogEntryPrefix = "logs/";

    /// <summary>
    /// The sentence stored in every manifest, so a reader of the archive knows
    /// what was deliberately left out.
    /// </summary>
    public const string PrivacyNotice =
        "This bundle contains privacy-safe application identity only. Window " +
        "titles, browser URLs, executable paths, and command lines are never " +
        "recorded, and user profile directories are replaced with " +
        DiagnosticRedaction.UserProfileToken + ".";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = true,
    };

    /// <summary>
    /// Writes the bundle into <paramref name="destination"/>.
    /// </summary>
    /// <param name="destination">The writable, seekable stream to fill.</param>
    /// <param name="contents">What to put in the bundle.</param>
    /// <returns>What the bundle ended up containing.</returns>
    public static DiagnosticBundleSummary Write(
        Stream destination,
        DiagnosticBundleContents contents)
    {
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(contents);

        ImmutableArray<ActivityRecord> activity =
            contents.Activity.IsDefault ? [] : contents.Activity;
        ImmutableArray<DiagnosticBundleFile> logs =
            contents.Logs.IsDefault ? [] : contents.Logs;

        long startPosition = destination.CanSeek ? destination.Position : 0;

        using (ZipArchive archive = new(
            destination,
            ZipArchiveMode.Create,
            leaveOpen: true))
        {
            WriteEntry(
                archive,
                ManifestEntryName,
                Serialize(contents.Manifest with
                {
                    ActivityRecordCount = activity.Length,
                    LogFileCount = logs.Length,
                    LogLocation =
                        DiagnosticRedaction.Redact(contents.Manifest.LogLocation) ??
                        string.Empty,
                    PrivacyNotice = PrivacyNotice,
                }));
            WriteEntry(
                archive,
                ActivityEntryName,
                Serialize(
                    activity
                        .Select(DiagnosticLogEntry.From)
                        .ToArray()));

            foreach (DiagnosticBundleFile log in logs)
            {
                WriteEntry(
                    archive,
                    LogEntryPrefix + SanitizeEntryName(log.Name),
                    log.Content);
            }
        }

        long byteCount = destination.CanSeek
            ? destination.Position - startPosition
            : 0;
        return new DiagnosticBundleSummary(
            activity.Length,
            logs.Length,
            byteCount);
    }

    /// <summary>
    /// Builds the file name an exported bundle is offered under.
    /// </summary>
    /// <param name="createdAtUtc">When the bundle was produced.</param>
    /// <returns>A sortable, collision-resistant file name.</returns>
    public static string CreateFileName(DateTimeOffset createdAtUtc) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"desktopshift-diagnostics-{createdAtUtc.UtcDateTime:yyyyMMdd-HHmmss}.zip");

    private static void WriteEntry(
        ZipArchive archive,
        string entryName,
        string content)
    {
        ZipArchiveEntry entry = archive.CreateEntry(
            entryName,
            CompressionLevel.Optimal);
        using Stream entryStream = entry.Open();
        using StreamWriter writer = new(entryStream, new UTF8Encoding(false));
        writer.Write(DiagnosticRedaction.Redact(content));
    }

    private static string Serialize<TValue>(TValue value) =>
        JsonSerializer.Serialize(value, SerializerOptions);

    private static string SanitizeEntryName(string name)
    {
        StringBuilder result = new(name.Length);
        foreach (char character in name)
        {
            _ = result.Append(
                character is '/' or '\\' or ':' ? '_' : character);
        }

        return result.ToString();
    }
}

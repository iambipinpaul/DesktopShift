using System.Collections.Immutable;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DesktopShift.Core.Diagnostics;

/// <summary>One retained log file and its current size.</summary>
public sealed record DiagnosticLogFile(string Name, long Length);

/// <summary>
/// The file operations the rolling log needs, and nothing more.
/// </summary>
/// <remarks>
/// The rolling policy — when to start a new file and which old file to drop —
/// is the part worth proving, so it is kept in the log itself and every byte
/// that touches a disk goes through this seam. A test drives an in-memory
/// store and never writes to the user's real log location.
/// </remarks>
public interface IDiagnosticLogStore
{
    /// <summary>The directory the files live in, for display and export.</summary>
    string Location { get; }

    /// <summary>
    /// The files this store owns, ordered oldest first by name.
    /// </summary>
    IReadOnlyList<DiagnosticLogFile> List();

    /// <summary>Appends one line, creating the file when it is missing.</summary>
    void Append(string fileName, string line);

    /// <summary>Reads a file's full text, or an empty string when missing.</summary>
    string Read(string fileName);

    /// <summary>Deletes a file this store owns.</summary>
    void Delete(string fileName);
}

/// <summary>The limits a rolling log enforces.</summary>
/// <param name="MaxFileSizeBytes">
/// The largest a single file may grow before the log rolls to a new one.
/// </param>
/// <param name="MaxFileCount">
/// How many files are retained. The oldest beyond this count are deleted.
/// </param>
public sealed record RollingDiagnosticLogOptions(
    long MaxFileSizeBytes = RollingDiagnosticLogOptions.DefaultMaxFileSizeBytes,
    int MaxFileCount = RollingDiagnosticLogOptions.DefaultMaxFileCount)
{
    public const long DefaultMaxFileSizeBytes = 1024 * 1024;
    public const int DefaultMaxFileCount = 5;

    public static RollingDiagnosticLogOptions Default { get; } = new();
}

public interface IDiagnosticLogWriter
{
    void Write(ActivityRecord record);

    /// <summary>
    /// Deletes the files this log owns. Only files matching the log's own
    /// naming pattern are removed, so nothing the user put in the folder is
    /// touched.
    /// </summary>
    void Clear();
}

/// <summary>
/// Appends privacy-safe activity events as JSON lines, enforcing a size cap per
/// file and a cap on retained files.
/// </summary>
public sealed class RollingDiagnosticLog : IDiagnosticLogWriter
{
    /// <summary>The prefix every file this log owns starts with.</summary>
    public const string FileNamePrefix = "activity-";

    /// <summary>The extension every file this log owns ends with.</summary>
    public const string FileNameExtension = ".log";

    private readonly IDiagnosticLogStore store;
    private readonly RollingDiagnosticLogOptions options;
    private readonly object syncRoot = new();

    public RollingDiagnosticLog(
        IDiagnosticLogStore store,
        RollingDiagnosticLogOptions? options = null)
    {
        ArgumentNullException.ThrowIfNull(store);
        this.store = store;
        this.options = options ?? RollingDiagnosticLogOptions.Default;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            this.options.MaxFileSizeBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(
            this.options.MaxFileCount);
    }

    public string Location => store.Location;

    public RollingDiagnosticLogOptions Options => options;

    /// <summary>The total size of every retained file.</summary>
    public long TotalSizeBytes
    {
        get
        {
            lock (syncRoot)
            {
                return OwnedFiles().Sum(static file => file.Length);
            }
        }
    }

    public void Write(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        string line = DiagnosticLogEntry.Serialize(
            DiagnosticLogEntry.From(record));
        long lineBytes = Encoding.UTF8.GetByteCount(line) + 1;

        lock (syncRoot)
        {
            IReadOnlyList<DiagnosticLogFile> files = OwnedFiles();
            DiagnosticLogFile? current = files.Count == 0 ? null : files[^1];
            int nextIndex = current is null
                ? 1
                : ParseIndex(current.Name) + 1;

            // A line larger than the cap still has to be written somewhere, so
            // it gets a file of its own rather than being dropped.
            bool shouldRoll = current is not null &&
                current.Length > 0 &&
                current.Length + lineBytes > options.MaxFileSizeBytes;
            string target = current is null || shouldRoll
                ? BuildFileName(nextIndex)
                : current.Name;

            store.Append(target, line);
            Prune();
        }
    }

    /// <summary>The files this log currently owns, oldest first.</summary>
    public IReadOnlyList<DiagnosticLogFile> Files()
    {
        lock (syncRoot)
        {
            return OwnedFiles();
        }
    }

    /// <summary>
    /// Reads every retained file, oldest first, for a diagnostic bundle.
    /// </summary>
    public ImmutableArray<DiagnosticBundleFile> ReadAll()
    {
        lock (syncRoot)
        {
            return
            [
                .. OwnedFiles()
                    .Select(file => new DiagnosticBundleFile(
                        file.Name,
                        store.Read(file.Name))),
            ];
        }
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            foreach (DiagnosticLogFile file in OwnedFiles())
            {
                store.Delete(file.Name);
            }
        }
    }

    private void Prune()
    {
        IReadOnlyList<DiagnosticLogFile> files = OwnedFiles();
        for (int index = 0; index < files.Count - options.MaxFileCount; index++)
        {
            store.Delete(files[index].Name);
        }
    }

    private IReadOnlyList<DiagnosticLogFile> OwnedFiles() =>
        [
            .. store.List()
                .Where(static file => IsOwned(file.Name))
                .OrderBy(static file => ParseIndex(file.Name)),
        ];

    private static bool IsOwned(string fileName) =>
        fileName.StartsWith(FileNamePrefix, StringComparison.OrdinalIgnoreCase) &&
        fileName.EndsWith(FileNameExtension, StringComparison.OrdinalIgnoreCase) &&
        ParseIndex(fileName) > 0;

    private static int ParseIndex(string fileName)
    {
        if (fileName.Length <= FileNamePrefix.Length + FileNameExtension.Length)
        {
            return 0;
        }

        ReadOnlySpan<char> digits = fileName.AsSpan(
            FileNamePrefix.Length,
            fileName.Length - FileNamePrefix.Length - FileNameExtension.Length);
        return int.TryParse(
            digits,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out int index)
            ? index
            : 0;
    }

    private static string BuildFileName(int index) =>
        $"{FileNamePrefix}{index.ToString("D4", CultureInfo.InvariantCulture)}{FileNameExtension}";
}

/// <summary>
/// The exact shape written to the log, one JSON object per line.
/// </summary>
/// <remarks>
/// <para>
/// The privacy invariant is structural: this type has no member a window title,
/// a browser URL, an executable path, or a command line could occupy, so no
/// change to an upstream record can leak one into a log file.
/// </para>
/// <para>
/// <see cref="TopologyReason"/> and <see cref="RecoverySignal"/> are the two
/// members that are not about a window at all. Both only ever receive an
/// enumeration name DesktopShift itself produced, which is what lets them be
/// filtered on without widening what a log file can contain.
/// </para>
/// </remarks>
/// <param name="TopologyReason">
/// Why Windows said the desktop topology changed, on topology events only.
/// </param>
/// <param name="RecoverySignal">
/// Which disruption drove a recovery event — <c>explorer_restarted</c>,
/// <c>session_resumed</c>, or <c>display_changed</c> — on recovery events only.
/// It is written as its own field rather than left inside the result code so
/// that "every Explorer recovery in this log" is a filter rather than a prefix
/// match.
/// </param>
public sealed record DiagnosticLogEntry(
    DateTimeOffset Timestamp,
    Guid SessionId,
    Guid CorrelationId,
    string Source,
    string Result,
    string ResultCode,
    string Message,
    string Trigger,
    string? Application,
    string? PackageFamilyName,
    string? AppUserModelId,
    string? WindowClass,
    string? RuleId,
    string? TargetDesktopKey,
    double? DurationMilliseconds,
    long? EventSequence,
    string? ErrorCode,
    string? ErrorMessage,
    string? HResult,
    int? WindowsErrorCode,
    string? TopologyReason = null,
    string? RecoverySignal = null,
    Guid? SourceDesktopId = null,
    Guid? DestinationDesktopId = null)
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        WriteIndented = false,
    };

    public static DiagnosticLogEntry From(ActivityRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new DiagnosticLogEntry(
            record.OccurredAt.ToUniversalTime(),
            record.SessionId,
            record.CorrelationId,
            ActivityRecordFactory.ToCode(record.Source),
            ActivityRecordFactory.ToCode(record.Result),
            record.ResultCode,
            DiagnosticRedaction.Redact(record.Summary) ?? string.Empty,
            ActivityRecordFactory.ToCode(record.Trigger),
            record.Identity?.ProcessName ?? record.Application,
            record.Identity?.PackageFamilyName,
            record.Identity?.AppUserModelId,
            record.Identity?.WindowClass,
            record.RuleId,
            record.TargetDesktopKey,
            record.Duration?.TotalMilliseconds,
            record.EventSequence,
            record.Error?.Code,
            DiagnosticRedaction.Redact(record.Error?.Message),
            record.Error?.HResultText,
            record.Error?.NativeErrorCode,
            record.TopologyReason,
            record.RecoverySignal,
            record.SourceDesktopId,
            record.DestinationDesktopId);
    }

    public static string Serialize(DiagnosticLogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);
        return JsonSerializer.Serialize(entry, SerializerOptions);
    }
}

/// <summary>
/// An in-memory log store. It is the safe default for tests and for any host
/// that has not been given a real location.
/// </summary>
public sealed class InMemoryDiagnosticLogStore : IDiagnosticLogStore
{
    private readonly object syncRoot = new();
    private readonly Dictionary<string, StringBuilder> files =
        new(StringComparer.OrdinalIgnoreCase);

    public InMemoryDiagnosticLogStore(string location = "memory://activity")
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(location);
        Location = location;
    }

    public string Location { get; }

    public IReadOnlyList<DiagnosticLogFile> List()
    {
        lock (syncRoot)
        {
            return
            [
                .. files
                    .OrderBy(static pair => pair.Key, StringComparer.Ordinal)
                    .Select(static pair => new DiagnosticLogFile(
                        pair.Key,
                        Encoding.UTF8.GetByteCount(pair.Value.ToString()))),
            ];
        }
    }

    public void Append(string fileName, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(line);

        lock (syncRoot)
        {
            if (!files.TryGetValue(fileName, out StringBuilder? content))
            {
                content = new StringBuilder();
                files[fileName] = content;
            }

            _ = content.Append(line).Append('\n');
        }
    }

    public string Read(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        lock (syncRoot)
        {
            return files.TryGetValue(fileName, out StringBuilder? content)
                ? content.ToString()
                : string.Empty;
        }
    }

    public void Delete(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        lock (syncRoot)
        {
            _ = files.Remove(fileName);
        }
    }
}

using System.Text;
using DesktopShift.Core;
using DesktopShift.Core.Diagnostics;

namespace DesktopShift.Infrastructure.Diagnostics;

/// <summary>
/// The application's own log directory under local application data.
/// </summary>
public sealed class LocalAppDataDiagnosticLogLocation : IDiagnosticLogLocation
{
    /// <summary>The folder name the logs live in, below the product folder.</summary>
    public const string LogFolderName = "logs";

    /// <summary>The folder exported bundles are written to by default.</summary>
    public const string BundleFolderName = "diagnostics";

    public LocalAppDataDiagnosticLogLocation()
        : this(
            Path.Combine(
                Environment.GetFolderPath(
                    Environment.SpecialFolder.LocalApplicationData),
                ProductInfo.ApplicationName),
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile))
    {
    }

    /// <summary>
    /// Creates a location below an explicit root, which is how a test drives a
    /// temporary directory instead of the machine's real log folder.
    /// </summary>
    /// <param name="rootDirectoryPath">The product folder to sit below.</param>
    /// <param name="userProfilePath">
    /// The profile directory to redact from <see cref="DisplayPath"/>, or null
    /// to redact only the standard profile pattern.
    /// </param>
    public LocalAppDataDiagnosticLogLocation(
        string rootDirectoryPath,
        string? userProfilePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootDirectoryPath);
        RootDirectoryPath = rootDirectoryPath;
        DirectoryPath = Path.Combine(rootDirectoryPath, LogFolderName);
        BundleDirectoryPath = Path.Combine(rootDirectoryPath, BundleFolderName);
        DisplayPath =
            DiagnosticRedaction.Redact(DirectoryPath, userProfilePath) ??
            DirectoryPath;
    }

    public string RootDirectoryPath { get; }

    public string DirectoryPath { get; }

    public string BundleDirectoryPath { get; }

    public string DisplayPath { get; }
}

/// <summary>
/// Backs the rolling log with real files.
/// </summary>
/// <remarks>
/// The directory is created lazily, on the first write, so a host that never
/// records anything leaves no trace on disk. Every read and delete is confined
/// to a bare file name inside the log directory, so the log can never reach a
/// file the user owns.
/// </remarks>
public sealed class FileSystemDiagnosticLogStore : IDiagnosticLogStore
{
    private static readonly UTF8Encoding Utf8NoBom = new(false);

    private readonly object syncRoot = new();
    private readonly string directoryPath;

    public FileSystemDiagnosticLogStore(IDiagnosticLogLocation location)
    {
        ArgumentNullException.ThrowIfNull(location);
        directoryPath = location.DirectoryPath;
        Location = location.DirectoryPath;
    }

    public string Location { get; }

    public IReadOnlyList<DiagnosticLogFile> List()
    {
        lock (syncRoot)
        {
            if (!Directory.Exists(directoryPath))
            {
                return [];
            }

            return
            [
                .. new DirectoryInfo(directoryPath)
                    .EnumerateFiles(
                        $"{RollingDiagnosticLog.FileNamePrefix}*{RollingDiagnosticLog.FileNameExtension}")
                    .Select(static file => new DiagnosticLogFile(
                        file.Name,
                        file.Length)),
            ];
        }
    }

    public void Append(string fileName, string line)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        ArgumentNullException.ThrowIfNull(line);

        lock (syncRoot)
        {
            _ = Directory.CreateDirectory(directoryPath);
            File.AppendAllText(Resolve(fileName), line + "\n", Utf8NoBom);
        }
    }

    public string Read(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        lock (syncRoot)
        {
            string path = Resolve(fileName);
            return File.Exists(path)
                ? File.ReadAllText(path, Utf8NoBom)
                : string.Empty;
        }
    }

    public void Delete(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);

        lock (syncRoot)
        {
            string path = Resolve(fileName);
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
    }

    private string Resolve(string fileName)
    {
        string bareName = Path.GetFileName(fileName);
        if (!string.Equals(bareName, fileName, StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "A diagnostic log file name may not contain a path.",
                nameof(fileName));
        }

        return Path.Combine(directoryPath, bareName);
    }
}

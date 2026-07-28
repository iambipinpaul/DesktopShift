using DesktopShift.Core.Diagnostics;
using DesktopShift.Infrastructure.Diagnostics;

namespace DesktopShift.Core.Tests.Diagnostics;

/// <summary>
/// Drives the real file-backed store against a temporary directory. Nothing
/// here ever touches the machine's own log location.
/// </summary>
[TestClass]
public sealed class FileSystemDiagnosticLogStoreTests
{
    [TestMethod]
    public void Store_CreatesNothingUntilSomethingIsWritten()
    {
        using TemporaryDirectory root = new();
        LocalAppDataDiagnosticLogLocation location = new(root.Path, null);

        _ = new FileSystemDiagnosticLogStore(location);

        Assert.IsFalse(Directory.Exists(location.DirectoryPath));
    }

    [TestMethod]
    public void RollingLog_WritesRotatesAndPrunesRealFiles()
    {
        using TemporaryDirectory root = new();
        LocalAppDataDiagnosticLogLocation location = new(root.Path, null);
        RollingDiagnosticLog log = new(
            new FileSystemDiagnosticLogStore(location),
            new RollingDiagnosticLogOptions(
                MaxFileSizeBytes: 1000,
                MaxFileCount: 2));

        for (int index = 0; index < 20; index++)
        {
            log.Write(
                ActivityRecordFactory.FromObservation(
                    DiagnosticTestData.MatchedObservation(),
                    DiagnosticTestData.Session));
        }

        string[] onDisk = Directory
            .GetFiles(location.DirectoryPath, "activity-*.log")
            .Select(Path.GetFileName)
            .ToArray()!;
        Assert.HasCount(2, onDisk);
        Assert.IsLessThanOrEqualTo(2000L, log.TotalSizeBytes);
        Assert.IsTrue(
            log.Files().All(static file => file.Length <= 1000),
            "No retained file may exceed the configured size cap.");
    }

    [TestMethod]
    public void Clear_DeletesTheLogFilesButLeavesForeignFilesAlone()
    {
        using TemporaryDirectory root = new();
        LocalAppDataDiagnosticLogLocation location = new(root.Path, null);
        RollingDiagnosticLog log = new(
            new FileSystemDiagnosticLogStore(location));
        log.Write(
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(),
                DiagnosticTestData.Session));
        string foreignFile = Path.Combine(location.DirectoryPath, "notes.txt");
        File.WriteAllText(foreignFile, "keep me");

        log.Clear();

        Assert.IsEmpty(Directory.GetFiles(location.DirectoryPath, "activity-*.log"));
        Assert.IsTrue(File.Exists(foreignFile));
    }

    [TestMethod]
    public void APathInAFileName_IsRefused()
    {
        using TemporaryDirectory root = new();
        FileSystemDiagnosticLogStore store = new(
            new LocalAppDataDiagnosticLogLocation(root.Path, null));

        _ = Assert.ThrowsExactly<ArgumentException>(
            () => store.Append(@"..\escape.log", "{}"));
    }

    [TestMethod]
    public void DisplayPath_RedactsTheUserProfile()
    {
        LocalAppDataDiagnosticLogLocation location = new(
            @"C:\Users\marguerite\AppData\Local\DesktopShift",
            @"C:\Users\marguerite");

        Assert.AreEqual(
            @"%USERPROFILE%\AppData\Local\DesktopShift\logs",
            location.DisplayPath);
        Assert.AreEqual(
            @"C:\Users\marguerite\AppData\Local\DesktopShift\logs",
            location.DirectoryPath);
    }

    [TestMethod]
    public void BundleFolder_IsSeparateFromTheLogFolder()
    {
        LocalAppDataDiagnosticLogLocation location = new(
            @"C:\ProgramData\DesktopShift",
            null);

        Assert.AreEqual(
            @"C:\ProgramData\DesktopShift\logs",
            location.DirectoryPath);
        Assert.AreEqual(
            @"C:\ProgramData\DesktopShift\diagnostics",
            location.BundleDirectoryPath);
    }

    private sealed class TemporaryDirectory : IDisposable
    {
        public TemporaryDirectory()
        {
            Path = System.IO.Path.Combine(
                System.IO.Path.GetTempPath(),
                "DesktopShift.Tests",
                Guid.NewGuid().ToString("N"));
            _ = Directory.CreateDirectory(Path);
        }

        public string Path { get; }

        public void Dispose()
        {
            try
            {
                if (Directory.Exists(Path))
                {
                    Directory.Delete(Path, recursive: true);
                }
            }
            catch (IOException)
            {
                // A leftover temporary directory is not a test failure.
            }
        }
    }
}

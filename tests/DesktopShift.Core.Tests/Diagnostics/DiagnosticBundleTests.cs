using System.Collections.Immutable;
using System.IO.Compression;
using System.Text.Json;
using DesktopShift.Core.Diagnostics;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class DiagnosticBundleTests
{
    [TestMethod]
    public void Bundle_ContainsAManifestActivityAndEveryLogFile()
    {
        using MemoryStream destination = new();

        DiagnosticBundleSummary summary = DiagnosticBundleWriter.Write(
            destination,
            Contents());

        using ZipArchive archive = OpenArchive(destination);
        CollectionAssert.AreEquivalent(
            new[]
            {
                "manifest.json",
                "activity.json",
                "logs/activity-0001.log",
                "logs/activity-0002.log",
            },
            archive.Entries.Select(static entry => entry.FullName).ToArray());
        Assert.AreEqual(4, summary.ActivityRecordCount);
        Assert.AreEqual(2, summary.LogFileCount);
        Assert.IsGreaterThan(0L, summary.ByteCount);
    }

    [TestMethod]
    public void Manifest_ReportsTheLimitsSessionAndCounts()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(destination, Contents());

        using JsonDocument manifest = ReadJson(destination, "manifest.json");
        JsonElement root = manifest.RootElement;
        Assert.AreEqual("DesktopShift", root.GetProperty("product").GetString());
        Assert.AreEqual(
            DiagnosticTestData.Session.ToString(),
            root.GetProperty("sessionId").GetString());
        Assert.AreEqual(
            1024L,
            root.GetProperty("maxLogFileSizeBytes").GetInt64());
        Assert.AreEqual(3, root.GetProperty("maxLogFileCount").GetInt32());
        Assert.AreEqual(4, root.GetProperty("activityRecordCount").GetInt32());
        Assert.AreEqual(2, root.GetProperty("logFileCount").GetInt32());
        Assert.AreEqual(
            DiagnosticBundleWriter.PrivacyNotice,
            root.GetProperty("privacyNotice").GetString());
        Assert.IsNotNull(
            root.GetProperty("environment").GetProperty("operatingSystem").GetString());
    }

    [TestMethod]
    public void ActivityEntry_IsAnArrayOfTheSameShapeAsALogLine()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(destination, Contents());

        using JsonDocument activity = ReadJson(destination, "activity.json");
        Assert.AreEqual(JsonValueKind.Array, activity.RootElement.ValueKind);
        Assert.AreEqual(4, activity.RootElement.GetArrayLength());

        foreach (JsonElement element in activity.RootElement.EnumerateArray())
        {
            Assert.IsTrue(element.TryGetProperty("correlationId", out _));
            Assert.IsTrue(element.TryGetProperty("source", out _));
            Assert.IsTrue(element.TryGetProperty("resultCode", out _));
        }
    }

    [TestMethod]
    public void EveryStageOfOneAssignment_ShareOneCorrelationInsideTheBundle()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(destination, Contents());

        using JsonDocument activity = ReadJson(destination, "activity.json");
        string[] correlations = activity.RootElement
            .EnumerateArray()
            .Select(static element =>
                element.GetProperty("correlationId").GetString()!)
            .Distinct()
            .ToArray();

        Assert.HasCount(1, correlations);
        Assert.AreEqual(DiagnosticTestData.Correlation.ToString(), correlations[0]);
    }

    [TestMethod]
    public void Bundle_NeverContainsTitlesUrlsCommandLinesOrProfilePaths()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(destination, Contents(withSecrets: true));

        string text = ReadAllText(destination);
        Assert.DoesNotContain(DiagnosticTestData.SecretTitle, text);
        Assert.DoesNotContain(DiagnosticTestData.SecretCommandLine, text);
        Assert.DoesNotContain(DiagnosticTestData.SecretExecutablePath, text);
        Assert.DoesNotContain("Quarterly", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("marguerite", text);
        Assert.DoesNotContain("https://", text);
        Assert.Contains("Code.exe", text);
    }

    [TestMethod]
    public void LogLocationInTheManifest_IsRedacted()
    {
        using MemoryStream destination = new();
        _ = DiagnosticBundleWriter.Write(
            destination,
            Contents() with
            {
                Manifest = Manifest() with
                {
                    LogLocation =
                        @"C:\Users\marguerite\AppData\Local\DesktopShift\logs",
                },
            });

        using JsonDocument manifest = ReadJson(destination, "manifest.json");
        Assert.AreEqual(
            @"%USERPROFILE%\AppData\Local\DesktopShift\logs",
            manifest.RootElement.GetProperty("logLocation").GetString());
    }

    [TestMethod]
    public void DefaultArrays_ProduceAnEmptyButValidBundle()
    {
        using MemoryStream destination = new();

        DiagnosticBundleSummary summary = DiagnosticBundleWriter.Write(
            destination,
            new DiagnosticBundleContents(Manifest(), default, default));

        Assert.AreEqual(0, summary.ActivityRecordCount);
        Assert.AreEqual(0, summary.LogFileCount);
        using JsonDocument activity = ReadJson(destination, "activity.json");
        Assert.AreEqual(0, activity.RootElement.GetArrayLength());
    }

    [TestMethod]
    public void BundleFileName_IsSortableAndCarriesTheProductName()
    {
        string name = DiagnosticBundleWriter.CreateFileName(
            new DateTimeOffset(2026, 7, 28, 9, 30, 15, TimeSpan.Zero));

        Assert.AreEqual("desktopshift-diagnostics-20260728-093015.zip", name);
    }

    private static DiagnosticBundleContents Contents(bool withSecrets = false)
    {
        ImmutableArray<ActivityRecord> activity =
        [
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(),
                DiagnosticTestData.Session),
            .. ActivityRecordFactory.FromAssignment(
                DiagnosticTestData.Assignment(),
                DiagnosticTestData.Session),
        ];

        // A log file whose bytes were produced before any redaction, to prove
        // the writer scrubs what it is handed rather than trusting its source.
        string logContent = withSecrets
            ? "{\"message\":\"read C:\\\\Users\\\\marguerite\\\\notes.txt\"}\n"
            : "{\"message\":\"ok\"}\n";

        return new DiagnosticBundleContents(
            Manifest(),
            activity,
            [
                new DiagnosticBundleFile("activity-0001.log", logContent),
                new DiagnosticBundleFile("activity-0002.log", "{\"message\":\"ok\"}\n"),
            ]);
    }

    private static DiagnosticBundleManifest Manifest() =>
        new(
            "DesktopShift",
            "0.1.0.0",
            DiagnosticTestData.Occurred,
            DiagnosticTestData.Session,
            DiagnosticTestData.Occurred,
            new DiagnosticEnvironmentSummary(
                "Microsoft Windows 10.0.26200",
                "X64",
                ".NET 10.0.0"),
            @"%USERPROFILE%\AppData\Local\DesktopShift\logs",
            1024,
            3,
            0,
            0,
            DiagnosticBundleWriter.PrivacyNotice);

    private static ZipArchive OpenArchive(MemoryStream destination)
    {
        destination.Position = 0;
        return new ZipArchive(destination, ZipArchiveMode.Read, leaveOpen: true);
    }

    private static JsonDocument ReadJson(MemoryStream destination, string entryName)
    {
        using ZipArchive archive = OpenArchive(destination);
        ZipArchiveEntry entry = archive.GetEntry(entryName)!;
        using StreamReader reader = new(entry.Open());
        return JsonDocument.Parse(reader.ReadToEnd());
    }

    private static string ReadAllText(MemoryStream destination)
    {
        using ZipArchive archive = OpenArchive(destination);
        return string.Join(
            "\n",
            archive.Entries.Select(static entry =>
            {
                using StreamReader reader = new(entry.Open());
                return reader.ReadToEnd();
            }));
    }
}

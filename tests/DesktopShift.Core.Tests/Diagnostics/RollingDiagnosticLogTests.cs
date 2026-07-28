using System.Text.Json;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class RollingDiagnosticLogTests
{
    [TestMethod]
    public void FirstWrite_CreatesTheFirstNumberedFile()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);

        log.Write(Record());

        Assert.HasCount(1, log.Files());
        Assert.AreEqual("activity-0001.log", log.Files()[0].Name);
    }

    [TestMethod]
    public void FileSizeCap_IsEnforcedByRollingToANewFile()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(
            store,
            new RollingDiagnosticLogOptions(MaxFileSizeBytes: 1000));

        for (int index = 0; index < 12; index++)
        {
            log.Write(Record());
        }

        IReadOnlyList<DiagnosticLogFile> files = log.Files();
        Assert.IsGreaterThan(1, files.Count);
        Assert.IsTrue(
            files.All(static file => file.Length <= 1000),
            "No retained file may exceed the configured size cap.");
    }

    [TestMethod]
    public void RetentionCap_DeletesTheOldestFiles()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(
            store,
            new RollingDiagnosticLogOptions(
                MaxFileSizeBytes: 1000,
                MaxFileCount: 2));

        for (int index = 0; index < 30; index++)
        {
            log.Write(Record());
        }

        IReadOnlyList<DiagnosticLogFile> files = log.Files();
        Assert.HasCount(2, files);
        Assert.IsLessThanOrEqualTo(
            2000L,
            log.TotalSizeBytes,
            "The whole log stays inside size cap times file count.");
        Assert.IsFalse(
            files.Any(static file => file.Name == "activity-0001.log"),
            "The oldest file must be the one dropped.");
    }

    [TestMethod]
    public void AnOversizedLine_StillGetsAFileOfItsOwnRatherThanBeingDropped()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(
            store,
            new RollingDiagnosticLogOptions(MaxFileSizeBytes: 8));

        log.Write(Record());
        log.Write(Record());

        Assert.HasCount(2, log.Files());
    }

    [TestMethod]
    public void EveryLine_IsOneSelfContainedJsonObject()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);

        log.Write(Record());
        log.Write(Record(ActivityEventSource.Move, ActivityResult.Failed));

        string[] lines = ReadLines(log, store);
        Assert.HasCount(2, lines);
        foreach (string line in lines)
        {
            using JsonDocument document = JsonDocument.Parse(line);
            Assert.AreEqual(JsonValueKind.Object, document.RootElement.ValueKind);
            Assert.IsTrue(document.RootElement.TryGetProperty(
                "correlationId",
                out _));
            Assert.IsTrue(document.RootElement.TryGetProperty("sessionId", out _));
            Assert.IsTrue(document.RootElement.TryGetProperty("timestamp", out _));
            Assert.IsTrue(document.RootElement.TryGetProperty("source", out _));
            Assert.IsTrue(document.RootElement.TryGetProperty("trigger", out _));
        }
    }

    [TestMethod]
    public void FailureLines_CarryTheHResultAndWindowsErrorCode()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);

        log.Write(
            ActivityRecordFactory
                .FromAssignment(
                    DiagnosticTestData.Assignment(
                        outcome: WindowAssignmentOutcome.Failed,
                        moveOutcome: WindowMoveOutcome.Failed,
                        switchOutcome: DesktopSwitchOutcome.NotRequested,
                        error: new WindowAssignmentError(
                            "window_placement.move_access_denied",
                            "Windows denied access to the window.",
                            unchecked((int)0x80070005),
                            5)),
                    DiagnosticTestData.Session)
                .First(static record =>
                    record.Source == ActivityEventSource.Move));

        using JsonDocument document = JsonDocument.Parse(
            ReadLines(log, store)[0]);
        Assert.AreEqual(
            "window_placement.move_access_denied",
            document.RootElement.GetProperty("errorCode").GetString());
        Assert.AreEqual(
            "0x80070005",
            document.RootElement.GetProperty("hResult").GetString());
        Assert.AreEqual(
            5,
            document.RootElement.GetProperty("windowsErrorCode").GetInt32());
    }

    [TestMethod]
    public void LogLines_NeverCarryTitlesCommandLinesOrExecutablePaths()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);

        log.Write(
            ActivityRecordFactory.FromObservation(
                DiagnosticTestData.MatchedObservation(),
                DiagnosticTestData.Session));
        foreach (ActivityRecord record in ActivityRecordFactory.FromAssignment(
            DiagnosticTestData.Assignment(),
            DiagnosticTestData.Session))
        {
            log.Write(record);
        }

        string content = string.Concat(ReadLines(log, store));
        Assert.DoesNotContain(DiagnosticTestData.SecretTitle, content);
        Assert.DoesNotContain(DiagnosticTestData.SecretCommandLine, content);
        Assert.DoesNotContain(DiagnosticTestData.SecretExecutablePath, content);
        Assert.DoesNotContain("marguerite", content);
        Assert.DoesNotContain("Quarterly", content);
        Assert.DoesNotContain("hunter2", content);
        Assert.Contains("Code.exe", content);
    }

    [TestMethod]
    public void UserProfilePaths_InMessagesAreRedacted()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);

        log.Write(
            Record() with
            {
                Error = new ActivityErrorDetail(
                    "test.path",
                    @"Failed while reading C:\Users\marguerite\notes.txt."),
            });

        string content = ReadLines(log, store)[0];
        Assert.Contains(@"%USERPROFILE%\\notes.txt", content);
        Assert.DoesNotContain("marguerite", content);
    }

    [TestMethod]
    public void Clear_RemovesOnlyTheFilesTheLogOwns()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);
        store.Append("user-notes.txt", "keep me");
        log.Write(Record());

        log.Clear();

        Assert.IsEmpty(log.Files());
        Assert.AreEqual("keep me\n", store.Read("user-notes.txt"));
    }

    [TestMethod]
    public void ForeignFiles_AreNotCountedTowardsTheLog()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(store);
        store.Append("activity-notes.log", "not mine");
        store.Append("readme.md", "not mine either");

        log.Write(Record());

        Assert.HasCount(1, log.Files());
        Assert.AreEqual("activity-0001.log", log.Files()[0].Name);
    }

    [TestMethod]
    public void ReadAll_ReturnsEveryRetainedFileOldestFirst()
    {
        InMemoryDiagnosticLogStore store = new();
        RollingDiagnosticLog log = new(
            store,
            new RollingDiagnosticLogOptions(MaxFileSizeBytes: 1000));

        for (int index = 0; index < 12; index++)
        {
            log.Write(Record());
        }

        IReadOnlyList<DiagnosticBundleFile> files = log.ReadAll();
        Assert.IsGreaterThan(1, files.Count);
        CollectionAssert.AreEqual(
            log.Files().Select(static file => file.Name).ToArray(),
            files.Select(static file => file.Name).ToArray());
    }

    [TestMethod]
    public void NonPositiveLimits_AreRejected()
    {
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            static () => new RollingDiagnosticLog(
                new InMemoryDiagnosticLogStore(),
                new RollingDiagnosticLogOptions(MaxFileSizeBytes: 0)));
        _ = Assert.ThrowsExactly<ArgumentOutOfRangeException>(
            static () => new RollingDiagnosticLog(
                new InMemoryDiagnosticLogStore(),
                new RollingDiagnosticLogOptions(MaxFileCount: 0)));
    }

    private static string[] ReadLines(
        RollingDiagnosticLog log,
        InMemoryDiagnosticLogStore store) =>
        log.Files()
            .SelectMany(file => store
                .Read(file.Name)
                .Split('\n', StringSplitOptions.RemoveEmptyEntries))
            .ToArray();

    private static ActivityRecord Record(
        ActivityEventSource source = ActivityEventSource.Observation,
        ActivityResult result = ActivityResult.Succeeded) =>
        new(
            Guid.NewGuid(),
            DiagnosticTestData.Session,
            DiagnosticTestData.Occurred,
            source,
            WindowEventKind.Created,
            result,
            "observation.matched",
            "Matched vscode to code by process name.",
            "Code.exe",
            DiagnosticTestData.SafeIdentity(),
            "vscode",
            "code",
            TimeSpan.FromMilliseconds(3));
}

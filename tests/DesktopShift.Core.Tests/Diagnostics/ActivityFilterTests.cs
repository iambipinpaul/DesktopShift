using DesktopShift.Core.Diagnostics;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tests.Diagnostics;

[TestClass]
public sealed class ActivityFilterTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 28, 12, 0, 0, TimeSpan.Zero);

    private static readonly Guid SessionA = Guid.Parse(
        "aaaaaaaa-0000-0000-0000-000000000001");

    private static readonly Guid SessionB = Guid.Parse(
        "bbbbbbbb-0000-0000-0000-000000000002");

    [TestMethod]
    public void EmptyFilter_AdmitsEverything()
    {
        IReadOnlyList<ActivityRecord> result =
            ActivityFilter.None.Apply(Sample(), Now);

        Assert.HasCount(Sample().Count, result);
    }

    [TestMethod]
    public void DefaultResultsArray_IsTreatedAsNoRestriction()
    {
        // A default ImmutableArray cannot be enumerated, so the filter has to
        // normalize it rather than throw when nothing was ever selected.
        ActivityFilter filter = new()
        {
            Results = default,
        };

        Assert.HasCount(Sample().Count, filter.Apply(Sample(), Now));
    }

    [TestMethod]
    public void ResultFilter_AdmitsOnlyTheSelectedResults()
    {
        ActivityFilter filter = new()
        {
            Results = [ActivityResult.Failed],
        };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.HasCount(1, result);
        Assert.AreEqual(ActivityResult.Failed, result[0].Result);
    }

    [TestMethod]
    public void ResultFilter_AdmitsSeveralResultsAtOnce()
    {
        ActivityFilter filter = new()
        {
            Results = [ActivityResult.Failed, ActivityResult.Skipped],
        };

        Assert.HasCount(3, filter.Apply(Sample(), Now));
    }

    [TestMethod]
    public void ApplicationFilter_IsCaseInsensitiveAndExact()
    {
        ActivityFilter filter = new() { Application = "code.EXE" };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.HasCount(3, result);
        Assert.IsTrue(
            result.All(static record => record.Application == "Code.exe"));
    }

    [TestMethod]
    public void ApplicationFilter_ExcludesRecordsWithNoApplication()
    {
        ActivityFilter filter = new() { Application = "Code.exe" };

        Assert.IsFalse(
            filter.Matches(
                Record(
                    ActivityResult.Skipped,
                    application: null,
                    ruleId: null),
                Now));
    }

    [TestMethod]
    public void RuleFilter_AdmitsOnlyTheNamedRule()
    {
        ActivityFilter filter = new() { RuleId = "browser" };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.HasCount(1, result);
        Assert.AreEqual("browser", result[0].RuleId);
    }

    [TestMethod]
    public void WhitespaceValues_AreTreatedAsNoRestriction()
    {
        ActivityFilter filter = new() { Application = "   ", RuleId = "" };

        Assert.HasCount(Sample().Count, filter.Apply(Sample(), Now));
    }

    [TestMethod]
    public void SessionFilter_AdmitsOnlyTheCurrentSession()
    {
        ActivityFilter filter = new()
        {
            Date = ActivityDateFilter.CurrentSession,
            SessionId = SessionB,
        };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.HasCount(1, result);
        Assert.AreEqual(SessionB, result[0].SessionId);
    }

    [TestMethod]
    public void SessionFilter_WithoutASession_AdmitsNothing()
    {
        ActivityFilter filter = new()
        {
            Date = ActivityDateFilter.CurrentSession,
            SessionId = null,
        };

        Assert.IsEmpty(filter.Apply(Sample(), Now));
    }

    [TestMethod]
    public void LastHourFilter_ExcludesOlderRecords()
    {
        ActivityFilter filter = new() { Date = ActivityDateFilter.LastHour };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.IsTrue(
            result.All(record => record.OccurredAt > Now - TimeSpan.FromHours(1)));
        Assert.HasCount(2, result);
    }

    [TestMethod]
    public void LastSevenDaysFilter_ExcludesRecordsOlderThanAWeek()
    {
        ActivityFilter filter = new()
        {
            Date = ActivityDateFilter.LastSevenDays,
        };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.HasCount(3, result);
        Assert.IsFalse(
            result.Any(static record => record.ResultCode == "observation.stale"));
    }

    [TestMethod]
    public void TodayFilter_UsesTheLocalDay()
    {
        ActivityFilter filter = new() { Date = ActivityDateFilter.Today };
        ActivityRecord today = Record(
            ActivityResult.Succeeded,
            occurredAt: Now.ToLocalTime());
        ActivityRecord yesterday = Record(
            ActivityResult.Succeeded,
            occurredAt: Now.ToLocalTime().AddDays(-1));

        Assert.IsTrue(filter.Matches(today, Now));
        Assert.IsFalse(filter.Matches(yesterday, Now));
    }

    [TestMethod]
    public void CombinedFilters_AreConjunctive()
    {
        ActivityFilter filter = new()
        {
            Results = [ActivityResult.Succeeded],
            Application = "Code.exe",
            RuleId = "vscode",
            Date = ActivityDateFilter.LastSevenDays,
        };

        IReadOnlyList<ActivityRecord> result = filter.Apply(Sample(), Now);

        Assert.HasCount(1, result);
        Assert.AreEqual("assignment.succeeded", result[0].ResultCode);
    }

    [TestMethod]
    public void Apply_PreservesSourceOrder()
    {
        IReadOnlyList<ActivityRecord> result =
            ActivityFilter.None.Apply(Sample(), Now);

        CollectionAssert.AreEqual(
            Sample().Select(static record => record.ResultCode).ToArray(),
            result.Select(static record => record.ResultCode).ToArray());
    }

    [TestMethod]
    public void FilterOptions_ListDistinctApplicationsAndRulesInOrder()
    {
        ActivityFilterOptions options = ActivityFilterOptions.From(Sample());

        CollectionAssert.AreEqual(
            new[] { "Code.exe", "msedge.exe" },
            options.Applications.ToArray());
        CollectionAssert.AreEqual(
            new[] { "browser", "vscode" },
            options.RuleIds.ToArray());
    }

    [TestMethod]
    public void FilterOptions_FromNothing_AreEmpty()
    {
        ActivityFilterOptions options =
            ActivityFilterOptions.From(Array.Empty<ActivityRecord>());

        Assert.IsEmpty(options.Applications);
        Assert.IsEmpty(options.RuleIds);
    }

    private static IReadOnlyList<ActivityRecord> Sample() =>
    [
        Record(
            ActivityResult.Succeeded,
            resultCode: "assignment.succeeded",
            occurredAt: Now.AddMinutes(-5)),
        Record(
            ActivityResult.Failed,
            resultCode: "move.failed",
            occurredAt: Now.AddMinutes(-10),
            sessionId: SessionB),
        Record(
            ActivityResult.Skipped,
            resultCode: "switch.suppressed",
            occurredAt: Now.AddDays(-2)),
        Record(
            ActivityResult.Skipped,
            resultCode: "observation.stale",
            application: "msedge.exe",
            ruleId: "browser",
            occurredAt: Now.AddDays(-30)),
    ];

    private static ActivityRecord Record(
        ActivityResult result,
        string resultCode = "observation.matched",
        string? application = "Code.exe",
        string? ruleId = "vscode",
        DateTimeOffset? occurredAt = null,
        Guid? sessionId = null) =>
        new(
            Guid.NewGuid(),
            sessionId ?? SessionA,
            occurredAt ?? Now,
            ActivityEventSource.Observation,
            WindowEventKind.Created,
            result,
            resultCode,
            "Summary.",
            application,
            application is null
                ? null
                : new WindowSafeIdentity(application, null, null, "Window"),
            ruleId,
            "code");
}

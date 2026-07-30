using System.Text.RegularExpressions;
using DesktopShift.Windows.Tests.Security;

namespace DesktopShift.Windows.Tests.Performance;

/// <summary>
/// One way of making work happen over and over, and why DesktopShift does not use
/// it.
/// </summary>
/// <param name="Name">What the pattern is called, for the failure message.</param>
/// <param name="Pattern">
/// The construct that proves work recurs. Patterns name APIs and language
/// constructs rather than English words, so prose can explain why a loop is absent
/// without tripping the scan.
/// </param>
/// <param name="Rationale">Why it is refused, and what is used instead.</param>
internal sealed record RecurringWorkPattern(
    string Name,
    string Pattern,
    string Rationale);

/// <summary>
/// Fails if the shipping source contains anything that does work on a schedule.
/// </summary>
/// <remarks>
/// <para>
/// The whole performance story rests on one claim: DesktopShift does nothing until
/// Windows tells it something happened. Window events arrive from an
/// out-of-context WinEvent hook, desktop changes from a topology notification, and
/// shell disruptions from Windows messages. Every one of those is a push. Nothing
/// asks.
/// </para>
/// <para>
/// That claim is worth exactly as much as the evidence behind it, so it is checked
/// against the repository's own files on every run rather than asserted in a
/// document. A single <c>PeriodicTimer</c> added in good faith would make the
/// reported idle cost a fiction, and it would fail no other test.
/// </para>
/// <para>
/// One-shot timers are deliberately not forbidden, and cannot be: the coalescing
/// window and the open-window follow grace period are each a timer that fires once
/// and is disposed. What is forbidden is a <em>period</em> — a timer that rearms,
/// or a wait by the clock that a loop goes back round to.
/// </para>
/// </remarks>
[TestClass]
public sealed class NoRecurringPollingScanTests
{
    private static readonly RecurringWorkPattern[] Patterns =
    [
        new(
            "periodic timer",
            @"\bPeriodicTimer\b",
            "A timer whose entire purpose is to fire again. Window events come " +
            "from a WinEvent hook and desktop changes from a topology " +
            "notification, so there is nothing to wake up and check."),
        new(
            "rearmed timer",
            @"\.Change\s*\(",
            "Giving a timer a new due time after it fired, which is how a " +
            "one-shot timer becomes a polling loop. The two timers DesktopShift " +
            "owns are created with an infinite period and disposed after firing."),
        new(
            "waiting by the clock",
            @"\bThread\.Sleep\b|\bTask\.Delay\b|\bSpinWait\.SpinUntil\b|\bThread\.SpinWait\b",
            "Waiting for time to pass rather than for something to happen. Every " +
            "wait in DesktopShift is on a channel, a semaphore, a timer that " +
            "fires once, or a Windows message."),
        new(
            "process enumeration",
            @"\bProcess\.GetProcesses\b|\bProcess\.GetProcessesByName\b|\bEnumProcesses\b",
            "Listing the machine's processes. Identity is resolved for the one " +
            "process that owns the window an event named, and for no other."),
        new(
            "desktop polling",
            @"\bwhile\s*\([^)]*\)\s*\{[^}]*\b(EnumerateDesktopsAsync|GetCurrentDesktopIdAsync)\b",
            "Reading the desktop inventory in a loop. Topology changes arrive as " +
            "a notification, so the inventory is read in answer to one."),
    ];

    /// <summary>
    /// The two files allowed to enumerate top-level windows, and what asks them
    /// to.
    /// </summary>
    /// <remarks>
    /// Enumeration is the most expensive read in the application, so where it can
    /// happen from is pinned by name. Both of these are driven by intent — a
    /// startup pass, the user pressing Reassign All, or the user opening the rule
    /// editor's application picker — and neither is reachable from a timer.
    /// </remarks>
    private static readonly string[] FilesAllowedToEnumerateWindows =
    [
        "WindowReassignmentService.cs",
        "RunningApplicationInventory.cs",
    ];

    [TestMethod]
    public void ShippingSource_SchedulesNoRecurringWork()
    {
        IReadOnlyList<SourceFile>? sources = TryReadShippingSources();
        if (sources is null)
        {
            return;
        }

        List<string> violations = [];
        foreach (SourceFile source in sources)
        {
            foreach (RecurringWorkPattern pattern in Patterns)
            {
                foreach (Match match in Matches(source.Code, pattern.Pattern))
                {
                    violations.Add(
                        $"{source.RelativePath}: '{Condense(match.Value)}' is " +
                        $"{pattern.Name}. {pattern.Rationale}");
                }
            }
        }

        Assert.IsEmpty(
            violations,
            "Recurring work found in shipping source, which would make the " +
            "reported idle cost untrue:" +
            Environment.NewLine +
            string.Join(Environment.NewLine, violations));
    }

    [TestMethod]
    public void EveryTimerTheApplicationOwns_IsCreatedToFireExactlyOnce()
    {
        // The positive half of the claim. The scan above proves no timer is
        // rearmed; this proves the timers that exist were each given an infinite
        // period in the first place.
        IReadOnlyList<SourceFile>? sources = TryReadShippingSources();
        if (sources is null)
        {
            return;
        }

        int constructions = 0;
        int infinitePeriods = 0;
        foreach (SourceFile source in sources)
        {
            constructions += Matches(source.Code, @"\bnew\s+Timer\s*\(").Count;
            infinitePeriods +=
                Matches(source.Code, @"\bTimeout\.InfiniteTimeSpan\b").Count;
        }

        Assert.AreEqual(
            2,
            constructions,
            "The coalescing window and the open-window follow grace period are " +
            "the only timers DesktopShift owns. A different count means one was " +
            "added or removed, and either way this test and " +
            "docs/performance/budgets.md need re-reading.");
        Assert.AreEqual(
            constructions,
            infinitePeriods,
            "A timer was constructed without naming Timeout.InfiniteTimeSpan as " +
            "its period, so it may fire more than once.");
    }

    [TestMethod]
    public void TopLevelWindowEnumeration_HappensOnlyWhereIntentDrivesIt()
    {
        // Enumerating every top-level window is the most expensive read in the
        // application. Where it is reachable from is therefore pinned by file
        // name rather than left to review.
        IReadOnlyList<SourceFile>? sources = TryReadShippingSources();
        if (sources is null)
        {
            return;
        }

        List<string> callSites = [];
        foreach (SourceFile source in sources)
        {
            if (Matches(source.Code, @"windowEnumerator\.Enumerate\s*\(").Count > 0)
            {
                callSites.Add(Path.GetFileName(source.RelativePath));
            }
        }

        CollectionAssert.AreEquivalent(
            FilesAllowedToEnumerateWindows,
            callSites.ToArray(),
            "Top-level window enumeration moved. It may only be reached from a " +
            "startup pass, the user's Reassign All, and the rule editor's " +
            "application picker — never from anything that recurs.");
    }

    [TestMethod]
    public void Scanner_DetectsAPeriodicTimerWrittenAsCode()
    {
        // The control that makes the scan meaningful: without it, a pattern that
        // silently stopped matching would read as a clean repository.
        const string offending = """
            internal sealed class Poller
            {
                private readonly PeriodicTimer timer = new(TimeSpan.FromSeconds(5));
            }
            """;

        AssertDetected(offending);
    }

    [TestMethod]
    public void Scanner_DetectsARearmedTimer()
    {
        AssertDetected("""
            internal sealed class Rearming
            {
                private void Fired(Timer timer) => timer.Change(1000, 1000);
            }
            """);
    }

    [TestMethod]
    public void Scanner_DetectsASleepingLoop()
    {
        AssertDetected("""
            internal sealed class Waiting
            {
                private static async Task RunAsync()
                {
                    while (true)
                    {
                        await Task.Delay(500);
                    }
                }
            }
            """);
    }

    [TestMethod]
    public void Scanner_DetectsADesktopInventoryLoop()
    {
        AssertDetected("""
            internal sealed class Watching
            {
                private static async Task WatchAsync(IDesktopTopologyProvider provider)
                {
                    while (!done) { await provider.EnumerateDesktopsAsync(); }
                }
            }
            """);
    }

    [TestMethod]
    public void Scanner_AcceptsAOneShotTimer()
    {
        // The coalescing window and the follow grace period are both this shape. A
        // scanner that punished them would force the codebase to poll instead,
        // which is the opposite of the point.
        AssertClean("""
            internal sealed class Once
            {
                private static IDisposable Schedule(TimeSpan due, Action callback) =>
                    new Timer(_ => callback(), null, due, Timeout.InfiniteTimeSpan);
            }
            """);
    }

    [TestMethod]
    public void Scanner_AcceptsASingleEnumerationDrivenByACall()
    {
        // A startup pass and the user's Reassign All each enumerate once. The
        // pattern has to be able to tell that apart from a loop that keeps asking.
        AssertClean("""
            internal sealed class OncePerRequest
            {
                private static void Reconcile(ITopLevelWindowEnumerator enumerator)
                {
                    foreach (nint handle in enumerator.Enumerate())
                    {
                        Place(handle);
                    }
                }
            }
            """);
    }

    [TestMethod]
    public void Scanner_IgnoresRecurringWorkNamedOnlyInAComment()
    {
        // The codebase explains, in the files where it matters, why it does not
        // poll. A scanner that tripped on those explanations would push them out
        // of the code.
        AssertClean("""
            /// <summary>
            /// No PeriodicTimer and no Task.Delay loop exists here. Nothing calls
            /// Thread.Sleep or Process.GetProcesses either.
            /// </summary>
            internal static class Honest
            {
                // A rearming timer.Change(0, 100) would be wrong here.
                internal static int Value => 1; /* while (true) { EnumProcesses(); } */
            }
            """);
    }

    private static void AssertDetected(string offending)
    {
        string code = RepositorySource.RemoveComments(offending);

        Assert.IsTrue(
            Patterns.Any(pattern => Regex.IsMatch(
                code,
                pattern.Pattern,
                RegexOptions.Singleline,
                TimeSpan.FromSeconds(5))),
            $"The scanner missed recurring work in: {offending}");
    }

    private static void AssertClean(string acceptable)
    {
        string code = RepositorySource.RemoveComments(acceptable);

        foreach (RecurringWorkPattern pattern in Patterns)
        {
            Assert.IsFalse(
                Regex.IsMatch(
                    code,
                    pattern.Pattern,
                    RegexOptions.Singleline,
                    TimeSpan.FromSeconds(5)),
                $"Acceptable code was treated as {pattern.Name}: {acceptable}");
        }
    }

    /// <summary>
    /// Reads the shipping source, or reports inconclusive and returns null when
    /// the tree is not next to the test assembly.
    /// </summary>
    /// <remarks>
    /// Absence of the source is not evidence of a polling loop, so a published
    /// test assembly reports inconclusive rather than passing or failing.
    /// </remarks>
    private static IReadOnlyList<SourceFile>? TryReadShippingSources()
    {
        string? root = RepositorySource.TryFindRoot();
        if (root is null)
        {
            Assert.Inconclusive(
                "The repository source tree is not present next to the test assembly.");
            return null;
        }

        IReadOnlyList<SourceFile> sources =
            RepositorySource.ReadShippingSources(root);
        Assert.IsGreaterThan(
            0,
            sources.Count,
            "No shipping source was found, so the scan proved nothing.");
        return sources;
    }

    private static MatchCollection Matches(string code, string pattern) =>
        Regex.Matches(
            code,
            pattern,
            RegexOptions.Singleline,
            TimeSpan.FromSeconds(5));

    /// <summary>
    /// Collapses a multi-line match onto one line, so a failure message stays
    /// readable.
    /// </summary>
    private static string Condense(string value) =>
        Regex.Replace(value, @"\s+", " ", RegexOptions.None, TimeSpan.FromSeconds(5))
            .Trim();
}

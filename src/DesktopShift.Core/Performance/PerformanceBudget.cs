using System.Collections.Immutable;
using System.Globalization;

namespace DesktopShift.Core.Performance;

/// <summary>
/// The numbers DesktopShift promises on an ordinary local desktop, and the
/// smallest measurement that is allowed to hold it to them.
/// </summary>
/// <remarks>
/// <para>
/// The workload these describe is written down in
/// <c>docs/performance/budgets.md</c>. In one sentence: a signed-in local
/// session, a handful of Managed Desktops already created, rules that name a few
/// applications, and windows opening and being activated one at a time the way a
/// person opens them. It is deliberately not a stress test — bursts have their
/// own tests, and a burst is not what responsiveness should be judged on.
/// </para>
/// <para>
/// The latency figures are compared against
/// <see cref="AssignmentLatencySample.PipelineLatency"/>: event receipt through
/// move completion, with the open-window follow grace period taken out. The wait
/// is a deliberate policy, and a budget that counted it would only be passable by
/// abandoning the policy.
/// </para>
/// </remarks>
/// <param name="AssignmentLatencyP50">What half of all assignments must beat.</param>
/// <param name="AssignmentLatencyP95">What 95 in 100 must beat.</param>
/// <param name="AssignmentLatencyP99">What 99 in 100 must beat.</param>
/// <param name="IdleCpuPercent">
/// The share of the machine the process may use while nothing is happening.
/// </param>
/// <param name="MinimumSampleCount">
/// How many assignments have to have been measured before a latency figure is
/// allowed to fail a release. Below this the report says the budget is untested
/// rather than met or missed, because three samples cannot carry a P99.
/// </param>
public sealed record PerformanceBudget(
    TimeSpan AssignmentLatencyP50,
    TimeSpan AssignmentLatencyP95,
    TimeSpan AssignmentLatencyP99,
    double IdleCpuPercent,
    int MinimumSampleCount = 20)
{
    /// <summary>
    /// The documented normal-local workload budget.
    /// </summary>
    /// <remarks>
    /// 30 / 100 / 150 milliseconds and one percent of the machine. The upper end
    /// is set by what a person notices: a window that lands within about a tenth
    /// of a second reads as instant, and one that takes longer than about a
    /// seventh of a second reads as the application thinking about it.
    /// </remarks>
    public static PerformanceBudget NormalLocal { get; } = new(
        TimeSpan.FromMilliseconds(30),
        TimeSpan.FromMilliseconds(100),
        TimeSpan.FromMilliseconds(150),
        1d);
}

/// <summary>
/// One budget figure that was missed, said plainly enough to paste into a
/// release decision.
/// </summary>
/// <param name="Metric">Which figure was missed.</param>
/// <param name="Budget">What it was supposed to be.</param>
/// <param name="Measured">What it actually was.</param>
/// <param name="Consequence">What the miss means for somebody using the app.</param>
public sealed record PerformanceBudgetBreach(
    string Metric,
    string Budget,
    string Measured,
    string Consequence)
{
    /// <summary>The breach as one line of a release blocker list.</summary>
    public override string ToString() =>
        $"{Metric}: {Measured} against a budget of {Budget}. {Consequence}";
}

/// <summary>
/// Whether a measured run met the budget, and what blocks a release when it did
/// not.
/// </summary>
/// <remarks>
/// A run that missed a figure does not silently pass and does not merely warn: it
/// produces a <see cref="PerformanceBudgetBreach"/> per missed figure, and
/// <see cref="IsReleaseBlocked"/> is true. That is the whole contract — either the
/// numbers are met, or the reason they were not is written down as a blocker.
/// </remarks>
/// <param name="Budget">The budget the run was judged against.</param>
/// <param name="IsTested">
/// Whether enough assignments were measured for the latency figures to mean
/// anything. An untested run is neither a pass nor a blocker.
/// </param>
/// <param name="Breaches">Every figure that was missed.</param>
public sealed record PerformanceBudgetVerdict(
    PerformanceBudget Budget,
    bool IsTested,
    ImmutableArray<PerformanceBudgetBreach> Breaches)
{
    /// <summary>Whether every figure the run could test came in under budget.</summary>
    public bool IsMet => Breaches.IsDefaultOrEmpty;

    /// <summary>Whether this run must not ship as it stands.</summary>
    public bool IsReleaseBlocked => !IsMet;

    /// <summary>The verdict in a sentence.</summary>
    public string Summary => IsReleaseBlocked
        ? $"{Breaches.Length} performance budget " +
            (Breaches.Length == 1 ? "figure was" : "figures were") +
            " missed, which blocks release."
        : IsTested
            ? "Every performance budget figure was met."
            : "Too few assignments were measured to test the latency budget.";

    /// <summary>
    /// Judges a report's measurements against a budget.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Latency is judged only once <see cref="PerformanceBudget.MinimumSampleCount"/>
    /// assignments have been measured. Idle processor use is judged whenever
    /// there was a measurable interval to judge it over, because it does not need
    /// a population — it is one reading against one ceiling.
    /// </para>
    /// <para>
    /// Queue drops and the coalescing rate are reported but not budgeted. A drop
    /// already has its own alarm in
    /// <see cref="Observation.WindowEventQueueSnapshot.Dropped"/>, and coalescing
    /// is a rate whose right value depends entirely on what the user was doing.
    /// </para>
    /// </remarks>
    /// <param name="latency">The measured assignment latency.</param>
    /// <param name="idleCpuPercent">The measured processor share.</param>
    /// <param name="hasCpuMeasurement">
    /// Whether the interval was long enough for the processor share to mean
    /// anything.
    /// </param>
    /// <param name="budget">The figures to judge against.</param>
    /// <returns>The verdict, with a blocker per missed figure.</returns>
    public static PerformanceBudgetVerdict Evaluate(
        LatencySummary latency,
        double idleCpuPercent,
        bool hasCpuMeasurement,
        PerformanceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(latency);
        ArgumentNullException.ThrowIfNull(budget);

        ImmutableArray<PerformanceBudgetBreach>.Builder breaches =
            ImmutableArray.CreateBuilder<PerformanceBudgetBreach>();
        bool isTested = latency.SampleCount >= budget.MinimumSampleCount;

        if (isTested)
        {
            AddLatencyBreach(
                breaches,
                "Assignment latency P50",
                latency.P50,
                budget.AssignmentLatencyP50,
                "Half of all windows take longer than the budget to land, so " +
                "assignment reads as sluggish rather than immediate.");
            AddLatencyBreach(
                breaches,
                "Assignment latency P95",
                latency.P95,
                budget.AssignmentLatencyP95,
                "One window in twenty is slow enough for the user to watch it " +
                "move.");
            AddLatencyBreach(
                breaches,
                "Assignment latency P99",
                latency.P99,
                budget.AssignmentLatencyP99,
                "The worst one percent is slow enough to look like the " +
                "application hung.");
        }

        if (hasCpuMeasurement && idleCpuPercent > budget.IdleCpuPercent)
        {
            breaches.Add(
                new PerformanceBudgetBreach(
                    "Idle processor use",
                    FormatPercent(budget.IdleCpuPercent),
                    FormatPercent(idleCpuPercent),
                    "A background application that costs measurable processor " +
                    "time while doing nothing costs battery and heat all day."));
        }

        return new PerformanceBudgetVerdict(budget, isTested, breaches.ToImmutable());
    }

    private static void AddLatencyBreach(
        ImmutableArray<PerformanceBudgetBreach>.Builder breaches,
        string metric,
        TimeSpan measured,
        TimeSpan budget,
        string consequence)
    {
        if (measured >= budget)
        {
            breaches.Add(
                new PerformanceBudgetBreach(
                    metric,
                    FormatDuration(budget),
                    FormatDuration(measured),
                    consequence));
        }
    }

    private static string FormatDuration(TimeSpan value) =>
        string.Create(
            CultureInfo.InvariantCulture,
            $"{value.TotalMilliseconds:0.###} ms");

    private static string FormatPercent(double value) =>
        string.Create(CultureInfo.InvariantCulture, $"{value:0.###}%");
}

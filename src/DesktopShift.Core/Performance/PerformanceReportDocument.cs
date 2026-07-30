using System.Collections.Immutable;

namespace DesktopShift.Core.Performance;

/// <summary>One latency series, as a diagnostic bundle carries it.</summary>
/// <param name="ObservedCount">How many durations were recorded in total.</param>
/// <param name="SampleCount">How many of those the summary describes.</param>
/// <param name="P50Milliseconds">The median.</param>
/// <param name="P95Milliseconds">The 95th percentile.</param>
/// <param name="P99Milliseconds">The 99th percentile.</param>
/// <param name="MaxMilliseconds">The slowest.</param>
/// <param name="MeanMilliseconds">The average.</param>
public sealed record LatencySeriesDocument(
    long ObservedCount,
    int SampleCount,
    double P50Milliseconds,
    double P95Milliseconds,
    double P99Milliseconds,
    double MaxMilliseconds,
    double MeanMilliseconds)
{
    public static LatencySeriesDocument From(LatencySummary summary)
    {
        ArgumentNullException.ThrowIfNull(summary);
        return new LatencySeriesDocument(
            summary.ObservedCount,
            summary.SampleCount,
            summary.P50.TotalMilliseconds,
            summary.P95.TotalMilliseconds,
            summary.P99.TotalMilliseconds,
            summary.Max.TotalMilliseconds,
            summary.Mean.TotalMilliseconds);
    }
}

/// <summary>The budget a bundle's report was judged against.</summary>
public sealed record PerformanceBudgetDocument(
    double AssignmentLatencyP50Milliseconds,
    double AssignmentLatencyP95Milliseconds,
    double AssignmentLatencyP99Milliseconds,
    double IdleCpuPercent,
    int MinimumSampleCount)
{
    public static PerformanceBudgetDocument From(PerformanceBudget budget)
    {
        ArgumentNullException.ThrowIfNull(budget);
        return new PerformanceBudgetDocument(
            budget.AssignmentLatencyP50.TotalMilliseconds,
            budget.AssignmentLatencyP95.TotalMilliseconds,
            budget.AssignmentLatencyP99.TotalMilliseconds,
            budget.IdleCpuPercent,
            budget.MinimumSampleCount);
    }
}

/// <summary>One release blocker, as a diagnostic bundle carries it.</summary>
public sealed record PerformanceBudgetBreachDocument(
    string Metric,
    string Budget,
    string Measured,
    string Consequence)
{
    public static PerformanceBudgetBreachDocument From(
        PerformanceBudgetBreach breach)
    {
        ArgumentNullException.ThrowIfNull(breach);
        return new PerformanceBudgetBreachDocument(
            breach.Metric,
            breach.Budget,
            breach.Measured,
            breach.Consequence);
    }
}

/// <summary>
/// A performance report flattened for a diagnostic bundle.
/// </summary>
/// <remarks>
/// <para>
/// Durations are milliseconds and memory is megabytes, both as plain numbers, so
/// a maintainer reading the archive does not have to parse a
/// <see cref="TimeSpan"/> literal or divide by 1024 twice to compare two runs.
/// </para>
/// <para>
/// <see cref="IdleCpuPercent"/> is null rather than zero when the measured
/// interval was too short to answer. Those are very different findings, and a
/// bundle that reported the second as the first would be claiming a result it
/// never had.
/// </para>
/// <para>
/// Nothing here carries window identity, a path, or a name the user typed. The
/// report is counts and durations, which is why it can be exported without
/// touching the privacy rules the rest of the bundle lives under.
/// </para>
/// </remarks>
public sealed record PerformanceReportDocument(
    DateTimeOffset ObservedAtUtc,
    double MeasuredOverSeconds,
    LatencySeriesDocument EventToMoveLatency,
    LatencySeriesDocument AssignmentLatency,
    LatencySeriesDocument DesktopCreation,
    LatencySeriesDocument DesktopSwitch,
    long AssignmentsMeasured,
    long AssignmentsMoved,
    long DesktopsCreated,
    int QueueCapacity,
    long QueueAccepted,
    long QueueRead,
    long QueueDropped,
    long QueueSaturationEpisodes,
    long CoalescingEvaluated,
    long CoalescingCoalesced,
    double CoalescingRate,
    double? IdleCpuPercent,
    double WorkingSetMegabytes,
    double PrivateMemoryMegabytes,
    bool BudgetMet,
    bool BudgetTested,
    bool ReleaseBlocked,
    string Summary,
    PerformanceBudgetDocument Budget,
    ImmutableArray<PerformanceBudgetBreachDocument> ReleaseBlockers)
{
    private const double BytesPerMegabyte = 1024d * 1024d;

    public static PerformanceReportDocument From(PerformanceReport report)
    {
        ArgumentNullException.ThrowIfNull(report);

        return new PerformanceReportDocument(
            report.ObservedAtUtc,
            report.MeasuredOver.TotalSeconds,
            LatencySeriesDocument.From(report.EventToMoveLatency),
            LatencySeriesDocument.From(report.AssignmentLatency),
            LatencySeriesDocument.From(report.DesktopCreation),
            LatencySeriesDocument.From(report.DesktopSwitch),
            report.AssignmentsMeasured,
            report.AssignmentsMoved,
            report.DesktopsCreated,
            report.Queue.Capacity,
            report.Queue.Accepted,
            report.Queue.Read,
            report.Queue.Dropped,
            report.Queue.SaturationEpisodes,
            report.Coalescing.Evaluated,
            report.Coalescing.Coalesced,
            report.CoalescingRate,
            report.HasCpuMeasurement ? report.IdleCpuPercent : null,
            report.WorkingSetBytes / BytesPerMegabyte,
            report.PrivateMemoryBytes / BytesPerMegabyte,
            report.Verdict.IsMet,
            report.Verdict.IsTested,
            report.Verdict.IsReleaseBlocked,
            report.Verdict.Summary,
            PerformanceBudgetDocument.From(report.Verdict.Budget),
            [
                .. report.Verdict.Breaches.Select(
                    PerformanceBudgetBreachDocument.From),
            ]);
    }
}

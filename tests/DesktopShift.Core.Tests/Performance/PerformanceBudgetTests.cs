using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// The rule that a missed figure is written down rather than warned about.
/// </summary>
/// <remarks>
/// The acceptance this covers is "meets the budget, or records an explicit release
/// blocker". A verdict that reported a miss as a pass, or that reported it only in
/// prose, would satisfy neither half.
/// </remarks>
[TestClass]
public sealed class PerformanceBudgetTests
{
    [TestMethod]
    public void TheNormalLocalBudget_IsTheDocumentedOne()
    {
        // Pinned as literals. These numbers appear in docs/performance/budgets.md
        // and in the release checklist, so drifting them silently would make both
        // untrue.
        PerformanceBudget budget = PerformanceBudget.NormalLocal;

        Assert.AreEqual(TimeSpan.FromMilliseconds(30), budget.AssignmentLatencyP50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), budget.AssignmentLatencyP95);
        Assert.AreEqual(TimeSpan.FromMilliseconds(150), budget.AssignmentLatencyP99);
        Assert.AreEqual(1d, budget.IdleCpuPercent);
    }

    [TestMethod]
    public void EveryFigureMet_PassesWithNoBlockers()
    {
        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            Measured(p50: 8, p95: 40, p99: 90, sampleCount: 500),
            idleCpuPercent: 0.2,
            hasCpuMeasurement: true,
            PerformanceBudget.NormalLocal);

        Assert.IsTrue(verdict.IsTested);
        Assert.IsTrue(verdict.IsMet);
        Assert.IsFalse(verdict.IsReleaseBlocked);
        Assert.IsEmpty(verdict.Breaches);
        Assert.AreEqual("Every performance budget figure was met.", verdict.Summary);
    }

    [TestMethod]
    public void AFigureExactlyOnTheBudget_IsAMiss()
    {
        // The budget says "below 30 ms", not "at most 30 ms". A figure sitting
        // exactly on the line has no headroom left and must not read as a pass.
        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            Measured(p50: 30, p95: 40, p99: 90, sampleCount: 500),
            idleCpuPercent: 0d,
            hasCpuMeasurement: true,
            PerformanceBudget.NormalLocal);

        Assert.IsTrue(verdict.IsReleaseBlocked);
        Assert.HasCount(1, verdict.Breaches);
        Assert.AreEqual("Assignment latency P50", verdict.Breaches[0].Metric);
    }

    [TestMethod]
    public void EachMissedFigure_BecomesItsOwnReleaseBlocker()
    {
        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            Measured(p50: 45, p95: 220, p99: 480, sampleCount: 250),
            idleCpuPercent: 3.5,
            hasCpuMeasurement: true,
            PerformanceBudget.NormalLocal);

        Assert.IsFalse(verdict.IsMet);
        Assert.IsTrue(verdict.IsReleaseBlocked);
        Assert.HasCount(4, verdict.Breaches);
        Assert.AreEqual(
            "4 performance budget figures were missed, which blocks release.",
            verdict.Summary);

        // Each blocker has to be readable on its own: what was missed, by how
        // much, and what it costs the person using the application.
        foreach (PerformanceBudgetBreach breach in verdict.Breaches)
        {
            Assert.IsNotEmpty(breach.Metric);
            Assert.IsNotEmpty(breach.Budget);
            Assert.IsNotEmpty(breach.Measured);
            Assert.IsNotEmpty(breach.Consequence);
            Assert.Contains(breach.Metric, breach.ToString());
            Assert.Contains(breach.Measured, breach.ToString());
        }

        // Formatted invariantly, so a blocker pasted from a machine with a comma
        // decimal separator still reads the same.
        Assert.Contains("45 ms", verdict.Breaches[0].Measured);
        Assert.Contains("30 ms", verdict.Breaches[0].Budget);
        Assert.AreEqual("Idle processor use", verdict.Breaches[^1].Metric);
        Assert.Contains("3.5%", verdict.Breaches[^1].Measured);
    }

    [TestMethod]
    public void TooFewSamples_ReportsUntestedRatherThanEitherAnswer()
    {
        // Three samples cannot carry a P99. Reporting a pass would be a claim
        // nothing supports; reporting a blocker would fail a release over noise.
        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            Measured(p50: 900, p95: 900, p99: 900, sampleCount: 3),
            idleCpuPercent: 0.1,
            hasCpuMeasurement: true,
            PerformanceBudget.NormalLocal);

        Assert.IsFalse(verdict.IsTested);
        Assert.IsTrue(verdict.IsMet);
        Assert.IsFalse(verdict.IsReleaseBlocked);
        Assert.AreEqual(
            "Too few assignments were measured to test the latency budget.",
            verdict.Summary);
    }

    [TestMethod]
    public void IdleProcessorUse_IsJudgedEvenWhenNoAssignmentWasMeasured()
    {
        // A process burning the machine while assigning nothing is the worst case
        // of all, and it is exactly the case with no latency samples in it.
        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            LatencySummary.Empty,
            idleCpuPercent: 6d,
            hasCpuMeasurement: true,
            PerformanceBudget.NormalLocal);

        Assert.IsFalse(verdict.IsTested);
        Assert.IsTrue(verdict.IsReleaseBlocked);
        Assert.HasCount(1, verdict.Breaches);
        Assert.AreEqual("Idle processor use", verdict.Breaches[0].Metric);
    }

    [TestMethod]
    public void AnUnmeasurableInterval_IsNotJudgedAsZero()
    {
        // Over a few milliseconds a single scheduler tick swings the answer past
        // the whole budget, so an interval too short to measure must not be able
        // to produce a pass.
        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            LatencySummary.Empty,
            idleCpuPercent: 90d,
            hasCpuMeasurement: false,
            PerformanceBudget.NormalLocal);

        Assert.IsEmpty(verdict.Breaches);
        Assert.IsFalse(verdict.IsReleaseBlocked);
    }

    [TestMethod]
    public void ABudgetIsCarriedByItsVerdict()
    {
        // A blocker is only actionable next to the figures it was judged against,
        // so the verdict carries the budget rather than referring to a default.
        PerformanceBudget strict = new(
            TimeSpan.FromMilliseconds(5),
            TimeSpan.FromMilliseconds(10),
            TimeSpan.FromMilliseconds(15),
            0.1,
            MinimumSampleCount: 2);

        PerformanceBudgetVerdict verdict = PerformanceBudgetVerdict.Evaluate(
            Measured(p50: 6, p95: 6, p99: 6, sampleCount: 2),
            idleCpuPercent: 0d,
            hasCpuMeasurement: true,
            strict);

        Assert.AreSame(strict, verdict.Budget);
        Assert.IsTrue(verdict.IsTested);
        Assert.IsTrue(verdict.IsReleaseBlocked);
    }

    private static LatencySummary Measured(
        double p50,
        double p95,
        double p99,
        int sampleCount) =>
        new(
            sampleCount,
            sampleCount,
            TimeSpan.FromMilliseconds(p50),
            TimeSpan.FromMilliseconds(p95),
            TimeSpan.FromMilliseconds(p99),
            TimeSpan.FromMilliseconds(p99),
            TimeSpan.FromMilliseconds(p50));
}

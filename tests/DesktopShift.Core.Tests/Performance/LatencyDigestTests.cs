using DesktopShift.Core.Performance;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// The arithmetic every performance claim rests on.
/// </summary>
/// <remarks>
/// A percentile that is quietly wrong would let a slow build pass its budget, so
/// the ranks are asserted against a series whose answers can be counted by hand
/// rather than against a distribution.
/// </remarks>
[TestClass]
public sealed class LatencyDigestTests
{
    [TestMethod]
    public void NothingRecorded_SummarisesAsEmptyRatherThanZero()
    {
        // Zero milliseconds and "never measured" are different findings, and a
        // budget must not be able to pass on the second.
        LatencyDigest digest = new();

        LatencySummary summary = digest.Summarize();

        Assert.AreSame(LatencySummary.Empty, summary);
        Assert.IsFalse(summary.HasSamples);
        Assert.AreEqual(0, summary.SampleCount);
        Assert.AreEqual(0L, summary.ObservedCount);
    }

    [TestMethod]
    public void OneHundredSamples_PutEachPercentileOnACountableRank()
    {
        // 1 through 100 milliseconds, so the nearest-rank answer for each
        // percentile is the millisecond of the same number.
        LatencyDigest digest = new();
        for (int value = 1; value <= 100; value++)
        {
            digest.Record(TimeSpan.FromMilliseconds(value));
        }

        LatencySummary summary = digest.Summarize();

        Assert.AreEqual(100, summary.SampleCount);
        Assert.AreEqual(100L, summary.ObservedCount);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50), summary.P50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(95), summary.P95);
        Assert.AreEqual(TimeSpan.FromMilliseconds(99), summary.P99);
        Assert.AreEqual(TimeSpan.FromMilliseconds(100), summary.Max);
        Assert.AreEqual(TimeSpan.FromMilliseconds(50.5), summary.Mean);
    }

    [TestMethod]
    public void PercentilesReadTheOrderOfTheSeries_NotTheOrderItArrivedIn()
    {
        LatencyDigest ascending = new();
        LatencyDigest shuffled = new();
        int[] values = [40, 10, 90, 20, 70, 30, 60, 50, 80, 100];

        foreach (int value in values.Order())
        {
            ascending.Record(TimeSpan.FromMilliseconds(value));
        }

        foreach (int value in values)
        {
            shuffled.Record(TimeSpan.FromMilliseconds(value));
        }

        Assert.AreEqual(ascending.Summarize(), shuffled.Summarize());
    }

    [TestMethod]
    public void ASingleSample_IsEveryPercentile()
    {
        LatencyDigest digest = new();
        digest.Record(TimeSpan.FromMilliseconds(7));

        LatencySummary summary = digest.Summarize();

        Assert.AreEqual(TimeSpan.FromMilliseconds(7), summary.P50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), summary.P95);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), summary.P99);
        Assert.AreEqual(TimeSpan.FromMilliseconds(7), summary.Max);
    }

    [TestMethod]
    public void PastCapacity_TheRecentSamplesAreKeptAndTheTotalStillCounts()
    {
        // A long session must cost the same as a short one, and a reader has to
        // be able to tell "the last 4" from "all 12".
        LatencyDigest digest = new(capacity: 4);
        for (int value = 1; value <= 12; value++)
        {
            digest.Record(TimeSpan.FromMilliseconds(value));
        }

        LatencySummary summary = digest.Summarize();

        Assert.AreEqual(4, digest.Capacity);
        Assert.AreEqual(4, summary.SampleCount);
        Assert.AreEqual(12L, summary.ObservedCount);
        Assert.AreEqual(12L, digest.ObservedCount);

        // 9, 10, 11, 12 remain. The mean proves the overwritten samples left the
        // running total rather than being added to it forever.
        Assert.AreEqual(TimeSpan.FromMilliseconds(12), summary.Max);
        Assert.AreEqual(TimeSpan.FromMilliseconds(10.5), summary.Mean);

        // The second of four by nearest rank, which is a duration one of them
        // actually took rather than the 10.5 an interpolated median would invent.
        Assert.AreEqual(TimeSpan.FromMilliseconds(10), summary.P50);
    }

    [TestMethod]
    public void ANegativeDuration_IsClampedRatherThanRefused()
    {
        // Durations come from a substitutable clock. One that runs backwards must
        // not be able to throw from an assignment or poison a percentile.
        LatencyDigest digest = new();

        digest.Record(TimeSpan.FromMilliseconds(-5));
        digest.Record(TimeSpan.FromMilliseconds(5));

        LatencySummary summary = digest.Summarize();

        Assert.AreEqual(2, summary.SampleCount);
        Assert.AreEqual(TimeSpan.Zero, summary.P50);
        Assert.AreEqual(TimeSpan.FromMilliseconds(5), summary.Max);
    }

    [TestMethod]
    public void Reset_DropsTheSeriesAndTheTotalWithIt()
    {
        LatencyDigest digest = new();
        digest.Record(TimeSpan.FromMilliseconds(11));

        digest.Reset();

        Assert.AreEqual(0L, digest.ObservedCount);
        Assert.IsFalse(digest.Summarize().HasSamples);
    }

    [TestMethod]
    public void ZeroOrNegativeCapacity_IsRejected()
    {
        Assert.Throws<ArgumentOutOfRangeException>(static () => new LatencyDigest(0));
        Assert.Throws<ArgumentOutOfRangeException>(static () => new LatencyDigest(-1));
    }

    [TestMethod]
    public void ConcurrentRecording_LosesNothing()
    {
        // Assignments for unrelated windows run at the same time by design, so
        // two of them recording at once is the ordinary case rather than an edge.
        LatencyDigest digest = new(capacity: 4096);

        Parallel.For(0, 2000, index =>
            digest.Record(TimeSpan.FromMilliseconds((index % 50) + 1)));

        Assert.AreEqual(2000L, digest.ObservedCount);
        Assert.AreEqual(2000, digest.Summarize().SampleCount);
    }

    [TestMethod]
    public void DefaultCapacity_IsLargeEnoughForAMeaningfulP99()
    {
        // P99 of a hundred samples is the second slowest, which one outlier
        // decides. A thousand puts it on the tenth.
        Assert.IsGreaterThanOrEqualTo(1000, LatencyDigest.DefaultCapacity);
    }
}

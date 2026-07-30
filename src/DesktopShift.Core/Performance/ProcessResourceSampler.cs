using System.Diagnostics;

namespace DesktopShift.Core.Performance;

/// <summary>
/// What the process had consumed at one moment.
/// </summary>
/// <param name="CapturedAtUtc">When the reading was taken.</param>
/// <param name="TotalProcessorTime">
/// Processor time the process has used since it started, across all its threads
/// and all cores.
/// </param>
/// <param name="WorkingSetBytes">Physical memory the process currently holds.</param>
/// <param name="PrivateMemoryBytes">
/// Memory the process has committed that is not shared with anything else, which
/// is the figure that answers "how much does leaving this running cost".
/// </param>
public readonly record struct ProcessResourceSample(
    DateTimeOffset CapturedAtUtc,
    TimeSpan TotalProcessorTime,
    long WorkingSetBytes,
    long PrivateMemoryBytes);

/// <summary>
/// Reads the process's own processor and memory use.
/// </summary>
/// <remarks>
/// Pull only. There is no sampling thread and no timer, because an idle-cost
/// measurement that ran on a schedule would be adding the very cost it claims to
/// measure. A reading is taken when a report is built and at no other time.
/// </remarks>
public interface IProcessResourceSampler
{
    /// <summary>
    /// How many cores the processor time is spread across, so a percentage can
    /// be worked out from it.
    /// </summary>
    int ProcessorCount { get; }

    /// <summary>Takes one reading.</summary>
    /// <returns>What the process had consumed just now.</returns>
    ProcessResourceSample Capture();
}

/// <summary>
/// Reads the running process through the documented process counters.
/// </summary>
/// <remarks>
/// This asks Windows about DesktopShift itself and about nothing else. No other
/// process is opened, enumerated, or queried, so the measurement carries none of
/// the privilege or privacy questions that reading another application's
/// counters would.
/// </remarks>
public sealed class CurrentProcessResourceSampler(TimeProvider timeProvider) :
    IProcessResourceSampler
{
    public int ProcessorCount => Environment.ProcessorCount;

    public ProcessResourceSample Capture()
    {
        using Process process = Process.GetCurrentProcess();
        return new ProcessResourceSample(
            timeProvider.GetUtcNow(),
            process.TotalProcessorTime,
            process.WorkingSet64,
            process.PrivateMemorySize64);
    }
}

/// <summary>
/// Turns two readings into the processor share between them.
/// </summary>
public static class ProcessResourceUsage
{
    /// <summary>
    /// Works out what share of one core's worth of time the process used
    /// between two readings, as a percentage.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Divided by the core count, so the answer is the percentage a task manager
    /// would show rather than a number that can reach 800 on an eight-core
    /// machine. One percent therefore means one percent of the whole machine.
    /// </para>
    /// <para>
    /// Two readings taken at the same instant, or taken out of order, produce
    /// zero rather than an error. A window too short to measure is not a
    /// failure — it is simply a window with no answer in it, and the sample
    /// count in the report is what says so.
    /// </para>
    /// </remarks>
    /// <param name="from">The earlier reading.</param>
    /// <param name="to">The later reading.</param>
    /// <param name="processorCount">How many cores the time was spread across.</param>
    /// <returns>The processor share as a percentage of the whole machine.</returns>
    public static double ComputeCpuPercent(
        ProcessResourceSample from,
        ProcessResourceSample to,
        int processorCount)
    {
        if (processorCount <= 0)
        {
            return 0d;
        }

        TimeSpan wallClock = to.CapturedAtUtc - from.CapturedAtUtc;
        TimeSpan processor = to.TotalProcessorTime - from.TotalProcessorTime;
        if (wallClock <= TimeSpan.Zero || processor <= TimeSpan.Zero)
        {
            return 0d;
        }

        return processor.TotalMilliseconds /
            (wallClock.TotalMilliseconds * processorCount) *
            100d;
    }
}

using DesktopShift.Core.Assignments;

namespace DesktopShift.Core.Tests.Assignments;

[TestClass]
public sealed class EarlyForegroundActivationMemoryTests
{
    private static readonly DateTimeOffset Now =
        new(2026, 7, 30, 22, 6, 9, TimeSpan.Zero);

    [TestMethod]
    public void RememberedActivation_IsAvailableOnceAndThenGone()
    {
        // One activation is one request from the user. A window that is created
        // and then shown produces two open events, and the second must not
        // inherit an activation the first already spent.
        FixedTimeProvider time = new(Now);
        BoundedEarlyForegroundActivationMemory memory = new(time);

        memory.Remember((nint)1);

        Assert.IsTrue(memory.TryConsume((nint)1));
        Assert.IsFalse(memory.TryConsume((nint)1));
    }

    [TestMethod]
    public void UnrememberedWindow_IsNeverClaimed()
    {
        FixedTimeProvider time = new(Now);
        BoundedEarlyForegroundActivationMemory memory = new(time);

        memory.Remember((nint)1);

        Assert.IsFalse(memory.TryConsume((nint)2));
        Assert.IsTrue(memory.TryConsume((nint)1));
    }

    [TestMethod]
    public void ActivationOlderThanItsLifetime_NoLongerFollows()
    {
        // The memory decides whether the user is taken to another desktop. An
        // activation old enough to have been forgotten must not be able to.
        FixedTimeProvider time = new(Now);
        BoundedEarlyForegroundActivationMemory memory = new(time);

        memory.Remember((nint)1);
        time.UtcNow = Now +
            BoundedEarlyForegroundActivationMemory.DefaultLifetime +
            TimeSpan.FromMilliseconds(1);

        Assert.IsFalse(memory.TryConsume((nint)1));
    }

    [TestMethod]
    public void ActivationInsideItsLifetime_StillFollows()
    {
        // The gap this exists to bridge — Windows Terminal focusing its window
        // 665ms before showing it — has to fit comfortably inside.
        FixedTimeProvider time = new(Now);
        BoundedEarlyForegroundActivationMemory memory = new(time);

        memory.Remember((nint)1);
        time.UtcNow = Now + TimeSpan.FromMilliseconds(665);

        Assert.IsTrue(memory.TryConsume((nint)1));
    }

    [TestMethod]
    public void ClearedWindow_IsForgotten()
    {
        // A window that closed inside its memory is not one the user is waiting
        // for, and its handle may already belong to something else.
        FixedTimeProvider time = new(Now);
        BoundedEarlyForegroundActivationMemory memory = new(time);

        memory.Remember((nint)1);
        memory.Clear((nint)1);

        Assert.IsFalse(memory.TryConsume((nint)1));
    }

    [TestMethod]
    public void PastCapacity_TheOldestActivationIsDropped()
    {
        // Bounded because it is fed by an event stream nothing throttles. The
        // oldest goes first: it is the one least likely to still be a launch
        // anyone is waiting on.
        FixedTimeProvider time = new(Now);
        BoundedEarlyForegroundActivationMemory memory = new(time);
        int capacity = BoundedEarlyForegroundActivationMemory.DefaultCapacity;

        for (int handle = 1; handle <= capacity; handle++)
        {
            memory.Remember((nint)handle);
        }

        memory.Remember((nint)(capacity + 1));

        Assert.IsFalse(memory.TryConsume((nint)1));
        Assert.IsTrue(memory.TryConsume((nint)2));
        Assert.IsTrue(memory.TryConsume((nint)(capacity + 1)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}

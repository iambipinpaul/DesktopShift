using DesktopShift.Core.Hosting;

namespace DesktopShift.Core.Tests.Hosting;

[TestClass]
public sealed class ApplicationShutdownSequenceTests
{
    [TestMethod]
    public async Task RunAsync_DisposesEveryStepInDeclaredOrder()
    {
        List<string> order = [];
        ApplicationShutdownSequence sequence = new(
        [
            Step("notification area", order),
            Step("single instance registration", order),
            Step("hosted services", order),
            Step("host container", order),
        ]);

        ShutdownReport report = await sequence.RunAsync();

        Assert.IsTrue(report.IsClean);
        CollectionAssert.AreEqual(
            new[]
            {
                "notification area",
                "single instance registration",
                "hosted services",
                "host container",
            },
            order);
        CollectionAssert.AreEqual(order, report.CompletedSteps.ToArray());
    }

    [TestMethod]
    public async Task RunAsync_FailingStepDoesNotStrandLaterSteps()
    {
        List<string> order = [];
        ApplicationShutdownSequence sequence = new(
        [
            new ShutdownStep(
                "notification area",
                _ => throw new InvalidOperationException("The icon refused to delete.")),
            Step("hosted services", order),
            Step("host container", order),
        ]);

        ShutdownReport report = await sequence.RunAsync();

        Assert.IsFalse(report.IsClean);
        CollectionAssert.AreEqual(new[] { "hosted services", "host container" }, order);
        Assert.HasCount(1, report.Failures);
        Assert.AreEqual("notification area", report.Failures[0].Name);
        Assert.AreEqual(
            "The icon refused to delete.",
            report.Failures[0].Exception.Message);
    }

    [TestMethod]
    public async Task RunAsync_RunsOnceWhenExitArrivesFromTrayAndWindowTogether()
    {
        int disposeCount = 0;
        ApplicationShutdownSequence sequence = new(
        [
            new ShutdownStep(
                "host container",
                cancellationToken =>
                {
                    _ = cancellationToken;
                    _ = Interlocked.Increment(ref disposeCount);
                    return ValueTask.CompletedTask;
                }),
        ]);

        Assert.IsFalse(sequence.HasRun);

        ShutdownReport[] reports = await Task.WhenAll(
            sequence.RunAsync(),
            sequence.RunAsync());

        Assert.AreEqual(1, disposeCount);
        Assert.IsTrue(sequence.HasRun);
        Assert.AreSame(reports[0], reports[1]);
    }

    private static ShutdownStep Step(string name, ICollection<string> order) =>
        new(
            name,
            _ =>
            {
                order.Add(name);
                return ValueTask.CompletedTask;
            });
}

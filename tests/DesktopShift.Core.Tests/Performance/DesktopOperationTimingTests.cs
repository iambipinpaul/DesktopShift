using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;
using DesktopShift.Core.Observation;
using DesktopShift.Core.Performance;
using DesktopShift.Core.Tests.ManagedDesktops;

namespace DesktopShift.Core.Tests.Performance;

/// <summary>
/// Creating a desktop and switching to one are timed apart from each other and
/// apart from assignment latency.
/// </summary>
/// <remarks>
/// They are the two most expensive things DesktopShift asks Windows to do, and
/// they answer different questions. A slow switch makes every followed window
/// feel slow; a creation that happens at the wrong moment makes exactly one
/// window feel slow and is invisible in an average. Neither is readable if both
/// are folded into one series.
/// </remarks>
[TestClass]
public sealed class DesktopOperationTimingTests
{
    private static readonly DateTimeOffset Start =
        new(2026, 7, 30, 9, 0, 0, TimeSpan.Zero);

    [TestMethod]
    public async Task EachCreatedDesktop_IsTimedOnItsOwn()
    {
        SteppingTimeProvider clock = new(Start);
        CapturingPerformanceRecorder recorder = new();
        LatencyPipelineHarness.SlowTopologyProvider topology = new(clock)
        {
            CreateCost = TimeSpan.FromMilliseconds(120),
        };
        topology.Desktops.Clear();

        ManagedDesktopReconciliationService service = new(
            StubConfigurationService(
                PerformanceDefinitions.Create("work", "comms")),
            topology,
            new MaintenanceBindingStore(),
            clock,
            recreationGate: null,
            recorder);

        ManagedDesktopReconciliationSnapshot snapshot = await service.ReconcileAsync(
            ManagedDesktopReconciliationTrigger.Startup);

        Assert.AreEqual(
            ManagedDesktopReconciliationOutcome.Succeeded,
            snapshot.Outcome);
        Assert.AreEqual(2, topology.CreateCallCount);

        // One duration per desktop, not one for the pass. A pass that made four
        // desktops has to be distinguishable from a single very slow creation.
        Assert.HasCount(2, recorder.Creations);
        Assert.AreEqual(TimeSpan.FromMilliseconds(120), recorder.Creations[0]);
        Assert.AreEqual(TimeSpan.FromMilliseconds(120), recorder.Creations[1]);
        Assert.IsEmpty(recorder.Switches);
        Assert.IsEmpty(recorder.Assignments);
    }

    [TestMethod]
    public async Task ADesktopThatAlreadyExists_IsNotTimedAsACreation()
    {
        // Reusing a desktop costs nothing and creates nothing. Counting it would
        // make the creation count useless as the answer to "was a desktop made on
        // a window's path".
        SteppingTimeProvider clock = new(Start);
        CapturingPerformanceRecorder recorder = new();
        LatencyPipelineHarness.SlowTopologyProvider topology = new(clock);
        topology.Desktops.Clear();
        topology.Desktops.Add(
            new VirtualDesktopDescriptor(
                LatencyPipelineHarness.ManagedDesktopId,
                "work",
                0,
                IsCurrent: true));

        ManagedDesktopReconciliationService service = new(
            StubConfigurationService(PerformanceDefinitions.Create("work")),
            topology,
            new MaintenanceBindingStore(),
            clock,
            recreationGate: null,
            recorder);

        _ = await service.ReconcileAsync(ManagedDesktopReconciliationTrigger.Startup);

        Assert.AreEqual(0, topology.CreateCallCount);
        Assert.IsEmpty(recorder.Creations);
    }

    [TestMethod]
    public async Task ARefusedCreation_IsNotTimedAsACreation()
    {
        // A provider that cannot create desktops still takes time to say so. That
        // duration is the cost of a refusal, not of making a desktop, and a
        // series mixing the two would describe neither.
        SteppingTimeProvider clock = new(Start);
        CapturingPerformanceRecorder recorder = new();
        LatencyPipelineHarness.SlowTopologyProvider topology = new(clock)
        {
            CreateCost = TimeSpan.FromMilliseconds(40),
            CanCreate = false,
        };
        topology.Desktops.Clear();

        ManagedDesktopReconciliationService service = new(
            StubConfigurationService(PerformanceDefinitions.Create("work")),
            topology,
            new MaintenanceBindingStore(),
            clock,
            recreationGate: null,
            recorder);

        ManagedDesktopReconciliationSnapshot snapshot = await service.ReconcileAsync(
            ManagedDesktopReconciliationTrigger.Startup);

        Assert.AreEqual(1, topology.CreateCallCount);
        Assert.AreEqual(
            ManagedDesktopMappingStatus.Limited,
            snapshot.Mappings[0].Status);
        Assert.IsEmpty(recorder.Creations);
    }

    [TestMethod]
    public async Task OnlyASwitchThatMovedTheUser_IsTimedAsASwitch()
    {
        // Reading the current desktop and finding it already correct is a query.
        // Timing it as a switch would pull the series towards zero and hide how
        // long switching actually takes.
        LatencyPipelineHarness harness = new();
        harness.Topology.SwitchCost = TimeSpan.FromMilliseconds(35);
        harness.Placement.SetCurrent(20, LatencyPipelineHarness.OtherDesktopId);

        _ = await harness.ObserveAsync(WindowEventKind.Shown, 20);
        _ = await harness.ProcessAsync(WindowEventKind.ForegroundActivated, 20);

        Assert.HasCount(1, harness.Recorder.Switches);
        Assert.AreEqual(TimeSpan.FromMilliseconds(35), harness.Recorder.Switches[0]);

        // A second activation of the same window is no longer its first, so the
        // policy declines and nothing new is timed.
        _ = await harness.ProcessAsync(WindowEventKind.ForegroundActivated, 20);

        Assert.HasCount(1, harness.Recorder.Switches);
        Assert.AreEqual(1, harness.Topology.SwitchCallCount);
    }

    [TestMethod]
    public async Task ABackgroundWindow_IsMovedWithoutTimingASwitch()
    {
        // The desktop follows a window only when Windows says the user activated
        // it. Nothing was switched here, so nothing may appear in the switch
        // series.
        LatencyPipelineHarness harness = new();
        harness.Topology.SwitchCost = TimeSpan.FromMilliseconds(35);
        harness.Placement.SetCurrent(21, LatencyPipelineHarness.OtherDesktopId);

        _ = await harness.ProcessAsync(WindowEventKind.Shown, 21);

        Assert.HasCount(1, harness.Placement.Moves);
        Assert.AreEqual(0, harness.Topology.SwitchCallCount);
        Assert.IsEmpty(harness.Recorder.Switches);
        Assert.HasCount(1, harness.Recorder.Assignments);
    }

    private static MaintenanceConfigurationService StubConfigurationService(
        ImmutableArray<ManagedDesktopDefinition> definitions) =>
        new(
            new ConfigurationDocument(
                ConfigurationDefaults.CurrentSchemaVersion,
                definitions,
                [],
                ConfigurationDefaults.Create().Behavior));
}

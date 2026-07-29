using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Core.Tests.Hotkeys;

/// <summary>
/// What a pressed digit actually does to the desktop the user is looking at.
/// </summary>
[TestClass]
public sealed class DesktopSwitchShortcutServiceTests
{
    [TestMethod]
    public async Task PressingADigit_SwitchesToTheDesktopAtThatPosition()
    {
        FakeTopologyProvider topology = new(desktopCount: 4, currentPosition: 1);
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result =
            await service.SwitchToDesktopAsync(3);

        Assert.AreEqual(DesktopSwitchShortcutOutcome.Switched, result.Outcome);
        Assert.AreEqual(topology.DesktopIdAt(3), topology.SwitchedTo.Single());
        Assert.IsFalse(
            result.ShouldNotify,
            "A switch the user can see does not need a balloon.");
    }

    [TestMethod]
    public async Task PressingZero_SwitchesToDesktopTen()
    {
        FakeTopologyProvider topology = new(desktopCount: 10, currentPosition: 1);
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result = await service.SwitchToDesktopAsync(
            DesktopSwitchShortcuts.DesktopOrdinalFor(HotkeyKey.D0));

        Assert.AreEqual(DesktopSwitchShortcutOutcome.Switched, result.Outcome);
        Assert.AreEqual(10, result.DesktopOrdinal);
        Assert.AreEqual(topology.DesktopIdAt(10), topology.SwitchedTo.Single());
    }

    [TestMethod]
    public async Task ADesktopThatDoesNotExist_IsReportedWithWhatTheUserHas()
    {
        FakeTopologyProvider topology = new(desktopCount: 4, currentPosition: 1);
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result =
            await service.SwitchToDesktopAsync(7);

        Assert.AreEqual(
            DesktopSwitchShortcutOutcome.DesktopMissing,
            result.Outcome);
        Assert.AreEqual(4, result.DesktopCount);
        Assert.IsTrue(result.ShouldNotify);
        Assert.Contains("no Desktop 7", result.Message!);
        Assert.Contains("4 desktops", result.Message!);
        Assert.IsEmpty(
            topology.SwitchedTo,
            "Nothing should be switched when the target does not exist.");
    }

    [TestMethod]
    public async Task ASingleDesktop_IsDescribedInTheSingular()
    {
        FakeTopologyProvider topology = new(desktopCount: 1, currentPosition: 1);
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result =
            await service.SwitchToDesktopAsync(2);

        Assert.AreEqual(
            DesktopSwitchShortcutOutcome.DesktopMissing,
            result.Outcome);
        Assert.Contains("one desktop", result.Message!);
    }

    [TestMethod]
    public async Task TheDesktopAlreadyInFront_IsLeftAloneAndSaysNothing()
    {
        FakeTopologyProvider topology = new(desktopCount: 4, currentPosition: 2);
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result =
            await service.SwitchToDesktopAsync(2);

        Assert.AreEqual(
            DesktopSwitchShortcutOutcome.AlreadyCurrent,
            result.Outcome);
        Assert.IsEmpty(topology.SwitchedTo);
        Assert.IsFalse(result.ShouldNotify);
    }

    [TestMethod]
    public async Task LimitedMode_ReportsThatSwitchingIsUnavailable()
    {
        FakeTopologyProvider topology = new(
            desktopCount: 4,
            currentPosition: 1,
            capabilities: VirtualDesktopCapabilities.DocumentedLimited);
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result =
            await service.SwitchToDesktopAsync(2);

        Assert.AreEqual(DesktopSwitchShortcutOutcome.Unsupported, result.Outcome);
        Assert.IsTrue(result.ShouldNotify);
        Assert.Contains("Limited Mode", result.Message!);
        Assert.AreEqual(
            0,
            topology.EnumerateCount,
            "A provider that cannot switch should not be asked to enumerate.");
    }

    [TestMethod]
    public async Task AFailedSwitch_IsReportedRatherThanSwallowed()
    {
        FakeTopologyProvider topology = new(desktopCount: 4, currentPosition: 1)
        {
            SwitchFails = true,
        };
        DesktopSwitchShortcutService service = new(topology);

        DesktopSwitchShortcutResult result =
            await service.SwitchToDesktopAsync(3);

        Assert.AreEqual(DesktopSwitchShortcutOutcome.Failed, result.Outcome);
        Assert.IsTrue(result.ShouldNotify);
        Assert.Contains("Desktop 3", result.Message!);
    }

    [TestMethod]
    public async Task PositionIsResolvedOnEveryPress_NotCached()
    {
        FakeTopologyProvider topology = new(desktopCount: 4, currentPosition: 1);
        DesktopSwitchShortcutService service = new(topology);

        _ = await service.SwitchToDesktopAsync(3);
        _ = await service.SwitchToDesktopAsync(3);

        // Desktops come and go while DesktopShift runs, so a cached third
        // desktop would send the user somewhere the digit no longer means.
        Assert.AreEqual(2, topology.EnumerateCount);
    }

    [TestMethod]
    public async Task ADigitOutsideTheSupportedRange_IsARejectedProgrammingError()
    {
        FakeTopologyProvider topology = new(desktopCount: 4, currentPosition: 1);
        DesktopSwitchShortcutService service = new(topology);

        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await service.SwitchToDesktopAsync(11));
        _ = await Assert.ThrowsExactlyAsync<ArgumentOutOfRangeException>(
            async () => await service.SwitchToDesktopAsync(0));
    }

    private sealed class FakeTopologyProvider : IDesktopTopologyProvider
    {
        private readonly List<VirtualDesktopDescriptor> desktops = [];

        public FakeTopologyProvider(
            int desktopCount,
            int currentPosition,
            VirtualDesktopCapabilities? capabilities = null)
        {
            Capabilities = capabilities ?? new VirtualDesktopCapabilities(
                CanGetWindowDesktopId: true,
                CanMoveWindowToDesktop: true,
                CanEnumerateDesktops: true,
                CanGetCurrentDesktop: true,
                CanCreateDesktop: true,
                CanSwitchDesktop: true,
                CanObserveTopologyChanges: true);

            for (int position = 1; position <= desktopCount; position++)
            {
                desktops.Add(
                    new VirtualDesktopDescriptor(
                        DeterministicId(position),
                        $"Desktop {position}",
                        position,
                        position == currentPosition));
            }
        }

        public bool SwitchFails { get; init; }

        public List<Guid> SwitchedTo { get; } = [];

        public int EnumerateCount { get; private set; }

        public DesktopTopologyProviderIdentity Identity { get; } = new(
            "fake",
            "Fake provider",
            "1.0",
            DesktopTopologyProviderMode.Full,
            UsesPrivateApis: false);

        public VirtualDesktopCapabilities Capabilities { get; }

        public event EventHandler<DesktopTopologyChangedEventArgs>? TopologyChanged;

        public Guid DesktopIdAt(int position) => DeterministicId(position);

        public ValueTask<DesktopTopologyProviderResult> TestCompatibilityAsync(
            WindowsBuildInfo build,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(DesktopTopologyProviderResult.Succeeded());

        public ValueTask<DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>>
            EnumerateDesktopsAsync(CancellationToken cancellationToken = default)
        {
            EnumerateCount++;
            return ValueTask.FromResult(
                DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>>
                    .Succeeded(desktops));
        }

        public ValueTask<DesktopTopologyProviderResult<Guid>> GetCurrentDesktopIdAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Succeeded(
                    desktops.First(static desktop => desktop.IsCurrent).Id));

        public ValueTask<DesktopTopologyProviderResult<Guid>> CreateDesktopAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(
                DesktopTopologyProviderResult<Guid>.Unsupported(
                    "fake.create_unsupported",
                    "The fake provider does not create desktops."));

        public ValueTask<DesktopTopologyProviderResult> SwitchDesktopAsync(
            Guid desktopId,
            CancellationToken cancellationToken = default)
        {
            if (SwitchFails)
            {
                return ValueTask.FromResult(
                    DesktopTopologyProviderResult.Failed(
                        "fake.switch_failed",
                        "Windows refused the switch."));
            }

            SwitchedTo.Add(desktopId);
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Succeeded());
        }

        public ValueTask<DesktopTopologyProviderResult> StartTopologyNotificationsAsync(
            CancellationToken cancellationToken = default)
        {
            TopologyChanged?.Invoke(
                this,
                new DesktopTopologyChangedEventArgs("started"));
            return ValueTask.FromResult(
                DesktopTopologyProviderResult.Succeeded());
        }

        private static Guid DeterministicId(int position) =>
            new(position, 0, 0, [0, 0, 0, 0, 0, 0, 0, 0]);
    }
}

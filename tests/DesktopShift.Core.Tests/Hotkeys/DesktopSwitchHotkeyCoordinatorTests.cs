using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;
using DesktopShift.Infrastructure.Hotkeys;

namespace DesktopShift.Core.Tests.Hotkeys;

[TestClass]
public sealed class DesktopSwitchHotkeyCoordinatorTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 29, 9, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void Apply_ClaimsTenCombinationsWhenTheProfileIsEnabled()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        using DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, new FixedTimeProvider(ObservedAt));

        DesktopSwitchHotkeyState state = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.CtrlAlt));

        Assert.IsTrue(state.IsEnabled);
        Assert.AreEqual(10, state.RegisteredCount);
        Assert.AreEqual(10, registrar.HeldCount);
        Assert.IsEmpty(state.Issues);
        Assert.AreEqual(ObservedAt, state.ObservedAtUtc);
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 },
            registrar.HeldDesktopOrdinals.ToArray());
    }

    [TestMethod]
    public void Apply_SwitchedOffReleasesEverythingAndClaimsNothing()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        using DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        _ = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.CtrlAlt));

        DesktopSwitchHotkeyState state = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                false,
                DesktopSwitchShortcutProfile.CtrlAlt));

        Assert.IsFalse(state.IsEnabled);
        Assert.AreEqual(0, state.RegisteredCount);
        Assert.AreEqual(0, registrar.HeldCount);
        Assert.AreEqual(2, registrar.UnregisterAllCount);
    }

    [TestMethod]
    public void ChangingProfile_ReleasesTheOldChordsBeforeClaimingTheNewOnes()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        using DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        _ = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.CtrlAlt));

        DesktopSwitchHotkeyState state = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.WinAlt));

        // Twenty attempts, ten held: nothing from the first profile survived to
        // be claimed twice or left behind.
        Assert.AreEqual(2, registrar.UnregisterAllCount);
        Assert.HasCount(20, registrar.Attempts);
        Assert.AreEqual(10, registrar.HeldCount);
        Assert.AreEqual(DesktopSwitchShortcutProfile.WinAlt, state.Profile);
        Assert.IsTrue(
            registrar.Attempts[^1].Chord.Modifiers.HasFlag(
                HotkeyModifiers.Windows),
            "The replacement profile should be the one being claimed.");
    }

    [TestMethod]
    public void AProfileThatCouldNeverWork_IsNotHandedToTheRegistrarAtAll()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        using DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);

        DesktopSwitchHotkeyState state = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.Custom,
                HotkeyModifiers.None));

        Assert.IsFalse(state.IsEnabled);
        Assert.IsEmpty(registrar.Attempts);
        Assert.AreEqual(
            ConfigurationValidationCode.DesktopSwitchShortcutMissingModifier,
            state.Issues.Single().Code);
    }

    [TestMethod]
    public void ARefusedDesktop_IsReportedWithoutAbandoningTheOthers()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        HotkeyChord contested = new(
            DesktopSwitchShortcuts.WinAltModifiers,
            DesktopSwitchShortcuts.KeyFor(3));
        registrar.Refuse(contested);
        using DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);

        DesktopSwitchHotkeyState state = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.WinAlt));

        Assert.AreEqual(9, state.RegisteredCount);
        CollectionAssert.AreEqual(
            new[] { 3 },
            state.RefusedDesktopOrdinals.ToArray());

        DesktopSwitchHotkeyRegistration refused =
            state.Registrations.Single(static value => !value.IsRegistered);
        Assert.AreEqual(3, refused.DesktopOrdinal);
        Assert.IsNotNull(refused.Failure);
        Assert.Contains("Desktop 3", refused.Failure);
        Assert.Contains("1409", refused.Failure);

        // One issue for the profile, not ten near-identical rows.
        ConfigurationValidationIssue issue = state.Issues.Single();
        Assert.AreEqual(
            ConfigurationValidationCode.DesktopSwitchShortcutRegistrationFailed,
            issue.Code);
        Assert.Contains("Jump List", issue.Message);
        Assert.Contains("9 of 10", issue.Message);
    }

    [TestMethod]
    public void APressForADesktopNoLongerHeld_IsDropped()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        using DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        List<int> switched = [];
        coordinator.Invoked += (_, args) => switched.Add(args.DesktopOrdinal);

        _ = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.CtrlAlt));
        registrar.SimulatePress(4);

        _ = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                false,
                DesktopSwitchShortcutProfile.CtrlAlt));

        // A hotkey message already queued when the profile was switched off must
        // not move the user to a desktop they withdrew the shortcut for.
        registrar.SimulatePress(4);

        CollectionAssert.AreEqual(new[] { 4 }, switched);
    }

    [TestMethod]
    public void Dispose_ReleasesEveryCombinationAndDisposesTheRegistrar()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        _ = coordinator.Apply(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.CtrlAlt));

        coordinator.Dispose();

        Assert.AreEqual(0, registrar.HeldCount);
        Assert.IsTrue(registrar.IsDisposed);
        Assert.IsFalse(coordinator.Current.IsEnabled);
        Assert.IsEmpty(coordinator.Current.Registrations);

        // Idempotent: the shutdown sequence may reach this more than once.
        coordinator.Dispose();
        Assert.AreEqual(1, registrar.DisposeCount);
    }

    [TestMethod]
    public void Apply_AfterDispose_Throws()
    {
        InMemoryDesktopSwitchHotkeyRegistrar registrar = new();
        DesktopSwitchHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        coordinator.Dispose();

        _ = Assert.ThrowsExactly<ObjectDisposedException>(
            () => coordinator.Apply(
                new DesktopSwitchShortcutSettings(
                    true,
                    DesktopSwitchShortcutProfile.CtrlAlt)));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}

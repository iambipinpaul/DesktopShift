using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;
using DesktopShift.Infrastructure.Hotkeys;

namespace DesktopShift.Core.Tests.Hotkeys;

[TestClass]
public sealed class GlobalHotkeyCoordinatorTests
{
    private static readonly DateTimeOffset ObservedAt =
        new(2026, 7, 29, 9, 30, 0, TimeSpan.Zero);

    [TestMethod]
    public void Apply_MasterSwitchOffReleasesExistingRegistrationsAndClaimsNothing()
    {
        InMemoryGlobalHotkeyRegistrar registrar = new();
        using GlobalHotkeyCoordinator coordinator =
            new(registrar, new FixedTimeProvider(ObservedAt));

        HotkeyState enabled =
            coordinator.Apply(new HotkeySettings(true, HotkeyDefaults.Bindings));
        Assert.AreEqual(4, enabled.RegisteredCount);
        Assert.AreEqual(4, registrar.HeldCount);

        HotkeyState disabled =
            coordinator.Apply(new HotkeySettings(false, HotkeyDefaults.Bindings));

        Assert.IsFalse(disabled.IsEnabled);
        Assert.AreEqual(0, disabled.RegisteredCount);
        Assert.AreEqual(0, registrar.HeldCount);
        Assert.AreEqual(2, registrar.UnregisterAllCount);
        Assert.HasCount(4, registrar.Attempts);
        Assert.AreEqual(ObservedAt, disabled.ObservedAtUtc);
    }

    [TestMethod]
    public void Apply_RegistersOnlyValidEnabledBindings()
    {
        InMemoryGlobalHotkeyRegistrar registrar = new();
        using GlobalHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        HotkeyBinding valid = HotkeyDefaults.Bindings[0];
        HotkeyBinding disabled = HotkeyDefaults.Bindings[1] with { IsEnabled = false };
        HotkeyBinding invalid = HotkeyDefaults.Bindings[2] with
        {
            Modifiers = HotkeyModifiers.None,
        };

        HotkeyState state = coordinator.Apply(
            new HotkeySettings(true, [valid, disabled, invalid]));

        Assert.IsTrue(state.IsEnabled);
        Assert.AreEqual(1, state.RegisteredCount);
        Assert.HasCount(1, registrar.Attempts);
        Assert.AreEqual(valid.Action, registrar.Attempts[0].Action);
        Assert.IsTrue(
            state.Issues.Any(
                static issue =>
                    issue.Code ==
                    ConfigurationValidationCode.HotkeyMissingModifier));
    }

    [TestMethod]
    public void Apply_ReportsARegistrarRefusalAgainstTheOriginalRow()
    {
        InMemoryGlobalHotkeyRegistrar registrar = new();
        HotkeyBinding refused = HotkeyDefaults.Bindings[2];
        registrar.Refuse(refused.Chord, "Owned by another app.", 1409);
        using GlobalHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);

        HotkeyState state = coordinator.Apply(
            new HotkeySettings(true, HotkeyDefaults.Bindings));

        Assert.AreEqual(3, state.RegisteredCount);
        HotkeyRegistration registration = state.Registrations.Single(
            value => value.Action == refused.Action);
        Assert.IsFalse(registration.IsRegistered);
        Assert.IsNotNull(registration.Failure);
        Assert.Contains("Owned by another app.", registration.Failure);
        Assert.Contains("1409", registration.Failure);
        ConfigurationValidationIssue issue = state.Issues.Single(
            static value =>
                value.Code == ConfigurationValidationCode.HotkeyRegistrationFailed);
        Assert.AreEqual("$.behavior.hotkeys[2]", issue.Path);
        Assert.AreEqual(refused.Action.ToString(), issue.EntryId);
    }

    [TestMethod]
    public void Apply_UnregistersBeforeAttemptingTheReplacementSet()
    {
        RecordingRegistrar registrar = new();
        using GlobalHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);

        coordinator.Apply(
            new HotkeySettings(true, [HotkeyDefaults.Bindings[0]]));
        coordinator.Apply(
            new HotkeySettings(true, [HotkeyDefaults.Bindings[1]]));

        CollectionAssert.AreEqual(
            new[]
            {
                "unregister",
                $"register:{HotkeyAction.ReassignAllWindows}",
                "unregister",
                $"register:{HotkeyAction.ReassignForegroundWindow}",
            },
            registrar.Events.ToArray());
    }

    [TestMethod]
    public void Press_IsForwardedOnlyWhileThatActionIsRegistered()
    {
        InMemoryGlobalHotkeyRegistrar registrar = new();
        using GlobalHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        List<HotkeyAction> invoked = [];
        coordinator.Invoked += (_, args) => invoked.Add(args.Action);
        coordinator.Apply(
            new HotkeySettings(true, [HotkeyDefaults.Bindings[0]]));

        registrar.SimulatePress(HotkeyAction.ReassignAllWindows);
        registrar.SimulatePress(HotkeyAction.TogglePause);
        coordinator.Apply(HotkeySettings.Disabled);
        registrar.SimulatePress(HotkeyAction.ReassignAllWindows);

        CollectionAssert.AreEqual(
            new[] { HotkeyAction.ReassignAllWindows },
            invoked);
    }

    [TestMethod]
    public void Dispose_ReleasesAndDisposesExactlyOnceAndApplyThenThrows()
    {
        InMemoryGlobalHotkeyRegistrar registrar = new();
        GlobalHotkeyCoordinator coordinator =
            new(registrar, TimeProvider.System);
        coordinator.Apply(new HotkeySettings(true, HotkeyDefaults.Bindings));

        coordinator.Dispose();
        coordinator.Dispose();

        Assert.AreEqual(2, registrar.UnregisterAllCount);
        Assert.AreEqual(1, registrar.DisposeCount);
        Assert.AreEqual(0, registrar.HeldCount);
        Assert.IsFalse(coordinator.Current.IsEnabled);
        Assert.Throws<ObjectDisposedException>(() =>
            coordinator.Apply(HotkeySettings.Disabled));
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class RecordingRegistrar : IGlobalHotkeyRegistrar
    {
        public event EventHandler<HotkeyInvokedEventArgs>? Pressed
        {
            add { }
            remove { }
        }

        public List<string> Events { get; } = [];

        public HotkeyRegistrationOutcome Register(
            HotkeyAction action,
            HotkeyChord chord)
        {
            Events.Add($"register:{action}");
            return HotkeyRegistrationOutcome.Success;
        }

        public void UnregisterAll() => Events.Add("unregister");

        public void Dispose()
        {
        }
    }
}

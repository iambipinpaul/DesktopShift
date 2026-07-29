using DesktopShift.Core.Hotkeys;
using DesktopShift.Windows.Hotkeys;

namespace DesktopShift.Windows.Tests.Hotkeys;

/// <summary>
/// The desktop-switching registrar, driven through the User32 seam so no test
/// ever claims a combination from the machine it runs on.
/// </summary>
[TestClass]
public sealed class WindowsDesktopSwitchHotkeyRegistrarTests
{
    private const uint ModNoRepeat = 0x4000;
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModWindows = 0x0008;

    [TestMethod]
    public void ADesktopChord_IsRegisteredWithNoRepeatAndRaisesItsPosition()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);
        List<int> pressed = [];
        registrar.Pressed += (_, args) => pressed.Add(args.DesktopOrdinal);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            3,
            new HotkeyChord(
                DesktopSwitchShortcuts.CtrlAltModifiers,
                DesktopSwitchShortcuts.KeyFor(3)));

        Assert.IsTrue(outcome.Succeeded, outcome.FailureMessage);
        Assert.AreEqual(1, native.CreateWindowCount);

        NativeRegistration registration = native.Registrations.Single();
        Assert.AreEqual(
            WindowsDesktopSwitchHotkeyRegistrar.RegistrationIdFor(3),
            registration.RegistrationId);

        // MOD_NOREPEAT is not optional: holding the chord has to mean one
        // switch, not a stream of them.
        Assert.AreEqual(
            ModNoRepeat | ModControl | ModAlt,
            registration.Modifiers);
        Assert.AreEqual((uint)HotkeyKey.D3, registration.VirtualKey);

        native.DeliverHotkey(registration.RegistrationId);
        CollectionAssert.AreEqual(new[] { 3 }, pressed);
    }

    [TestMethod]
    public void DesktopTen_IsRegisteredOnTheZeroKey()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);
        List<int> pressed = [];
        registrar.Pressed += (_, args) => pressed.Add(args.DesktopOrdinal);

        // Windows and Alt together, so the Windows modifier's translation is
        // covered even though no built-in profile uses it.
        HotkeyRegistrationOutcome outcome = registrar.Register(
            10,
            new HotkeyChord(
                HotkeyModifiers.Windows | HotkeyModifiers.Alt,
                DesktopSwitchShortcuts.KeyFor(10)));

        Assert.IsTrue(outcome.Succeeded, outcome.FailureMessage);
        NativeRegistration registration = native.Registrations.Single();
        Assert.AreEqual((uint)HotkeyKey.D0, registration.VirtualKey);
        Assert.AreEqual(
            ModNoRepeat | ModWindows | ModAlt,
            registration.Modifiers);

        native.DeliverHotkey(registration.RegistrationId);
        CollectionAssert.AreEqual(new[] { 10 }, pressed);
    }

    [TestMethod]
    public void EveryDesktop_GetsADistinctRegistrationId()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);

        foreach (int ordinal in DesktopSwitchShortcuts.DesktopOrdinals)
        {
            HotkeyRegistrationOutcome outcome = registrar.Register(
                ordinal,
                new HotkeyChord(
                    DesktopSwitchShortcuts.CtrlAltModifiers,
                    DesktopSwitchShortcuts.KeyFor(ordinal)));
            Assert.IsTrue(outcome.Succeeded, outcome.FailureMessage);
        }

        Assert.HasCount(10, native.Registrations);
        Assert.AreEqual(
            10,
            native.Registrations
                .Select(static registration => registration.RegistrationId)
                .Distinct()
                .Count());

        // Clear of the four command shortcuts, so a native trace is never
        // ambiguous about which set a misfiring id belongs to.
        Assert.IsTrue(
            native.Registrations.All(static registration =>
                registration.RegistrationId >=
                WindowsDesktopSwitchHotkeyRegistrar.DesktopRegistrationIdBase),
            "Desktop ids must not overlap the command shortcut ids.");
    }

    [TestMethod]
    public void AClaimedCombination_IsReturnedAsASpecificFailure()
    {
        FakeWindowsHotkeyNativeApi native = new()
        {
            RegisterSucceeds = false,
            LastError = 1409,
        };
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            1,
            new HotkeyChord(
                DesktopSwitchShortcuts.CtrlAltModifiers,
                DesktopSwitchShortcuts.KeyFor(1)));

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(
            "Another application is already using this shortcut.",
            outcome.FailureMessage);
        Assert.AreEqual(1409, outcome.NativeErrorCode);
    }

    [TestMethod]
    public void UnregisterAll_ReleasesEveryHeldIdAndDropsLateMessages()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);
        List<int> pressed = [];
        registrar.Pressed += (_, args) => pressed.Add(args.DesktopOrdinal);
        _ = registrar.Register(
            1,
            new HotkeyChord(
                DesktopSwitchShortcuts.CtrlAltModifiers,
                DesktopSwitchShortcuts.KeyFor(1)));
        _ = registrar.Register(
            2,
            new HotkeyChord(
                DesktopSwitchShortcuts.CtrlAltModifiers,
                DesktopSwitchShortcuts.KeyFor(2)));

        registrar.UnregisterAll();
        native.DeliverHotkey(
            WindowsDesktopSwitchHotkeyRegistrar.RegistrationIdFor(1));

        CollectionAssert.AreEquivalent(
            new[]
            {
                WindowsDesktopSwitchHotkeyRegistrar.RegistrationIdFor(1),
                WindowsDesktopSwitchHotkeyRegistrar.RegistrationIdFor(2),
            },
            native.UnregisteredIds);
        Assert.IsEmpty(pressed);
    }

    [TestMethod]
    public void Dispose_ReleasesShortcutsBeforeDestroyingTheWindow_AndIsIdempotent()
    {
        FakeWindowsHotkeyNativeApi native = new();
        WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);
        _ = registrar.Register(
            1,
            new HotkeyChord(
                DesktopSwitchShortcuts.CtrlAltModifiers,
                DesktopSwitchShortcuts.KeyFor(1)));

        registrar.Dispose();
        registrar.Dispose();

        CollectionAssert.AreEqual(
            new[]
            {
                $"unregister:{WindowsDesktopSwitchHotkeyRegistrar.RegistrationIdFor(1)}",
                "destroy-window",
                "unregister-class",
            },
            native.CleanupOperations);
    }

    [TestMethod]
    public void ADesktopOutsideTheRange_IsRefusedWithoutTouchingWindows()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            11,
            new HotkeyChord(
                DesktopSwitchShortcuts.CtrlAltModifiers,
                HotkeyKey.D1));

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(0, native.CreateWindowCount);
        Assert.IsEmpty(native.Registrations);
    }

    [TestMethod]
    public void AnUnassignedChord_DoesNotCreateAWindowOrCallRegisterHotKey()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsDesktopSwitchHotkeyRegistrar registrar = new(native);

        HotkeyRegistrationOutcome outcome =
            registrar.Register(1, HotkeyChord.Unassigned);

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(0, native.CreateWindowCount);
        Assert.IsEmpty(native.Registrations);
    }

    private sealed class FakeWindowsHotkeyNativeApi : IWindowsHotkeyNativeApi
    {
        private WindowsHotkeyWindowProcedure? windowProcedure;

        public bool RegisterSucceeds { get; init; } = true;

        public int LastError { get; init; }

        public uint CurrentThreadId { get; init; } = 17;

        public int CreateWindowCount { get; private set; }

        public List<NativeRegistration> Registrations { get; } = [];

        public List<int> UnregisteredIds { get; } = [];

        public List<string> CleanupOperations { get; } = [];

        public bool RegisterWindowClass(
            string className,
            WindowsHotkeyWindowProcedure procedure)
        {
            windowProcedure = procedure;
            return true;
        }

        public nint CreateMessageOnlyWindow(string className)
        {
            CreateWindowCount++;
            return 42;
        }

        public bool RegisterHotKey(
            nint windowHandle,
            int registrationId,
            uint modifiers,
            uint virtualKey)
        {
            Registrations.Add(
                new NativeRegistration(registrationId, modifiers, virtualKey));
            return RegisterSucceeds;
        }

        public bool UnregisterHotKey(nint windowHandle, int registrationId)
        {
            UnregisteredIds.Add(registrationId);
            CleanupOperations.Add($"unregister:{registrationId}");
            return true;
        }

        public bool DestroyWindow(nint windowHandle)
        {
            CleanupOperations.Add("destroy-window");
            return true;
        }

        public bool UnregisterWindowClass(string className)
        {
            CleanupOperations.Add("unregister-class");
            return true;
        }

        public nint DefWindowProc(
            nint windowHandle,
            uint message,
            nint wParam,
            nint lParam) => 0;

        public int GetLastError() => LastError;

        public void DeliverHotkey(int registrationId)
        {
            Assert.IsNotNull(windowProcedure);
            _ = windowProcedure(
                42,
                WindowsGlobalHotkeyRegistrar.HotkeyMessage,
                registrationId,
                0);
        }
    }

    private sealed record NativeRegistration(
        int RegistrationId,
        uint Modifiers,
        uint VirtualKey);
}

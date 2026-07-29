using DesktopShift.Core.Hotkeys;
using DesktopShift.Windows.Hotkeys;

namespace DesktopShift.Windows.Tests.Hotkeys;

[TestClass]
public sealed class WindowsGlobalHotkeyRegistrarTests
{
    [TestMethod]
    public void Registration_UsesAHiddenMessageWindowAndRaisesTheMappedAction()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsGlobalHotkeyRegistrar registrar = new(native);
        List<HotkeyAction> pressed = [];
        registrar.Pressed += (_, args) => pressed.Add(args.Action);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            HotkeyAction.ReassignForegroundWindow,
            new HotkeyChord(
                HotkeyModifiers.Control | HotkeyModifiers.Alt,
                HotkeyKey.F));

        Assert.IsTrue(outcome.Succeeded, outcome.FailureMessage);
        Assert.AreEqual(1, native.CreateWindowCount);
        Assert.AreEqual(
            new NativeRegistration(
                WindowsGlobalHotkeyRegistrar.ForegroundWindowRegistrationId,
                0x4003,
                0x46),
            native.Registrations.Single());

        native.DeliverHotkey(
            WindowsGlobalHotkeyRegistrar.ForegroundWindowRegistrationId);

        CollectionAssert.AreEqual(
            new[] { HotkeyAction.ReassignForegroundWindow },
            pressed);
    }

    [TestMethod]
    public void AWindowsConflict_IsReturnedAsASpecificFailure()
    {
        FakeWindowsHotkeyNativeApi native = new()
        {
            RegisterSucceeds = false,
            LastError = WindowsGlobalHotkeyRegistrar.HotkeyAlreadyRegisteredError,
        };
        using WindowsGlobalHotkeyRegistrar registrar = new(native);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            HotkeyAction.OpenDesktopShift,
            new HotkeyChord(HotkeyModifiers.Control, HotkeyKey.D));

        Assert.IsFalse(outcome.Succeeded);
        Assert.AreEqual(
            "Another application is already using this shortcut.",
            outcome.FailureMessage);
        Assert.AreEqual(1409, outcome.NativeErrorCode);
    }

    [TestMethod]
    public void AGeneralWindowsFailure_PreservesTheNativeError()
    {
        FakeWindowsHotkeyNativeApi native = new()
        {
            RegisterSucceeds = false,
            LastError = 5,
        };
        using WindowsGlobalHotkeyRegistrar registrar = new(native);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            HotkeyAction.TogglePause,
            new HotkeyChord(HotkeyModifiers.Control, HotkeyKey.P));

        Assert.IsFalse(outcome.Succeeded);
        StringAssert.Contains(
            outcome.FailureMessage,
            "Windows could not register this shortcut");
        Assert.AreEqual(5, outcome.NativeErrorCode);
    }

    [TestMethod]
    public void UnregisterAll_ReleasesEveryHeldIdAndDropsLateMessages()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsGlobalHotkeyRegistrar registrar = new(native);
        List<HotkeyAction> pressed = [];
        registrar.Pressed += (_, args) => pressed.Add(args.Action);

        _ = registrar.Register(
            HotkeyAction.ReassignAllWindows,
            new HotkeyChord(HotkeyModifiers.Control, HotkeyKey.R));
        _ = registrar.Register(
            HotkeyAction.TogglePause,
            new HotkeyChord(HotkeyModifiers.Control, HotkeyKey.P));

        registrar.UnregisterAll();
        native.DeliverHotkey(
            WindowsGlobalHotkeyRegistrar.ReassignAllRegistrationId);

        CollectionAssert.AreEquivalent(
            new[]
            {
                WindowsGlobalHotkeyRegistrar.ReassignAllRegistrationId,
                WindowsGlobalHotkeyRegistrar.TogglePauseRegistrationId,
            },
            native.UnregisteredIds);
        Assert.IsEmpty(pressed);
    }

    [TestMethod]
    public void Dispose_ReleasesShortcutsBeforeDestroyingTheWindow_AndIsIdempotent()
    {
        FakeWindowsHotkeyNativeApi native = new();
        WindowsGlobalHotkeyRegistrar registrar = new(native);
        _ = registrar.Register(
            HotkeyAction.OpenDesktopShift,
            new HotkeyChord(HotkeyModifiers.Control, HotkeyKey.D));

        registrar.Dispose();
        registrar.Dispose();

        CollectionAssert.AreEqual(
            new[]
            {
                "unregister:4",
                "destroy-window",
                "unregister-class",
            },
            native.CleanupOperations);
    }

    [TestMethod]
    public void InvalidInput_DoesNotCreateAWindowOrCallRegisterHotKey()
    {
        FakeWindowsHotkeyNativeApi native = new();
        using WindowsGlobalHotkeyRegistrar registrar = new(native);

        HotkeyRegistrationOutcome outcome = registrar.Register(
            HotkeyAction.ReassignAllWindows,
            HotkeyChord.Unassigned);

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
                new NativeRegistration(
                    registrationId,
                    modifiers,
                    virtualKey));
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

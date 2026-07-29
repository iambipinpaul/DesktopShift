using System.ComponentModel;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>
/// Claims optional global shortcuts through a private message-only window.
/// </summary>
/// <remarks>
/// The window is created lazily, so constructing the service does not reserve
/// a key or create native state. Registration and disposal must occur on the
/// thread that first registered a shortcut because User32 windows and hotkeys
/// are owned by that thread.
/// </remarks>
public sealed class WindowsGlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    public const uint HotkeyMessage = 0x0312;
    public const int HotkeyAlreadyRegisteredError = 1409;
    public const int ReassignAllRegistrationId = 1;
    public const int ForegroundWindowRegistrationId = 2;
    public const int TogglePauseRegistrationId = 3;
    public const int OpenDesktopShiftRegistrationId = 4;

    private const nint MessageHandled = 0;
    private const string WindowClassPrefix = "DesktopShift.GlobalHotkeys.";

    private readonly IWindowsHotkeyNativeApi native;
    private readonly WindowsHotkeyWindowProcedure windowProcedure;
    private readonly string windowClassName;
    private readonly object syncRoot = new();
    private readonly Dictionary<int, HotkeyAction> registrations = [];
    private nint windowHandle;
    private uint ownerThreadId;
    private bool isWindowClassRegistered;
    private bool isDisposed;

    public WindowsGlobalHotkeyRegistrar()
        : this(new User32WindowsHotkeyNativeApi())
    {
    }

    public WindowsGlobalHotkeyRegistrar(IWindowsHotkeyNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);

        this.native = native;
        windowProcedure = OnWindowMessage;
        windowClassName = WindowClassPrefix + Guid.NewGuid().ToString("N");
    }

    public event EventHandler<HotkeyInvokedEventArgs>? Pressed;

    public HotkeyRegistrationOutcome Register(
        HotkeyAction action,
        HotkeyChord chord)
    {
        lock (syncRoot)
        {
            ObjectDisposedException.ThrowIf(isDisposed, this);

            if (!Win32HotkeyTranslation.TryTranslate(
                chord,
                out uint modifiers,
                out uint virtualKey,
                out string? translationFailure))
            {
                return new HotkeyRegistrationOutcome(
                    false,
                    translationFailure);
            }

            if (!TryGetRegistrationId(action, out int registrationId))
            {
                return new HotkeyRegistrationOutcome(
                    false,
                    "The shortcut action is not supported.");
            }

            if (registrations.ContainsKey(registrationId))
            {
                return new HotkeyRegistrationOutcome(
                    false,
                    "This DesktopShift shortcut is already registered.");
            }

            HotkeyRegistrationOutcome? windowFailure = EnsureMessageWindow();
            if (windowFailure is not null)
            {
                return windowFailure;
            }

            if (native.CurrentThreadId != ownerThreadId)
            {
                return new HotkeyRegistrationOutcome(
                    false,
                    "Global shortcuts must be changed on the thread that owns their Windows message window.");
            }

            if (!native.RegisterHotKey(
                windowHandle,
                registrationId,
                modifiers,
                virtualKey))
            {
                int error = native.GetLastError();
                return RegistrationFailure(error);
            }

            registrations.Add(registrationId, action);
            return HotkeyRegistrationOutcome.Success;
        }
    }

    public void UnregisterAll()
    {
        lock (syncRoot)
        {
            if (isDisposed)
            {
                return;
            }

            ReleaseRegistrations();
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (isDisposed)
            {
                return;
            }

            isDisposed = true;
            ReleaseRegistrations();

            if (windowHandle != 0)
            {
                _ = native.DestroyWindow(windowHandle);
                windowHandle = 0;
            }

            if (isWindowClassRegistered)
            {
                _ = native.UnregisterWindowClass(windowClassName);
                isWindowClassRegistered = false;
            }

            Pressed = null;
        }
    }

    private HotkeyRegistrationOutcome? EnsureMessageWindow()
    {
        if (windowHandle != 0)
        {
            return null;
        }

        ownerThreadId = native.CurrentThreadId;
        if (!native.RegisterWindowClass(windowClassName, windowProcedure))
        {
            int error = native.GetLastError();
            return NativeSetupFailure(
                "register its global-shortcut message window class",
                error);
        }

        isWindowClassRegistered = true;
        windowHandle = native.CreateMessageOnlyWindow(windowClassName);
        if (windowHandle != 0)
        {
            return null;
        }

        int createError = native.GetLastError();
        _ = native.UnregisterWindowClass(windowClassName);
        isWindowClassRegistered = false;
        ownerThreadId = 0;
        return NativeSetupFailure(
            "create its global-shortcut message window",
            createError);
    }

    private void ReleaseRegistrations()
    {
        foreach (int registrationId in registrations.Keys.Order())
        {
            if (windowHandle != 0)
            {
                _ = native.UnregisterHotKey(windowHandle, registrationId);
            }
        }

        registrations.Clear();
    }

    private nint OnWindowMessage(
        nint handle,
        uint message,
        nint wParam,
        nint lParam)
    {
        EventHandler<HotkeyInvokedEventArgs>? handler = null;
        HotkeyAction action = default;

        if (message == HotkeyMessage)
        {
            lock (syncRoot)
            {
                if (!isDisposed &&
                    registrations.TryGetValue((int)wParam, out action))
                {
                    handler = Pressed;
                }
            }

            if (handler is not null)
            {
                try
                {
                    handler(this, new HotkeyInvokedEventArgs(action));
                }
                catch (Exception)
                {
                    // No managed exception may cross a native window procedure.
                    // Shortcut commands report their own failures asynchronously.
                }
            }

            return MessageHandled;
        }

        return native.DefWindowProc(handle, message, wParam, lParam);
    }

    private static HotkeyRegistrationOutcome RegistrationFailure(int error)
    {
        if (error == HotkeyAlreadyRegisteredError)
        {
            return new HotkeyRegistrationOutcome(
                false,
                "Another application is already using this shortcut.",
                error);
        }

        string detail = error == 0
            ? "Windows returned no additional error information."
            : new Win32Exception(error).Message;
        return new HotkeyRegistrationOutcome(
            false,
            $"Windows could not register this shortcut: {detail}",
            error == 0 ? null : error);
    }

    private static HotkeyRegistrationOutcome NativeSetupFailure(
        string operation,
        int error)
    {
        string detail = error == 0
            ? "Windows returned no additional error information."
            : new Win32Exception(error).Message;
        return new HotkeyRegistrationOutcome(
            false,
            $"DesktopShift could not {operation}: {detail}",
            error == 0 ? null : error);
    }

    private static bool TryGetRegistrationId(
        HotkeyAction action,
        out int registrationId)
    {
        registrationId = action switch
        {
            HotkeyAction.ReassignAllWindows => ReassignAllRegistrationId,
            HotkeyAction.ReassignForegroundWindow =>
                ForegroundWindowRegistrationId,
            HotkeyAction.TogglePause => TogglePauseRegistrationId,
            HotkeyAction.OpenDesktopShift => OpenDesktopShiftRegistrationId,
            _ => 0,
        };
        return registrationId != 0;
    }
}

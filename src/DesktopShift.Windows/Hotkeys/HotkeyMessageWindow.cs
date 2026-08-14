using System.ComponentModel;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>
/// A private message-only window that holds <c>RegisterHotKey</c> claims and
/// reports the numeric id of whichever one was pressed.
/// </summary>
/// <remarks>
/// <para>
/// The window class registration, lazy creation, owning-thread rule, and
/// release-before-destroy order live together here so the desktop-switching
/// registrar stays focused on its profile and id mapping.
/// </para>
/// <para>
/// The window is created lazily, so constructing this reserves no combination
/// and creates no native state. Registration and disposal must happen on the
/// thread that first registered, because User32 windows and hotkeys are owned by
/// the thread that created them.
/// </para>
/// <para>
/// Ids are opaque here. What a given id means belongs to the registrar above.
/// </para>
/// </remarks>
internal sealed class HotkeyMessageWindow : IDisposable
{
    public const uint HotkeyMessage = 0x0312;
    public const int HotkeyAlreadyRegisteredError = 1409;

    private const nint MessageHandled = 0;

    private readonly IWindowsHotkeyNativeApi native;
    private readonly WindowsHotkeyWindowProcedure windowProcedure;
    private readonly string windowClassName;
    private readonly object syncRoot = new();
    private readonly HashSet<int> registrations = [];
    private nint windowHandle;
    private uint ownerThreadId;
    private bool isWindowClassRegistered;
    private bool isDisposed;

    /// <param name="native">The User32 boundary to work through.</param>
    /// <param name="windowClassPrefix">
    /// The class name prefix, made unique per instance so two registrars in one
    /// process never collide on a class name.
    /// </param>
    public HotkeyMessageWindow(
        IWindowsHotkeyNativeApi native,
        string windowClassPrefix)
    {
        ArgumentNullException.ThrowIfNull(native);
        ArgumentException.ThrowIfNullOrWhiteSpace(windowClassPrefix);

        this.native = native;
        windowProcedure = OnWindowMessage;
        windowClassName = windowClassPrefix + Guid.NewGuid().ToString("N");
    }

    /// <summary>Raised with the id of the combination that was pressed.</summary>
    public event EventHandler<int>? Pressed;

    /// <summary>Whether this window has been disposed.</summary>
    public bool IsDisposed
    {
        get
        {
            lock (syncRoot)
            {
                return isDisposed;
            }
        }
    }

    /// <summary>
    /// Claims one combination under <paramref name="registrationId"/>.
    /// </summary>
    /// <remarks>
    /// The chord is translated before the window is created, so a combination
    /// that could never be registered costs no native state at all.
    /// </remarks>
    public HotkeyRegistrationOutcome Register(int registrationId, HotkeyChord chord)
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
                return new HotkeyRegistrationOutcome(false, translationFailure);
            }

            if (registrations.Contains(registrationId))
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
                    "Desktop switching shortcuts must be changed on the thread that owns their Windows message window.");
            }

            if (!native.RegisterHotKey(
                windowHandle,
                registrationId,
                modifiers,
                virtualKey))
            {
                return RegistrationFailure(native.GetLastError());
            }

            _ = registrations.Add(registrationId);
            return HotkeyRegistrationOutcome.Success;
        }
    }

    /// <summary>Releases every combination this window currently holds.</summary>
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
            return NativeSetupFailure(
                "register its global-shortcut message window class",
                native.GetLastError());
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
        foreach (int registrationId in registrations.Order())
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
        if (message != HotkeyMessage)
        {
            return native.DefWindowProc(handle, message, wParam, lParam);
        }

        EventHandler<int>? handler = null;
        int registrationId = (int)wParam;

        lock (syncRoot)
        {
            if (!isDisposed && registrations.Contains(registrationId))
            {
                handler = Pressed;
            }
        }

        if (handler is not null)
        {
            try
            {
                handler(this, registrationId);
            }
            catch (Exception)
            {
                // No managed exception may cross a native window procedure.
                // Shortcut commands report their own failures asynchronously.
            }
        }

        return MessageHandled;
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
}

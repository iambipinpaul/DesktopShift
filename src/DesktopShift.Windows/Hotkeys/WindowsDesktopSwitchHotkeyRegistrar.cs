using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>
/// Claims the ten desktop-switching combinations through a private message-only
/// window.
/// </summary>
/// <remarks>
/// <para>
/// Hotkey ids are scoped to the private window that owns them, so a profile
/// change can release all ten claims together.
/// </para>
/// <para>
/// Every combination is registered with <c>MOD_NOREPEAT</c>, which
/// <see cref="Win32HotkeyTranslation"/> always requests. Holding Ctrl+Alt+2 has
/// to mean "go to Desktop 2" once, not a stream of switches for as long as the
/// key is down.
/// </para>
/// </remarks>
public sealed class WindowsDesktopSwitchHotkeyRegistrar :
    IDesktopSwitchHotkeyRegistrar
{
    /// <summary>
    /// The id of the shortcut for Desktop 1. The rest follow in desktop order,
    /// so Desktop 10 is <c>DesktopRegistrationIdBase + 9</c>.
    /// </summary>
    /// <remarks>
    /// Kept above ordinary control ids so native traces are easy to recognize.
    /// </remarks>
    public const int DesktopRegistrationIdBase = 101;

    private const string WindowClassPrefix = "DesktopShift.DesktopSwitchHotkeys.";

    private readonly HotkeyMessageWindow window;
    private readonly object syncRoot = new();
    private readonly Dictionary<int, int> desktopOrdinalsByRegistrationId = [];

    public WindowsDesktopSwitchHotkeyRegistrar()
        : this(new User32WindowsHotkeyNativeApi())
    {
    }

    public WindowsDesktopSwitchHotkeyRegistrar(IWindowsHotkeyNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);

        window = new HotkeyMessageWindow(native, WindowClassPrefix);
        window.Pressed += OnWindowPressed;
    }

    public event EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? Pressed;

    /// <summary>The registration id that carries a desktop position.</summary>
    public static int RegistrationIdFor(int desktopOrdinal) =>
        DesktopRegistrationIdBase + desktopOrdinal - 1;

    public HotkeyRegistrationOutcome Register(int desktopOrdinal, HotkeyChord chord)
    {
        if (desktopOrdinal is < DesktopSwitchShortcuts.MinDesktopOrdinal
            or > DesktopSwitchShortcuts.MaxDesktopOrdinal)
        {
            return new HotkeyRegistrationOutcome(
                false,
                "The desktop position is outside the range a shortcut can select.");
        }

        lock (syncRoot)
        {
            int registrationId = RegistrationIdFor(desktopOrdinal);
            HotkeyRegistrationOutcome outcome =
                window.Register(registrationId, chord);
            if (outcome.Succeeded)
            {
                desktopOrdinalsByRegistrationId[registrationId] = desktopOrdinal;
            }

            return outcome;
        }
    }

    public void UnregisterAll()
    {
        lock (syncRoot)
        {
            window.UnregisterAll();
            desktopOrdinalsByRegistrationId.Clear();
        }
    }

    public void Dispose()
    {
        lock (syncRoot)
        {
            if (window.IsDisposed)
            {
                return;
            }

            desktopOrdinalsByRegistrationId.Clear();
        }

        window.Pressed -= OnWindowPressed;
        window.Dispose();
        Pressed = null;
    }

    private void OnWindowPressed(object? sender, int registrationId)
    {
        EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? handler;
        int desktopOrdinal;

        lock (syncRoot)
        {
            if (!desktopOrdinalsByRegistrationId.TryGetValue(
                registrationId,
                out desktopOrdinal))
            {
                return;
            }

            handler = Pressed;
        }

        handler?.Invoke(
            this,
            new DesktopSwitchHotkeyInvokedEventArgs(desktopOrdinal));
    }
}

using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>
/// Claims the four optional command shortcuts through a private message-only
/// window.
/// </summary>
/// <remarks>
/// The window itself is <see cref="HotkeyMessageWindow"/>, which is shared with
/// desktop switching. All this type adds is the mapping between an action and
/// the numeric id Windows reports — the part that is genuinely specific to these
/// four shortcuts.
/// </remarks>
public sealed class WindowsGlobalHotkeyRegistrar : IGlobalHotkeyRegistrar
{
    public const uint HotkeyMessage = HotkeyMessageWindow.HotkeyMessage;
    public const int HotkeyAlreadyRegisteredError =
        HotkeyMessageWindow.HotkeyAlreadyRegisteredError;
    public const int ReassignAllRegistrationId = 1;
    public const int ForegroundWindowRegistrationId = 2;
    public const int TogglePauseRegistrationId = 3;
    public const int OpenDesktopShiftRegistrationId = 4;

    private const string WindowClassPrefix = "DesktopShift.GlobalHotkeys.";

    private readonly HotkeyMessageWindow window;
    private readonly object syncRoot = new();
    private readonly Dictionary<int, HotkeyAction> actionsByRegistrationId = [];

    public WindowsGlobalHotkeyRegistrar()
        : this(new User32WindowsHotkeyNativeApi())
    {
    }

    public WindowsGlobalHotkeyRegistrar(IWindowsHotkeyNativeApi native)
    {
        ArgumentNullException.ThrowIfNull(native);

        window = new HotkeyMessageWindow(native, WindowClassPrefix);
        window.Pressed += OnWindowPressed;
    }

    public event EventHandler<HotkeyInvokedEventArgs>? Pressed;

    public HotkeyRegistrationOutcome Register(
        HotkeyAction action,
        HotkeyChord chord)
    {
        lock (syncRoot)
        {
            if (!TryGetRegistrationId(action, out int registrationId))
            {
                return new HotkeyRegistrationOutcome(
                    false,
                    "The shortcut action is not supported.");
            }

            HotkeyRegistrationOutcome outcome =
                window.Register(registrationId, chord);
            if (outcome.Succeeded)
            {
                actionsByRegistrationId[registrationId] = action;
            }

            return outcome;
        }
    }

    public void UnregisterAll()
    {
        lock (syncRoot)
        {
            window.UnregisterAll();
            actionsByRegistrationId.Clear();
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

            actionsByRegistrationId.Clear();
        }

        window.Pressed -= OnWindowPressed;
        window.Dispose();
        Pressed = null;
    }

    private void OnWindowPressed(object? sender, int registrationId)
    {
        EventHandler<HotkeyInvokedEventArgs>? handler;
        HotkeyAction action;

        lock (syncRoot)
        {
            if (!actionsByRegistrationId.TryGetValue(registrationId, out action))
            {
                return;
            }

            handler = Pressed;
        }

        handler?.Invoke(this, new HotkeyInvokedEventArgs(action));
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

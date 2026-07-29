using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Windows.Hotkeys;

/// <summary>
/// Translates the portable shortcut vocabulary into the values consumed by
/// <c>RegisterHotKey</c>.
/// </summary>
public static class Win32HotkeyTranslation
{
    private const uint ModAlt = 0x0001;
    private const uint ModControl = 0x0002;
    private const uint ModShift = 0x0004;
    private const uint ModWindows = 0x0008;
    private const uint ModNoRepeat = 0x4000;

    private const HotkeyModifiers SupportedModifiers =
        HotkeyModifiers.Alt |
        HotkeyModifiers.Control |
        HotkeyModifiers.Shift |
        HotkeyModifiers.Windows;

    /// <summary>
    /// Produces native modifier and virtual-key values without calling Windows.
    /// </summary>
    /// <remarks>
    /// <c>MOD_NOREPEAT</c> is always requested. Holding a shortcut must produce
    /// one action, not a stream of queued reassignment batches.
    /// </remarks>
    public static bool TryTranslate(
        HotkeyChord chord,
        out uint modifiers,
        out uint virtualKey,
        out string? failure)
    {
        modifiers = 0;
        virtualKey = 0;

        if (!chord.IsAssigned)
        {
            failure = "The shortcut has no key assigned.";
            return false;
        }

        if ((chord.Modifiers & ~SupportedModifiers) != 0)
        {
            failure = "The shortcut contains an unsupported modifier value.";
            return false;
        }

        if (chord.Modifiers == HotkeyModifiers.None)
        {
            failure = "The shortcut needs at least one modifier key.";
            return false;
        }

        if (!Enum.IsDefined(chord.Key))
        {
            failure = "The shortcut contains an unsupported key value.";
            return false;
        }

        uint translated = ModNoRepeat;
        if (chord.Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            translated |= ModAlt;
        }

        if (chord.Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            translated |= ModControl;
        }

        if (chord.Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            translated |= ModShift;
        }

        if (chord.Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            translated |= ModWindows;
        }

        modifiers = translated;
        virtualKey = (uint)chord.Key;
        failure = null;
        return true;
    }
}

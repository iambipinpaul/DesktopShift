namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// The modifier keys a chord may carry.
/// </summary>
/// <remarks>
/// The values match the Win32 <c>MOD_*</c> constants, but the mapping is still
/// written out explicitly in the Windows layer. Sharing the numbers here is a
/// convenience; depending on them silently would make a future divergence
/// invisible.
/// </remarks>
[Flags]
public enum HotkeyModifiers
{
    None = 0,
    Alt = 1,
    Control = 2,
    Shift = 4,
    Windows = 8,
}

/// <summary>
/// The number-row keys a desktop-switching chord may use, valued as Win32
/// virtual-key codes.
/// </summary>
/// <remarks>
/// Deliberately a closed enum rather than an arbitrary code. A configuration
/// document is hand-editable, and an arbitrary numeric virtual key would let a
/// document ask for a chord that cannot be typed, cannot be shown, and cannot
/// be diagnosed when it silently fails to register.
/// </remarks>
public enum HotkeyKey
{
    None = 0,
    D0 = 0x30,
    D1 = 0x31,
    D2 = 0x32,
    D3 = 0x33,
    D4 = 0x34,
    D5 = 0x35,
    D6 = 0x36,
    D7 = 0x37,
    D8 = 0x38,
    D9 = 0x39,
}

/// <summary>
/// One key combination, independent of what it is bound to.
/// </summary>
/// <remarks>
/// Shared by the desktop-switching profile and its Windows registrar so
/// configuration and platform translation use one representation.
/// </remarks>
public readonly record struct HotkeyChord(HotkeyModifiers Modifiers, HotkeyKey Key)
{
    /// <summary>A chord that names no key, which is how "unassigned" is spelled.</summary>
    public static HotkeyChord Unassigned => default;

    /// <summary>Whether a key was actually chosen.</summary>
    public bool IsAssigned => Key != HotkeyKey.None;

    /// <summary>
    /// The combination as a user reads it, in the order Windows itself prints
    /// modifiers.
    /// </summary>
    public string Describe()
    {
        if (!IsAssigned)
        {
            return "Not assigned";
        }

        List<string> parts = [];
        if (Modifiers.HasFlag(HotkeyModifiers.Control))
        {
            parts.Add("Ctrl");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Alt))
        {
            parts.Add("Alt");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Shift))
        {
            parts.Add("Shift");
        }

        if (Modifiers.HasFlag(HotkeyModifiers.Windows))
        {
            parts.Add("Win");
        }

        parts.Add(DescribeKey(Key));
        return string.Join(" + ", parts);
    }

    private static string DescribeKey(HotkeyKey key) =>
        key is >= HotkeyKey.D0 and <= HotkeyKey.D9
            ? ((char)key).ToString()
            : key.ToString();
}

/// <summary>
/// What one attempt to claim a chord system-wide produced.
/// </summary>
/// <param name="Succeeded">Whether Windows handed the combination over.</param>
/// <param name="FailureMessage">
/// Why it did not, phrased for the user. Null on success.
/// </param>
/// <param name="NativeErrorCode">
/// The Win32 error, when there was one, so a report can distinguish "another
/// application already owns this" from anything else.
/// </param>
public sealed record HotkeyRegistrationOutcome(
    bool Succeeded,
    string? FailureMessage = null,
    int? NativeErrorCode = null)
{
    /// <summary>The outcome of a chord Windows accepted.</summary>
    public static HotkeyRegistrationOutcome Success { get; } = new(true);
}

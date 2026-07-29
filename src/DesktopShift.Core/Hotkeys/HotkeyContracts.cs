using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// The four things a global shortcut is allowed to do.
/// </summary>
/// <remarks>
/// A global hotkey is the most intrusive thing a background utility can claim:
/// the combination stops working everywhere else on the machine for as long as
/// it is held. So the set is closed and small, and every member maps to an
/// action the user can already reach from the notification area. Nothing here
/// can create, delete, or reorder a desktop.
/// </remarks>
public enum HotkeyAction
{
    /// <summary>Runs the manual reassignment batch over every open window.</summary>
    ReassignAllWindows,

    /// <summary>Reassigns only the window that currently has the foreground.</summary>
    ReassignForegroundWindow,

    /// <summary>Flips automatic assignment between paused and running.</summary>
    TogglePause,

    /// <summary>Restores and focuses the shell window.</summary>
    OpenDesktopShift,
}

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
/// The keys a chord may be built on, valued as Win32 virtual-key codes.
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
    Space = 0x20,
    PageUp = 0x21,
    PageDown = 0x22,
    End = 0x23,
    Home = 0x24,
    Insert = 0x2D,
    Delete = 0x2E,
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
    A = 0x41,
    B = 0x42,
    C = 0x43,
    D = 0x44,
    E = 0x45,
    F = 0x46,
    G = 0x47,
    H = 0x48,
    I = 0x49,
    J = 0x4A,
    K = 0x4B,
    L = 0x4C,
    M = 0x4D,
    N = 0x4E,
    O = 0x4F,
    P = 0x50,
    Q = 0x51,
    R = 0x52,
    S = 0x53,
    T = 0x54,
    U = 0x55,
    V = 0x56,
    W = 0x57,
    X = 0x58,
    Y = 0x59,
    Z = 0x5A,
    F1 = 0x70,
    F2 = 0x71,
    F3 = 0x72,
    F4 = 0x73,
    F5 = 0x74,
    F6 = 0x75,
    F7 = 0x76,
    F8 = 0x77,
    F9 = 0x78,
    F10 = 0x79,
    F11 = 0x7A,
    F12 = 0x7B,
}

/// <summary>
/// One key combination, independent of what it is bound to.
/// </summary>
/// <remarks>
/// Separate from <see cref="HotkeyBinding"/> because conflict detection asks
/// only whether two chords are the same, and that question must not depend on
/// which action, or which enabled flag, happens to sit beside them.
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

    private static string DescribeKey(HotkeyKey key) => key switch
    {
        >= HotkeyKey.D0 and <= HotkeyKey.D9 => ((char)key).ToString(),
        HotkeyKey.PageUp => "Page Up",
        HotkeyKey.PageDown => "Page Down",
        _ => key.ToString(),
    };
}

/// <summary>
/// One action bound to one chord.
/// </summary>
/// <remarks>
/// The modifiers and the key are separate members rather than a nested chord so
/// the persisted JSON stays flat and hand-editable, which is the whole reason
/// the configuration document is human-readable in the first place.
/// </remarks>
/// <param name="Action">What the chord does.</param>
/// <param name="Modifiers">The modifier keys held down.</param>
/// <param name="Key">The key pressed.</param>
/// <param name="IsEnabled">
/// Whether this one binding takes part. An enabled binding still registers
/// nothing while global hotkeys are switched off as a whole.
/// </param>
public sealed record HotkeyBinding(
    HotkeyAction Action,
    HotkeyModifiers Modifiers,
    HotkeyKey Key,
    bool IsEnabled)
{
    /// <summary>The combination this binding claims.</summary>
    public HotkeyChord Chord => new(Modifiers, Key);
}

/// <summary>
/// The complete hotkey configuration: the master switch and every binding.
/// </summary>
/// <param name="IsEnabled">
/// The master switch. While it is off nothing is registered, whatever the
/// individual bindings say. This is what makes "global hotkeys remain
/// unregistered until enabled" a single checkable condition rather than a
/// property of four separate rows.
/// </param>
/// <param name="Bindings">Every action's binding, valid or not.</param>
public sealed record HotkeySettings(
    bool IsEnabled,
    ImmutableArray<HotkeyBinding> Bindings)
{
    /// <summary>Hotkeys switched off with the shipped chords still shown.</summary>
    public static HotkeySettings Disabled { get; } =
        new(false, HotkeyDefaults.Bindings);

    /// <summary>
    /// Bindings, normalized so an omitted collection is empty rather than a
    /// default array. A default array cannot be enumerated.
    /// </summary>
    public ImmutableArray<HotkeyBinding> Bindings { get; init; } =
        Bindings.IsDefault ? [] : Bindings;
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

/// <summary>
/// One action's registration as it actually stands right now.
/// </summary>
public sealed record HotkeyRegistration(
    HotkeyAction Action,
    HotkeyChord Chord,
    bool IsRegistered,
    string? Failure = null);

/// <summary>
/// Everything a caller can know about the current hotkey registrations.
/// </summary>
/// <param name="IsEnabled">Whether the master switch is on.</param>
/// <param name="Registrations">One entry per binding that was attempted.</param>
/// <param name="Issues">
/// Conflicts, unassigned chords, and registration failures, reported as
/// ordinary configuration validation issues so the Settings page shows them the
/// same way it shows every other problem.
/// </param>
/// <param name="ObservedAtUtc">When this state was produced.</param>
public sealed record HotkeyState(
    bool IsEnabled,
    ImmutableArray<HotkeyRegistration> Registrations,
    ImmutableArray<ConfigurationValidationIssue> Issues,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>Registrations, normalized away from a default array.</summary>
    public ImmutableArray<HotkeyRegistration> Registrations { get; init; } =
        Registrations.IsDefault ? [] : Registrations;

    /// <summary>Issues, normalized away from a default array.</summary>
    public ImmutableArray<ConfigurationValidationIssue> Issues { get; init; } =
        Issues.IsDefault ? [] : Issues;

    /// <summary>The number of chords Windows actually handed over.</summary>
    public int RegisteredCount =>
        Registrations.Count(static registration => registration.IsRegistered);
}

/// <summary>Raised when a registered chord was pressed.</summary>
public sealed class HotkeyInvokedEventArgs : EventArgs
{
    public HotkeyInvokedEventArgs(HotkeyAction action)
    {
        Action = action;
    }

    public HotkeyAction Action { get; }
}

/// <summary>
/// The Win32 <c>RegisterHotKey</c> surface, and nothing else.
/// </summary>
/// <remarks>
/// Every call that actually claims a combination system-wide lives behind this
/// interface. That is not a style preference: <c>RegisterHotKey</c> needs a real
/// message loop and takes the combination away from every other application on
/// the machine for as long as it is held, so no automated test may ever reach
/// it. The conflict rules, the ordering, and the disposal are all decided above
/// this seam and proved against a fake.
/// </remarks>
public interface IGlobalHotkeyRegistrar : IDisposable
{
    /// <summary>Raised on the pressed chord's action.</summary>
    event EventHandler<HotkeyInvokedEventArgs>? Pressed;

    /// <summary>Attempts to claim one combination for one action.</summary>
    HotkeyRegistrationOutcome Register(HotkeyAction action, HotkeyChord chord);

    /// <summary>Releases every combination this registrar currently holds.</summary>
    void UnregisterAll();
}

/// <summary>
/// Owns the gap between the settings a user wrote and the combinations Windows
/// is actually holding.
/// </summary>
public interface IGlobalHotkeyCoordinator : IDisposable
{
    /// <summary>The registrations as they stand.</summary>
    HotkeyState Current { get; }

    /// <summary>Raised when a registered chord was pressed.</summary>
    event EventHandler<HotkeyInvokedEventArgs>? Invoked;

    /// <summary>
    /// Makes the live registrations match <paramref name="settings"/>, releasing
    /// everything first so no stale combination survives an edit.
    /// </summary>
    HotkeyState Apply(HotkeySettings settings);
}

/// <summary>
/// The foreground window, as the reassign-foreground shortcut needs to see it.
/// </summary>
public interface IForegroundWindowProvider
{
    /// <summary>
    /// The window with the foreground, or zero when nothing has it.
    /// </summary>
    nint GetForegroundWindow();
}

/// <summary>
/// What a reassign-foreground shortcut did.
/// </summary>
/// <param name="HadForegroundWindow">
/// Whether there was a window to act on at all. A shortcut pressed against an
/// empty desktop is not a failure.
/// </param>
/// <param name="WindowHandle">The window that was processed, or zero.</param>
public sealed record ForegroundReassignmentResult(
    bool HadForegroundWindow,
    nint WindowHandle);

/// <summary>
/// Reassigns the single window that currently has the foreground.
/// </summary>
public interface IForegroundWindowReassignment
{
    Task<ForegroundReassignmentResult> ReassignForegroundWindowAsync(
        CancellationToken cancellationToken = default);
}

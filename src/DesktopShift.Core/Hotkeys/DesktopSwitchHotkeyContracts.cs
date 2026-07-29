using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// The three shapes the desktop-switching shortcuts can take.
/// </summary>
/// <remarks>
/// <para>
/// A profile rather than ten editable rows. The ten shortcuts only make sense
/// as a set — a user who has Ctrl+Alt+1 through Ctrl+Alt+9 and then something
/// unrelated on 0 has a keyboard they cannot predict — so the modifiers are
/// chosen once and the digit row supplies the rest.
/// </para>
/// <para>
/// Exactly one profile is active. There is no combination of profiles, because
/// two live sets would claim twenty combinations from the machine to do ten
/// things.
/// </para>
/// </remarks>
public enum DesktopSwitchShortcutProfile
{
    /// <summary>
    /// Ctrl + Alt + digit. The shipped choice: nothing in Windows claims this
    /// space, so every one of the ten registers.
    /// </summary>
    CtrlAlt,

    /// <summary>Whatever combination of Ctrl, Alt, Shift, and Win the user picked.</summary>
    Custom,
}

/// <summary>
/// Which desktop-switching shortcuts are wanted, and what they are built from.
/// </summary>
/// <remarks>
/// <para>
/// The custom modifiers are carried even while a built-in profile is active, so
/// switching to Custom and back does not lose what the user had chosen. Only
/// <see cref="ActiveModifiers"/> decides what is actually claimed.
/// </para>
/// <para>
/// <paramref name="IsEnabled"/> defaults to off, for the same reason
/// <see cref="Configuration.BehaviorSettings.AreHotkeysEnabled"/> does: ten
/// global combinations are taken from every other application on the machine,
/// so they are claimed only when asked for.
/// </para>
/// </remarks>
/// <param name="IsEnabled">Whether the ten combinations are claimed at all.</param>
/// <param name="Profile">Which profile supplies the modifiers.</param>
/// <param name="CustomModifiers">
/// The modifiers the Custom profile uses. Ignored by the other two.
/// </param>
public sealed record DesktopSwitchShortcutSettings(
    bool IsEnabled,
    DesktopSwitchShortcutProfile Profile = DesktopSwitchShortcutProfile.CtrlAlt,
    HotkeyModifiers CustomModifiers = DesktopSwitchShortcuts.CtrlAltModifiers)
{
    /// <summary>Switched off, with the recommended profile still selected.</summary>
    public static DesktopSwitchShortcutSettings Disabled { get; } = new(false);

    /// <summary>
    /// The modifiers this configuration actually claims, whichever profile is
    /// selected.
    /// </summary>
    public HotkeyModifiers ActiveModifiers =>
        DesktopSwitchShortcuts.ModifiersFor(Profile, CustomModifiers);

    /// <summary>
    /// The ten desktop-to-chord bindings this configuration describes, whether
    /// or not it is enabled.
    /// </summary>
    /// <remarks>
    /// Produced rather than stored. A stored list could drift from the profile
    /// that is supposed to explain it, and then the radio button and the live
    /// registrations would disagree with nothing to say which was right.
    /// </remarks>
    public ImmutableArray<DesktopSwitchHotkeyBinding> Bindings() =>
        DesktopSwitchShortcuts.Bindings(ActiveModifiers);
}

/// <summary>
/// One desktop position bound to one combination.
/// </summary>
/// <param name="DesktopOrdinal">
/// The desktop's one-based position in Task View: 1 through
/// <see cref="DesktopSwitchShortcuts.MaxDesktopOrdinal"/>.
/// </param>
/// <param name="Chord">The combination that selects it.</param>
public sealed record DesktopSwitchHotkeyBinding(
    int DesktopOrdinal,
    HotkeyChord Chord);

/// <summary>Raised when a registered desktop-switching chord was pressed.</summary>
public sealed class DesktopSwitchHotkeyInvokedEventArgs : EventArgs
{
    public DesktopSwitchHotkeyInvokedEventArgs(int desktopOrdinal)
    {
        DesktopOrdinal = desktopOrdinal;
    }

    /// <summary>The one-based desktop position the pressed chord selects.</summary>
    public int DesktopOrdinal { get; }
}

/// <summary>
/// One desktop-switching registration as it actually stands right now.
/// </summary>
public sealed record DesktopSwitchHotkeyRegistration(
    int DesktopOrdinal,
    HotkeyChord Chord,
    bool IsRegistered,
    string? Failure = null);

/// <summary>
/// Everything a caller can know about the live desktop-switching shortcuts.
/// </summary>
/// <param name="IsEnabled">Whether the shortcuts are switched on.</param>
/// <param name="Profile">The profile that produced the chords.</param>
/// <param name="Registrations">One entry per desktop that was attempted.</param>
/// <param name="Issues">
/// Everything wrong, reported as ordinary configuration validation issues so the
/// Settings page shows them the way it shows every other problem.
/// </param>
/// <param name="ObservedAtUtc">When this state was produced.</param>
public sealed record DesktopSwitchHotkeyState(
    bool IsEnabled,
    DesktopSwitchShortcutProfile Profile,
    ImmutableArray<DesktopSwitchHotkeyRegistration> Registrations,
    ImmutableArray<ConfigurationValidationIssue> Issues,
    DateTimeOffset ObservedAtUtc)
{
    /// <summary>Registrations, normalized away from a default array.</summary>
    public ImmutableArray<DesktopSwitchHotkeyRegistration> Registrations { get; init; } =
        Registrations.IsDefault ? [] : Registrations;

    /// <summary>Issues, normalized away from a default array.</summary>
    public ImmutableArray<ConfigurationValidationIssue> Issues { get; init; } =
        Issues.IsDefault ? [] : Issues;

    /// <summary>The number of combinations Windows actually handed over.</summary>
    public int RegisteredCount =>
        Registrations.Count(static registration => registration.IsRegistered);

    /// <summary>
    /// The desktop positions Windows refused, in ascending order, so a report
    /// can name them rather than only count them.
    /// </summary>
    public ImmutableArray<int> RefusedDesktopOrdinals =>
    [
        .. Registrations
            .Where(static registration => !registration.IsRegistered)
            .Select(static registration => registration.DesktopOrdinal)
            .Order(),
    ];
}

/// <summary>
/// The <c>RegisterHotKey</c> surface for desktop switching, and nothing else.
/// </summary>
/// <remarks>
/// Separate from <see cref="IGlobalHotkeyRegistrar"/> because the two claim
/// different things and fail independently: the four command shortcuts are
/// bound one action at a time, and these ten are bound as a profile. Sharing one
/// registrar would mean a refused desktop chord and a refused command chord
/// landing in the same list with nothing to tell them apart.
/// </remarks>
public interface IDesktopSwitchHotkeyRegistrar : IDisposable
{
    /// <summary>Raised on the pressed chord's desktop position.</summary>
    event EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? Pressed;

    /// <summary>Attempts to claim one combination for one desktop position.</summary>
    HotkeyRegistrationOutcome Register(int desktopOrdinal, HotkeyChord chord);

    /// <summary>Releases every combination this registrar currently holds.</summary>
    void UnregisterAll();
}

/// <summary>
/// Owns the gap between the profile a user picked and the combinations Windows
/// is actually holding.
/// </summary>
public interface IDesktopSwitchHotkeyCoordinator : IDisposable
{
    /// <summary>The registrations as they stand.</summary>
    DesktopSwitchHotkeyState Current { get; }

    /// <summary>Raised when a registered desktop-switching chord was pressed.</summary>
    event EventHandler<DesktopSwitchHotkeyInvokedEventArgs>? Invoked;

    /// <summary>
    /// Makes the live registrations match <paramref name="settings"/>, releasing
    /// everything first so no combination from the previous profile survives.
    /// </summary>
    DesktopSwitchHotkeyState Apply(DesktopSwitchShortcutSettings settings);
}

/// <summary>What one desktop-switching shortcut press produced.</summary>
public enum DesktopSwitchShortcutOutcome
{
    /// <summary>The foreground moved to the requested desktop.</summary>
    Switched,

    /// <summary>The requested desktop was already the current one.</summary>
    AlreadyCurrent,

    /// <summary>
    /// There is no desktop at that position. The user has fewer desktops than
    /// the digit they pressed.
    /// </summary>
    DesktopMissing,

    /// <summary>The selected provider cannot switch desktops on this machine.</summary>
    Unsupported,

    /// <summary>Windows refused the switch.</summary>
    Failed,
}

/// <summary>
/// What pressing one desktop-switching shortcut did, and what to tell the user.
/// </summary>
/// <param name="Outcome">What happened.</param>
/// <param name="DesktopOrdinal">The position that was asked for.</param>
/// <param name="DesktopCount">
/// How many desktops exist, so a missing-desktop message can say what the user
/// actually has rather than only what they do not.
/// </param>
/// <param name="Message">
/// The sentence worth showing, or null when nothing needs saying. A successful
/// switch is its own feedback — the screen changes — so it carries no message.
/// </param>
public sealed record DesktopSwitchShortcutResult(
    DesktopSwitchShortcutOutcome Outcome,
    int DesktopOrdinal,
    int DesktopCount,
    string? Message = null)
{
    /// <summary>Whether this outcome is worth interrupting the user over.</summary>
    public bool ShouldNotify => Message is not null;
}

/// <summary>
/// Moves the foreground to a desktop chosen by position.
/// </summary>
public interface IDesktopSwitchShortcutService
{
    Task<DesktopSwitchShortcutResult> SwitchToDesktopAsync(
        int desktopOrdinal,
        CancellationToken cancellationToken = default);
}

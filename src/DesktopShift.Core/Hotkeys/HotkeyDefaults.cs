using System.Collections.Immutable;

namespace DesktopShift.Core.Hotkeys;

/// <summary>
/// The chords DesktopShift suggests, and the order the Settings page lists them.
/// </summary>
/// <remarks>
/// <para>
/// Every default carries Ctrl, Alt, and Shift together. Three modifiers make a
/// combination almost impossible to press by accident and leaves the one- and
/// two-modifier space, which applications and Windows itself use heavily, alone.
/// </para>
/// <para>
/// No default uses the Windows key. Windows reserves most of that space for
/// itself, and a reserved combination fails to register with an error the user
/// can do nothing about — a shipped default that cannot work is worse than no
/// default at all.
/// </para>
/// <para>
/// Every default binding is individually enabled but the master switch is off,
/// so nothing is claimed until the user asks for it. The chords exist only so
/// the Settings page has something to show instead of four empty rows.
/// </para>
/// </remarks>
public static class HotkeyDefaults
{
    /// <summary>
    /// Every action in the order the Settings page lists them, which is the
    /// order of decreasing scope: all windows, one window, the pause switch,
    /// then the window itself.
    /// </summary>
    public static ImmutableArray<HotkeyAction> Actions { get; } =
    [
        HotkeyAction.ReassignAllWindows,
        HotkeyAction.ReassignForegroundWindow,
        HotkeyAction.TogglePause,
        HotkeyAction.OpenDesktopShift,
    ];

    /// <summary>The shipped chord for every action.</summary>
    public static ImmutableArray<HotkeyBinding> Bindings { get; } =
    [
        Create(HotkeyAction.ReassignAllWindows, HotkeyKey.R),
        Create(HotkeyAction.ReassignForegroundWindow, HotkeyKey.F),
        Create(HotkeyAction.TogglePause, HotkeyKey.P),
        Create(HotkeyAction.OpenDesktopShift, HotkeyKey.D),
    ];

    /// <summary>
    /// A short label for an action, used wherever a chord is shown next to what
    /// it does.
    /// </summary>
    public static string Describe(HotkeyAction action) => action switch
    {
        HotkeyAction.ReassignAllWindows => "Reassign all windows",
        HotkeyAction.ReassignForegroundWindow => "Reassign the foreground window",
        HotkeyAction.TogglePause => "Pause or resume automatic assignment",
        HotkeyAction.OpenDesktopShift => "Open DesktopShift",
        _ => action.ToString(),
    };

    /// <summary>
    /// One sentence saying what pressing the chord actually does, so the
    /// Settings page never has to explain an action twice.
    /// </summary>
    public static string Explain(HotkeyAction action) => action switch
    {
        HotkeyAction.ReassignAllWindows =>
            "Runs the same batch as Reassign all windows in the notification area. It works while automatic assignment is paused.",
        HotkeyAction.ReassignForegroundWindow =>
            "Moves only the window you are looking at, using the rule that claims it.",
        HotkeyAction.TogglePause =>
            "Stops DesktopShift acting on window events, or starts it again. Explicit reassignment keeps working either way.",
        HotkeyAction.OpenDesktopShift =>
            "Restores the DesktopShift window and brings it to the front.",
        _ => string.Empty,
    };

    /// <summary>
    /// Fills in a binding for every action, keeping whatever the document
    /// already says and falling back to the shipped chord.
    /// </summary>
    /// <remarks>
    /// A configuration written before hotkeys existed carries none at all, and a
    /// hand-edited document can carry some but not others. The Settings page
    /// must still show four rows, so the gap is closed here rather than in the
    /// page, where it could not be tested.
    /// </remarks>
    /// <param name="bindings">Whatever the document carries.</param>
    /// <returns>Exactly one binding per action, in <see cref="Actions"/> order.</returns>
    public static ImmutableArray<HotkeyBinding> Complete(
        ImmutableArray<HotkeyBinding> bindings)
    {
        ImmutableArray<HotkeyBinding> present = bindings.IsDefault ? [] : bindings;

        return
        [
            .. Actions.Select(action =>
                present.FirstOrDefault(binding => binding.Action == action) ??
                    Bindings.First(binding => binding.Action == action)),
        ];
    }

    private static HotkeyBinding Create(HotkeyAction action, HotkeyKey key) =>
        new(
            action,
            HotkeyModifiers.Control | HotkeyModifiers.Alt | HotkeyModifiers.Shift,
            key,
            IsEnabled: true);
}

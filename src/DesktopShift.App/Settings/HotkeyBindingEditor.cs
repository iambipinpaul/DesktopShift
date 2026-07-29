using DesktopShift.Core.Hotkeys;

namespace DesktopShift.App.Settings;

/// <summary>Editable presentation of one immutable hotkey binding.</summary>
public sealed class HotkeyBindingEditor
{
    public HotkeyBindingEditor(HotkeyBinding binding)
    {
        ArgumentNullException.ThrowIfNull(binding);

        Action = binding.Action;
        IsEnabled = binding.IsEnabled;
        Control = binding.Modifiers.HasFlag(HotkeyModifiers.Control);
        Alt = binding.Modifiers.HasFlag(HotkeyModifiers.Alt);
        Shift = binding.Modifiers.HasFlag(HotkeyModifiers.Shift);
        Windows = binding.Modifiers.HasFlag(HotkeyModifiers.Windows);
        Key = binding.Key;
    }

    public HotkeyAction Action { get; }

    public string ActionLabel => HotkeyDefaults.Describe(Action);

    public string Explanation => HotkeyDefaults.Explain(Action);

    public bool IsEnabled { get; set; }

    public bool Control { get; set; }

    public bool Alt { get; set; }

    public bool Shift { get; set; }

    public bool Windows { get; set; }

    public HotkeyKey Key { get; set; }

    public IReadOnlyList<HotkeyKey> KeyOptions { get; } =
        Enum.GetValues<HotkeyKey>();

    public HotkeyBinding ToBinding()
    {
        HotkeyModifiers modifiers = HotkeyModifiers.None;
        modifiers |= Control ? HotkeyModifiers.Control : HotkeyModifiers.None;
        modifiers |= Alt ? HotkeyModifiers.Alt : HotkeyModifiers.None;
        modifiers |= Shift ? HotkeyModifiers.Shift : HotkeyModifiers.None;
        modifiers |= Windows ? HotkeyModifiers.Windows : HotkeyModifiers.None;

        return new HotkeyBinding(Action, modifiers, Key, IsEnabled);
    }
}

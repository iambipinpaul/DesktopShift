using DesktopShift.Core.Hotkeys;
using DesktopShift.Windows.Hotkeys;

namespace DesktopShift.Windows.Tests.Hotkeys;

[TestClass]
public sealed class Win32HotkeyTranslationTests
{
    [TestMethod]
    public void AChord_MapsEveryModifierAndRequestsNoRepeat()
    {
        HotkeyChord chord = new(
            HotkeyModifiers.Control |
            HotkeyModifiers.Alt |
            HotkeyModifiers.Shift |
            HotkeyModifiers.Windows,
            HotkeyKey.D3);

        bool translated = Win32HotkeyTranslation.TryTranslate(
            chord,
            out uint modifiers,
            out uint virtualKey,
            out string? failure);

        Assert.IsTrue(translated, failure);
        Assert.AreEqual(0x400Fu, modifiers);
        Assert.AreEqual(0x33u, virtualKey);
    }

    [TestMethod]
    public void AnUnassignedChord_NeverReachesWindows()
    {
        bool translated = Win32HotkeyTranslation.TryTranslate(
            HotkeyChord.Unassigned,
            out _,
            out _,
            out string? failure);

        Assert.IsFalse(translated);
        Assert.AreEqual("The shortcut has no key assigned.", failure);
    }

    [TestMethod]
    public void AnUnknownModifier_NeverReachesWindows()
    {
        HotkeyChord chord = new((HotkeyModifiers)0x10, HotkeyKey.D3);

        bool translated = Win32HotkeyTranslation.TryTranslate(
            chord,
            out _,
            out _,
            out string? failure);

        Assert.IsFalse(translated);
        Assert.AreEqual(
            "The shortcut contains an unsupported modifier value.",
            failure);
    }

    [TestMethod]
    public void AKeyWithoutAModifier_NeverReachesWindows()
    {
        HotkeyChord chord = new(HotkeyModifiers.None, HotkeyKey.D3);

        bool translated = Win32HotkeyTranslation.TryTranslate(
            chord,
            out _,
            out _,
            out string? failure);

        Assert.IsFalse(translated);
        Assert.AreEqual(
            "The shortcut needs at least one modifier key.",
            failure);
    }

    [TestMethod]
    public void AnUnknownVirtualKey_NeverReachesWindows()
    {
        HotkeyChord chord = new(HotkeyModifiers.Control, (HotkeyKey)0xFF);

        bool translated = Win32HotkeyTranslation.TryTranslate(
            chord,
            out _,
            out _,
            out string? failure);

        Assert.IsFalse(translated);
        Assert.AreEqual(
            "The shortcut contains an unsupported key value.",
            failure);
    }
}

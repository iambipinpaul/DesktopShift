using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Core.Tests.Hotkeys;

/// <summary>
/// The rules that decide what a profile claims, proved without Windows.
/// </summary>
[TestClass]
public sealed class DesktopSwitchShortcutTests
{
    [TestMethod]
    public void DigitRow_MapsOneThroughNineDirectlyAndZeroToDesktopTen()
    {
        Assert.AreEqual(HotkeyKey.D1, DesktopSwitchShortcuts.KeyFor(1));
        Assert.AreEqual(HotkeyKey.D5, DesktopSwitchShortcuts.KeyFor(5));
        Assert.AreEqual(HotkeyKey.D9, DesktopSwitchShortcuts.KeyFor(9));
        Assert.AreEqual(HotkeyKey.D0, DesktopSwitchShortcuts.KeyFor(10));

        // Out of range produces no key rather than an exception: the value can
        // arrive from a hand-edited document.
        Assert.AreEqual(HotkeyKey.None, DesktopSwitchShortcuts.KeyFor(0));
        Assert.AreEqual(HotkeyKey.None, DesktopSwitchShortcuts.KeyFor(11));
    }

    [TestMethod]
    public void DigitRow_RoundTripsThroughItsInverse()
    {
        foreach (int ordinal in DesktopSwitchShortcuts.DesktopOrdinals)
        {
            Assert.AreEqual(
                ordinal,
                DesktopSwitchShortcuts.DesktopOrdinalFor(
                    DesktopSwitchShortcuts.KeyFor(ordinal)),
                $"Desktop {ordinal} did not survive the round trip.");
        }

        // A non-digit is not a desktop, however it reached us.
        Assert.AreEqual(0, DesktopSwitchShortcuts.DesktopOrdinalFor(HotkeyKey.F));
        Assert.AreEqual(0, DesktopSwitchShortcuts.DesktopOrdinalFor(HotkeyKey.None));
    }

    [TestMethod]
    public void CtrlAltProfile_ProducesTenChordsOverTheDigitRow()
    {
        DesktopSwitchShortcutSettings settings =
            new(true, DesktopSwitchShortcutProfile.CtrlAlt);

        ImmutableArray<DesktopSwitchHotkeyBinding> bindings = settings.Bindings();

        Assert.HasCount(10, bindings);
        Assert.AreEqual(
            DesktopSwitchShortcuts.CtrlAltModifiers,
            settings.ActiveModifiers);
        Assert.IsTrue(
            bindings.All(static binding =>
                binding.Chord.Modifiers ==
                (HotkeyModifiers.Control | HotkeyModifiers.Alt)),
            "Every chord should carry exactly Ctrl and Alt.");
        Assert.AreEqual(HotkeyKey.D1, bindings[0].Chord.Key);
        Assert.AreEqual(1, bindings[0].DesktopOrdinal);
        Assert.AreEqual(HotkeyKey.D0, bindings[^1].Chord.Key);
        Assert.AreEqual(10, bindings[^1].DesktopOrdinal);
        Assert.AreEqual("Ctrl + Alt + 1", bindings[0].Chord.Describe());
        Assert.AreEqual("Ctrl + Alt + 0", bindings[^1].Chord.Describe());
    }

    [TestMethod]
    public void WinAltProfile_ClaimsTheWindowsKeyAndWarnsAboutJumpLists()
    {
        DesktopSwitchShortcutSettings settings =
            new(true, DesktopSwitchShortcutProfile.WinAlt);

        Assert.AreEqual(
            HotkeyModifiers.Windows | HotkeyModifiers.Alt,
            settings.ActiveModifiers);

        string? warning =
            DesktopSwitchShortcuts.Warn(DesktopSwitchShortcutProfile.WinAlt);
        Assert.IsNotNull(warning, "The override profile has to state its cost.");
        Assert.Contains("Jump List", warning);

        // The recommended profile costs the user nothing, so it says nothing.
        Assert.IsNull(
            DesktopSwitchShortcuts.Warn(DesktopSwitchShortcutProfile.CtrlAlt));
        Assert.IsNull(
            DesktopSwitchShortcuts.Warn(DesktopSwitchShortcutProfile.Custom));
    }

    [TestMethod]
    public void CustomProfile_UsesTheChosenModifiersAndIgnoresThemOtherwise()
    {
        HotkeyModifiers custom =
            HotkeyModifiers.Control | HotkeyModifiers.Shift;

        DesktopSwitchShortcutSettings selected = new(
            true,
            DesktopSwitchShortcutProfile.Custom,
            custom);
        Assert.AreEqual(custom, selected.ActiveModifiers);

        // The same custom value is carried but not claimed while a built-in
        // profile is selected, so switching back does not lose it.
        DesktopSwitchShortcutSettings unselected = selected with
        {
            Profile = DesktopSwitchShortcutProfile.CtrlAlt,
        };
        Assert.AreEqual(
            DesktopSwitchShortcuts.CtrlAltModifiers,
            unselected.ActiveModifiers);
        Assert.AreEqual(custom, unselected.CustomModifiers);
    }

    [TestMethod]
    public void ACustomProfileWithNoModifier_IsRefusedBeforeAnythingIsClaimed()
    {
        DesktopSwitchShortcutSettings settings = new(
            true,
            DesktopSwitchShortcutProfile.Custom,
            HotkeyModifiers.None);

        ImmutableArray<ConfigurationValidationIssue> issues =
            DesktopSwitchShortcuts.Validate(settings);

        ConfigurationValidationIssue issue = issues.Single();
        Assert.AreEqual(
            ConfigurationValidationCode.DesktopSwitchShortcutMissingModifier,
            issue.Code);
        Assert.AreEqual(ConfigurationEntryKind.Hotkey, issue.EntryKind);
        Assert.AreEqual(
            "$.behavior.desktopSwitchShortcuts.customModifiers",
            issue.Path);
        Assert.IsFalse(
            DesktopSwitchShortcuts.IsRegistrable(settings),
            "Ten bare digits must never reach the registrar.");
    }

    [TestMethod]
    public void AHandEditedProfile_IsReportedRatherThanGuessedAt()
    {
        DesktopSwitchShortcutSettings settings =
            new(true, (DesktopSwitchShortcutProfile)99);

        ConfigurationValidationIssue issue =
            DesktopSwitchShortcuts.Validate(settings).Single();

        Assert.AreEqual(
            ConfigurationValidationCode.InvalidDesktopSwitchShortcutValue,
            issue.Code);
        Assert.IsFalse(DesktopSwitchShortcuts.IsRegistrable(settings));
    }

    [TestMethod]
    public void AnUnsupportedModifierBit_IsReported()
    {
        DesktopSwitchShortcutSettings settings = new(
            true,
            DesktopSwitchShortcutProfile.Custom,
            (HotkeyModifiers)64);

        ConfigurationValidationIssue issue =
            DesktopSwitchShortcuts.Validate(settings).Single();

        Assert.AreEqual(
            ConfigurationValidationCode.InvalidDesktopSwitchShortcutValue,
            issue.Code);
    }

    [TestMethod]
    public void AValidProfileThatIsSwitchedOff_IsValidButNotRegistrable()
    {
        DesktopSwitchShortcutSettings settings =
            new(false, DesktopSwitchShortcutProfile.CtrlAlt);

        Assert.IsEmpty(DesktopSwitchShortcuts.Validate(settings));
        Assert.IsFalse(DesktopSwitchShortcuts.IsRegistrable(settings));
    }

    [TestMethod]
    public void Summary_NamesTheWholeSetWithoutListingTenCombinations()
    {
        string summary = DesktopSwitchShortcuts.Summarize(
            DesktopSwitchShortcuts.CtrlAltModifiers);

        Assert.Contains("Ctrl + Alt + 1", summary);
        Assert.Contains("Ctrl + Alt + 9", summary);
        Assert.Contains("Ctrl + Alt + 0", summary);
        Assert.Contains("Desktop 10", summary);

        Assert.Contains(
            "No modifier",
            DesktopSwitchShortcuts.Summarize(HotkeyModifiers.None));
    }
}

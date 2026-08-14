using DesktopShift.App.Settings;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.App.Presentation.Tests.Settings;

/// <summary>
/// What the Settings page shows for a desktop-switching profile, and what it
/// saves back.
/// </summary>
[TestClass]
public sealed class DesktopSwitchShortcutEditorTests
{
    [TestMethod]
    public void Editor_RoundTripsTheSavedProfile()
    {
        DesktopSwitchShortcutSettings original = new(
            true,
            DesktopSwitchShortcutProfile.Custom,
            HotkeyModifiers.Control | HotkeyModifiers.Shift);

        DesktopSwitchShortcutEditor editor = new(original);

        Assert.IsTrue(editor.IsEnabled);
        Assert.IsTrue(editor.IsCustom);
        Assert.IsTrue(editor.Control);
        Assert.IsTrue(editor.Shift);
        Assert.IsFalse(editor.Alt);
        Assert.IsFalse(editor.Windows);
        Assert.AreEqual(original, editor.ToSettings());
    }

    [TestMethod]
    public void SwitchingAwayFromCustomAndBack_KeepsTheChosenModifiers()
    {
        DesktopSwitchShortcutEditor editor = new(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.Custom,
                HotkeyModifiers.Control | HotkeyModifiers.Shift |
                    HotkeyModifiers.Windows));

        editor.Profile = DesktopSwitchShortcutProfile.CtrlAlt;
        Assert.AreEqual(
            DesktopSwitchShortcuts.CtrlAltModifiers,
            editor.ActiveModifiers);
        Assert.IsFalse(
            editor.IsCustom,
            "The modifier boxes should go inert while a built-in profile is selected.");

        editor.Profile = DesktopSwitchShortcutProfile.Custom;
        Assert.AreEqual(
            HotkeyModifiers.Control | HotkeyModifiers.Shift |
                HotkeyModifiers.Windows,
            editor.ActiveModifiers);
    }

    [TestMethod]
    public void TheSummary_FollowsTheSelectedProfile()
    {
        DesktopSwitchShortcutEditor editor =
            new(DesktopSwitchShortcutSettings.Disabled);

        Assert.Contains("Ctrl + Alt + 1", editor.Summary);

        editor.Profile = DesktopSwitchShortcutProfile.Custom;
        editor.Control = true;
        editor.Alt = false;
        editor.Shift = true;
        editor.Windows = false;
        Assert.Contains("Ctrl + Shift + 1", editor.Summary);
        Assert.Contains("Desktop 10", editor.Summary);
    }

    [TestMethod]
    public void ACustomProfileWithNoModifier_SaysSoRatherThanShowingAnEmptyChord()
    {
        DesktopSwitchShortcutEditor editor = new(
            new DesktopSwitchShortcutSettings(
                true,
                DesktopSwitchShortcutProfile.Custom,
                HotkeyModifiers.None));

        Assert.Contains("No modifier", editor.Summary);
    }

    [TestMethod]
    public void BehaviorEditor_PreservesEverythingOutsideDesktopSwitching()
    {
        BehaviorSettings original = new(
            StartWithWindows: false,
            StartMinimized: true,
            CloseToTray: false,
            Theme: AppTheme.Dark,
            StartAssignmentPaused: true,
            NotifyOnAssignmentFailure: false,
            NotifyOnCompatibilityWarning: true,
            NameWindowsDesktops: false);

        BehaviorSettings updated =
            SettingsBehaviorEditor.WithDesktopSwitchShortcuts(
                original,
                new DesktopSwitchShortcutSettings(
                    true,
                    DesktopSwitchShortcutProfile.Custom,
                    HotkeyModifiers.Shift));

        Assert.IsTrue(updated.AreDesktopSwitchShortcutsEnabled);
        Assert.AreEqual(
            DesktopSwitchShortcutProfile.Custom,
            updated.DesktopSwitchProfile);
        Assert.AreEqual(
            HotkeyModifiers.Shift,
            updated.DesktopSwitchCustomModifiers);

        Assert.AreEqual(original.Theme, updated.Theme);
        Assert.AreEqual(
            original.NameWindowsDesktops,
            updated.NameWindowsDesktops);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            updated.StartAssignmentPaused);
    }

    [TestMethod]
    public void ADocumentWrittenBeforeDesktopSwitchingExisted_ReadsAsOffAndRecommended()
    {
        // Every new member trails the originals and carries the value such a
        // document should be read as, which is why the schema version did not
        // have to move.
        BehaviorSettings legacy = new(
            StartWithWindows: true,
            StartMinimized: true,
            CloseToTray: true);

        DesktopSwitchShortcutSettings settings =
            legacy.ToDesktopSwitchShortcutSettings();

        Assert.IsFalse(settings.IsEnabled);
        Assert.AreEqual(DesktopSwitchShortcutProfile.CtrlAlt, settings.Profile);
        Assert.IsEmpty(DesktopSwitchShortcuts.Validate(settings));
    }
}

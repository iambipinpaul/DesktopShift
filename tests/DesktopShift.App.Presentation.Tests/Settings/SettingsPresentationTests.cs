using DesktopShift.App.Settings;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.App.Presentation.Tests.Settings;

[TestClass]
public sealed class SettingsPresentationTests
{
    [TestMethod]
    public void SectionCatalog_CoversEveryAcceptanceAreaInDisplayOrder()
    {
        CollectionAssert.AreEqual(
            new[]
            {
                SettingsSection.Startup,
                SettingsSection.Assignment,
                SettingsSection.Desktops,
                SettingsSection.Compatibility,
                SettingsSection.Notifications,
                SettingsSection.Appearance,
                SettingsSection.Diagnostics,
                SettingsSection.Hotkeys,
                SettingsSection.DesktopSwitching,
            },
            SettingsSectionCatalog.Sections
                .Select(static section => section.Section)
                .ToArray());
        Assert.AreEqual(
            SettingsSectionCatalog.Sections.Length,
            SettingsSectionCatalog.Sections
                .Select(static section => section.Key)
                .Distinct(StringComparer.Ordinal)
                .Count());
    }

    [TestMethod]
    public void BehaviorEditor_PreservesFieldsOutsideTheEditedSection()
    {
        BehaviorSettings original = new(
            StartWithWindows: false,
            StartMinimized: true,
            CloseToTray: false,
            Theme: AppTheme.Dark,
            StartAssignmentPaused: true,
            NotifyOnAssignmentFailure: false,
            NotifyOnCompatibilityWarning: true,
            AreHotkeysEnabled: true,
            Hotkeys: HotkeyDefaults.Bindings);

        BehaviorSettings startup = SettingsBehaviorEditor.WithStartup(
            original,
            startWithWindows: true,
            startMinimized: false,
            closeToTray: true);
        Assert.AreEqual(original.Theme, startup.Theme);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            startup.StartAssignmentPaused);
        CollectionAssert.AreEqual(
            original.Hotkeys.ToArray(),
            startup.Hotkeys.ToArray());

        BehaviorSettings assignment =
            SettingsBehaviorEditor.WithAssignment(original, false);
        Assert.AreEqual(original.Theme, assignment.Theme);
        Assert.AreEqual(
            original.NotifyOnAssignmentFailure,
            assignment.NotifyOnAssignmentFailure);
        CollectionAssert.AreEqual(
            original.Hotkeys.ToArray(),
            assignment.Hotkeys.ToArray());

        BehaviorSettings notifications =
            SettingsBehaviorEditor.WithNotifications(original, true, false);
        Assert.AreEqual(original.Theme, notifications.Theme);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            notifications.StartAssignmentPaused);
        CollectionAssert.AreEqual(
            original.Hotkeys.ToArray(),
            notifications.Hotkeys.ToArray());

        BehaviorSettings appearance =
            SettingsBehaviorEditor.WithAppearance(original, AppTheme.Light);
        Assert.AreEqual(
            original.NotifyOnAssignmentFailure,
            appearance.NotifyOnAssignmentFailure);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            appearance.StartAssignmentPaused);
        CollectionAssert.AreEqual(
            original.Hotkeys.ToArray(),
            appearance.Hotkeys.ToArray());
    }

    [TestMethod]
    public void HotkeyEditor_RoundTripsEveryBindingMember()
    {
        HotkeyBinding source = new(
            HotkeyAction.ReassignForegroundWindow,
            HotkeyModifiers.Control |
                HotkeyModifiers.Shift |
                HotkeyModifiers.Windows,
            HotkeyKey.F9,
            IsEnabled: false);

        HotkeyBinding roundTrip = new HotkeyBindingEditor(source).ToBinding();

        Assert.AreEqual(source, roundTrip);
    }

    [TestMethod]
    public void HotkeyEditor_TakesAKeyChosenFromThePicker()
    {
        HotkeyBindingEditor editor = new(
            new HotkeyBinding(
                HotkeyAction.TogglePause,
                HotkeyModifiers.Control,
                HotkeyKey.F9,
                IsEnabled: true));

        editor.SelectKey(HotkeyKey.F4);

        Assert.AreEqual(HotkeyKey.F4, editor.Key);
    }

    /// <summary>
    /// Rebuilding the shortcut list replaces each picker's item source, and a
    /// ComboBox drops its selection to null when that happens. Treating it as a
    /// choice would wipe the row's key on every refresh — and, when it was
    /// written straight back through a TwoWay binding, unboxing that null into
    /// <see cref="HotkeyKey"/> took the whole application down.
    /// </summary>
    [TestMethod]
    public void HotkeyEditor_KeepsItsKeyWhenThePickerClearsItself()
    {
        HotkeyBindingEditor editor = new(
            new HotkeyBinding(
                HotkeyAction.TogglePause,
                HotkeyModifiers.Control,
                HotkeyKey.F9,
                IsEnabled: true));

        editor.SelectKey(null);
        editor.SelectKey("F4");

        Assert.AreEqual(HotkeyKey.F9, editor.Key);
    }
}

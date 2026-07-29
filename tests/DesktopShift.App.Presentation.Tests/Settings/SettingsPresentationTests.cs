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
}

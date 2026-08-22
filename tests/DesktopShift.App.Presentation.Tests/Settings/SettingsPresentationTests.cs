using DesktopShift.App.Settings;
using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;

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
                SettingsSection.Tiling,
                SettingsSection.Desktops,
                SettingsSection.Compatibility,
                SettingsSection.Notifications,
                SettingsSection.Appearance,
                SettingsSection.Diagnostics,
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
            RecordLocalActivity: true);

        BehaviorSettings startup = SettingsBehaviorEditor.WithStartup(
            original,
            startWithWindows: true,
            startMinimized: false,
            closeToTray: true);
        Assert.AreEqual(original.Theme, startup.Theme);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            startup.StartAssignmentPaused);
        Assert.AreEqual(
            original.RecordLocalActivity,
            startup.RecordLocalActivity);

        BehaviorSettings assignment =
            SettingsBehaviorEditor.WithAssignment(original, false);
        Assert.AreEqual(original.Theme, assignment.Theme);
        Assert.AreEqual(
            original.NotifyOnAssignmentFailure,
            assignment.NotifyOnAssignmentFailure);

        BehaviorSettings notifications =
            SettingsBehaviorEditor.WithNotifications(original, true, false);
        Assert.AreEqual(original.Theme, notifications.Theme);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            notifications.StartAssignmentPaused);

        BehaviorSettings diagnostics =
            SettingsBehaviorEditor.WithDiagnostics(original, false);
        Assert.IsFalse(diagnostics.RecordLocalActivity);
        Assert.AreEqual(original.Theme, diagnostics.Theme);
        Assert.AreEqual(
            original.NotifyOnAssignmentFailure,
            diagnostics.NotifyOnAssignmentFailure);

        BehaviorSettings appearance =
            SettingsBehaviorEditor.WithAppearance(
                original,
                AppTheme.Light,
                AppAccent.AditiKraftBlue);
        Assert.AreEqual(AppTheme.Light, appearance.Theme);
        Assert.AreEqual(AppAccent.AditiKraftBlue, appearance.Accent);
        Assert.AreEqual(
            original.NotifyOnAssignmentFailure,
            appearance.NotifyOnAssignmentFailure);
        Assert.AreEqual(
            original.StartAssignmentPaused,
            appearance.StartAssignmentPaused);
    }
}

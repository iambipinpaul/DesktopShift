using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class BehaviorSettingsPersistenceTests
{
    private const string LegacyVersionOneJson = """
        {
          "schemaVersion": 1,
          "managedDesktops": [
            {
              "semanticKey": "work",
              "displayName": "Work",
              "preferredOrder": 1,
              "recreateWhenMissing": true
            }
          ],
          "applicationRules": [],
          "behavior": {
            "startWithWindows": false,
            "startMinimized": true,
            "closeToTray": false,
            "areHotkeysEnabled": true,
            "hotkeys": [
              {
                "action": "openDesktopShift",
                "modifiers": "control",
                "key": "d",
                "isEnabled": true
              }
            ]
          }
        }
        """;

    [TestMethod]
    public async Task EveryBehaviorSettingSurvivesAReload()
    {
        using TestConfigurationDirectory storage = new();
        BehaviorSettings expected = new(
            StartWithWindows: false,
            StartMinimized: false,
            CloseToTray: false,
            Theme: AppTheme.Dark,
            Accent: AppAccent.AditiKraftBlue,
            StartAssignmentPaused: true,
            NotifyOnAssignmentFailure: false,
            NotifyOnCompatibilityWarning: false);

        await using (ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            ConfigurationSaveResult save = await service.SaveCandidateAsync(
                ConfigurationDefaults.Create() with { Behavior = expected });
            Assert.IsTrue(save.Accepted);
        }

        await using ServiceProvider reloaded =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        ConfigurationState state = await reloaded
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(state.Issues);
        BehaviorSettings actual = state.Active!.Behavior;
        Assert.AreEqual(expected.StartWithWindows, actual.StartWithWindows);
        Assert.AreEqual(expected.StartMinimized, actual.StartMinimized);
        Assert.AreEqual(expected.CloseToTray, actual.CloseToTray);
        Assert.AreEqual(expected.Theme, actual.Theme);
        Assert.AreEqual(expected.Accent, actual.Accent);
        Assert.AreEqual(expected.StartAssignmentPaused, actual.StartAssignmentPaused);
        Assert.AreEqual(
            expected.NotifyOnAssignmentFailure,
            actual.NotifyOnAssignmentFailure);
        Assert.AreEqual(
            expected.NotifyOnCompatibilityWarning,
            actual.NotifyOnCompatibilityWarning);
    }

    [TestMethod]
    public async Task LegacyVersionOneBehaviorIgnoresRetiredGlobalShortcuts()
    {
        using TestConfigurationDirectory storage = new();
        Directory.CreateDirectory(storage.DirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"),
            LegacyVersionOneJson);

        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        ConfigurationState state = await provider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(state.Issues);
        BehaviorSettings behavior = state.Active!.Behavior;
        Assert.AreEqual(AppTheme.System, behavior.Theme);
        Assert.AreEqual(AppAccent.System, behavior.Accent);
        Assert.IsFalse(behavior.StartAssignmentPaused);
        Assert.IsTrue(behavior.NotifyOnAssignmentFailure);
        Assert.IsTrue(behavior.NotifyOnCompatibilityWarning);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        Assert.IsTrue((await service.SaveCandidateAsync(state.Active)).Accepted);
        string rewritten = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"));
        Assert.DoesNotContain("areHotkeysEnabled", rewritten, StringComparison.Ordinal);
        Assert.DoesNotContain("\"hotkeys\"", rewritten, StringComparison.Ordinal);
    }
}

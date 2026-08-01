using DesktopShift.Core.Appearance;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;
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
            "closeToTray": false
          }
        }
        """;

    [TestMethod]
    public async Task EveryBehaviorSettingAndHotkeySurvivesAReload()
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
            NotifyOnCompatibilityWarning: false,
            AreHotkeysEnabled: true,
            Hotkeys:
            [
                new(
                    HotkeyAction.ReassignAllWindows,
                    HotkeyModifiers.Control | HotkeyModifiers.Shift,
                    HotkeyKey.F9,
                    true),
                new(
                    HotkeyAction.ReassignForegroundWindow,
                    HotkeyModifiers.Alt,
                    HotkeyKey.F8,
                    false),
                new(
                    HotkeyAction.TogglePause,
                    HotkeyModifiers.Control,
                    HotkeyKey.P,
                    true),
                new(
                    HotkeyAction.OpenDesktopShift,
                    HotkeyModifiers.Control | HotkeyModifiers.Alt,
                    HotkeyKey.D,
                    true),
            ]);

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
        Assert.AreEqual(expected.AreHotkeysEnabled, actual.AreHotkeysEnabled);
        CollectionAssert.AreEqual(
            expected.Hotkeys.ToArray(),
            actual.Hotkeys.ToArray());
    }

    [TestMethod]
    public async Task LegacyVersionOneBehaviorUsesBackwardCompatibleDefaults()
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
        Assert.IsFalse(behavior.AreHotkeysEnabled);
        Assert.IsEmpty(behavior.Hotkeys);
        CollectionAssert.AreEqual(
            HotkeyDefaults.Actions.ToArray(),
            behavior.ToHotkeySettings().Bindings
                .Select(static binding => binding.Action)
                .ToArray());
    }

    [TestMethod]
    public async Task InvalidHotkeysRemainInTheCandidateWhileTheLastValidSnapshotRuns()
    {
        using TestConfigurationDirectory storage = new();
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();

        ConfigurationDocument valid = ConfigurationDefaults.Create() with
        {
            Behavior = ConfigurationDefaults.Create().Behavior with
            {
                Theme = AppTheme.Dark,
            },
        };
        Assert.IsTrue((await service.SaveCandidateAsync(valid)).Accepted);

        HotkeyBinding first = HotkeyDefaults.Bindings[0];
        HotkeyBinding conflict = HotkeyDefaults.Bindings[1] with
        {
            Modifiers = first.Modifiers,
            Key = first.Key,
        };
        ConfigurationDocument invalid = valid with
        {
            Behavior = valid.Behavior with
            {
                AreHotkeysEnabled = true,
                Hotkeys = [first, conflict, .. HotkeyDefaults.Bindings.Skip(2)],
            },
        };

        ConfigurationSaveResult result =
            await service.SaveCandidateAsync(invalid);

        Assert.IsFalse(result.Accepted);
        Assert.AreEqual(invalid, result.State.Candidate);
        Assert.AreEqual(valid, result.State.Active);
        Assert.IsTrue(
            result.State.Issues.Any(
                static issue =>
                    issue.Code == ConfigurationValidationCode.HotkeyConflict));
    }
}

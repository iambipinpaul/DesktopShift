using System.Collections.Immutable;
using DesktopShift.App.Presentation.Tests.Tray;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hosting;

namespace DesktopShift.App.Presentation.Tests.ViewModels;

[TestClass]
public sealed class BehaviorSettingsCommandTests
{
    [TestMethod]
    public async Task LoadAsync_ReportsTheRegistrationRatherThanTheStoredRequest()
    {
        // The document asks to start with Windows; Windows says otherwise.
        StubConfigurationService configuration = CreateConfiguration(
            new BehaviorSettings(
                StartWithWindows: true,
                StartMinimized: true,
                CloseToTray: true));
        FakeStartupRegistration startup = new(StartupRegistrationState.DisabledByUser);
        BehaviorSettingsCommand command = new(configuration, startup);

        BehaviorSettingsPresentation presentation = await command.LoadAsync();

        Assert.IsFalse(presentation.StartWithWindows);
        Assert.IsFalse(presentation.IsStartupToggleEnabled);
        Assert.Contains("Windows Settings", presentation.StartupStateDescription);
        Assert.IsTrue(presentation.StartMinimized);
        Assert.IsTrue(presentation.CloseToTray);
    }

    [TestMethod]
    public async Task ApplyAsync_SavesTheDocumentAndRegistersStartup()
    {
        StubConfigurationService configuration = CreateConfiguration(
            new BehaviorSettings(
                StartWithWindows: false,
                StartMinimized: false,
                CloseToTray: false));
        FakeStartupRegistration startup = new();
        BehaviorSettingsCommand command = new(configuration, startup);

        BehaviorSettingsPresentation presentation = await command.ApplyAsync(
            new BehaviorSettings(
                StartWithWindows: true,
                StartMinimized: true,
                CloseToTray: true));

        Assert.AreEqual(1, configuration.SaveCount);
        Assert.AreEqual(1, startup.SetCount);
        Assert.IsTrue(presentation.StartWithWindows);
        Assert.IsTrue(presentation.StartMinimized);
        Assert.IsTrue(presentation.CloseToTray);
        Assert.IsTrue(presentation.IsStartupToggleEnabled);

        BehaviorSettings saved =
            BehaviorSettingsCommand.ResolveBehavior(configuration.CurrentState);
        Assert.IsTrue(saved.StartMinimized);
        Assert.IsTrue(saved.CloseToTray);
    }

    [TestMethod]
    public async Task ApplyAsync_ReportsAStartupRequestWindowsRefuses()
    {
        StubConfigurationService configuration = CreateConfiguration(
            new BehaviorSettings(false, false, true));
        FakeStartupRegistration startup = new(StartupRegistrationState.DisabledByPolicy);
        BehaviorSettingsCommand command = new(configuration, startup);

        BehaviorSettingsPresentation presentation = await command.ApplyAsync(
            new BehaviorSettings(
                StartWithWindows: true,
                StartMinimized: false,
                CloseToTray: true));

        Assert.IsFalse(presentation.StartWithWindows);
        Assert.IsFalse(presentation.IsStartupToggleEnabled);
        Assert.Contains("policy", presentation.StartupStateDescription);
    }

    [TestMethod]
    public void ResolveBehavior_PrefersTheAcceptedDocument()
    {
        ConfigurationDocument accepted = ConfigurationDefaults.Create() with
        {
            Behavior = new BehaviorSettings(false, false, false),
        };
        ConfigurationDocument candidate = ConfigurationDefaults.Create() with
        {
            Behavior = new BehaviorSettings(true, true, true),
        };

        BehaviorSettings resolved = BehaviorSettingsCommand.ResolveBehavior(
            new ConfigurationState(
                candidate,
                accepted,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch));

        Assert.IsFalse(resolved.CloseToTray);
    }

    [TestMethod]
    public void ResolveBehavior_FallsBackToTheCandidateOnFirstRun()
    {
        ConfigurationDocument candidate = ConfigurationDefaults.Create();

        BehaviorSettings resolved = BehaviorSettingsCommand.ResolveBehavior(
            new ConfigurationState(
                candidate,
                Active: null,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch));

        Assert.AreEqual(candidate.Behavior, resolved);
    }

    private static StubConfigurationService CreateConfiguration(BehaviorSettings behavior)
    {
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            Behavior = behavior,
        };

        return new StubConfigurationService(
            new ConfigurationState(
                document,
                document,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch));
    }
}

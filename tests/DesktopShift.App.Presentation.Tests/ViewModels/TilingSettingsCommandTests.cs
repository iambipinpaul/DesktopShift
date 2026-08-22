using System.Collections.Immutable;
using DesktopShift.App.Presentation.Tests.Tray;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Configuration;

namespace DesktopShift.App.Presentation.Tests.ViewModels;

[TestClass]
public sealed class TilingSettingsCommandTests
{
    [TestMethod]
    public void SetManagedDesktopEnabled_AddsAndRemovesStableKey()
    {
        TilingSettings source = new(
            IsEnabled: true,
            DisabledManagedDesktopKeys: ["remote"]);

        TilingSettings disabled = TilingSettingsCommand
            .SetManagedDesktopEnabled(source, "IDE-DEVELOPMENT", isEnabled: false);
        CollectionAssert.AreEqual(
            new[] { "IDE-DEVELOPMENT", "remote" },
            disabled.DisabledManagedDesktopKeys.ToArray());

        TilingSettings enabled = TilingSettingsCommand
            .SetManagedDesktopEnabled(disabled, "Remote", isEnabled: true);
        CollectionAssert.AreEqual(
            new[] { "IDE-DEVELOPMENT" },
            enabled.DisabledManagedDesktopKeys.ToArray());
    }

    [TestMethod]
    public async Task LoadAsync_ReturnsSavedTilingSettings()
    {
        TilingSettings expected = new(
            IsEnabled: true,
            OuterGap: 7,
            InnerGap: 12,
            MinimumTileWidth: 360,
            MinimumTileHeight: 260);
        StubConfigurationService configuration = CreateConfiguration(expected);
        TilingSettingsCommand command = new(configuration);

        TilingSettingsPresentation presentation = await command.LoadAsync();

        Assert.AreEqual(expected, presentation.Settings);
        Assert.AreEqual(expected, presentation.ActiveSettings);
        Assert.IsTrue(presentation.Accepted);
    }

    [TestMethod]
    public async Task ApplyAsync_ChangesOnlyTheTilingSection()
    {
        StubConfigurationService configuration = CreateConfiguration(
            TilingSettings.Disabled);
        ConfigurationDocument before = configuration.CurrentState.Candidate;
        TilingSettings requested = new(
            IsEnabled: true,
            OuterGap: 8,
            InnerGap: 14,
            MinimumTileWidth: 400,
            MinimumTileHeight: 280);
        TilingSettingsCommand command = new(configuration);

        TilingSettingsPresentation presentation =
            await command.ApplyAsync(requested);

        Assert.IsTrue(presentation.Accepted);
        Assert.AreEqual(requested, presentation.ActiveSettings);
        CollectionAssert.AreEqual(
            before.ApplicationRules.ToArray(),
            configuration.CurrentState.Candidate.ApplicationRules.ToArray());
        Assert.AreEqual(
            before.Behavior,
            configuration.CurrentState.Candidate.Behavior);
    }

    [TestMethod]
    public void ResolveActive_PrefersTheAcceptedDocument()
    {
        ConfigurationDocument accepted = ConfigurationDefaults.Create() with
        {
            Tiling = new TilingSettings(IsEnabled: false),
        };
        ConfigurationDocument candidate = accepted with
        {
            Tiling = new TilingSettings(IsEnabled: true),
        };

        TilingSettings resolved = TilingSettingsCommand.ResolveActive(
            new ConfigurationState(
                candidate,
                accepted,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch));

        Assert.IsFalse(resolved.IsEnabled);
    }

    private static StubConfigurationService CreateConfiguration(
        TilingSettings settings)
    {
        ConfigurationDocument document = ConfigurationDefaults.Create() with
        {
            Tiling = settings,
        };
        return new StubConfigurationService(
            new ConfigurationState(
                document,
                document,
                ImmutableArray<ConfigurationValidationIssue>.Empty,
                DateTimeOffset.UnixEpoch));
    }
}

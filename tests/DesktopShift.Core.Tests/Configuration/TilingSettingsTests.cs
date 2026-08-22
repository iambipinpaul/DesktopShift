using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class TilingSettingsTests
{
    private const string LegacyDocumentWithoutTilingJson = """
        {
          "schemaVersion": 3,
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

    private const string NullTilingSectionJson = """
        {
          "schemaVersion": 3,
          "managedDesktops": [],
          "applicationRules": [],
          "behavior": {
            "startWithWindows": false,
            "startMinimized": true,
            "closeToTray": false
          },
          "tiling": null
        }
        """;

    [TestMethod]
    public async Task EveryTilingSettingSurvivesAReload()
    {
        using TestConfigurationDirectory storage = new();
        ImmutableArray<TilingIdentityRule> floatRules =
        [
            new TilingIdentityRule(
                Id: "float-terminal",
                DisplayName: "Terminal",
                IsEnabled: true,
                ProcessNames: ["WindowsTerminal.exe"],
                WindowClasses: ["CASCADIA_HOSTING_WINDOW_CLASS"]),
        ];
        ImmutableArray<TilingIdentityRule> ignoreRules =
        [
            new TilingIdentityRule(
                Id: "ignore-shell",
                DisplayName: "Shell surfaces",
                IsEnabled: true,
                PackageFamilyNames: ["Microsoft.WindowsShell_8wekyb3d8bbwe"]),
        ];
        TilingSettings expected = new(
            IsEnabled: true,
            Layout: TilingLayout.Bsp,
            OuterGap: 6,
            InnerGap: 12,
            InsertMode: TilingInsertMode.SplitFocusedOrLargest,
            MinimumTileWidth: 360,
            MinimumTileHeight: 280,
            ApplyToAllVirtualDesktops: true,
            FloatRules: floatRules,
            IgnoreRules: ignoreRules,
            DisabledManagedDesktopKeys: ["remote"]);

        await using (ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage))
        {
            IConfigurationService service =
                provider.GetRequiredService<IConfigurationService>();
            ConfigurationSaveResult save = await service.SaveCandidateAsync(
                ConfigurationDefaults.Create() with { Tiling = expected });
            Assert.IsTrue(save.Accepted);
        }

        await using ServiceProvider reloaded =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        ConfigurationState state = await reloaded
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        Assert.IsEmpty(state.Issues);
        TilingSettings actual = state.Active!.Tiling;
        Assert.AreEqual(expected.IsEnabled, actual.IsEnabled);
        Assert.AreEqual(expected.Layout, actual.Layout);
        Assert.AreEqual(expected.OuterGap, actual.OuterGap);
        Assert.AreEqual(expected.InnerGap, actual.InnerGap);
        Assert.AreEqual(expected.InsertMode, actual.InsertMode);
        Assert.AreEqual(expected.MinimumTileWidth, actual.MinimumTileWidth);
        Assert.AreEqual(expected.MinimumTileHeight, actual.MinimumTileHeight);
        Assert.IsTrue(actual.ApplyToAllVirtualDesktops);
        Assert.HasCount(1, actual.FloatRules);
        Assert.HasCount(1, actual.IgnoreRules);
        Assert.HasCount(1, actual.DisabledManagedDesktopKeys);
        Assert.AreEqual("remote", actual.DisabledManagedDesktopKeys[0]);
        Assert.IsFalse(actual.IsEnabledForManagedDesktop("REMOTE"));
        Assert.IsTrue(actual.IsEnabledForManagedDesktop("work"));

        TilingIdentityRule floatRule = actual.FloatRules[0];
        Assert.AreEqual("float-terminal", floatRule.Id);
        Assert.AreEqual("Terminal", floatRule.DisplayName);
        Assert.IsTrue(floatRule.IsEnabled);
        Assert.AreEqual("WindowsTerminal.exe", floatRule.ProcessNames[0]);
        Assert.AreEqual(
            "CASCADIA_HOSTING_WINDOW_CLASS",
            floatRule.WindowClasses[0]);

        TilingIdentityRule ignoreRule = actual.IgnoreRules[0];
        Assert.AreEqual("ignore-shell", ignoreRule.Id);
        Assert.AreEqual(
            "Microsoft.WindowsShell_8wekyb3d8bbwe",
            ignoreRule.PackageFamilyNames[0]);
    }

    [TestMethod]
    public async Task DocumentWithoutTilingSectionReadsAsDisabledAndResavesClean()
    {
        using TestConfigurationDirectory storage = new();
        Directory.CreateDirectory(storage.DirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"),
            LegacyDocumentWithoutTilingJson);

        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationState state = await service.LoadAsync();

        Assert.IsEmpty(state.Issues);
        Assert.IsFalse(state.Active!.Tiling.IsEnabled);
        Assert.IsEmpty(state.Active.Tiling.FloatRules);
        Assert.IsEmpty(state.Active.Tiling.IgnoreRules);

        // A document that predates tiling must survive a rewrite without the
        // new section turning into a validation problem of its own.
        ConfigurationSaveResult save = await service.SaveCandidateAsync(
            state.Active);
        Assert.IsTrue(save.Accepted);
        string rewritten = await File.ReadAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"));
        Assert.Contains("\"tiling\"", rewritten, StringComparison.Ordinal);
    }

    [TestMethod]
    public async Task ApplyToAllVirtualDesktopsFalseIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(IsEnabled: true, ApplyToAllVirtualDesktops: false),
            ConfigurationValidationCode.InvalidTilingValue,
            "$.tiling.applyToAllVirtualDesktops");
    }

    [TestMethod]
    public async Task InnerGapAboveTheMaximumIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(InnerGap: TilingSettings.MaximumGap + 1),
            ConfigurationValidationCode.InvalidTilingValue,
            "$.tiling.innerGap");
    }

    [TestMethod]
    public async Task NegativeOuterGapIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(OuterGap: -1),
            ConfigurationValidationCode.InvalidTilingValue,
            "$.tiling.outerGap");
    }

    [TestMethod]
    public async Task MinimumTileWidthBelowTheFloorIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(
                MinimumTileWidth: TilingSettings.AbsoluteMinimumTileDimension - 1),
            ConfigurationValidationCode.InvalidTilingValue,
            "$.tiling.minimumTileWidth");
    }

    [TestMethod]
    public async Task MinimumTileHeightAboveTheCeilingIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(
                MinimumTileHeight: TilingSettings.MaximumTileDimension + 1),
            ConfigurationValidationCode.InvalidTilingValue,
            "$.tiling.minimumTileHeight");
    }

    [TestMethod]
    public async Task RuleIdDuplicatedAcrossFloatAndIgnoreRulesIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(
                FloatRules:
                [
                    MakeRule("shared-id"),
                ],
                IgnoreRules:
                [
                    MakeRule("shared-id"),
                ]),
            ConfigurationValidationCode.DuplicateRuleId,
            "$.tiling.ignoreRules[0].id");
    }

    [TestMethod]
    public async Task TilingRuleWithoutIdentityIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(
                FloatRules:
                [
                    new TilingIdentityRule(
                        Id: "empty",
                        DisplayName: "Empty",
                        IsEnabled: true,
                        ProcessNames: []),
                ]),
            ConfigurationValidationCode.MissingApplicationIdentity,
            "$.tiling.floatRules[0]");
    }

    [TestMethod]
    public async Task TilingRuleWithMalformedProcessNameIsRejected()
    {
        await AssertSingleTilingIssue(
            new TilingSettings(
                IgnoreRules:
                [
                    MakeRule("bad-shape", processNames: ["tools\\notepad.exe"]),
                ]),
            ConfigurationValidationCode.InvalidIdentityPattern,
            "$.tiling.ignoreRules[0].processNames");
    }

    [TestMethod]
    public async Task NullTilingSectionReadsAsDisabled()
    {
        using TestConfigurationDirectory storage = new();
        Directory.CreateDirectory(storage.DirectoryPath);
        await File.WriteAllTextAsync(
            Path.Combine(storage.DirectoryPath, "configuration.json"),
            NullTilingSectionJson);

        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        ConfigurationState state = await provider
            .GetRequiredService<IConfigurationService>()
            .LoadAsync();

        // An explicit null carries no more intent than an absent section, so
        // both read as the disabled defaults rather than as a validation
        // problem.
        Assert.IsEmpty(state.Issues);
        Assert.IsFalse(state.Active!.Tiling.IsEnabled);
    }

    private static TilingIdentityRule MakeRule(
        string id,
        ImmutableArray<string>? processNames = null)
    {
        return new TilingIdentityRule(
            Id: id,
            DisplayName: id,
            IsEnabled: true,
            ProcessNames: processNames ?? ["app.exe"]);
    }

    private static async Task AssertSingleTilingIssue(
        TilingSettings tiling,
        ConfigurationValidationCode expectedCode,
        string expectedPath)
    {
        using TestConfigurationDirectory storage = new();
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService service =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationSaveResult result = await service.SaveCandidateAsync(
            ConfigurationDefaults.Create() with { Tiling = tiling });

        Assert.IsFalse(result.Accepted);
        ConfigurationValidationIssue? issue = result.State.Issues.FirstOrDefault(
            candidate =>
                candidate.Code == expectedCode && candidate.Path == expectedPath);
        Assert.IsNotNull(issue, $"Expected {expectedCode} at {expectedPath}.");
    }
}

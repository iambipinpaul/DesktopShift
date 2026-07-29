using System.Text;
using System.Text.Json.Nodes;
using DesktopShift.Core.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class ConfigurationImportTests
{
    [TestMethod]
    public async Task Import_AcceptsBothAnExportEnvelopeAndARawConfiguration()
    {
        using TestConfigurationDirectory sourceStorage = new();
        await using ServiceProvider sourceProvider =
            Issue17ConfigurationTestSupport.CreateProvider(sourceStorage);
        IConfigurationService sourceConfiguration =
            sourceProvider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument document = ConfigurationDefaults.Create();
        Assert.IsTrue(
            (await sourceConfiguration.SaveCandidateAsync(document)).Accepted);

        await using MemoryStream envelope = new();
        await sourceProvider.GetRequiredService<IConfigurationExchangeService>()
            .ExportAsync(envelope);

        using TestConfigurationDirectory envelopeStorage = new();
        await using ServiceProvider envelopeProvider =
            Issue17ConfigurationTestSupport.CreateProvider(envelopeStorage);
        envelope.Position = 0;
        ConfigurationImportResult envelopeResult = await envelopeProvider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(envelope);

        Assert.IsTrue(envelopeResult.Accepted);
        AssertDocumentsEqual(document, envelopeResult.State!.Active!);

        using TestConfigurationDirectory rawStorage = new();
        await using ServiceProvider rawProvider =
            Issue17ConfigurationTestSupport.CreateProvider(rawStorage);
        await using MemoryStream raw = Issue17ConfigurationTestSupport.Stream(
            Issue17ConfigurationTestSupport.Serialize(document));
        ConfigurationImportResult rawResult = await rawProvider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(raw);

        Assert.IsTrue(rawResult.Accepted);
        AssertDocumentsEqual(document, rawResult.State!.Active!);
    }

    [TestMethod]
    public async Task Import_InvalidCandidateRetainsEveryEntryAndKeepsLastValidActive()
    {
        using TestConfigurationDirectory storage = new();
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService configuration =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument active = ConfigurationDefaults.Create();
        Assert.IsTrue((await configuration.SaveCandidateAsync(active)).Accepted);

        ConfigurationDocument invalid = active with
        {
            ManagedDesktops =
            [
                active.ManagedDesktops[0],
                active.ManagedDesktops[0],
                .. active.ManagedDesktops.Skip(2),
            ],
            ApplicationRules =
            [
                .. active.ApplicationRules,
                active.ApplicationRules[0] with
                {
                    Id = "broken-import-rule",
                    TargetDesktopKey = "missing",
                },
            ],
        };
        await using MemoryStream source = Issue17ConfigurationTestSupport.Stream(
            Issue17ConfigurationTestSupport.Serialize(invalid));

        ConfigurationImportResult result = await provider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(source);

        Assert.IsFalse(result.Accepted);
        Assert.IsNotNull(result.State);
        Assert.AreEqual(invalid.ManagedDesktops.Length, result.State.Candidate.ManagedDesktops.Length);
        Assert.AreEqual(invalid.ApplicationRules.Length, result.State.Candidate.ApplicationRules.Length);
        Assert.AreEqual(active, result.State.Active);
        Assert.IsTrue(
            result.Issues.Any(
                static issue =>
                    issue.Code ==
                    ConfigurationValidationCode.DuplicateDesktopSemanticKey));
        Assert.IsTrue(
            result.Issues.Any(
                static issue =>
                    issue.Code ==
                    ConfigurationValidationCode.UnknownDesktopReference));
    }

    [TestMethod]
    [DataRow("{")]
    [DataRow("[]")]
    [DataRow("""{"application":"DesktopShift"}""")]
    [DataRow("""{"configuration":"not-an-object"}""")]
    public async Task Import_UnreadableInputDoesNotChangeTheCurrentConfiguration(
        string input)
    {
        using TestConfigurationDirectory storage = new();
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(storage);
        IConfigurationService configuration =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument active = ConfigurationDefaults.Create();
        Assert.IsTrue((await configuration.SaveCandidateAsync(active)).Accepted);
        ConfigurationState before = configuration.CurrentState;

        await using MemoryStream source =
            Issue17ConfigurationTestSupport.Stream(input);
        ConfigurationImportResult result = await provider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(source);

        Assert.IsFalse(result.Accepted);
        Assert.IsNull(result.State);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual(
            ConfigurationValidationCode.ImportDocumentUnreadable,
            result.Issues[0].Code);
        Assert.AreEqual(before, configuration.CurrentState);
    }

    [TestMethod]
    public async Task Import_MigratesBeforeValidationAndReportsEveryAppliedStep()
    {
        using TestConfigurationDirectory storage = new();
        ConfigurationSchema schema = new(
            3,
            [
                new StubConfigurationMigration(
                    2,
                    3,
                    "finish",
                    static document => document),
                new StubConfigurationMigration(
                    1,
                    2,
                    "prepare",
                    static document => document),
            ]);
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(
                storage,
                schema: schema);
        ConfigurationDocument sourceDocument = ConfigurationDefaults.Create();
        await using MemoryStream source = Issue17ConfigurationTestSupport.Stream(
            Issue17ConfigurationTestSupport.Serialize(sourceDocument));

        ConfigurationImportResult result = await provider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(source);

        Assert.IsTrue(result.Accepted);
        Assert.AreEqual(1, result.SourceSchemaVersion);
        CollectionAssert.AreEqual(
            new[] { 1, 2 },
            result.AppliedMigrations
                .Select(static migration => migration.FromVersion)
                .ToArray());
        Assert.AreEqual(3, result.State!.Active!.SchemaVersion);
    }

    [TestMethod]
    public async Task Import_AFailedMigrationNeverOffersOrReplacesAConfiguration()
    {
        using TestConfigurationDirectory storage = new();
        ConfigurationSchema schema = new(2, []);
        await using ServiceProvider provider =
            Issue17ConfigurationTestSupport.CreateProvider(
                storage,
                schema: schema);
        IConfigurationService configuration =
            provider.GetRequiredService<IConfigurationService>();
        ConfigurationState before = configuration.CurrentState;
        await using MemoryStream source = Issue17ConfigurationTestSupport.Stream(
            Issue17ConfigurationTestSupport.Serialize(ConfigurationDefaults.Create()));

        ConfigurationImportResult result = await provider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(source);

        Assert.IsFalse(result.Accepted);
        Assert.IsNull(result.State);
        Assert.AreEqual(
            ConfigurationValidationCode.MigrationUnavailable,
            result.Issues.Single().Code);
        Assert.AreEqual(before, configuration.CurrentState);
    }

    [TestMethod]
    public async Task ExportThenImport_RehomesUserProfilePaths()
    {
        using TestConfigurationDirectory sourceStorage = new();
        await using ServiceProvider sourceProvider =
            Issue17ConfigurationTestSupport.CreateProvider(
                sourceStorage,
                @"C:\Users\Alice");
        IConfigurationService sourceConfiguration =
            sourceProvider.GetRequiredService<IConfigurationService>();
        ConfigurationDocument defaults = ConfigurationDefaults.Create();
        ApplicationRule vscode = defaults.ApplicationRules.Single(
            static rule => rule.Id == "vscode");
        ConfigurationDocument sourceDocument = defaults with
        {
            ApplicationRules = defaults.ApplicationRules.Replace(
                vscode,
                vscode with
                {
                    ExecutablePaths = [@"C:\Users\Alice\Apps\Code.exe"],
                }),
        };
        Assert.IsTrue(
            (await sourceConfiguration.SaveCandidateAsync(sourceDocument)).Accepted);
        await using MemoryStream portable = new();
        await sourceProvider.GetRequiredService<IConfigurationExchangeService>()
            .ExportAsync(portable);
        Assert.Contains(
            ConfigurationPortability.UserProfileToken,
            Encoding.UTF8.GetString(portable.ToArray()));

        using TestConfigurationDirectory targetStorage = new();
        await using ServiceProvider targetProvider =
            Issue17ConfigurationTestSupport.CreateProvider(
                targetStorage,
                @"D:\Profiles\Bob");
        portable.Position = 0;
        ConfigurationImportResult result = await targetProvider
            .GetRequiredService<IConfigurationExchangeService>()
            .ImportAsync(portable);

        Assert.IsTrue(result.Accepted);
        ApplicationRule imported = result.State!.Active!.ApplicationRules.Single(
            static rule => rule.Id == "vscode");
        CollectionAssert.AreEqual(
            new[] { @"D:\Profiles\Bob\Apps\Code.exe" },
            imported.ExecutablePaths.ToArray());
    }

    private static void AssertDocumentsEqual(
        ConfigurationDocument expected,
        ConfigurationDocument actual) =>
        Assert.AreEqual(
            Issue17ConfigurationTestSupport.Serialize(expected),
            Issue17ConfigurationTestSupport.Serialize(actual));
}

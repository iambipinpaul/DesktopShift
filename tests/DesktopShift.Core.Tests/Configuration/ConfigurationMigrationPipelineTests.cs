using System.Collections.Immutable;
using System.Text.Json.Nodes;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class ConfigurationMigrationPipelineTests
{
    [TestMethod]
    public void Migrate_ChainsByVersionAndRecordsTheActualOrder()
    {
        List<int> executionOrder = [];
        ConfigurationSchema schema = new(
            3,
            [
                Migration(2, 3, "second", document =>
                {
                    executionOrder.Add(2);
                    document["second"] = true;
                    return document;
                }),
                Migration(1, 2, "first", document =>
                {
                    executionOrder.Add(1);
                    document["first"] = true;
                    return document;
                }),
            ]);

        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                JsonNode.Parse("""{"schemaVersion":1}""")!.AsObject(),
                schema);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(1, result.SourceVersion);
        CollectionAssert.AreEqual(new[] { 1, 2 }, executionOrder);
        CollectionAssert.AreEqual(
            new[] { "Schema 1 → 2: first", "Schema 2 → 3: second" },
            result.AppliedSteps.Select(static step => step.Describe()).ToArray());
        Assert.AreEqual(3, result.Document!["schemaVersion"]!.GetValue<int>());
        Assert.IsTrue(result.Document["first"]!.GetValue<bool>());
        Assert.IsTrue(result.Document["second"]!.GetValue<bool>());
        Assert.IsEmpty(result.Issues);
    }

    [TestMethod]
    [DataRow("""{}""", 0)]
    [DataRow("""{"schemaVersion":"one"}""", 0)]
    [DataRow("""{"schemaVersion":0}""", 0)]
    [DataRow("""{"schemaVersion":4}""", 4)]
    public void Migrate_RejectsMissingMalformedAndFutureVersions(
        string json,
        int expectedSourceVersion)
    {
        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                JsonNode.Parse(json)!.AsObject(),
                new ConfigurationSchema(3, []));

        Assert.IsFalse(result.Succeeded);
        Assert.AreEqual(expectedSourceVersion, result.SourceVersion);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual(
            ConfigurationValidationCode.UnsupportedSchemaVersion,
            result.Issues[0].Code);
        Assert.AreEqual("$.schemaVersion", result.Issues[0].Path);
    }

    [TestMethod]
    public void Migrate_ReportsTheGapAndKeepsCompletedStepsInTheReport()
    {
        ConfigurationSchema schema = new(
            4,
            [Migration(1, 2, "only available step", static document => document)]);

        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                JsonNode.Parse("""{"schemaVersion":1}""")!.AsObject(),
                schema);

        Assert.IsFalse(result.Succeeded);
        Assert.HasCount(1, result.AppliedSteps);
        Assert.AreEqual(1, result.AppliedSteps[0].FromVersion);
        Assert.HasCount(1, result.Issues);
        Assert.AreEqual(
            ConfigurationValidationCode.MigrationUnavailable,
            result.Issues[0].Code);
        Assert.Contains("version 2", result.Issues[0].Message);
    }

    [TestMethod]
    public void Migrate_ConvertsAThrowingOrNullStepIntoASpecificFailure()
    {
        ConfigurationMigrationResult throwing =
            ConfigurationMigrationPipeline.Migrate(
                JsonNode.Parse("""{"schemaVersion":1}""")!.AsObject(),
                new ConfigurationSchema(
                    2,
                    [
                        Migration(
                            1,
                            2,
                            "throws",
                            static _ => throw new InvalidOperationException("broken step")),
                    ]));

        Assert.IsFalse(throwing.Succeeded);
        Assert.AreEqual(
            ConfigurationValidationCode.MigrationFailed,
            throwing.Issues.Single().Code);
        Assert.Contains("broken step", throwing.Issues.Single().Message);

        ConfigurationMigrationResult returnsNull =
            ConfigurationMigrationPipeline.Migrate(
                JsonNode.Parse("""{"schemaVersion":1}""")!.AsObject(),
                new ConfigurationSchema(
                    2,
                    [
                        new StubConfigurationMigration(
                            1,
                            2,
                            "returns null",
                            static _ => null!),
                    ]));

        Assert.IsFalse(returnsNull.Succeeded);
        Assert.AreEqual(
            ConfigurationValidationCode.MigrationFailed,
            returnsNull.Issues.Single().Code);
        Assert.Contains("no document", returnsNull.Issues.Single().Message);
    }

    [TestMethod]
    public void Migrate_RejectsAnAmbiguousOrNonForwardMigrationSet()
    {
        JsonObject document =
            JsonNode.Parse("""{"schemaVersion":1}""")!.AsObject();

        Assert.Throws<ArgumentException>(() =>
            ConfigurationMigrationPipeline.Migrate(
                document,
                new ConfigurationSchema(
                    3,
                    [
                        Migration(1, 2, "a", static value => value),
                        Migration(1, 3, "b", static value => value),
                    ])));

        Assert.Throws<ArgumentException>(() =>
            ConfigurationMigrationPipeline.Migrate(
                document,
                new ConfigurationSchema(
                    2,
                    [Migration(1, 1, "stuck", static value => value)])));
    }

    [TestMethod]
    public void Migrate_A_CurrentDocumentRunsNoMigration()
    {
        bool ran = false;
        JsonObject document =
            JsonNode.Parse("""{"schemaVersion":2,"name":"current"}""")!.AsObject();

        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                document,
                new ConfigurationSchema(
                    2,
                    [
                        Migration(1, 2, "unused", value =>
                        {
                            ran = true;
                            return value;
                        }),
                    ]));

        Assert.IsTrue(result.Succeeded);
        Assert.IsFalse(ran);
        Assert.AreEqual(document.ToJsonString(), result.Document!.ToJsonString());
        Assert.IsEmpty(result.AppliedSteps);
    }

    private static StubConfigurationMigration Migration(
        int from,
        int to,
        string description,
        Func<JsonObject, JsonObject> apply) =>
        new(from, to, description, apply);
}

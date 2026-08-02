using System.Collections.Immutable;
using System.Text.Json.Nodes;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Tests.Configuration;

[TestClass]
public sealed class ConfigurationMigrationPipelineTests
{
    [TestMethod]
    public void CurrentSchema_ReordersUntouchedLegacyManagedDesktops()
    {
        JsonObject legacy = JsonNode.Parse(
            """
            {
              "schemaVersion": 1,
              "managedDesktops": [
                { "semanticKey": "ide-development", "displayName": "IDE Development", "preferredOrder": 1, "recreateWhenMissing": true },
                { "semanticKey": "run-observe", "displayName": "Run & Observe", "preferredOrder": 2, "recreateWhenMissing": true },
                { "semanticKey": "agent-development", "displayName": "Agent Development", "preferredOrder": 3, "recreateWhenMissing": true },
                { "semanticKey": "infrastructure", "displayName": "Infrastructure", "preferredOrder": 4, "recreateWhenMissing": true },
                { "semanticKey": "remote", "displayName": "Remote", "preferredOrder": 5, "recreateWhenMissing": true }
              ]
            }
            """)!.AsObject();
        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                legacy,
                ConfigurationSchema.Current);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.Document!["schemaVersion"]!.GetValue<int>());
        JsonArray desktops = result.Document["managedDesktops"]!.AsArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "run-observe",
                "ide-development",
                "agent-development",
                "infrastructure",
                "remote",
            },
            desktops.Select(static node =>
                node!["semanticKey"]!.GetValue<string>()).ToArray());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3, 4, 5 },
            desktops.Select(static node =>
                node!["preferredOrder"]!.GetValue<int>()).ToArray());
    }

    [TestMethod]
    public void CurrentSchema_PreservesCustomizedLegacyManagedDesktopOrder()
    {
        JsonObject customized = JsonNode.Parse(
            """
            {
              "schemaVersion": 1,
              "managedDesktops": [
                { "semanticKey": "ide-development", "displayName": "My IDE", "preferredOrder": 1, "recreateWhenMissing": true },
                { "semanticKey": "run-observe", "displayName": "Run & Observe", "preferredOrder": 2, "recreateWhenMissing": true },
                { "semanticKey": "agent-development", "displayName": "Agent Development", "preferredOrder": 3, "recreateWhenMissing": true },
                { "semanticKey": "infrastructure", "displayName": "Infrastructure", "preferredOrder": 4, "recreateWhenMissing": true },
                { "semanticKey": "remote", "displayName": "Remote", "preferredOrder": 5, "recreateWhenMissing": true }
              ]
            }
            """)!.AsObject();
        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                customized,
                ConfigurationSchema.Current);

        Assert.IsTrue(result.Succeeded);
        JsonArray desktops = result.Document!["managedDesktops"]!.AsArray();
        CollectionAssert.AreEqual(
            new[]
            {
                "ide-development",
                "run-observe",
                "agent-development",
                "infrastructure",
                "remote",
            },
            desktops.Select(static node =>
                node!["semanticKey"]!.GetValue<string>()).ToArray());
        Assert.AreEqual("My IDE", desktops[0]!["displayName"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { 1, 2, 3, 4, 5 },
            desktops.Select(static node =>
                node!["preferredOrder"]!.GetValue<int>()).ToArray());
    }

    [TestMethod]
    public void CurrentSchema_CombinesUntouchedLegacyAnywhereDefaults()
    {
        JsonObject legacy = JsonNode.Parse(
            """
            {
              "schemaVersion": 1,
              "applicationRules": [
                {
                  "id": "file-explorer",
                  "displayName": "File Explorer",
                  "isEnabled": true,
                  "targetDesktopKey": "",
                  "processNames": ["explorer.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "action": "allowAnywhere",
                  "packageFamilyNames": [],
                  "appUserModelIds": [],
                  "executablePaths": ["C:\\Windows\\explorer.exe"],
                  "windowClasses": []
                },
                {
                  "id": "notepad",
                  "displayName": "Notepad",
                  "isEnabled": true,
                  "targetDesktopKey": "",
                  "processNames": ["Notepad.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "action": "allowAnywhere",
                  "packageFamilyNames": ["Microsoft.WindowsNotepad_8wekyb3d8bbwe"],
                  "appUserModelIds": [],
                  "executablePaths": [],
                  "windowClasses": []
                }
              ]
            }
            """)!.AsObject();

        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                legacy,
                ConfigurationSchema.Current);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.Document!["schemaVersion"]!.GetValue<int>());
        JsonArray rules = result.Document["applicationRules"]!.AsArray();
        Assert.HasCount(1, rules);
        JsonObject combined = rules[0]!.AsObject();
        Assert.AreEqual("default-anywhere", combined["id"]!.GetValue<string>());
        CollectionAssert.AreEqual(
            new[] { "explorer.exe", "Notepad.exe" },
            combined["processNames"]!.AsArray()
                .Select(static node => node!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public void CurrentSchema_PreservesCustomizedLegacyAnywhereDefaults()
    {
        JsonObject customized = JsonNode.Parse(
            """
            {
              "schemaVersion": 1,
              "applicationRules": [
                {
                  "id": "file-explorer",
                  "displayName": "Files for this workspace",
                  "isEnabled": true,
                  "targetDesktopKey": "",
                  "processNames": ["explorer.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "action": "allowAnywhere",
                  "packageFamilyNames": [],
                  "appUserModelIds": [],
                  "executablePaths": ["C:\\Windows\\explorer.exe"],
                  "windowClasses": []
                },
                {
                  "id": "notepad",
                  "displayName": "Notepad",
                  "isEnabled": true,
                  "targetDesktopKey": "",
                  "processNames": ["Notepad.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "action": "allowAnywhere",
                  "packageFamilyNames": ["Microsoft.WindowsNotepad_8wekyb3d8bbwe"],
                  "appUserModelIds": [],
                  "executablePaths": [],
                  "windowClasses": []
                }
              ]
            }
            """)!.AsObject();

        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                customized,
                ConfigurationSchema.Current);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.Document!["schemaVersion"]!.GetValue<int>());
        CollectionAssert.AreEqual(
            new[] { "file-explorer", "notepad" },
            result.Document["applicationRules"]!.AsArray()
                .Select(static node => node!["id"]!.GetValue<string>())
                .ToArray());
    }

    [TestMethod]
    public void CurrentSchema_MovesSettingsToBuiltInAndAlignsUntouchedRuleOrder()
    {
        JsonObject versionTwo = JsonNode.Parse(
            """
            {
              "schemaVersion": 2,
              "applicationRules": [
                {
                  "id": "ide-development",
                  "displayName": "IDE Development",
                  "isEnabled": true,
                  "targetDesktopKey": "ide-development",
                  "processNames": ["Code.exe", "devenv.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "packageFamilyNames": [],
                  "appUserModelIds": [],
                  "executablePaths": [],
                  "windowClasses": [],
                  "action": "moveToDesktop"
                },
                {
                  "id": "run-observe",
                  "displayName": "Run & Observe",
                  "isEnabled": true,
                  "targetDesktopKey": "run-observe",
                  "processNames": ["msedge.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "packageFamilyNames": [],
                  "appUserModelIds": [],
                  "executablePaths": [],
                  "windowClasses": [],
                  "action": "moveToDesktop"
                },
                {
                  "id": "default-anywhere",
                  "displayName": "Default — stays where opened",
                  "isEnabled": true,
                  "targetDesktopKey": "",
                  "processNames": ["explorer.exe", "Notepad.exe", "SystemSettings.exe"],
                  "triggers": ["windowCreated", "windowShown", "foregroundActivated", "startupReconciliation", "manualReassignment"],
                  "switchPolicy": "onForegroundActivation",
                  "packageFamilyNames": ["Microsoft.WindowsNotepad_8wekyb3d8bbwe", "windows.immersivecontrolpanel_cw5n1h2txyewy"],
                  "appUserModelIds": [],
                  "executablePaths": ["C:\\Windows\\explorer.exe"],
                  "windowClasses": [],
                  "action": "allowAnywhere"
                }
              ]
            }
            """)!.AsObject();
        JsonObject customized = (JsonObject)versionTwo.DeepClone();
        JsonArray customizedRules = customized["applicationRules"]!.AsArray();
        customizedRules[0]!["displayName"] = "My IDE";
        customizedRules[2]!["displayName"] = "My Anywhere";

        ConfigurationMigrationResult result =
            ConfigurationMigrationPipeline.Migrate(
                versionTwo,
                ConfigurationSchema.Current);

        Assert.IsTrue(result.Succeeded);
        Assert.AreEqual(3, result.Document!["schemaVersion"]!.GetValue<int>());
        JsonArray rules = result.Document["applicationRules"]!.AsArray();
        CollectionAssert.AreEqual(
            new[] { "run-observe", "ide-development", "default-anywhere" },
            rules.Select(static node => node!["id"]!.GetValue<string>()).ToArray());
        JsonObject anywhere = rules[2]!.AsObject();
        CollectionAssert.AreEqual(
            new[] { "explorer.exe", "Notepad.exe" },
            anywhere["processNames"]!.AsArray()
                .Select(static node => node!.GetValue<string>())
                .ToArray());
        CollectionAssert.AreEqual(
            new[] { "Microsoft.WindowsNotepad_8wekyb3d8bbwe" },
            anywhere["packageFamilyNames"]!.AsArray()
                .Select(static node => node!.GetValue<string>())
                .ToArray());

        ConfigurationMigrationResult customizedResult =
            ConfigurationMigrationPipeline.Migrate(
                customized,
                ConfigurationSchema.Current);

        Assert.IsTrue(customizedResult.Succeeded);
        JsonArray preserved = customizedResult.Document!["applicationRules"]!.AsArray();
        CollectionAssert.AreEqual(
            new[] { "ide-development", "run-observe", "default-anywhere" },
            preserved.Select(static node => node!["id"]!.GetValue<string>()).ToArray());
        Assert.Contains(
            "SystemSettings.exe",
            preserved[2]!["processNames"]!.AsArray()
                .Select(static node => node!.GetValue<string>())
                .ToArray());
    }

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

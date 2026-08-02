using System.Text.Json.Nodes;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// Updates the untouched desktop order and combines the two schema-1 Anywhere
/// defaults into the grouped default introduced in schema 2.
/// </summary>
/// <remarks>
/// User edits win independently for both changes. A customized desktop list is
/// not reordered, and if either legacy rule differs from the exact rule that
/// shipped, both rule entries are preserved.
/// </remarks>
internal sealed class DefaultAnywhereRuleMigration : IConfigurationMigration
{
    private static readonly string[] AllTriggers =
    [
        "windowCreated",
        "windowShown",
        "foregroundActivated",
        "startupReconciliation",
        "manualReassignment",
    ];

    public int FromVersion => 1;

    public int ToVersion => 2;

    public string Description =>
        "Updates untouched desktop and Anywhere defaults.";

    public JsonObject Apply(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ReorderUntouchedManagedDesktops(document);
        CombineUntouchedAnywhereDefaults(document);
        return document;
    }

    private static void ReorderUntouchedManagedDesktops(JsonObject document)
    {
        if (document["managedDesktops"] is not JsonArray desktops ||
            desktops.Count != 5 ||
            !IsUntouchedDesktop(
                desktops[0],
                "ide-development",
                "IDE Development",
                1) ||
            !IsUntouchedDesktop(
                desktops[1],
                "run-observe",
                "Run & Observe",
                2) ||
            !IsUntouchedDesktop(
                desktops[2],
                "agent-development",
                "Agent Development",
                3) ||
            !IsUntouchedDesktop(
                desktops[3],
                "infrastructure",
                "Infrastructure",
                4) ||
            !IsUntouchedDesktop(desktops[4], "remote", "Remote", 5))
        {
            return;
        }

        JsonObject ide = desktops[0]!.AsObject();
        JsonObject run = desktops[1]!.AsObject();
        ide["preferredOrder"] = 2;
        run["preferredOrder"] = 1;

        desktops.RemoveAt(1);
        desktops.RemoveAt(0);
        desktops.Insert(0, run);
        desktops.Insert(1, ide);
    }

    private static bool IsUntouchedDesktop(
        JsonNode? node,
        string semanticKey,
        string displayName,
        int preferredOrder) =>
        node is JsonObject desktop &&
        string.Equals(
            ReadString(desktop, "semanticKey"),
            semanticKey,
            StringComparison.Ordinal) &&
        string.Equals(
            ReadString(desktop, "displayName"),
            displayName,
            StringComparison.Ordinal) &&
        ReadInteger(desktop, "preferredOrder") == preferredOrder &&
        ReadBoolean(desktop, "recreateWhenMissing") == true;

    private static void CombineUntouchedAnywhereDefaults(JsonObject document)
    {
        if (document["applicationRules"] is not JsonArray rules ||
            FindRule(rules, "default-anywhere") >= 0)
        {
            return;
        }

        int explorerIndex = FindRule(rules, "file-explorer");
        int notepadIndex = FindRule(rules, "notepad");
        if (explorerIndex < 0 ||
            notepadIndex < 0 ||
            rules[explorerIndex] is not JsonObject explorer ||
            rules[notepadIndex] is not JsonObject notepad ||
            !IsUntouchedExplorer(explorer) ||
            !IsUntouchedNotepad(notepad))
        {
            return;
        }

        int insertionIndex = Math.Min(explorerIndex, notepadIndex);
        rules.RemoveAt(Math.Max(explorerIndex, notepadIndex));
        rules.RemoveAt(insertionIndex);
        rules.Insert(insertionIndex, CreateCombinedRule());
    }

    private static int FindRule(JsonArray rules, string id)
    {
        for (int index = 0; index < rules.Count; index++)
        {
            if (rules[index] is JsonObject rule &&
                string.Equals(
                    ReadString(rule, "id"),
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsUntouchedExplorer(JsonObject rule) =>
        IsUntouchedAnywhereRule(
            rule,
            "file-explorer",
            "File Explorer",
            ["explorer.exe"],
            [],
            [@"C:\Windows\explorer.exe"]);

    private static bool IsUntouchedNotepad(JsonObject rule) =>
        IsUntouchedAnywhereRule(
            rule,
            "notepad",
            "Notepad",
            ["Notepad.exe"],
            ["Microsoft.WindowsNotepad_8wekyb3d8bbwe"],
            []);

    private static bool IsUntouchedAnywhereRule(
        JsonObject rule,
        string id,
        string displayName,
        string[] processNames,
        string[] packageFamilyNames,
        string[] executablePaths) =>
        string.Equals(ReadString(rule, "id"), id, StringComparison.Ordinal) &&
        string.Equals(
            ReadString(rule, "displayName"),
            displayName,
            StringComparison.Ordinal) &&
        ReadBoolean(rule, "isEnabled") == true &&
        string.Equals(ReadString(rule, "targetDesktopKey"), string.Empty) &&
        string.Equals(ReadString(rule, "action"), "allowAnywhere") &&
        string.Equals(
            ReadString(rule, "switchPolicy"),
            "onForegroundActivation") &&
        Matches(rule, "processNames", processNames) &&
        Matches(rule, "triggers", AllTriggers) &&
        Matches(rule, "packageFamilyNames", packageFamilyNames) &&
        Matches(rule, "appUserModelIds", []) &&
        Matches(rule, "executablePaths", executablePaths) &&
        Matches(rule, "windowClasses", []);

    private static JsonObject CreateCombinedRule() =>
        new()
        {
            ["id"] = "default-anywhere",
            ["displayName"] = "Default — stays where opened",
            ["isEnabled"] = true,
            ["targetDesktopKey"] = string.Empty,
            ["processNames"] = Values(
                "explorer.exe",
                "Notepad.exe",
                "SystemSettings.exe"),
            ["triggers"] = Values(AllTriggers),
            ["switchPolicy"] = "onForegroundActivation",
            ["action"] = "allowAnywhere",
            ["packageFamilyNames"] = Values(
                "Microsoft.WindowsNotepad_8wekyb3d8bbwe",
                "windows.immersivecontrolpanel_cw5n1h2txyewy"),
            ["appUserModelIds"] = new JsonArray(),
            ["executablePaths"] = Values(@"C:\Windows\explorer.exe"),
            ["windowClasses"] = new JsonArray(),
        };

    private static bool Matches(
        JsonObject rule,
        string propertyName,
        IReadOnlyList<string> expected)
    {
        if (rule[propertyName] is not JsonArray values ||
            values.Count != expected.Count)
        {
            return false;
        }

        for (int index = 0; index < values.Count; index++)
        {
            if (values[index] is not JsonValue value ||
                !value.TryGetValue(out string? actual) ||
                !string.Equals(actual, expected[index], StringComparison.Ordinal))
            {
                return false;
            }
        }

        return true;
    }

    private static string? ReadString(JsonObject rule, string propertyName) =>
        rule[propertyName] is JsonValue value &&
        value.TryGetValue(out string? result)
            ? result
            : null;

    private static bool? ReadBoolean(JsonObject rule, string propertyName) =>
        rule[propertyName] is JsonValue value && value.TryGetValue(out bool result)
            ? result
            : null;

    private static int? ReadInteger(JsonObject value, string propertyName) =>
        value[propertyName] is JsonValue node && node.TryGetValue(out int result)
            ? result
            : null;

    private static JsonArray Values(params string[] values) =>
        new(values
            .Select(static value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
}

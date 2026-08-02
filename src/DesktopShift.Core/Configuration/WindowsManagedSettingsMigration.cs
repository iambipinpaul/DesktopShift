using System.Text.Json.Nodes;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// Moves untouched Windows Settings defaults into the immutable Windows-managed
/// catalog and aligns untouched starter rules with the desktop order.
/// </summary>
/// <remarks>
/// User edits win independently. A customized Anywhere rule keeps its declared
/// identities, and customized or reordered move rules keep their order.
/// </remarks>
internal sealed class WindowsManagedSettingsMigration : IConfigurationMigration
{
    private static readonly string[] AllTriggers =
    [
        "windowCreated",
        "windowShown",
        "foregroundActivated",
        "startupReconciliation",
        "manualReassignment",
    ];

    public int FromVersion => 2;

    public int ToVersion => 3;

    public string Description =>
        "Moves Windows Settings into built-in tools and aligns starter rule order.";

    public JsonObject Apply(JsonObject document)
    {
        ArgumentNullException.ThrowIfNull(document);

        if (document["applicationRules"] is not JsonArray rules)
        {
            return document;
        }

        RemoveSettingsFromUntouchedAnywhereDefault(rules);
        ReorderUntouchedStarterRules(rules);
        return document;
    }

    private static void RemoveSettingsFromUntouchedAnywhereDefault(JsonArray rules)
    {
        int index = FindRule(rules, "default-anywhere");
        if (index < 0 ||
            rules[index] is not JsonObject rule ||
            !IsUntouchedSettingsAnywhereDefault(rule))
        {
            return;
        }

        rule["processNames"] = Values("explorer.exe", "Notepad.exe");
        rule["packageFamilyNames"] =
            Values("Microsoft.WindowsNotepad_8wekyb3d8bbwe");
    }

    private static void ReorderUntouchedStarterRules(JsonArray rules)
    {
        int ideIndex = FindRule(rules, "ide-development");
        int runIndex = FindRule(rules, "run-observe");
        if (ideIndex < 0 ||
            runIndex != ideIndex + 1 ||
            !IsUntouchedMoveRule(
                rules[ideIndex],
                "ide-development",
                "IDE Development",
                ["Code.exe", "devenv.exe"]) ||
            !IsUntouchedMoveRule(
                rules[runIndex],
                "run-observe",
                "Run & Observe",
                ["msedge.exe"]))
        {
            return;
        }

        JsonNode run = rules[runIndex]!;
        rules.RemoveAt(runIndex);
        rules.Insert(ideIndex, run);
    }

    private static bool IsUntouchedSettingsAnywhereDefault(JsonObject rule) =>
        string.Equals(ReadString(rule, "id"), "default-anywhere", StringComparison.Ordinal) &&
        string.Equals(
            ReadString(rule, "displayName"),
            "Default — stays where opened",
            StringComparison.Ordinal) &&
        ReadBoolean(rule, "isEnabled") == true &&
        string.Equals(ReadString(rule, "targetDesktopKey"), string.Empty) &&
        string.Equals(ReadString(rule, "action"), "allowAnywhere") &&
        string.Equals(
            ReadString(rule, "switchPolicy"),
            "onForegroundActivation",
            StringComparison.Ordinal) &&
        Matches(
            rule,
            "processNames",
            ["explorer.exe", "Notepad.exe", "SystemSettings.exe"]) &&
        Matches(rule, "triggers", AllTriggers) &&
        Matches(
            rule,
            "packageFamilyNames",
            [
                "Microsoft.WindowsNotepad_8wekyb3d8bbwe",
                "windows.immersivecontrolpanel_cw5n1h2txyewy",
            ]) &&
        Matches(rule, "appUserModelIds", []) &&
        Matches(rule, "executablePaths", [@"C:\Windows\explorer.exe"]) &&
        Matches(rule, "windowClasses", []);

    private static bool IsUntouchedMoveRule(
        JsonNode? node,
        string id,
        string displayName,
        string[] processNames) =>
        node is JsonObject rule &&
        string.Equals(ReadString(rule, "id"), id, StringComparison.Ordinal) &&
        string.Equals(
            ReadString(rule, "displayName"),
            displayName,
            StringComparison.Ordinal) &&
        ReadBoolean(rule, "isEnabled") == true &&
        string.Equals(ReadString(rule, "targetDesktopKey"), id, StringComparison.Ordinal) &&
        string.Equals(ReadString(rule, "action"), "moveToDesktop", StringComparison.Ordinal) &&
        string.Equals(
            ReadString(rule, "switchPolicy"),
            "onForegroundActivation",
            StringComparison.Ordinal) &&
        Matches(rule, "processNames", processNames) &&
        Matches(rule, "triggers", AllTriggers) &&
        Matches(rule, "packageFamilyNames", []) &&
        Matches(rule, "appUserModelIds", []) &&
        Matches(rule, "executablePaths", []) &&
        Matches(rule, "windowClasses", []);

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

    private static JsonArray Values(params string[] values) =>
        new(values
            .Select(static value => (JsonNode?)JsonValue.Create(value))
            .ToArray());
}

using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Infrastructure.Configuration;

internal static class ConfigurationValidator
{
    public static ImmutableArray<ConfigurationValidationIssue> Validate(
        ConfigurationDocument candidate)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        ImmutableArray<ConfigurationValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();

        if (candidate.SchemaVersion != ConfigurationDefaults.CurrentSchemaVersion)
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.UnsupportedSchemaVersion,
                $"Schema version {candidate.SchemaVersion} is not supported. Expected version {ConfigurationDefaults.CurrentSchemaVersion}.",
                "$.schemaVersion",
                ConfigurationEntryKind.Document));
        }

        HashSet<string> desktopKeys = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < candidate.ManagedDesktops.Length; index++)
        {
            ManagedDesktopDefinition desktop = candidate.ManagedDesktops[index];
            string path = $"$.managedDesktops[{index}]";

            if (string.IsNullOrWhiteSpace(desktop.SemanticKey))
            {
                issues.Add(RequiredValue(
                    $"{path}.semanticKey",
                    ConfigurationEntryKind.ManagedDesktop,
                    desktop.SemanticKey));
            }
            else if (!desktopKeys.Add(desktop.SemanticKey))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.DuplicateDesktopSemanticKey,
                    $"Managed Desktop semantic key '{desktop.SemanticKey}' is duplicated.",
                    $"{path}.semanticKey",
                    ConfigurationEntryKind.ManagedDesktop,
                    desktop.SemanticKey));
            }

            if (string.IsNullOrWhiteSpace(desktop.DisplayName))
            {
                issues.Add(RequiredValue(
                    $"{path}.displayName",
                    ConfigurationEntryKind.ManagedDesktop,
                    desktop.SemanticKey));
            }
        }

        HashSet<string> ruleIds = new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0; index < candidate.ApplicationRules.Length; index++)
        {
            ApplicationRule rule = candidate.ApplicationRules[index];
            string path = $"$.applicationRules[{index}]";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                issues.Add(RequiredValue(
                    $"{path}.id",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }
            else if (!ruleIds.Add(rule.Id))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.DuplicateRuleId,
                    $"Application Rule ID '{rule.Id}' is duplicated.",
                    $"{path}.id",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }

            if (string.IsNullOrWhiteSpace(rule.DisplayName))
            {
                issues.Add(RequiredValue(
                    $"{path}.displayName",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }

            if (string.IsNullOrWhiteSpace(rule.TargetDesktopKey))
            {
                issues.Add(RequiredValue(
                    $"{path}.targetDesktopKey",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }
            else if (!desktopKeys.Contains(rule.TargetDesktopKey))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.UnknownDesktopReference,
                    $"Application Rule '{rule.Id}' references unknown Managed Desktop '{rule.TargetDesktopKey}'.",
                    $"{path}.targetDesktopKey",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }

            // A rule may be identified by packaged identity or by executable
            // path alone, so no single list is required. At least one of them
            // must carry a usable value. Window classes are deliberately absent
            // from this test: a class names a window shape inside an
            // application and never says which application it is, so a rule
            // carrying only classes would claim that shape from every process
            // that draws it.
            if (!HasIdentity(rule.ProcessNames) &&
                !HasIdentity(rule.PackageFamilyNames) &&
                !HasIdentity(rule.AppUserModelIds) &&
                !HasIdentity(rule.ExecutablePaths))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.MissingApplicationIdentity,
                    $"Application Rule '{rule.Id}' must contain at least one process name, package family name, AppUserModelId, or executable path.",
                    path,
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }

            AddBlankIdentityIssue(
                issues,
                rule.ProcessNames,
                $"{path}.processNames",
                rule.Id);
            AddBlankIdentityIssue(
                issues,
                rule.PackageFamilyNames,
                $"{path}.packageFamilyNames",
                rule.Id);
            AddBlankIdentityIssue(
                issues,
                rule.AppUserModelIds,
                $"{path}.appUserModelIds",
                rule.Id);
            AddBlankIdentityIssue(
                issues,
                rule.ExecutablePaths,
                $"{path}.executablePaths",
                rule.Id);
            AddBlankIdentityIssue(
                issues,
                rule.WindowClasses,
                $"{path}.windowClasses",
                rule.Id);

            if (rule.Triggers.IsDefaultOrEmpty)
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.MissingTrigger,
                    $"Application Rule '{rule.Id}' must contain at least one trigger.",
                    $"{path}.triggers",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }
        }

        return issues.ToImmutable();
    }

    private static bool HasIdentity(ImmutableArray<string> identities)
    {
        return !identities.IsDefaultOrEmpty &&
            identities.Any(static identity =>
                !string.IsNullOrWhiteSpace(identity));
    }

    private static void AddBlankIdentityIssue(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        ImmutableArray<string> identities,
        string path,
        string ruleId)
    {
        if (identities.IsDefaultOrEmpty ||
            !identities.Any(string.IsNullOrWhiteSpace))
        {
            return;
        }

        issues.Add(new ConfigurationValidationIssue(
            ConfigurationValidationCode.MissingApplicationIdentity,
            $"Application Rule '{ruleId}' contains an empty identity at '{path}'.",
            path,
            ConfigurationEntryKind.ApplicationRule,
            ruleId));
    }

    private static ConfigurationValidationIssue RequiredValue(
        string path,
        ConfigurationEntryKind entryKind,
        string? entryId)
    {
        return new ConfigurationValidationIssue(
            ConfigurationValidationCode.RequiredValue,
            $"A value is required at '{path}'.",
            path,
            entryKind,
            entryId);
    }
}

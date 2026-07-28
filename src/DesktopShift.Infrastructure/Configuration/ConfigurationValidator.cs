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

            if (rule.ProcessNames.IsDefaultOrEmpty ||
                rule.ProcessNames.Any(string.IsNullOrWhiteSpace))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.MissingProcessName,
                    $"Application Rule '{rule.Id}' must contain at least one non-empty process name.",
                    $"{path}.processNames",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }

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

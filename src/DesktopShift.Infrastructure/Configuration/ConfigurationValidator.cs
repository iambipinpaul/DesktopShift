using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Hotkeys;

namespace DesktopShift.Infrastructure.Configuration;

internal static class ConfigurationValidator
{
    /// <summary>
    /// Reports everything wrong with a document, without discarding any of it.
    /// </summary>
    /// <param name="candidate">The document as it was written or imported.</param>
    /// <param name="expectedSchemaVersion">
    /// The version a document has to declare by the time it reaches here. Passed
    /// in rather than read from the constant because migrations run first, and a
    /// validator that hard-coded the shipped version could never be shown to
    /// accept a document that only a migration made current.
    /// </param>
    /// <returns>One issue per problem, in document order.</returns>
    public static ImmutableArray<ConfigurationValidationIssue> Validate(
        ConfigurationDocument candidate,
        int expectedSchemaVersion = ConfigurationDefaults.CurrentSchemaVersion)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        ImmutableArray<ConfigurationValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ConfigurationValidationIssue>();

        if (candidate.SchemaVersion != expectedSchemaVersion)
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.UnsupportedSchemaVersion,
                $"Schema version {candidate.SchemaVersion} is not supported. Expected version {expectedSchemaVersion}.",
                "$.schemaVersion",
                ConfigurationEntryKind.Document));
        }

        if (candidate.Behavior is null)
        {
            issues.Add(RequiredValue(
                "$.behavior",
                ConfigurationEntryKind.Behavior,
                entryId: null));
        }

        HashSet<string> desktopKeys = new(StringComparer.OrdinalIgnoreCase);
        ImmutableArray<ManagedDesktopDefinition> managedDesktops =
            candidate.ManagedDesktops.IsDefault ? [] : candidate.ManagedDesktops;
        for (int index = 0; index < managedDesktops.Length; index++)
        {
            ManagedDesktopDefinition desktop = managedDesktops[index];
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
        ImmutableArray<ApplicationRule> applicationRules =
            candidate.ApplicationRules.IsDefault ? [] : candidate.ApplicationRules;
        for (int index = 0; index < applicationRules.Length; index++)
        {
            ApplicationRule rule = applicationRules[index];
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

            // A rule that never moves a window has no destination, no useful
            // trigger, and no reachable switch policy, so reporting any of the
            // three would be reporting a member the matcher does not read.
            if (!rule.AllowsAnywhere)
            {
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

            // The shape tests are shared with the rule editor, so a rule the
            // editor accepts is a rule this validator accepts. A value that no
            // running window could ever report is dead configuration and has to
            // be reported where it was written rather than silently kept.
            AddShapeIssues(
                issues,
                rule.ProcessNames,
                ApplicationRuleShape.IsValidProcessName,
                $"{path}.processNames",
                "an executable file name",
                rule.Id);
            AddShapeIssues(
                issues,
                rule.PackageFamilyNames,
                ApplicationRuleShape.IsValidPackageFamilyName,
                $"{path}.packageFamilyNames",
                "a package family name in the form Name_PublisherId",
                rule.Id);
            AddShapeIssues(
                issues,
                rule.AppUserModelIds,
                ApplicationRuleShape.IsValidAppUserModelId,
                $"{path}.appUserModelIds",
                "an AppUserModelId",
                rule.Id);
            AddShapeIssues(
                issues,
                rule.ExecutablePaths,
                ApplicationRuleShape.IsValidExecutablePath,
                $"{path}.executablePaths",
                "a full executable path",
                rule.Id);
            AddShapeIssues(
                issues,
                rule.WindowClasses,
                ApplicationRuleShape.IsValidWindowClass,
                $"{path}.windowClasses",
                "a window class",
                rule.Id);

            if (rule.AllowsAnywhere)
            {
                continue;
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
            else if (!ApplicationRuleShape.IsSwitchPolicyReachable(
                rule.SwitchPolicy,
                rule.Triggers))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.UnreachableSwitchPolicy,
                    $"Application Rule '{rule.Id}' declares switch policy '{rule.SwitchPolicy}', which its triggers can never reach. {ApplicationRuleShape.DescribeUnreachableSwitchPolicy(rule.SwitchPolicy)}",
                    $"{path}.switchPolicy",
                    ConfigurationEntryKind.ApplicationRule,
                    rule.Id));
            }
        }

        // Desktop switching is validated before a profile becomes active.
        // A profile carrying no modifier that reached the document would become
        // active configuration and be discovered as a number row that stopped
        // working everywhere else on the machine.
        issues.AddRange(
            DesktopSwitchShortcuts.Validate(
                candidate.Behavior?.ToDesktopSwitchShortcutSettings()));

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

    private static void AddShapeIssues(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        ImmutableArray<string> identities,
        Func<string?, bool> isValid,
        string path,
        string expectation,
        string ruleId)
    {
        if (identities.IsDefaultOrEmpty)
        {
            return;
        }

        foreach (string identity in identities)
        {
            // A blank entry is already reported as a missing identity, so
            // reporting it a second time as a malformed one would only make the
            // same mistake look like two.
            if (!string.IsNullOrWhiteSpace(identity) && !isValid(identity))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.InvalidIdentityPattern,
                    $"Application Rule '{ruleId}' declares '{identity}' at '{path}', which is not {expectation}.",
                    path,
                    ConfigurationEntryKind.ApplicationRule,
                    ruleId));
            }
        }
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

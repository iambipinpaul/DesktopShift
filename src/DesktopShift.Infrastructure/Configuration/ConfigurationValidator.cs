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
            if (rule.MovesWindows)
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

            if (!rule.MovesWindows)
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

        AddTilingIssues(issues, candidate.Tiling);

        return issues.ToImmutable();
    }

    /// <summary>
    /// Validates the automatic window layout section.
    /// </summary>
    /// <remarks>
    /// <para>
    /// The rule checks reuse the Application Rule machinery — the same shape
    /// tests, the same missing-identity test — because a tiling rule matches
    /// with exactly the same signals an Application Rule matches with. Two
    /// implementations of one matcher's input validation would eventually
    /// disagree about what a valid process name is.
    /// </para>
    /// <para>
    /// <see cref="TilingSettings.ApplyToAllVirtualDesktops"/> set to
    /// <see langword="false"/> is reported rather than silently read as its
    /// default. This build shares layout parameters across desktops. A managed
    /// desktop can opt out through its stable semantic key, but it cannot carry
    /// a different gap or layout policy.
    /// </para>
    /// </remarks>
    private static void AddTilingIssues(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        TilingSettings? tiling)
    {
        if (tiling is null)
        {
            issues.Add(RequiredValue(
                "$.tiling",
                ConfigurationEntryKind.Tiling,
                entryId: null));
            return;
        }

        AddTilingGapIssue(issues, tiling.OuterGap, "$.tiling.outerGap");
        AddTilingGapIssue(issues, tiling.InnerGap, "$.tiling.innerGap");

        if (!Enum.IsDefined(tiling.Layout))
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.InvalidTilingValue,
                $"The tiling layout '{tiling.Layout}' is not supported.",
                "$.tiling.layout",
                ConfigurationEntryKind.Tiling,
                EntryId: null));
        }

        if (!Enum.IsDefined(tiling.InsertMode))
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.InvalidTilingValue,
                $"The tiling insert mode '{tiling.InsertMode}' is not supported.",
                "$.tiling.insertMode",
                ConfigurationEntryKind.Tiling,
                EntryId: null));
        }

        AddTileDimensionIssue(
            issues,
            tiling.MinimumTileWidth,
            "$.tiling.minimumTileWidth");
        AddTileDimensionIssue(
            issues,
            tiling.MinimumTileHeight,
            "$.tiling.minimumTileHeight");

        if (!tiling.ApplyToAllVirtualDesktops)
        {
            issues.Add(new ConfigurationValidationIssue(
                ConfigurationValidationCode.InvalidTilingValue,
                "applyToAllVirtualDesktops set to false is not supported. Layout parameters are shared; use disabledManagedDesktopKeys to turn tiling off for selected managed desktops.",
                "$.tiling.applyToAllVirtualDesktops",
                ConfigurationEntryKind.Tiling,
                EntryId: null));
        }

        HashSet<string> disabledDesktopKeys =
            new(StringComparer.OrdinalIgnoreCase);
        for (int index = 0;
             index < tiling.DisabledManagedDesktopKeys.Length;
             index++)
        {
            string key = tiling.DisabledManagedDesktopKeys[index];
            string path = $"$.tiling.disabledManagedDesktopKeys[{index}]";
            if (string.IsNullOrWhiteSpace(key))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.InvalidTilingValue,
                    "A disabled managed desktop key must not be blank.",
                    path,
                    ConfigurationEntryKind.Tiling,
                    EntryId: null));
                continue;
            }

            if (!disabledDesktopKeys.Add(key))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.InvalidTilingValue,
                    $"The managed desktop key '{key}' is listed more than once.",
                    path,
                    ConfigurationEntryKind.Tiling,
                    EntryId: key));
            }
        }

        HashSet<string> ruleIds = new(StringComparer.OrdinalIgnoreCase);
        AddTilingRuleIssues(
            issues,
            tiling.FloatRules,
            "$.tiling.floatRules",
            "Float",
            ruleIds);
        AddTilingRuleIssues(
            issues,
            tiling.IgnoreRules,
            "$.tiling.ignoreRules",
            "Ignore",
            ruleIds);
    }

    private static void AddTilingGapIssue(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        int value,
        string path)
    {
        if (value is >= TilingSettings.MinimumGap and <= TilingSettings.MaximumGap)
        {
            return;
        }

        issues.Add(new ConfigurationValidationIssue(
            ConfigurationValidationCode.InvalidTilingValue,
            $"The tiling gap at '{path}' must be between {TilingSettings.MinimumGap} and {TilingSettings.MaximumGap} DPI-scaled units, but is {value}.",
            path,
            ConfigurationEntryKind.Tiling,
            EntryId: null));
    }

    private static void AddTileDimensionIssue(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        int value,
        string path)
    {
        if (value is >= TilingSettings.AbsoluteMinimumTileDimension &&
            value <= TilingSettings.MaximumTileDimension)
        {
            return;
        }

        issues.Add(new ConfigurationValidationIssue(
            ConfigurationValidationCode.InvalidTilingValue,
            $"The minimum tile dimension at '{path}' must be between {TilingSettings.AbsoluteMinimumTileDimension} and {TilingSettings.MaximumTileDimension} DPI-scaled units, but is {value}.",
            path,
            ConfigurationEntryKind.Tiling,
            EntryId: null));
    }

    private static void AddTilingRuleIssues(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        ImmutableArray<TilingIdentityRule> rules,
        string basePath,
        string kindName,
        HashSet<string> ruleIds)
    {
        if (rules.IsDefault)
        {
            return;
        }

        for (int index = 0; index < rules.Length; index++)
        {
            TilingIdentityRule rule = rules[index];
            string path = $"{basePath}[{index}]";

            if (string.IsNullOrWhiteSpace(rule.Id))
            {
                issues.Add(RequiredValue(
                    $"{path}.id",
                    ConfigurationEntryKind.TilingRule,
                    rule.Id));
            }
            else if (!ruleIds.Add(rule.Id))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.DuplicateRuleId,
                    $"{kindName} rule ID '{rule.Id}' is duplicated.",
                    $"{path}.id",
                    ConfigurationEntryKind.TilingRule,
                    rule.Id));
            }

            if (string.IsNullOrWhiteSpace(rule.DisplayName))
            {
                issues.Add(RequiredValue(
                    $"{path}.displayName",
                    ConfigurationEntryKind.TilingRule,
                    rule.Id));
            }

            if (!HasIdentity(rule.ProcessNames) &&
                !HasIdentity(rule.PackageFamilyNames) &&
                !HasIdentity(rule.AppUserModelIds) &&
                !HasIdentity(rule.ExecutablePaths))
            {
                issues.Add(new ConfigurationValidationIssue(
                    ConfigurationValidationCode.MissingApplicationIdentity,
                    $"{kindName} rule '{rule.Id}' must contain at least one process name, package family name, AppUserModelId, or executable path.",
                    path,
                    ConfigurationEntryKind.TilingRule,
                    rule.Id));
            }

            AddBlankTilingIdentityIssue(
                issues,
                rule.ProcessNames,
                $"{path}.processNames",
                kindName,
                rule.Id);
            AddBlankTilingIdentityIssue(
                issues,
                rule.PackageFamilyNames,
                $"{path}.packageFamilyNames",
                kindName,
                rule.Id);
            AddBlankTilingIdentityIssue(
                issues,
                rule.AppUserModelIds,
                $"{path}.appUserModelIds",
                kindName,
                rule.Id);
            AddBlankTilingIdentityIssue(
                issues,
                rule.ExecutablePaths,
                $"{path}.executablePaths",
                kindName,
                rule.Id);
            AddBlankTilingIdentityIssue(
                issues,
                rule.WindowClasses,
                $"{path}.windowClasses",
                kindName,
                rule.Id);

            AddShapeIssues(
                issues,
                rule.ProcessNames,
                ApplicationRuleShape.IsValidProcessName,
                $"{path}.processNames",
                "an executable file name",
                rule.Id,
                kindName,
                ConfigurationEntryKind.TilingRule);
            AddShapeIssues(
                issues,
                rule.PackageFamilyNames,
                ApplicationRuleShape.IsValidPackageFamilyName,
                $"{path}.packageFamilyNames",
                "a package family name in the form Name_PublisherId",
                rule.Id,
                kindName,
                ConfigurationEntryKind.TilingRule);
            AddShapeIssues(
                issues,
                rule.AppUserModelIds,
                ApplicationRuleShape.IsValidAppUserModelId,
                $"{path}.appUserModelIds",
                "an AppUserModelId",
                rule.Id,
                kindName,
                ConfigurationEntryKind.TilingRule);
            AddShapeIssues(
                issues,
                rule.ExecutablePaths,
                ApplicationRuleShape.IsValidExecutablePath,
                $"{path}.executablePaths",
                "a full executable path",
                rule.Id,
                kindName,
                ConfigurationEntryKind.TilingRule);
            AddShapeIssues(
                issues,
                rule.WindowClasses,
                ApplicationRuleShape.IsValidWindowClass,
                $"{path}.windowClasses",
                "a window class",
                rule.Id,
                kindName,
                ConfigurationEntryKind.TilingRule);
        }
    }

    private static void AddBlankTilingIdentityIssue(
        ImmutableArray<ConfigurationValidationIssue>.Builder issues,
        ImmutableArray<string> identities,
        string path,
        string kindName,
        string ruleId)
    {
        if (identities.IsDefaultOrEmpty ||
            !identities.Any(string.IsNullOrWhiteSpace))
        {
            return;
        }

        issues.Add(new ConfigurationValidationIssue(
            ConfigurationValidationCode.MissingApplicationIdentity,
            $"{kindName} rule '{ruleId}' contains an empty identity at '{path}'.",
            path,
            ConfigurationEntryKind.TilingRule,
            ruleId));
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
        string ruleId,
        string kindName = "Application Rule",
        ConfigurationEntryKind entryKind = ConfigurationEntryKind.ApplicationRule)
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
                    $"{kindName} rule '{ruleId}' declares '{identity}' at '{path}', which is not {expectation}.",
                    path,
                    entryKind,
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

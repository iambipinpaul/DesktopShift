using System.Collections.Immutable;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// The part of the rule editor a validation issue belongs to, so a message can
/// be shown against the control that produced it instead of in a list at the
/// bottom of the dialog.
/// </summary>
public enum ApplicationRuleField
{
    Id,
    DisplayName,
    TargetDesktopKey,
    ProcessNames,
    PackageFamilyNames,
    AppUserModelIds,
    ExecutablePaths,
    WindowClasses,
    Triggers,
    SwitchPolicy,
}

/// <summary>
/// One inline validation message, addressed to the field that caused it.
/// </summary>
/// <param name="Field">The editor field the message belongs beside.</param>
/// <param name="Code">
/// The shared configuration validation code, so an editor message and the
/// document validator's message for the same fault are recognisably the same
/// fault.
/// </param>
/// <param name="Message">The message shown to the user.</param>
public sealed record ApplicationRuleValidationIssue(
    ApplicationRuleField Field,
    ConfigurationValidationCode Code,
    string Message);

/// <summary>
/// Turns a draft's issues into the strings the editor shows.
/// </summary>
public static class ApplicationRuleValidation
{
    /// <summary>
    /// The messages belonging beside one field, joined into the single line an
    /// inline error label shows.
    /// </summary>
    /// <param name="issues">Every issue the draft reported.</param>
    /// <param name="field">The field being labelled.</param>
    /// <returns>The message, empty when the field is fine.</returns>
    public static string DescribeField(
        ImmutableArray<ApplicationRuleValidationIssue> issues,
        ApplicationRuleField field)
    {
        if (issues.IsDefaultOrEmpty)
        {
            return string.Empty;
        }

        return string.Join(
            " ",
            issues
                .Where(issue => issue.Field == field)
                .Select(static issue => issue.Message));
    }

    /// <summary>
    /// Every message, one per line, for the summary bar at the top of the
    /// editor.
    /// </summary>
    /// <param name="issues">Every issue the draft reported.</param>
    /// <returns>The summary, empty when the draft can be applied.</returns>
    public static string Summarize(
        ImmutableArray<ApplicationRuleValidationIssue> issues) =>
        issues.IsDefaultOrEmpty
            ? string.Empty
            : string.Join(
                Environment.NewLine,
                issues.Select(static issue => issue.Message));
}

/// <summary>
/// An Application Rule while it is being edited.
/// </summary>
/// <remarks>
/// <para>
/// Every identity list is held as the raw text of its field rather than as a
/// parsed collection. That is what lets validation report a fault without losing
/// anything: a draft carrying a mistyped package family name is still a draft
/// carrying everything else the user typed, and re-showing it re-shows their
/// text exactly, down to the ordering and the line breaks.
/// </para>
/// <para>
/// A draft is never persisted. It is turned into an
/// <see cref="ApplicationRule"/> only when the user applies it, and only the
/// resulting document is saved.
/// </para>
/// </remarks>
/// <param name="Id">The rule identifier being typed.</param>
/// <param name="DisplayName">The rule name being typed.</param>
/// <param name="IsEnabled">Whether the rule will take part in matching.</param>
/// <param name="TargetDesktopKey">The selected Managed Desktop key.</param>
/// <param name="ProcessNames">The process name field's raw text.</param>
/// <param name="PackageFamilyNames">The package family name field's raw text.</param>
/// <param name="AppUserModelIds">The AppUserModelId field's raw text.</param>
/// <param name="ExecutablePaths">The executable path field's raw text.</param>
/// <param name="WindowClasses">The window class field's raw text.</param>
/// <param name="Triggers">The selected triggers.</param>
/// <param name="SwitchPolicy">The selected switch policy.</param>
/// <param name="EditedRuleId">
/// The identifier of the rule being edited, or <see langword="null"/> when the
/// draft is a new rule. It is what lets a rule keep its own identifier without
/// colliding with itself, and what decides whether applying the draft replaces a
/// rule in place or appends one.
/// </param>
/// <param name="Action">
/// The selected destination: a Managed Desktop, or Anywhere. Selecting Anywhere
/// keeps <paramref name="TargetDesktopKey"/>, <paramref name="Triggers"/>, and
/// <paramref name="SwitchPolicy"/> untouched rather than clearing them, so
/// changing the destination and changing it back does not discard what the user
/// had chosen.
/// </param>
public sealed record ApplicationRuleDraft(
    string Id,
    string DisplayName,
    bool IsEnabled,
    string TargetDesktopKey,
    string ProcessNames,
    string PackageFamilyNames,
    string AppUserModelIds,
    string ExecutablePaths,
    string WindowClasses,
    ImmutableArray<ApplicationRuleTrigger> Triggers,
    DesktopSwitchPolicy SwitchPolicy,
    string? EditedRuleId = null,
    ApplicationRuleAction Action = ApplicationRuleAction.MoveToDesktop)
{
    /// <summary>
    /// The selected triggers, normalized so an omitted collection is empty
    /// rather than a default array that cannot be enumerated.
    /// </summary>
    public ImmutableArray<ApplicationRuleTrigger> Triggers { get; init; } =
        Triggers.IsDefault ? [] : Triggers;

    /// <summary>
    /// Whether the draft creates a rule rather than editing one.
    /// </summary>
    public bool IsNewRule => EditedRuleId is null;

    /// <summary>
    /// Whether the selected destination is Anywhere, which is what the editor
    /// hides the trigger and switch policy controls on.
    /// </summary>
    public bool AllowsAnywhere => Action == ApplicationRuleAction.AllowAnywhere;

    /// <summary>
    /// Starts a new rule, pre-targeted at the document's first Managed Desktop
    /// and carrying the same triggers and switch policy the starter rules use.
    /// </summary>
    /// <param name="document">The document the rule will be added to.</param>
    /// <returns>An empty draft with a free identifier.</returns>
    public static ApplicationRuleDraft ForNewRule(
        ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        return new ApplicationRuleDraft(
            ApplicationRuleCatalog.CreateUniqueId(document, "rule"),
            string.Empty,
            IsEnabled: true,
            document.ManagedDesktops.IsDefaultOrEmpty
                ? string.Empty
                : document.ManagedDesktops[0].SemanticKey,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            string.Empty,
            ConfigurationDefaults.DefaultTriggers,
            DesktopSwitchPolicy.OnForegroundActivation);
    }

    /// <summary>
    /// Starts a new rule for a window DesktopShift already observed, so a user
    /// who sees an application swept to the first desktop can write its rule
    /// without hunting for it in a picker.
    /// </summary>
    /// <remarks>
    /// Only the privacy-safe identity is available here, which is deliberate:
    /// what the Activity view records never includes a window title, a URL, or a
    /// command line. That leaves the packaged identities and the process name —
    /// which is exactly the set that says which application a rule is about.
    /// The window class is not adopted, because a class narrows a rule to one
    /// window shape and narrowing is a decision rather than a detail of what was
    /// observed.
    /// </remarks>
    /// <param name="document">The document the rule will be added to.</param>
    /// <param name="identity">The identity captured from the observed window.</param>
    /// <param name="displayName">
    /// The rule name, or <see langword="null"/> to derive one from the process
    /// name.
    /// </param>
    /// <returns>A draft carrying the observed identity.</returns>
    public static ApplicationRuleDraft ForCapturedWindow(
        ConfigurationDocument document,
        WindowSafeIdentity identity,
        string? displayName = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(identity);

        string name = string.IsNullOrWhiteSpace(displayName)
            ? Path.GetFileNameWithoutExtension(identity.ProcessName)
            : displayName.Trim();

        return ForNewRule(document) with
        {
            Id = ApplicationRuleCatalog.CreateUniqueId(
                document,
                ToRuleIdSeed(name)),
            DisplayName = name,
            ProcessNames = AppendEntry(string.Empty, identity.ProcessName),
            PackageFamilyNames = AppendEntry(
                string.Empty,
                identity.PackageFamilyName),
            AppUserModelIds = AppendEntry(string.Empty, identity.AppUserModelId),
        };
    }

    /// <summary>
    /// Loads an existing rule into the editor.
    /// </summary>
    /// <param name="rule">The rule being edited.</param>
    /// <returns>A draft showing one identity per line.</returns>
    public static ApplicationRuleDraft ForExistingRule(ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        return new ApplicationRuleDraft(
            rule.Id,
            rule.DisplayName,
            rule.IsEnabled,
            rule.TargetDesktopKey,
            ApplicationRuleShape.FormatEntries(rule.ProcessNames),
            ApplicationRuleShape.FormatEntries(rule.PackageFamilyNames),
            ApplicationRuleShape.FormatEntries(rule.AppUserModelIds),
            ApplicationRuleShape.FormatEntries(rule.ExecutablePaths),
            ApplicationRuleShape.FormatEntries(rule.WindowClasses),
            rule.Triggers,
            rule.SwitchPolicy,
            rule.Id,
            rule.Action);
    }

    /// <summary>
    /// Turns the draft into the rule it describes.
    /// </summary>
    /// <returns>The parsed rule.</returns>
    public ApplicationRule ToRule() =>
        new(
            Id.Trim(),
            DisplayName.Trim(),
            IsEnabled,
            TargetDesktopKey.Trim(),
            ApplicationRuleShape.ParseEntries(ProcessNames),
            Triggers,
            SwitchPolicy,
            ApplicationRuleShape.ParseEntries(PackageFamilyNames),
            ApplicationRuleShape.ParseEntries(AppUserModelIds),
            ApplicationRuleShape.ParseEntries(ExecutablePaths),
            ApplicationRuleShape.ParseEntries(WindowClasses),
            Action);

    /// <summary>
    /// Applies the draft to a document, replacing the edited rule in place or
    /// appending a new one.
    /// </summary>
    /// <param name="document">The document to apply the draft to.</param>
    /// <returns>The edited document.</returns>
    public ConfigurationDocument Apply(ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ApplicationRule rule = ToRule();
        return EditedRuleId is null
            ? ApplicationRuleCatalog.Add(document, rule)
            : ApplicationRuleCatalog.Replace(document, EditedRuleId, rule);
    }

    /// <summary>
    /// Adopts the identities detected on a running application.
    /// </summary>
    /// <remarks>
    /// The packaged identities and the process name are always adopted, because
    /// they say which application the rule is about. The executable path and the
    /// window class are adopted only on request: both narrow a rule, a path to
    /// one installation and a class to one window shape, and narrowing is a
    /// decision rather than a detail of the selection.
    /// </remarks>
    /// <param name="candidate">The selected running application.</param>
    /// <param name="includeExecutablePath">
    /// Whether to pin the rule to the detected installation.
    /// </param>
    /// <param name="includeWindowClasses">
    /// Whether to narrow the rule to the detected window shapes.
    /// </param>
    /// <returns>A draft carrying the detected identities.</returns>
    public ApplicationRuleDraft WithRunningApplication(
        RunningApplicationCandidate candidate,
        bool includeExecutablePath = false,
        bool includeWindowClasses = false)
    {
        ArgumentNullException.ThrowIfNull(candidate);

        return this with
        {
            DisplayName = string.IsNullOrWhiteSpace(DisplayName)
                ? candidate.DisplayName
                : DisplayName,
            ProcessNames = AppendEntry(ProcessNames, candidate.ProcessName),
            PackageFamilyNames = AppendEntry(
                PackageFamilyNames,
                candidate.PackageFamilyName),
            AppUserModelIds = AppendEntry(
                AppUserModelIds,
                candidate.AppUserModelId),
            ExecutablePaths = includeExecutablePath
                ? AppendEntry(ExecutablePaths, candidate.ExecutablePath)
                : ExecutablePaths,
            WindowClasses = includeWindowClasses
                ? candidate.WindowClasses.Aggregate(
                    WindowClasses,
                    static (text, windowClass) => AppendEntry(text, windowClass))
                : WindowClasses,
        };
    }

    /// <summary>
    /// Adds a browsed or typed executable path.
    /// </summary>
    /// <param name="executablePath">The path to add.</param>
    /// <returns>A draft carrying the path.</returns>
    public ApplicationRuleDraft WithExecutablePath(string? executablePath) =>
        this with
        {
            ExecutablePaths = AppendEntry(ExecutablePaths, executablePath),
            DisplayName = string.IsNullOrWhiteSpace(DisplayName) &&
                !string.IsNullOrWhiteSpace(executablePath)
                    ? Path.GetFileNameWithoutExtension(executablePath)
                    : DisplayName,
            ProcessNames = string.IsNullOrWhiteSpace(executablePath)
                ? ProcessNames
                : AppendEntry(ProcessNames, Path.GetFileName(executablePath)),
        };

    /// <summary>
    /// Turns a single trigger on or off.
    /// </summary>
    /// <param name="trigger">The trigger being switched.</param>
    /// <param name="isSelected">Whether the trigger is selected.</param>
    /// <returns>A draft carrying the requested trigger set.</returns>
    public ApplicationRuleDraft WithTrigger(
        ApplicationRuleTrigger trigger,
        bool isSelected)
    {
        if (isSelected == Triggers.Contains(trigger))
        {
            return this;
        }

        return this with
        {
            Triggers = isSelected
                ? [.. ConfigurationDefaults.DefaultTriggers.Where(
                    candidate => candidate == trigger ||
                        Triggers.Contains(candidate))]
                : Triggers.Remove(trigger),
        };
    }

    /// <summary>
    /// Reports every fault in the draft, addressed to the field that caused it.
    /// </summary>
    /// <remarks>
    /// Every fault is reported in one pass. Stopping at the first would make a
    /// user apply, read one message, fix it, and apply again to discover the
    /// next, and each round trip is a chance to lose an entry.
    /// </remarks>
    /// <param name="document">
    /// The document the rule will live in, which is what makes a duplicate
    /// identifier and an unknown Managed Desktop reference detectable.
    /// </param>
    /// <returns>The issues, empty when the draft can be applied.</returns>
    public ImmutableArray<ApplicationRuleValidationIssue> Validate(
        ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        ImmutableArray<ApplicationRuleValidationIssue>.Builder issues =
            ImmutableArray.CreateBuilder<ApplicationRuleValidationIssue>();
        string trimmedId = Id.Trim();

        if (string.IsNullOrWhiteSpace(trimmedId))
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.Id,
                ConfigurationValidationCode.RequiredValue,
                "An identifier is required."));
        }
        else if (!ApplicationRuleShape.IsValidRuleId(trimmedId))
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.Id,
                ConfigurationValidationCode.InvalidIdentityPattern,
                "An identifier may only contain letters, digits, hyphens, underscores, and periods."));
        }
        else if (document.ApplicationRules.Any(rule =>
            !string.Equals(
                rule.Id,
                EditedRuleId,
                StringComparison.OrdinalIgnoreCase) &&
            string.Equals(
                rule.Id,
                trimmedId,
                StringComparison.OrdinalIgnoreCase)))
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.Id,
                ConfigurationValidationCode.DuplicateRuleId,
                $"Another rule already uses the identifier '{trimmedId}'."));
        }

        if (string.IsNullOrWhiteSpace(DisplayName))
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.DisplayName,
                ConfigurationValidationCode.RequiredValue,
                "A name is required."));
        }

        // An Anywhere rule never moves a window, so it has no destination to
        // check. Its triggers and switch policy are skipped below for the same
        // reason: they say when a move happens, and no move happens.
        string trimmedTarget = TargetDesktopKey.Trim();
        if (!AllowsAnywhere)
        {
            if (string.IsNullOrWhiteSpace(trimmedTarget))
            {
                issues.Add(new ApplicationRuleValidationIssue(
                    ApplicationRuleField.TargetDesktopKey,
                    ConfigurationValidationCode.RequiredValue,
                    "A target Managed Desktop is required."));
            }
            else if (!document.ManagedDesktops.Any(desktop =>
                string.Equals(
                    desktop.SemanticKey,
                    trimmedTarget,
                    StringComparison.OrdinalIgnoreCase)))
            {
                issues.Add(new ApplicationRuleValidationIssue(
                    ApplicationRuleField.TargetDesktopKey,
                    ConfigurationValidationCode.UnknownDesktopReference,
                    $"No Managed Desktop uses the key '{trimmedTarget}'."));
            }
        }

        AddPatternIssues(
            issues,
            ApplicationRuleField.ProcessNames,
            ProcessNames,
            ApplicationRuleShape.IsValidProcessName,
            "is not an executable file name");
        AddPatternIssues(
            issues,
            ApplicationRuleField.PackageFamilyNames,
            PackageFamilyNames,
            ApplicationRuleShape.IsValidPackageFamilyName,
            "is not a package family name in the form Name_PublisherId");
        AddPatternIssues(
            issues,
            ApplicationRuleField.AppUserModelIds,
            AppUserModelIds,
            ApplicationRuleShape.IsValidAppUserModelId,
            "is not an AppUserModelId");
        AddPatternIssues(
            issues,
            ApplicationRuleField.ExecutablePaths,
            ExecutablePaths,
            ApplicationRuleShape.IsValidExecutablePath,
            "is not a full executable path");
        AddPatternIssues(
            issues,
            ApplicationRuleField.WindowClasses,
            WindowClasses,
            ApplicationRuleShape.IsValidWindowClass,
            "is not a window class");

        if (!HasAnyEntry(ProcessNames) &&
            !HasAnyEntry(PackageFamilyNames) &&
            !HasAnyEntry(AppUserModelIds) &&
            !HasAnyEntry(ExecutablePaths))
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.ProcessNames,
                ConfigurationValidationCode.MissingApplicationIdentity,
                HasAnyEntry(WindowClasses)
                    ? "A window class names a window shape, never an application. Add a process name, package family name, AppUserModelId, or executable path."
                    : "Add at least one process name, package family name, AppUserModelId, or executable path."));
        }

        if (AllowsAnywhere)
        {
            return issues.ToImmutable();
        }

        if (Triggers.IsDefaultOrEmpty)
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.Triggers,
                ConfigurationValidationCode.MissingTrigger,
                "Select at least one trigger."));
        }
        else if (!ApplicationRuleShape.IsSwitchPolicyReachable(
            SwitchPolicy,
            Triggers))
        {
            issues.Add(new ApplicationRuleValidationIssue(
                ApplicationRuleField.SwitchPolicy,
                ConfigurationValidationCode.UnreachableSwitchPolicy,
                ApplicationRuleShape.DescribeUnreachableSwitchPolicy(
                    SwitchPolicy)));
        }

        return issues.ToImmutable();
    }

    private static void AddPatternIssues(
        ImmutableArray<ApplicationRuleValidationIssue>.Builder issues,
        ApplicationRuleField field,
        string text,
        Func<string?, bool> isValid,
        string explanation)
    {
        foreach (string entry in ApplicationRuleShape.ParseEntries(text))
        {
            if (!isValid(entry))
            {
                issues.Add(new ApplicationRuleValidationIssue(
                    field,
                    ConfigurationValidationCode.InvalidIdentityPattern,
                    $"'{entry}' {explanation}."));
            }
        }
    }

    /// <summary>
    /// Turns a display name into something
    /// <see cref="ApplicationRuleShape.IsValidRuleId"/> accepts, so a
    /// pre-filled rule opens with an identifier the user does not have to fix.
    /// </summary>
    private static string ToRuleIdSeed(string displayName)
    {
        string seed = new(
        [
            .. displayName
                .ToLowerInvariant()
                .Select(static character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '-' or '_' or '.'
                        ? character
                        : '-'),
        ]);

        return string.IsNullOrWhiteSpace(seed.Trim('-')) ? "rule" : seed.Trim('-');
    }

    private static bool HasAnyEntry(string text) =>
        !ApplicationRuleShape.ParseEntries(text).IsEmpty;

    private static string AppendEntry(string text, string? entry)
    {
        if (string.IsNullOrWhiteSpace(entry))
        {
            return text;
        }

        string trimmed = entry.Trim();
        ImmutableArray<string> existing = ApplicationRuleShape.ParseEntries(text);
        return existing.Contains(trimmed, StringComparer.OrdinalIgnoreCase)
            ? text
            : ApplicationRuleShape.FormatEntries(existing.Add(trimmed));
    }
}

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using DesktopShift.Core.Configuration;

namespace DesktopShift.App.Rules;

/// <summary>
/// One entry in the editor's destination picker.
/// </summary>
/// <remarks>
/// Anywhere sits in the same list as the Managed Desktops rather than in a
/// control of its own. Two lists could both claim the same application, which
/// would need a tie-break the user has to remember; one list already has one,
/// because the matcher resolves a tie by strength and then by rule order.
/// </remarks>
/// <param name="SemanticKey">
/// The key stored on the rule, empty for the Anywhere entry.
/// </param>
/// <param name="DisplayName">The name shown to the user.</param>
/// <param name="Action">What choosing this entry makes the rule do.</param>
public sealed record ManagedDesktopChoice(
    string SemanticKey,
    string DisplayName,
    ApplicationRuleAction Action = ApplicationRuleAction.MoveToDesktop);

/// <summary>
/// The rule editor's bindable surface.
/// </summary>
/// <remarks>
/// <para>
/// Every decision this class makes it delegates: the shape of a rule, what is
/// wrong with a draft, and what a draft would match all live in
/// <see cref="ApplicationRuleDraft"/> and its neighbours, where they are tested.
/// What lives here is only the translation between those types and the controls
/// — indices for combo boxes, booleans for check boxes, strings for text boxes.
/// </para>
/// <para>
/// Validation runs when the user tries to save or to test, never on every
/// keystroke. A message that appears while a word is half typed is noise, and
/// the draft keeps every character either way.
/// </para>
/// </remarks>
public sealed class RuleEditorViewModel : INotifyPropertyChanged
{
    private const string SystemSettingsProcessName = "SystemSettings.exe";
    private const string SystemSettingsPackageFamilyName =
        "windows.immersivecontrolpanel_cw5n1h2txyewy";
    private const string SystemSettingsAppUserModelId =
        "windows.immersivecontrolpanel_cw5n1h2txyewy!microsoft.windows.immersivecontrolpanel";

    private static readonly ImmutableArray<DesktopSwitchPolicy> SwitchPolicies =
    [
        DesktopSwitchPolicy.Never,
        DesktopSwitchPolicy.OnForegroundActivation,
        DesktopSwitchPolicy.OnNewWindowActivation,
    ];

    private readonly ConfigurationDocument document;
    private ApplicationRuleDraft draft;
    private ImmutableArray<ApplicationRuleValidationIssue> issues = [];
    private int selectedRunningApplicationIndex = -1;
    private bool includeExecutablePath;
    private bool includeWindowClasses;
    private bool isLoadingRunningApplications;
    private string runningApplicationStatus = string.Empty;
    private string testSummary = string.Empty;
    private string testDetails = string.Empty;
    private string saveError = string.Empty;

    public RuleEditorViewModel(
        ConfigurationDocument document,
        ApplicationRuleDraft draft)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(draft);

        this.document = document;
        this.draft = draft;
        ManagedDesktops =
        [
            .. document.ManagedDesktops.Select(desktop =>
                new ManagedDesktopChoice(
                    desktop.SemanticKey,
                    desktop.DisplayName)),
            new ManagedDesktopChoice(
                string.Empty,
                "Anywhere — never move this app",
                ApplicationRuleAction.AllowAnywhere),
        ];
        RunningApplications = [];
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>
    /// The dialog title, which is also what tells a user whether they are about
    /// to create a rule or change one.
    /// </summary>
    public string Title =>
        draft.IsNewRule ? "New application rule" : "Edit application rule";

    public IReadOnlyList<ManagedDesktopChoice> ManagedDesktops { get; }

    public ObservableCollection<RunningApplicationCandidate> RunningApplications
    {
        get;
    }

    public string Id
    {
        get => draft.Id;
        set => SetDraft(draft with { Id = value ?? string.Empty });
    }

    public string DisplayName
    {
        get => draft.DisplayName;
        set => SetDraft(draft with { DisplayName = value ?? string.Empty });
    }

    public bool IsEnabled
    {
        get => draft.IsEnabled;
        set => SetDraft(draft with { IsEnabled = value });
    }

    public int SelectedDesktopIndex
    {
        get
        {
            if (draft.AllowsAnywhere)
            {
                return ManagedDesktops.Count - 1;
            }

            for (int index = 0; index < ManagedDesktops.Count; index++)
            {
                if (ManagedDesktops[index].Action ==
                        ApplicationRuleAction.MoveToDesktop &&
                    string.Equals(
                        ManagedDesktops[index].SemanticKey,
                        draft.TargetDesktopKey,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return index;
                }
            }

            return -1;
        }

        set
        {
            if (value < 0 || value >= ManagedDesktops.Count)
            {
                return;
            }

            ManagedDesktopChoice choice = ManagedDesktops[value];

            // Choosing Anywhere leaves the previously selected Managed Desktop
            // on the draft rather than clearing it, so a user who is only
            // looking at the option can change their mind without losing it.
            SetDraft(
                choice.Action == ApplicationRuleAction.AllowAnywhere
                    ? draft with { Action = choice.Action }
                    : draft with
                    {
                        Action = choice.Action,
                        TargetDesktopKey = choice.SemanticKey,
                    });
            Raise(nameof(AllowsAnywhere));
            Raise(nameof(IsPlacementConfigurable));
        }
    }

    /// <summary>
    /// Whether the selected destination is Anywhere.
    /// </summary>
    public bool AllowsAnywhere => draft.AllowsAnywhere;

    /// <summary>
    /// Whether the trigger and switch policy controls apply at all.
    /// </summary>
    /// <remarks>
    /// Both say when a window is moved, and an Anywhere rule never moves one, so
    /// showing them would be offering settings that cannot take effect.
    /// </remarks>
    public bool IsPlacementConfigurable => !draft.AllowsAnywhere;

    public string ProcessNames
    {
        get => draft.ProcessNames;
        set
        {
            SetDraft(draft with { ProcessNames = value ?? string.Empty });
            Raise(nameof(HasSystemSettingsPlacementNote));
        }
    }

    public string PackageFamilyNames
    {
        get => draft.PackageFamilyNames;
        set
        {
            SetDraft(draft with { PackageFamilyNames = value ?? string.Empty });
            Raise(nameof(HasSystemSettingsPlacementNote));
        }
    }

    public string AppUserModelIds
    {
        get => draft.AppUserModelIds;
        set
        {
            SetDraft(draft with { AppUserModelIds = value ?? string.Empty });
            Raise(nameof(HasSystemSettingsPlacementNote));
        }
    }

    /// <summary>
    /// Whether the draft identifies Windows Settings, whose reused window has
    /// placement behaviour worth explaining before the user chooses a target.
    /// </summary>
    public bool HasSystemSettingsPlacementNote =>
        ContainsIdentity(ProcessNames, SystemSettingsProcessName) ||
        ContainsIdentity(
            PackageFamilyNames,
            SystemSettingsPackageFamilyName) ||
        ContainsIdentity(AppUserModelIds, SystemSettingsAppUserModelId);

    /// <summary>
    /// Explains the two predictable placement choices for Windows Settings.
    /// </summary>
    public string SystemSettingsPlacementNote =>
        "Windows Settings may reuse and relocate its existing window when launched from another desktop. Choose Anywhere to let it remain on the desktop where you open it. To enforce one Managed Desktop instead, keep Foreground activated enabled; focusing Settings may then move it and switch desktops.";

    public string ExecutablePaths
    {
        get => draft.ExecutablePaths;
        set => SetDraft(draft with { ExecutablePaths = value ?? string.Empty });
    }

    public string WindowClasses
    {
        get => draft.WindowClasses;
        set => SetDraft(draft with { WindowClasses = value ?? string.Empty });
    }

    public bool TriggerWindowCreated
    {
        get => HasTrigger(ApplicationRuleTrigger.WindowCreated);
        set => SetTrigger(ApplicationRuleTrigger.WindowCreated, value);
    }

    public bool TriggerWindowShown
    {
        get => HasTrigger(ApplicationRuleTrigger.WindowShown);
        set => SetTrigger(ApplicationRuleTrigger.WindowShown, value);
    }

    public bool TriggerForegroundActivated
    {
        get => HasTrigger(ApplicationRuleTrigger.ForegroundActivated);
        set => SetTrigger(ApplicationRuleTrigger.ForegroundActivated, value);
    }

    public bool TriggerStartupReconciliation
    {
        get => HasTrigger(ApplicationRuleTrigger.StartupReconciliation);
        set => SetTrigger(ApplicationRuleTrigger.StartupReconciliation, value);
    }

    public bool TriggerManualReassignment
    {
        get => HasTrigger(ApplicationRuleTrigger.ManualReassignment);
        set => SetTrigger(ApplicationRuleTrigger.ManualReassignment, value);
    }

    public int SwitchPolicyIndex
    {
        get => SwitchPolicies.IndexOf(draft.SwitchPolicy);
        set
        {
            if (value >= 0 && value < SwitchPolicies.Length)
            {
                SetDraft(draft with { SwitchPolicy = SwitchPolicies[value] });
            }
        }
    }

    public int SelectedRunningApplicationIndex
    {
        get => selectedRunningApplicationIndex;
        set => SetField(ref selectedRunningApplicationIndex, value);
    }

    public bool IncludeExecutablePath
    {
        get => includeExecutablePath;
        set => SetField(ref includeExecutablePath, value);
    }

    public bool IncludeWindowClasses
    {
        get => includeWindowClasses;
        set => SetField(ref includeWindowClasses, value);
    }

    public bool IsLoadingRunningApplications
    {
        get => isLoadingRunningApplications;
        set => SetField(ref isLoadingRunningApplications, value);
    }

    public string RunningApplicationStatus
    {
        get => runningApplicationStatus;
        set => SetField(ref runningApplicationStatus, value);
    }

    public string TestSummary
    {
        get => testSummary;
        set
        {
            if (SetField(ref testSummary, value))
            {
                Raise(nameof(HasTestResult));
            }
        }
    }

    public string TestDetails
    {
        get => testDetails;
        set => SetField(ref testDetails, value);
    }

    public bool HasTestResult => !string.IsNullOrWhiteSpace(TestSummary);

    /// <summary>
    /// Why the last save attempt did not go through, when it did not.
    /// </summary>
    /// <remarks>
    /// Kept apart from the validation summary because it answers a different
    /// question: validation says the rule is not ready, this says the rule was
    /// ready and the document still refused it.
    /// </remarks>
    public string SaveError
    {
        get => saveError;
        set
        {
            if (SetField(ref saveError, value))
            {
                Raise(nameof(HasSaveError));
            }
        }
    }

    public bool HasSaveError => !string.IsNullOrWhiteSpace(SaveError);

    public string IdError => Describe(ApplicationRuleField.Id);

    public string DisplayNameError => Describe(ApplicationRuleField.DisplayName);

    public string TargetDesktopError =>
        Describe(ApplicationRuleField.TargetDesktopKey);

    public string ProcessNamesError =>
        Describe(ApplicationRuleField.ProcessNames);

    public string PackageFamilyNamesError =>
        Describe(ApplicationRuleField.PackageFamilyNames);

    public string AppUserModelIdsError =>
        Describe(ApplicationRuleField.AppUserModelIds);

    public string ExecutablePathsError =>
        Describe(ApplicationRuleField.ExecutablePaths);

    public string WindowClassesError =>
        Describe(ApplicationRuleField.WindowClasses);

    public string TriggersError => Describe(ApplicationRuleField.Triggers);

    public string SwitchPolicyError =>
        Describe(ApplicationRuleField.SwitchPolicy);

    public string ValidationSummary =>
        ApplicationRuleValidation.Summarize(issues);

    public bool HasValidationIssues => !issues.IsDefaultOrEmpty;

    /// <summary>
    /// The rule the editor currently describes.
    /// </summary>
    /// <returns>The draft.</returns>
    public ApplicationRuleDraft ToDraft() => draft;

    /// <summary>
    /// Re-runs validation and republishes every inline message.
    /// </summary>
    /// <returns>Whether the draft can be applied.</returns>
    public bool Validate()
    {
        issues = draft.Validate(document);
        RaiseValidationChanged();
        return issues.IsEmpty;
    }

    /// <summary>
    /// Adopts the identities of the selected running application.
    /// </summary>
    /// <returns>Whether an application was selected.</returns>
    public bool ApplySelectedRunningApplication()
    {
        if (selectedRunningApplicationIndex < 0 ||
            selectedRunningApplicationIndex >= RunningApplications.Count)
        {
            return false;
        }

        SetDraft(draft.WithRunningApplication(
            RunningApplications[selectedRunningApplicationIndex],
            IncludeExecutablePath,
            IncludeWindowClasses));
        RaiseAllFields();
        return true;
    }

    /// <summary>
    /// Adopts a browsed or typed executable path.
    /// </summary>
    /// <param name="executablePath">The path.</param>
    public void ApplyExecutablePath(string? executablePath)
    {
        SetDraft(draft.WithExecutablePath(executablePath));
        RaiseAllFields();
    }

    /// <summary>
    /// Replaces the offered applications after a fresh read of the running
    /// windows.
    /// </summary>
    /// <param name="candidates">The applications now running.</param>
    public void SetRunningApplications(
        IReadOnlyList<RunningApplicationCandidate> candidates)
    {
        ArgumentNullException.ThrowIfNull(candidates);

        RunningApplications.Clear();
        foreach (RunningApplicationCandidate candidate in candidates)
        {
            RunningApplications.Add(candidate);
        }

        SelectedRunningApplicationIndex = candidates.Count > 0 ? 0 : -1;
        RunningApplicationStatus = candidates.Count == 0
            ? "No selectable application windows are open right now."
            : $"{candidates.Count} applications have windows open.";
    }

    /// <summary>
    /// Shows what the draft would claim if it were saved.
    /// </summary>
    /// <param name="result">The evaluated result.</param>
    public void SetTestResult(ApplicationRuleTestResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        TestSummary = result.MatchCount == 0
            ? $"No open window matches this rule. {result.EvaluatedWindowCount} windows were checked."
            : $"{result.MatchCount} of {result.EvaluatedWindowCount} open windows match, on {result.StrongestSignal}.";

        List<string> details =
        [
            .. result.Matches
                .Select(static match =>
                    $"{match.Identity.ProcessName} ({match.Identity.WindowClass}) — {match.Strength}")
                .Distinct(StringComparer.OrdinalIgnoreCase),
        ];

        if (!result.IsRuleEnabled)
        {
            details.Insert(
                0,
                "This rule is disabled, so it does not name these windows at all — and a window no rule names is moved to the first desktop. To stop moving this app without moving it away, set its destination to Anywhere instead.");
        }

        // Triggers say when a move happens, so they have nothing to say about a
        // rule that never moves anything.
        if (draft.AllowsAnywhere)
        {
            details.Insert(
                0,
                "This rule sends these windows Anywhere, so they stay where they open and are never swept to the first desktop.");
        }
        else if (!result.SupportsManualReassignment)
        {
            details.Insert(
                0,
                "This rule does not use the manual reassignment trigger, so reassigning matching windows now will skip it.");
        }

        TestDetails = string.Join(Environment.NewLine, details);
    }

    private bool HasTrigger(ApplicationRuleTrigger trigger) =>
        draft.Triggers.Contains(trigger);

    private static bool ContainsIdentity(string text, string identity) =>
        ApplicationRuleShape.ParseEntries(text).Any(entry =>
            string.Equals(entry, identity, StringComparison.OrdinalIgnoreCase));

    private void SetTrigger(ApplicationRuleTrigger trigger, bool isSelected)
    {
        if (isSelected != HasTrigger(trigger))
        {
            SetDraft(draft.WithTrigger(trigger, isSelected));
        }
    }

    private string Describe(ApplicationRuleField field) =>
        ApplicationRuleValidation.DescribeField(issues, field);

    private void SetDraft(
        ApplicationRuleDraft value,
        [CallerMemberName] string? propertyName = null)
    {
        draft = value;
        Raise(propertyName);
    }

    private bool SetField<T>(
        ref T field,
        T value,
        [CallerMemberName] string? propertyName = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        Raise(propertyName);
        return true;
    }

    private void RaiseAllFields()
    {
        Raise(nameof(Id));
        Raise(nameof(DisplayName));
        Raise(nameof(IsEnabled));
        Raise(nameof(SelectedDesktopIndex));
        Raise(nameof(AllowsAnywhere));
        Raise(nameof(IsPlacementConfigurable));
        Raise(nameof(ProcessNames));
        Raise(nameof(PackageFamilyNames));
        Raise(nameof(AppUserModelIds));
        Raise(nameof(HasSystemSettingsPlacementNote));
        Raise(nameof(ExecutablePaths));
        Raise(nameof(WindowClasses));
        Raise(nameof(TriggerWindowCreated));
        Raise(nameof(TriggerWindowShown));
        Raise(nameof(TriggerForegroundActivated));
        Raise(nameof(TriggerStartupReconciliation));
        Raise(nameof(TriggerManualReassignment));
        Raise(nameof(SwitchPolicyIndex));
    }

    private void RaiseValidationChanged()
    {
        Raise(nameof(IdError));
        Raise(nameof(DisplayNameError));
        Raise(nameof(TargetDesktopError));
        Raise(nameof(ProcessNamesError));
        Raise(nameof(PackageFamilyNamesError));
        Raise(nameof(AppUserModelIdsError));
        Raise(nameof(ExecutablePathsError));
        Raise(nameof(WindowClassesError));
        Raise(nameof(TriggersError));
        Raise(nameof(SwitchPolicyError));
        Raise(nameof(ValidationSummary));
        Raise(nameof(HasValidationIssues));
    }

    private void Raise(string? propertyName) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
}

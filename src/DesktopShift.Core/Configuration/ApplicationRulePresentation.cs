using System.Collections.Immutable;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// One Application Rule as the Rules page shows it.
/// </summary>
/// <param name="RuleId">The stable rule identifier.</param>
/// <param name="DisplayName">The rule name.</param>
/// <param name="Icon">
/// The icon of the executable the rule names, when the rule names a path and the
/// shell has an icon for it.
/// </param>
/// <param name="FallbackGlyph">
/// The glyph shown in place of an icon, so a list item never renders as a hole.
/// </param>
/// <param name="MatchSummary">
/// Every identity the rule declares, strongest signal first.
/// </param>
/// <param name="IdentityStrengthLabel">
/// What the rule is really identified by, so a packaged identity is not confused
/// with a process name that any application could share.
/// </param>
/// <param name="TargetSummary">The Managed Desktop the rule assigns to.</param>
/// <param name="TriggerSummary">The window events that activate the rule.</param>
/// <param name="SwitchPolicySummary">When a match may switch the desktop.</param>
/// <param name="IsEnabled">Whether the rule takes part in matching.</param>
/// <param name="EnabledLabel">The enabled state as a word.</param>
/// <param name="LastMatchSummary">When the rule last claimed a window.</param>
/// <param name="AdvancedDetails">
/// The full declared identity, one line per list, for the expanded view.
/// </param>
/// <param name="AutomationName">
/// The whole item as one sentence, so a screen reader announces the state
/// without the user having to walk every child element.
/// </param>
public sealed record ApplicationRulePresentation(
    string RuleId,
    string DisplayName,
    ApplicationIcon? Icon,
    string FallbackGlyph,
    string MatchSummary,
    string IdentityStrengthLabel,
    string TargetSummary,
    string TriggerSummary,
    string SwitchPolicySummary,
    bool IsEnabled,
    string EnabledLabel,
    string LastMatchSummary,
    string AdvancedDetails,
    string AutomationName);

/// <summary>
/// What one manual reassignment did for a single rule.
/// </summary>
/// <param name="RuleId">The rule the batch was run for.</param>
/// <param name="EnumeratedWindowCount">How many windows the batch walked.</param>
/// <param name="MatchedWindowCount">How many of them this rule claimed.</param>
/// <param name="MovedWindowCount">How many were moved to the rule's desktop.</param>
/// <param name="PinnedWindowCount">
/// How many were pinned to every desktop. A pin never moves a window, so it is
/// counted apart from <paramref name="MovedWindowCount"/>.
/// </param>
/// <param name="AlreadyOnTargetCount">
/// How many were already where the rule wants them: on its desktop, or already
/// pinned to every desktop.
/// </param>
/// <param name="UnavailableWindowCount">
/// How many were left alone because this host has no pinning surface to pin them
/// on. Counted apart from <paramref name="FailedWindowCount"/>, because nothing
/// went wrong.
/// </param>
/// <param name="FailedWindowCount">How many could not be moved or pinned.</param>
/// <param name="ShowsOnAllDesktops">
/// Whether the rule pins its windows instead of moving them, which is what the
/// sentence's verb follows.
/// </param>
public sealed record ApplicationRuleReassignmentSummary(
    string RuleId,
    int EnumeratedWindowCount,
    int MatchedWindowCount,
    int MovedWindowCount,
    int PinnedWindowCount,
    int AlreadyOnTargetCount,
    int UnavailableWindowCount,
    int FailedWindowCount,
    bool ShowsOnAllDesktops)
{
    /// <summary>
    /// The single sentence shown after the batch finishes.
    /// </summary>
    /// <remarks>
    /// A rule that pins its windows gets its own verb, because a pin never moves
    /// one: a count of nothing moved would read as a batch that did nothing.
    /// What the batch could not do is said out loud for the same reason — on a
    /// host with no pinning surface, clicking Reassign All has to report the pin
    /// it did not make rather than look as though it had. Windows a host cannot
    /// pin and windows that failed are counted apart because they mean different
    /// things, and one batch can hit both: when it does, the message says both
    /// rather than letting one count hide the other.
    /// </remarks>
    public string Message =>
        MatchedWindowCount == 0
            ? $"No open window matched this rule. {EnumeratedWindowCount} windows were checked."
            : ShowsOnAllDesktops
                ? PinMessage
                : MoveMessage;

    private string MoveMessage =>
        FailedWindowCount > 0
            ? $"Moved {MovedWindowCount} of {MatchedWindowCount} matching windows. {AlreadyOnTargetCount} were already in place and {FailedWindowCount} could not be moved."
            : $"Moved {MovedWindowCount} of {MatchedWindowCount} matching windows. {AlreadyOnTargetCount} were already in place.";

    private string PinMessage =>
        UnavailableWindowCount > 0
            ? FailedWindowCount > 0
                ? $"Pinned {PinnedWindowCount} of {MatchedWindowCount} matching windows. {UnavailableWindowCount} were left alone because this host cannot pin windows to every desktop, and {FailedWindowCount} could not be pinned."
                : $"Pinned {PinnedWindowCount} of {MatchedWindowCount} matching windows. {UnavailableWindowCount} were left alone because this host cannot pin windows to every desktop."
            : FailedWindowCount > 0
                ? $"Pinned {PinnedWindowCount} of {MatchedWindowCount} matching windows. {AlreadyOnTargetCount} were already pinned and {FailedWindowCount} could not be pinned."
                : $"Pinned {PinnedWindowCount} of {MatchedWindowCount} matching windows. {AlreadyOnTargetCount} were already pinned.";
}

/// <summary>
/// Projects the persisted Application Rules onto the Rules page.
/// </summary>
/// <remarks>
/// The projection is the whole of the page's logic. The code-behind only hands
/// it a document and shows what comes back, which is what makes the page's
/// behavior testable without a window.
/// </remarks>
public static class ApplicationRulePresentationProjection
{
    /// <summary>
    /// The Segoe Fluent glyph used when no executable icon is available.
    /// </summary>
    public const string DefaultRuleGlyph = "\uECAA";

    private static readonly ImmutableArray<ApplicationRuleTrigger> AllTriggers =
        ConfigurationDefaults.DefaultTriggers;

    /// <summary>
    /// Projects every rule in a document.
    /// </summary>
    /// <param name="document">The document being shown.</param>
    /// <param name="observations">
    /// The recorded observation activity, which is where the last-match time
    /// comes from. It carries only privacy-safe identities.
    /// </param>
    /// <param name="nowUtc">The moment the page is being rendered.</param>
    /// <param name="iconReader">
    /// Reads the icon of a rule's executable, or <see langword="null"/> when no
    /// platform reader is available and every item falls back to its glyph.
    /// </param>
    /// <returns>The list items, in document order.</returns>
    public static ImmutableArray<ApplicationRulePresentation> Project(
        ConfigurationDocument document,
        IReadOnlyList<WindowObservationActivity> observations,
        DateTimeOffset nowUtc,
        IApplicationIconReader? iconReader = null)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(observations);

        ImmutableDictionary<string, DateTimeOffset> lastMatches =
            ProjectLastMatches(observations);

        return
        [
            .. document.ApplicationRules.Select(rule => Project(
                rule,
                document,
                lastMatches.TryGetValue(rule.Id, out DateTimeOffset lastMatch)
                    ? lastMatch
                    : null,
                nowUtc,
                iconReader)),
        ];
    }

    /// <summary>
    /// Projects a single rule.
    /// </summary>
    /// <param name="rule">The rule being shown.</param>
    /// <param name="document">The document the rule lives in.</param>
    /// <param name="lastMatchUtc">When the rule last claimed a window.</param>
    /// <param name="nowUtc">The moment the item is being rendered.</param>
    /// <param name="iconReader">The executable icon reader, when there is one.</param>
    /// <returns>The list item.</returns>
    public static ApplicationRulePresentation Project(
        ApplicationRule rule,
        ConfigurationDocument document,
        DateTimeOffset? lastMatchUtc,
        DateTimeOffset nowUtc,
        IApplicationIconReader? iconReader = null)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(document);

        string matchSummary = DescribeMatch(rule);
        string strength = DescribeIdentityStrength(rule);
        string target = DescribeTarget(rule, document);

        // A rule that never moves a window shows the triggers and the switch
        // policy as unused. Both say when a move happens, and neither Anywhere
        // nor Show on all desktops makes one.
        bool neverMoves = rule.AllowsAnywhere || rule.ShowsOnAllDesktops;
        string triggers = neverMoves
            ? "Not used"
            : DescribeTriggers(rule.Triggers);
        string switchPolicy = neverMoves
            ? DescribeSwitchPolicy(DesktopSwitchPolicy.Never)
            : DescribeSwitchPolicy(rule.SwitchPolicy);
        string enabledLabel = rule.IsEnabled ? "Enabled" : "Disabled";
        string lastMatch = DescribeLastMatch(lastMatchUtc, nowUtc);

        return new ApplicationRulePresentation(
            rule.Id,
            rule.DisplayName,
            ReadIcon(rule, iconReader),
            DefaultRuleGlyph,
            matchSummary,
            strength,
            target,
            triggers,
            switchPolicy,
            rule.IsEnabled,
            enabledLabel,
            lastMatch,
            DescribeAdvancedDetails(rule),
            $"{rule.DisplayName}, {enabledLabel}. {strength}. {matchSummary}. {target}. Triggers: {triggers}. {switchPolicy}. {lastMatch}.");
    }

    /// <summary>
    /// Reads the most recent moment each rule claimed a window.
    /// </summary>
    /// <param name="observations">The recorded observation activity.</param>
    /// <returns>The last match time per rule identifier.</returns>
    public static ImmutableDictionary<string, DateTimeOffset> ProjectLastMatches(
        IReadOnlyList<WindowObservationActivity> observations)
    {
        ArgumentNullException.ThrowIfNull(observations);

        ImmutableDictionary<string, DateTimeOffset>.Builder lastMatches =
            ImmutableDictionary.CreateBuilder<string, DateTimeOffset>(
                StringComparer.OrdinalIgnoreCase);

        foreach (WindowObservationActivity activity in observations)
        {
            if (activity.Outcome != WindowObservationOutcome.Matched ||
                activity.RuleId is not { Length: > 0 } ruleId)
            {
                continue;
            }

            if (!lastMatches.TryGetValue(ruleId, out DateTimeOffset existing) ||
                activity.OccurredAt > existing)
            {
                lastMatches[ruleId] = activity.OccurredAt;
            }
        }

        return lastMatches.ToImmutable();
    }

    /// <summary>
    /// Reduces a manual reassignment batch to what it did for one rule.
    /// </summary>
    /// <param name="result">The batch result.</param>
    /// <param name="rule">
    /// The rule the user asked about. The rule rather than its identifier,
    /// because whether the batch pinned or moved is the rule's answer: a pin that
    /// failed, or that this host could not make, has no outcome a reader could
    /// take the verb from.
    /// </param>
    /// <returns>The per-rule summary.</returns>
    public static ApplicationRuleReassignmentSummary Summarize(
        WindowReassignmentBatchResult result,
        ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(result);
        ArgumentNullException.ThrowIfNull(rule);

        ImmutableArray<WindowAssignmentActivity> matched =
        [
            .. result.Assignments.Where(assignment => string.Equals(
                assignment.RuleId,
                rule.Id,
                StringComparison.OrdinalIgnoreCase)),
        ];

        return new ApplicationRuleReassignmentSummary(
            rule.Id,
            result.EnumeratedWindowCount,
            matched.Length,
            matched.Count(static assignment =>
                assignment.MoveOutcome == WindowMoveOutcome.Succeeded),
            matched.Count(static assignment =>
                assignment.MoveOutcome == WindowMoveOutcome.PinnedToAllDesktops),
            matched.Count(static assignment =>
                assignment.MoveOutcome == WindowMoveOutcome.AlreadyCorrect ||
                assignment.MoveOutcome ==
                    WindowMoveOutcome.AlreadyPinnedToAllDesktops ||
                assignment.SkipReason ==
                    WindowAssignmentSkipReason.AlreadyOnTargetDesktop),
            matched.Count(static assignment =>
                assignment.SkipReason ==
                    WindowAssignmentSkipReason.PinUnavailable),
            matched.Count(static assignment =>
                assignment.Outcome == WindowAssignmentOutcome.Failed),
            rule.ShowsOnAllDesktops);
    }

    /// <summary>
    /// Lists every identity a rule declares, strongest signal first.
    /// </summary>
    /// <remarks>
    /// Showing only process names is what hid the packaged identity that
    /// actually claims a Windows Terminal window, and the executable paths and
    /// window classes a Remote Desktop rule can carry. A user cannot reason
    /// about a rule they cannot see.
    /// </remarks>
    /// <param name="rule">The rule being described.</param>
    /// <returns>The one-line match summary.</returns>
    public static string DescribeMatch(ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        List<string> parts = [];
        AddPart(parts, "Package", rule.PackageFamilyNames);
        AddPart(parts, "AppUserModelId", rule.AppUserModelIds);
        AddPart(parts, "Path", rule.ExecutablePaths);
        AddPart(parts, "Process", rule.ProcessNames);
        AddPart(parts, "Window class", rule.WindowClasses);

        return parts.Count == 0
            ? "No identity declared"
            : string.Join(" • ", parts);
    }

    /// <summary>
    /// Names the strongest identity a rule declares.
    /// </summary>
    /// <param name="rule">The rule being described.</param>
    /// <returns>The strength label.</returns>
    public static string DescribeIdentityStrength(ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        if (!rule.PackageFamilyNames.IsDefaultOrEmpty)
        {
            return "Packaged identity";
        }

        if (!rule.AppUserModelIds.IsDefaultOrEmpty)
        {
            return "Shell identity";
        }

        if (!rule.ExecutablePaths.IsDefaultOrEmpty)
        {
            return "Executable path";
        }

        return rule.ProcessNames.IsDefaultOrEmpty
            ? "No identity"
            : "Process name only";
    }

    /// <summary>
    /// Names the destination of a rule: a Managed Desktop, Anywhere, or every
    /// desktop.
    /// </summary>
    /// <param name="rule">The rule being described.</param>
    /// <param name="document">The document holding the Managed Desktops.</param>
    /// <returns>The target label.</returns>
    public static string DescribeTarget(
        ApplicationRule rule,
        ConfigurationDocument document)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(document);

        if (rule.AllowsAnywhere)
        {
            return "Anywhere — stays where it opens";
        }

        // Named with the same phrase the editor's picker offers, so a rule reads
        // the same way in the list as it did in the dialog that wrote it.
        if (rule.ShowsOnAllDesktops)
        {
            return "Show on all desktops";
        }

        ManagedDesktopDefinition? desktop = document.ManagedDesktops.FirstOrDefault(
            candidate => string.Equals(
                candidate.SemanticKey,
                rule.TargetDesktopKey,
                StringComparison.OrdinalIgnoreCase));

        return desktop is null
            ? $"Unknown Managed Desktop '{rule.TargetDesktopKey}'"
            : $"Assigns to {desktop.DisplayName}";
    }

    /// <summary>
    /// Lists the triggers a rule declares.
    /// </summary>
    /// <param name="triggers">The declared triggers.</param>
    /// <returns>The trigger label.</returns>
    public static string DescribeTriggers(
        ImmutableArray<ApplicationRuleTrigger> triggers)
    {
        if (triggers.IsDefaultOrEmpty)
        {
            return "No triggers";
        }

        if (AllTriggers.All(triggers.Contains))
        {
            return "All triggers";
        }

        return string.Join(
            ", ",
            AllTriggers
                .Where(triggers.Contains)
                .Select(DescribeTrigger));
    }

    /// <summary>
    /// Names one trigger.
    /// </summary>
    /// <param name="trigger">The trigger.</param>
    /// <returns>The trigger label.</returns>
    public static string DescribeTrigger(ApplicationRuleTrigger trigger) =>
        trigger switch
        {
            ApplicationRuleTrigger.WindowCreated => "Window created",
            ApplicationRuleTrigger.WindowShown => "Window shown",
            ApplicationRuleTrigger.ForegroundActivated => "Foreground activated",
            ApplicationRuleTrigger.StartupReconciliation => "Startup reconciliation",
            ApplicationRuleTrigger.ManualReassignment => "Manual reassignment",
            _ => trigger.ToString(),
        };

    /// <summary>
    /// Explains when a rule may switch the foreground desktop.
    /// </summary>
    /// <param name="policy">The declared switch policy.</param>
    /// <returns>The switch policy label.</returns>
    public static string DescribeSwitchPolicy(DesktopSwitchPolicy policy) =>
        policy switch
        {
            DesktopSwitchPolicy.Never => "Never switches desktop",
            DesktopSwitchPolicy.OnForegroundActivation =>
                "Switches on foreground activation",
            DesktopSwitchPolicy.OnNewWindowActivation =>
                "Switches on a new window's first activation",
            _ => policy.ToString(),
        };

    /// <summary>
    /// Says when a rule last claimed a window, in the coarsest unit that still
    /// answers the question.
    /// </summary>
    /// <param name="lastMatchUtc">The last match, when there was one.</param>
    /// <param name="nowUtc">The moment the item is being rendered.</param>
    /// <returns>The last-match label.</returns>
    public static string DescribeLastMatch(
        DateTimeOffset? lastMatchUtc,
        DateTimeOffset nowUtc)
    {
        if (lastMatchUtc is not DateTimeOffset lastMatch)
        {
            return "No matches recorded";
        }

        TimeSpan elapsed = nowUtc - lastMatch;
        if (elapsed < TimeSpan.Zero)
        {
            elapsed = TimeSpan.Zero;
        }

        if (elapsed < TimeSpan.FromMinutes(1))
        {
            return "Last match just now";
        }

        if (elapsed < TimeSpan.FromHours(1))
        {
            return $"Last match {Count(elapsed.TotalMinutes, "minute")} ago";
        }

        if (elapsed < TimeSpan.FromDays(1))
        {
            return $"Last match {Count(elapsed.TotalHours, "hour")} ago";
        }

        return $"Last match {Count(elapsed.TotalDays, "day")} ago";
    }

    /// <summary>
    /// Writes out every declared list, one per line, for the expanded view.
    /// </summary>
    /// <param name="rule">The rule being described.</param>
    /// <returns>The advanced detail block.</returns>
    public static string DescribeAdvancedDetails(ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(rule);

        List<string> lines =
        [
            $"Identifier: {rule.Id}",
            rule.AllowsAnywhere
                ? "Destination: Anywhere"
                : rule.ShowsOnAllDesktops
                    ? "Destination: Show on all desktops"
                    : $"Target key: {rule.TargetDesktopKey}",
            $"Package family names: {FormatList(rule.PackageFamilyNames)}",
            $"AppUserModelIds: {FormatList(rule.AppUserModelIds)}",
            $"Executable paths: {FormatList(rule.ExecutablePaths)}",
            $"Process names: {FormatList(rule.ProcessNames)}",
            $"Window classes: {FormatList(rule.WindowClasses)}",
        ];

        return string.Join(Environment.NewLine, lines);
    }

    private static ApplicationIcon? ReadIcon(
        ApplicationRule rule,
        IApplicationIconReader? iconReader)
    {
        if (iconReader is null || rule.ExecutablePaths.IsDefaultOrEmpty)
        {
            return null;
        }

        foreach (string path in rule.ExecutablePaths)
        {
            if (iconReader.TryRead(path) is ApplicationIcon icon)
            {
                return icon;
            }
        }

        return null;
    }

    private static void AddPart(
        List<string> parts,
        string label,
        ImmutableArray<string> values)
    {
        if (!values.IsDefaultOrEmpty)
        {
            parts.Add($"{label}: {string.Join(", ", values)}");
        }
    }

    private static string FormatList(ImmutableArray<string> values) =>
        values.IsDefaultOrEmpty ? "none" : string.Join(", ", values);

    private static string Count(double value, string unit)
    {
        int rounded = Math.Max(1, (int)value);
        return rounded == 1 ? $"1 {unit}" : $"{rounded} {unit}s";
    }
}

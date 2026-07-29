using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Observation;

/// <summary>
/// The window identity signals a rule matches against. Every signal is compared
/// case-insensitively.
/// </summary>
/// <remarks>
/// <para>
/// A process name is a profile-agnostic signal. An executable name identifies
/// the application only; it never identifies which browser profile, container,
/// or account a window belongs to. No profile inference may be built on it.
/// </para>
/// <para>
/// <see cref="PackageFamilyNames"/>, <see cref="AppUserModelIds"/>, and
/// <see cref="ExecutablePaths"/> are the stronger stable identities and are
/// preferred over <see cref="ProcessNames"/> when a rule must be precise.
/// <see cref="WindowClasses"/>, <see cref="TitleContains"/>, and
/// <see cref="CommandLineContains"/> are opt-in refinements: a rule that
/// declares one also requires it to match.
/// </para>
/// <para>
/// Together they are the extension point for future package, AppUserModelId,
/// path, and per-profile matching. A per-profile rule is expressed today as a
/// stable identity plus a <see cref="CommandLineContains"/> profile argument,
/// so no matcher change is required.
/// </para>
/// </remarks>
/// <param name="PackageFamilyNames">
/// Packaged application identities. The strongest signal.
/// </param>
/// <param name="AppUserModelIds">Shell application identities.</param>
/// <param name="ExecutablePaths">Full executable paths.</param>
/// <param name="ProcessNames">
/// Executable file names. Identifies the application, never a profile.
/// </param>
/// <param name="WindowClasses">
/// Optional exact window class refinement. Chromium frame classes are shared
/// by Electron shells, so a class alone does not identify a browser.
/// </param>
/// <param name="TitleContains">Optional window title substring refinement.</param>
/// <param name="CommandLineContains">
/// Optional command line substring refinement. This is where a per-profile
/// argument belongs.
/// </param>
public sealed record WindowMatchCriteria(
    ImmutableArray<string> PackageFamilyNames,
    ImmutableArray<string> AppUserModelIds,
    ImmutableArray<string> ExecutablePaths,
    ImmutableArray<string> ProcessNames,
    ImmutableArray<string> WindowClasses,
    ImmutableArray<string> TitleContains,
    ImmutableArray<string> CommandLineContains)
{
    /// <summary>
    /// Creates criteria that match on process name alone, the profile-agnostic
    /// default used by configured application rules.
    /// </summary>
    /// <param name="processNames">The executable file names to match.</param>
    /// <returns>Criteria carrying only <see cref="ProcessNames"/>.</returns>
    public static WindowMatchCriteria ForProcessNames(
        ImmutableArray<string> processNames) =>
        new(
            [],
            [],
            [],
            processNames,
            [],
            [],
            []);
}

/// <summary>
/// Where a rule sends the windows it claims.
/// </summary>
/// <remarks>
/// The three values are the three answers window placement can give, and every
/// window gets exactly one of them.
/// </remarks>
public enum WindowRuleDestination
{
    /// <summary>
    /// The Managed Desktop named by
    /// <see cref="WindowObservationRule.TargetDesktopKey"/>.
    /// </summary>
    ManagedDesktop,

    /// <summary>Wherever the window opened. It is never moved.</summary>
    Anywhere,

    /// <summary>
    /// The Windows desktop at position 0 — the one Task View calls "Desktop 1".
    /// </summary>
    /// <remarks>
    /// Only <see cref="UnmanagedWindowSweep"/> produces this. It is located by
    /// position rather than by name, and never enters the Managed Desktop
    /// catalog: a Managed Desktop can end up unresolved after reconciliation,
    /// and a fallback that can fail to resolve is not a fallback. Windows
    /// refuses to delete the last virtual desktop, so position 0 always exists.
    /// </remarks>
    FirstDesktop,
}

/// <param name="Destination">
/// Where the rule sends what it claims. Trailing and defaulted, so every
/// existing construction site keeps working and a rule that says nothing about
/// its destination is a Managed Desktop rule.
/// </param>
public sealed record WindowObservationRule(
    string Id,
    string DisplayName,
    bool IsEnabled,
    string TargetDesktopKey,
    ImmutableArray<ApplicationRuleTrigger> Triggers,
    DesktopSwitchPolicy SwitchPolicy,
    WindowMatchCriteria Criteria,
    int Order,
    WindowRuleDestination Destination = WindowRuleDestination.ManagedDesktop);

public enum WindowMatchStrength
{
    None = 0,
    CommandLine = 10,
    Title = 20,
    WindowClass = 30,
    ProcessName = 40,
    ExecutablePath = 50,
    AppUserModelId = 60,
    PackageFamilyName = 70,
}

public sealed record WindowRuleMatch(
    WindowObservationRule Rule,
    WindowMatchStrength Strength);

public interface IWindowRuleSource
{
    IReadOnlyList<WindowObservationRule> GetRules();
}

public sealed class WindowRuleMatcher
{
    public WindowRuleMatch? Match(
        WindowIdentity identity,
        WindowEventKind eventKind,
        IReadOnlyList<WindowObservationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(rules);

        ApplicationRuleTrigger trigger = ToRuleTrigger(eventKind);

        return rules
            .Where(rule => rule.IsEnabled && rule.Triggers.Contains(trigger))
            .Select(rule => CreateMatch(rule, identity))
            .OfType<WindowRuleMatch>()
            .OrderByDescending(static match => match.Strength)
            .ThenBy(static match => match.Rule.Order)
            .ThenBy(static match => match.Rule.Id, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault();
    }

    /// <summary>
    /// Whether any enabled rule names the application this window belongs to.
    /// </summary>
    /// <remarks>
    /// <para>
    /// This is deliberately not <see cref="Match"/> with the result ignored.
    /// <see cref="Match"/> filters by trigger before it compares identity, so a
    /// rule that omits the firing trigger produces no match — and if that
    /// counted as "no rule names this app", narrowing a rule's triggers would
    /// stop being a harmless no-op and start banishing the application's windows
    /// to the first desktop. A rule missing
    /// <see cref="ApplicationRuleTrigger.ManualReassignment"/> would fling its
    /// windows away the moment the user pressed Reassign all.
    /// </para>
    /// <para>
    /// <see cref="WindowObservationRule.Triggers"/>,
    /// <see cref="WindowObservationRule.SwitchPolicy"/>, and
    /// <see cref="WindowObservationRule.Destination"/> are therefore all ignored
    /// here. Only <see cref="WindowObservationRule.IsEnabled"/> and the identity
    /// criteria decide the answer: a disabled rule is inert everywhere else, so
    /// making it half-alive here would be a special case with no explanation.
    /// </para>
    /// </remarks>
    /// <param name="identity">The window identity being tested.</param>
    /// <param name="rules">The rules in force.</param>
    /// <returns>Whether the application is managed by name.</returns>
    public bool IsNamedByAnyRule(
        WindowIdentity identity,
        IReadOnlyList<WindowObservationRule> rules)
    {
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentNullException.ThrowIfNull(rules);

        return rules.Any(rule =>
            rule.IsEnabled && CreateMatch(rule, identity) is not null);
    }

    private static WindowRuleMatch? CreateMatch(
        WindowObservationRule rule,
        WindowIdentity identity)
    {
        WindowMatchStrength primaryStrength = GetPrimaryStrength(
            rule.Criteria,
            identity);

        if (primaryStrength == WindowMatchStrength.None ||
            !MatchesOptionalRefinement(
                rule.Criteria.WindowClasses,
                identity.WindowClass,
                contains: false) ||
            !MatchesOptionalRefinement(
                rule.Criteria.TitleContains,
                identity.WindowTitle,
                contains: true) ||
            !MatchesOptionalRefinement(
                rule.Criteria.CommandLineContains,
                identity.CommandLine,
                contains: true))
        {
            return null;
        }

        WindowMatchStrength refinementStrength =
            !rule.Criteria.CommandLineContains.IsDefaultOrEmpty
                ? WindowMatchStrength.CommandLine
                : !rule.Criteria.TitleContains.IsDefaultOrEmpty
                    ? WindowMatchStrength.Title
                    : !rule.Criteria.WindowClasses.IsDefaultOrEmpty
                        ? WindowMatchStrength.WindowClass
                        : WindowMatchStrength.None;

        return new WindowRuleMatch(
            rule,
            (WindowMatchStrength)Math.Max(
                (int)primaryStrength,
                (int)refinementStrength));
    }

    private static WindowMatchStrength GetPrimaryStrength(
        WindowMatchCriteria criteria,
        WindowIdentity identity)
    {
        if (MatchesAny(
            criteria.PackageFamilyNames,
            identity.PackageFamilyName))
        {
            return WindowMatchStrength.PackageFamilyName;
        }

        if (MatchesAny(criteria.AppUserModelIds, identity.AppUserModelId))
        {
            return WindowMatchStrength.AppUserModelId;
        }

        if (MatchesAny(criteria.ExecutablePaths, identity.ExecutablePath))
        {
            return WindowMatchStrength.ExecutablePath;
        }

        return MatchesAny(criteria.ProcessNames, identity.ProcessName)
            ? WindowMatchStrength.ProcessName
            : WindowMatchStrength.None;
    }

    private static bool MatchesOptionalRefinement(
        ImmutableArray<string> candidates,
        string? value,
        bool contains) =>
        candidates.IsDefaultOrEmpty ||
        (value is not null &&
            candidates.Any(
                candidate => contains
                    ? value.Contains(candidate, StringComparison.OrdinalIgnoreCase)
                    : string.Equals(
                        candidate,
                        value,
                        StringComparison.OrdinalIgnoreCase)));

    private static bool MatchesAny(
        ImmutableArray<string> candidates,
        string? value) =>
        !candidates.IsDefaultOrEmpty &&
        value is not null &&
        candidates.Any(
            candidate => string.Equals(
                candidate,
                value,
                StringComparison.OrdinalIgnoreCase));

    private static ApplicationRuleTrigger ToRuleTrigger(
        WindowEventKind eventKind) =>
        eventKind switch
        {
            WindowEventKind.Created => ApplicationRuleTrigger.WindowCreated,
            WindowEventKind.Shown => ApplicationRuleTrigger.WindowShown,
            WindowEventKind.ForegroundActivated =>
                ApplicationRuleTrigger.ForegroundActivated,
            WindowEventKind.StartupReconciliation =>
                ApplicationRuleTrigger.StartupReconciliation,
            WindowEventKind.ManualReassignment =>
                ApplicationRuleTrigger.ManualReassignment,
            _ => ApplicationRuleTrigger.ManualReassignment,
        };
}

/// <summary>
/// Projects the configured application rules onto observation rules.
/// </summary>
public sealed class ConfigurationWindowRuleSource : IWindowRuleSource
{
    private readonly Func<ConfigurationDocument> getConfiguration;

    public ConfigurationWindowRuleSource(
        Func<ConfigurationDocument> getConfiguration)
    {
        ArgumentNullException.ThrowIfNull(getConfiguration);
        this.getConfiguration = getConfiguration;
    }

    /// <summary>
    /// Carries every identity and refinement a configured rule declares, so a
    /// rule that names a packaged identity is ranked above one that only names
    /// a process, and a rule that narrows itself to a window class is honored
    /// rather than widened back to its process names. An omitted collection is
    /// empty, and the matcher treats an empty or default collection as "not
    /// declared".
    /// </summary>
    /// <remarks>
    /// <see cref="WindowMatchCriteria.TitleContains"/> and
    /// <see cref="WindowMatchCriteria.CommandLineContains"/> stay empty because
    /// no configured rule can express them. The identity resolver collects a
    /// window title and a command line only when they are explicitly opted
    /// into, so a configured refinement on either could never match.
    /// </remarks>
    /// <returns>The configured rules in document order.</returns>
    public IReadOnlyList<WindowObservationRule> GetRules() =>
        getConfiguration()
            .ApplicationRules
            .Select(
                static (rule, index) => new WindowObservationRule(
                    rule.Id,
                    rule.DisplayName,
                    rule.IsEnabled,
                    rule.TargetDesktopKey,
                    rule.Triggers,
                    rule.SwitchPolicy,
                    new WindowMatchCriteria(
                        rule.PackageFamilyNames,
                        rule.AppUserModelIds,
                        rule.ExecutablePaths,
                        rule.ProcessNames,
                        rule.WindowClasses,
                        TitleContains: [],
                        CommandLineContains: []),
                    index,
                    rule.Action == ApplicationRuleAction.AllowAnywhere
                        ? WindowRuleDestination.Anywhere
                        : WindowRuleDestination.ManagedDesktop))
            .ToArray();
}

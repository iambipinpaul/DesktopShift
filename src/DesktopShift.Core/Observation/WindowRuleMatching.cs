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

public sealed record WindowObservationRule(
    string Id,
    string DisplayName,
    bool IsEnabled,
    string TargetDesktopKey,
    ImmutableArray<ApplicationRuleTrigger> Triggers,
    DesktopSwitchPolicy SwitchPolicy,
    WindowMatchCriteria Criteria,
    int Order);

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
    /// Carries every identity a configured rule declares, so a rule that names
    /// a packaged identity is ranked above one that only names a process. An
    /// omitted collection is empty, and the matcher treats an empty or default
    /// collection as "not declared".
    /// </summary>
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
                        ExecutablePaths: [],
                        rule.ProcessNames,
                        WindowClasses: [],
                        TitleContains: [],
                        CommandLineContains: []),
                    index))
            .ToArray();
}

using System.Collections.Immutable;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.Observation;

public sealed record WindowMatchCriteria(
    ImmutableArray<string> PackageFamilyNames,
    ImmutableArray<string> AppUserModelIds,
    ImmutableArray<string> ExecutablePaths,
    ImmutableArray<string> ProcessNames,
    ImmutableArray<string> WindowClasses,
    ImmutableArray<string> TitleContains,
    ImmutableArray<string> CommandLineContains)
{
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
            _ => ApplicationRuleTrigger.ManualReassignment,
        };
}

public sealed class ConfigurationWindowRuleSource : IWindowRuleSource
{
    private readonly Func<ConfigurationDocument> getConfiguration;

    public ConfigurationWindowRuleSource(
        Func<ConfigurationDocument> getConfiguration)
    {
        ArgumentNullException.ThrowIfNull(getConfiguration);
        this.getConfiguration = getConfiguration;
    }

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
                    WindowMatchCriteria.ForProcessNames(rule.ProcessNames),
                    index))
            .ToArray();
}

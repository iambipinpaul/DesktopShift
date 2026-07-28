using System.Collections.Immutable;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// One window a candidate rule would claim.
/// </summary>
/// <param name="Identity">
/// The privacy-safe identity of the window, which is everything the matcher
/// used to decide.
/// </param>
/// <param name="Strength">The signal that selected the rule.</param>
public sealed record ApplicationRuleTestMatch(
    WindowSafeIdentity Identity,
    WindowMatchStrength Strength);

/// <summary>
/// What a candidate rule would do to the windows that are open right now.
/// </summary>
/// <param name="EvaluatedWindowCount">How many windows were evaluated.</param>
/// <param name="Matches">The windows the rule would claim.</param>
/// <param name="IsRuleEnabled">
/// Whether the rule is enabled. A disabled rule is still evaluated, so a user
/// can see what turning it on would do before turning it on.
/// </param>
/// <param name="SupportsManualReassignment">
/// Whether the rule declares the manual reassignment trigger. A rule without it
/// matches here but is skipped by "reassign matching windows now", and that
/// difference has to be visible or the button looks broken.
/// </param>
public sealed record ApplicationRuleTestResult(
    int EvaluatedWindowCount,
    ImmutableArray<ApplicationRuleTestMatch> Matches,
    bool IsRuleEnabled,
    bool SupportsManualReassignment)
{
    /// <summary>
    /// An untested result, shown before a test has been run.
    /// </summary>
    public static ApplicationRuleTestResult NotRun { get; } =
        new(0, [], IsRuleEnabled: true, SupportsManualReassignment: true);

    /// <summary>
    /// How many windows the rule would claim.
    /// </summary>
    public int MatchCount => Matches.IsDefaultOrEmpty ? 0 : Matches.Length;

    /// <summary>
    /// The strongest signal any match used, which is what the rule is really
    /// being identified by today.
    /// </summary>
    public WindowMatchStrength StrongestSignal =>
        Matches.IsDefaultOrEmpty
            ? WindowMatchStrength.None
            : Matches.Max(static match => match.Strength);
}

/// <summary>
/// Evaluates a candidate Application Rule against the windows that are open
/// right now.
/// </summary>
/// <remarks>
/// <para>
/// The evaluation runs through the production <see cref="WindowRuleMatcher"/>.
/// A second implementation written for the editor would be a second definition
/// of what "matches" means, and the moment the two disagreed the test would be
/// worse than not having one.
/// </para>
/// <para>
/// The rule under test is evaluated as if it were enabled and declared every
/// trigger, because the question the editor asks is which windows this identity
/// describes. When the rule is disabled, or does not declare the trigger the
/// manual reassignment path uses, the result says so rather than silently
/// reporting nothing.
/// </para>
/// </remarks>
public static class ApplicationRuleTester
{
    private static readonly WindowRuleMatcher Matcher = new();

    /// <summary>
    /// Runs the candidate rule against a set of window identities.
    /// </summary>
    /// <param name="rule">The candidate rule.</param>
    /// <param name="windows">The identities of the windows that are open.</param>
    /// <returns>The windows the rule would claim.</returns>
    public static ApplicationRuleTestResult Test(
        ApplicationRule rule,
        IReadOnlyList<WindowIdentity> windows)
    {
        ArgumentNullException.ThrowIfNull(rule);
        ArgumentNullException.ThrowIfNull(windows);

        IReadOnlyList<WindowObservationRule> rules = [ToTestRule(rule)];
        ImmutableArray<ApplicationRuleTestMatch>.Builder matches =
            ImmutableArray.CreateBuilder<ApplicationRuleTestMatch>();

        foreach (WindowIdentity identity in windows)
        {
            WindowRuleMatch? match = Matcher.Match(
                identity,
                WindowEventKind.ManualReassignment,
                rules);
            if (match is not null)
            {
                matches.Add(new ApplicationRuleTestMatch(
                    identity.ToSafeIdentity(),
                    match.Strength));
            }
        }

        return new ApplicationRuleTestResult(
            windows.Count,
            matches.ToImmutable(),
            rule.IsEnabled,
            !rule.Triggers.IsDefaultOrEmpty &&
                rule.Triggers.Contains(
                    ApplicationRuleTrigger.ManualReassignment));
    }

    /// <summary>
    /// Projects a configured rule onto the observation rule the matcher takes,
    /// carrying every identity and refinement the rule declares.
    /// </summary>
    /// <remarks>
    /// The projection mirrors <see cref="ConfigurationWindowRuleSource"/>, down
    /// to leaving the title and command line refinements empty, so a test result
    /// says what the running observer would decide and not something close to it.
    /// The only differences are deliberate: the rule is enabled and declares
    /// every trigger, because a test asks about identity, not about timing.
    /// </remarks>
    /// <param name="rule">The rule under test.</param>
    /// <returns>The observation rule the matcher evaluates.</returns>
    private static WindowObservationRule ToTestRule(ApplicationRule rule) =>
        new(
            rule.Id,
            rule.DisplayName,
            IsEnabled: true,
            rule.TargetDesktopKey,
            ConfigurationDefaults.DefaultTriggers,
            rule.SwitchPolicy,
            new WindowMatchCriteria(
                rule.PackageFamilyNames,
                rule.AppUserModelIds,
                rule.ExecutablePaths,
                rule.ProcessNames,
                rule.WindowClasses,
                TitleContains: [],
                CommandLineContains: []),
            Order: 0);
}

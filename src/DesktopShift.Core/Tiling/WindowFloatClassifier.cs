using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;

namespace DesktopShift.Core.Tiling;

/// <summary>What the layout does with a window.</summary>
public enum TilingDisposition
{
    /// <summary>The window joins the BSP tree.</summary>
    Tile,

    /// <summary>
    /// The window stays out of the tree but keeps being observed: assignments
    /// still move it between desktops, the layout never resizes or moves it.
    /// </summary>
    Float,

    /// <summary>
    /// The layout does not touch or track the window at all.
    /// </summary>
    Ignore,
}

/// <summary>
/// Windows with a fixed layout policy that is independent of user rules.
/// </summary>
public static class BuiltInTilingRules
{
    /// <summary>
    /// Task Manager normally runs above DesktopShift's integrity level, so
    /// Windows refuses placement. Keeping it floating avoids a dead BSP leaf.
    /// </summary>
    public static ImmutableArray<TilingIdentityRule> FloatRules { get; } =
    [
        new(
            Id: "built-in-float-task-manager",
            DisplayName: "Task Manager",
            IsEnabled: true,
            ProcessNames: ["Taskmgr.exe"]),
    ];

    /// <summary>
    /// Snipping Tool's recording surfaces are temporary overlays. The editor
    /// uses another class and remains governed by the user's rules.
    /// </summary>
    public static ImmutableArray<TilingIdentityRule> IgnoreRules { get; } =
    [
        new(
            Id: "built-in-ignore-snipping-overlays",
            DisplayName: "Snipping Tool recording overlays",
            IsEnabled: true,
            ProcessNames: ["SnippingTool.exe"],
            WindowClasses: ["XamlWindow", "SnipOverlayRootWindow"]),
    ];
}

/// <summary>
/// Decides whether a window tiles, floats, or is ignored entirely.
/// </summary>
/// <remarks>
/// <para>
/// The match fields are exactly an Application Rule's identity signals,
/// compared verbatim and without case sensitivity. Titles and command lines
/// are absent because the resolver only collects them when explicitly opted
/// into elsewhere — a rule written against them could never match.
/// </para>
/// <para>
/// Precedence when both kinds of rule match: ignore wins. A window the user
/// asked the layout to know nothing about must never end up tracked because a
/// float rule also happened to name its process; the stricter instruction is
/// the safer one to honour.
/// </para>
/// <para>
/// A window whose identity could not be resolved at all — typically an
/// elevated or protected process — is ignored. DesktopShift runs without
/// elevation, so such windows will also reject every placement attempt; acting
/// on them is not available, and guessing their identity from a process name
/// alone would be worse than leaving them alone.
/// </para>
/// </remarks>
public static class WindowFloatClassifier
{
    /// <summary>
    /// Classifies one window against the configured rules.
    /// </summary>
    /// <param name="identity">
    /// The resolved window identity, or null when resolution failed.
    /// </param>
    /// <param name="floatRules">Configured float rules.</param>
    /// <param name="ignoreRules">Configured ignore rules.</param>
    public static TilingDisposition Classify(
        WindowIdentity? identity,
        ImmutableArray<TilingIdentityRule> floatRules,
        ImmutableArray<TilingIdentityRule> ignoreRules)
    {
        if (identity is null)
        {
            return TilingDisposition.Ignore;
        }

        // Ignore first, deliberately: see the precedence remark above.
        if (MatchesAny(BuiltInTilingRules.IgnoreRules, identity) ||
            MatchesAny(ignoreRules, identity))
        {
            return TilingDisposition.Ignore;
        }

        if (MatchesAny(BuiltInTilingRules.FloatRules, identity) ||
            MatchesAny(floatRules, identity))
        {
            return TilingDisposition.Float;
        }

        return TilingDisposition.Tile;
    }

    private static bool MatchesAny(
        ImmutableArray<TilingIdentityRule> rules,
        WindowIdentity identity)
    {
        foreach (TilingIdentityRule rule in rules)
        {
            if (rule.IsEnabled && Matches(rule, identity))
            {
                return true;
            }
        }

        return false;
    }

    private static bool Matches(TilingIdentityRule rule, WindowIdentity identity)
    {
        bool processMatched = Contains(rule.ProcessNames, identity.ProcessName);
        bool packageMatched = Contains(
            rule.PackageFamilyNames,
            identity.PackageFamilyName);
        bool appIdMatched = Contains(rule.AppUserModelIds, identity.AppUserModelId);
        bool pathMatched = Contains(rule.ExecutablePaths, identity.ExecutablePath);

        bool identityMatched =
            processMatched || packageMatched || appIdMatched || pathMatched;
        if (!identityMatched)
        {
            return false;
        }

        // A class names a shape inside an application, so it can refine but
        // never carry a match by itself — same reasoning as Application Rules.
        if (rule.WindowClasses.Length > 0 &&
            !Contains(rule.WindowClasses, identity.WindowClass))
        {
            return false;
        }

        return true;
    }

    private static bool Contains(ImmutableArray<string> values, string? candidate)
    {
        if (candidate is null)
        {
            return false;
        }

        foreach (string value in values)
        {
            if (string.Equals(value, candidate, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }
}

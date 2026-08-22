using System.Collections.Immutable;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Tiling;

namespace DesktopShift.App.ViewModels;

/// <summary>What the tiler does with a listed application.</summary>
public enum TilingExceptionDisposition
{
    /// <summary>Observe the window but leave its size and position unchanged.</summary>
    Float,

    /// <summary>Do not track or place the window.</summary>
    Ignore,
}

/// <summary>One tiling exception shown in Settings.</summary>
public sealed record TilingExceptionPresentation(
    string Id,
    string DisplayName,
    TilingExceptionDisposition Disposition,
    string IdentitySummary,
    bool IsBuiltIn)
{
    /// <summary>The user-facing action name.</summary>
    public string DispositionLabel =>
        Disposition == TilingExceptionDisposition.Float ? "Float" : "Ignore";

    /// <summary>Whether the Settings page can delete this rule.</summary>
    public bool CanRemove => !IsBuiltIn;

    /// <summary>Explains whether the rule is fixed or user-defined.</summary>
    public string PolicySummary => IsBuiltIn
        ? $"Built in • {DispositionLabel}"
        : $"User rule • {DispositionLabel}";
}

/// <summary>
/// Converts tiling exception settings to UI items and applies picker edits.
/// </summary>
public static class TilingExceptionEditor
{
    /// <summary>Shows fixed policies first, followed by user rules.</summary>
    public static IReadOnlyList<TilingExceptionPresentation> Present(
        TilingSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);

        List<TilingExceptionPresentation> items = [];
        AddPresentations(
            items,
            BuiltInTilingRules.FloatRules,
            TilingExceptionDisposition.Float,
            isBuiltIn: true);
        AddPresentations(
            items,
            BuiltInTilingRules.IgnoreRules,
            TilingExceptionDisposition.Ignore,
            isBuiltIn: true);
        AddPresentations(
            items,
            settings.FloatRules,
            TilingExceptionDisposition.Float,
            isBuiltIn: false);
        AddPresentations(
            items,
            settings.IgnoreRules,
            TilingExceptionDisposition.Ignore,
            isBuiltIn: false);
        return items;
    }

    /// <summary>Adds one running application as a float or ignore rule.</summary>
    public static TilingSettings Add(
        TilingSettings settings,
        RunningApplicationCandidate candidate,
        TilingExceptionDisposition disposition)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentNullException.ThrowIfNull(candidate);
        ArgumentException.ThrowIfNullOrWhiteSpace(candidate.ProcessName);

        ImmutableArray<TilingIdentityRule> destination =
            disposition == TilingExceptionDisposition.Float
                ? settings.FloatRules
                : settings.IgnoreRules;
        if (destination.Any(rule => MatchesApplication(rule, candidate)))
        {
            return settings;
        }

        string prefix = disposition == TilingExceptionDisposition.Float
            ? "float"
            : "ignore";
        string id = CreateUniqueId(settings, prefix, candidate.DisplayName);
        TilingIdentityRule rule = new(
            Id: id,
            DisplayName: candidate.DisplayName,
            IsEnabled: true,
            ProcessNames: [candidate.ProcessName],
            PackageFamilyNames: One(candidate.PackageFamilyName),
            AppUserModelIds: One(candidate.AppUserModelId));

        return disposition == TilingExceptionDisposition.Float
            ? settings with { FloatRules = settings.FloatRules.Add(rule) }
            : settings with { IgnoreRules = settings.IgnoreRules.Add(rule) };
    }

    /// <summary>Removes one user rule by its stable identifier.</summary>
    public static TilingSettings Remove(TilingSettings settings, string id)
    {
        ArgumentNullException.ThrowIfNull(settings);
        ArgumentException.ThrowIfNullOrWhiteSpace(id);

        return settings with
        {
            FloatRules =
            [.. settings.FloatRules.Where(
                rule => !string.Equals(
                    rule.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase))],
            IgnoreRules =
            [.. settings.IgnoreRules.Where(
                rule => !string.Equals(
                    rule.Id,
                    id,
                    StringComparison.OrdinalIgnoreCase))],
        };
    }

    private static void AddPresentations(
        List<TilingExceptionPresentation> items,
        ImmutableArray<TilingIdentityRule> rules,
        TilingExceptionDisposition disposition,
        bool isBuiltIn)
    {
        foreach (TilingIdentityRule rule in rules)
        {
            items.Add(new TilingExceptionPresentation(
                rule.Id,
                rule.DisplayName,
                disposition,
                FormatIdentity(rule),
                isBuiltIn));
        }
    }

    private static string FormatIdentity(TilingIdentityRule rule)
    {
        List<string> parts = [];
        AddValues(parts, "Package", rule.PackageFamilyNames);
        AddValues(parts, "App ID", rule.AppUserModelIds);
        AddValues(parts, "Process", rule.ProcessNames);
        AddValues(parts, "Window class", rule.WindowClasses);
        AddValues(parts, "Path", rule.ExecutablePaths);
        return string.Join(" • ", parts);
    }

    private static void AddValues(
        List<string> parts,
        string label,
        ImmutableArray<string> values)
    {
        if (values.Length > 0)
        {
            parts.Add($"{label}: {string.Join(", ", values)}");
        }
    }

    private static bool MatchesApplication(
        TilingIdentityRule rule,
        RunningApplicationCandidate candidate) =>
        Contains(rule.ProcessNames, candidate.ProcessName) ||
        Contains(rule.PackageFamilyNames, candidate.PackageFamilyName) ||
        Contains(rule.AppUserModelIds, candidate.AppUserModelId);

    private static bool Contains(
        ImmutableArray<string> values,
        string? candidate) =>
        !string.IsNullOrWhiteSpace(candidate) &&
        values.Any(value => string.Equals(
            value,
            candidate,
            StringComparison.OrdinalIgnoreCase));

    private static ImmutableArray<string> One(string? value) =>
        string.IsNullOrWhiteSpace(value) ? [] : [value];

    private static string CreateUniqueId(
        TilingSettings settings,
        string prefix,
        string displayName)
    {
        string name = Path.GetFileNameWithoutExtension(displayName);
        string slug = string.Join(
            '-',
            name.ToLowerInvariant()
                .Split(
                    [.. name.Where(character => !char.IsLetterOrDigit(character))
                        .Distinct()],
                    StringSplitOptions.RemoveEmptyEntries));
        if (string.IsNullOrWhiteSpace(slug))
        {
            slug = "application";
        }

        HashSet<string> ids = settings.FloatRules
            .Concat(settings.IgnoreRules)
            .Select(static rule => rule.Id)
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        string baseId = $"{prefix}-{slug}";
        string id = baseId;
        for (int suffix = 2; ids.Contains(id); suffix++)
        {
            id = $"{baseId}-{suffix}";
        }

        return id;
    }
}

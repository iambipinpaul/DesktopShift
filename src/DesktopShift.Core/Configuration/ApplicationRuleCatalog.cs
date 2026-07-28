using System.Collections.Immutable;

namespace DesktopShift.Core.Configuration;

/// <summary>
/// The complete life cycle of an Application Rule inside a configuration
/// document: add, replace, duplicate, enable, disable, and delete.
/// </summary>
/// <remarks>
/// <para>
/// Every operation returns a new document rather than mutating one, so the
/// candidate a user is editing and the active configuration the matcher is
/// running against never share state. Document order is the matcher's tie
/// breaker when two rules match with equal strength, so every operation states
/// exactly where the rule it produces lands.
/// </para>
/// <para>
/// Nothing here validates. A rejected document still has to be persisted as a
/// candidate so the entries a user typed survive the rejection, so validation is
/// the caller's separate step.
/// </para>
/// </remarks>
public static class ApplicationRuleCatalog
{
    private const string DuplicateIdSuffix = "copy";

    /// <summary>
    /// Appends a rule to the end of the document.
    /// </summary>
    /// <param name="document">The document to add to.</param>
    /// <param name="rule">The rule to add.</param>
    /// <returns>A document whose last rule is <paramref name="rule"/>.</returns>
    public static ConfigurationDocument Add(
        ConfigurationDocument document,
        ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rule);

        return document with
        {
            ApplicationRules = document.ApplicationRules.Add(rule),
        };
    }

    /// <summary>
    /// Replaces the rule carrying <paramref name="ruleId"/> in place, so an edit
    /// never changes which rule wins a tie.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="ruleId">The identifier of the rule being replaced.</param>
    /// <param name="rule">The replacement rule.</param>
    /// <returns>
    /// A document with the replacement at the original position, or the
    /// unchanged document when no rule carries the identifier.
    /// </returns>
    public static ConfigurationDocument Replace(
        ConfigurationDocument document,
        string ruleId,
        ApplicationRule rule)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(rule);

        int index = IndexOf(document, ruleId);
        return index < 0
            ? document
            : document with
            {
                ApplicationRules = document.ApplicationRules.SetItem(index, rule),
            };
    }

    /// <summary>
    /// Removes the rule carrying <paramref name="ruleId"/>.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="ruleId">The identifier of the rule being removed.</param>
    /// <returns>
    /// A document without that rule, or the unchanged document when no rule
    /// carries the identifier.
    /// </returns>
    public static ConfigurationDocument Remove(
        ConfigurationDocument document,
        string ruleId)
    {
        ArgumentNullException.ThrowIfNull(document);

        int index = IndexOf(document, ruleId);
        return index < 0
            ? document
            : document with
            {
                ApplicationRules = document.ApplicationRules.RemoveAt(index),
            };
    }

    /// <summary>
    /// Turns a rule on or off without touching anything else about it, so a
    /// disabled rule keeps every identity it declared and can be turned back on
    /// unchanged.
    /// </summary>
    /// <param name="document">The document to edit.</param>
    /// <param name="ruleId">The identifier of the rule being switched.</param>
    /// <param name="isEnabled">Whether the rule takes part in matching.</param>
    /// <returns>A document carrying the requested enabled state.</returns>
    public static ConfigurationDocument SetEnabled(
        ConfigurationDocument document,
        string ruleId,
        bool isEnabled)
    {
        ArgumentNullException.ThrowIfNull(document);

        int index = IndexOf(document, ruleId);
        return index < 0
            ? document
            : document with
            {
                ApplicationRules = document.ApplicationRules.SetItem(
                    index,
                    document.ApplicationRules[index] with
                    {
                        IsEnabled = isEnabled,
                    }),
            };
    }

    /// <summary>
    /// Copies a rule and places the copy directly after the original.
    /// </summary>
    /// <remarks>
    /// The copy is created disabled. A duplicate exists to be narrowed — a
    /// second browser profile, a second installation — and an enabled copy would
    /// claim the original's windows from the moment it is created, before the
    /// user has changed anything about it.
    /// </remarks>
    /// <param name="document">The document to edit.</param>
    /// <param name="ruleId">The identifier of the rule being copied.</param>
    /// <returns>
    /// The edited document and the copy, or the unchanged document and
    /// <see langword="null"/> when no rule carries the identifier.
    /// </returns>
    public static (ConfigurationDocument Document, ApplicationRule? Duplicate)
        Duplicate(ConfigurationDocument document, string ruleId)
    {
        ArgumentNullException.ThrowIfNull(document);

        int index = IndexOf(document, ruleId);
        if (index < 0)
        {
            return (document, null);
        }

        ApplicationRule original = document.ApplicationRules[index];
        ApplicationRule duplicate = original with
        {
            Id = CreateUniqueId(document, $"{original.Id}-{DuplicateIdSuffix}"),
            DisplayName = $"{original.DisplayName} (copy)",
            IsEnabled = false,
        };

        return (
            document with
            {
                ApplicationRules =
                    document.ApplicationRules.Insert(index + 1, duplicate),
            },
            duplicate);
    }

    /// <summary>
    /// Produces an identifier no rule in the document already uses.
    /// </summary>
    /// <param name="document">The document the identifier has to be unique in.</param>
    /// <param name="seed">The preferred identifier.</param>
    /// <returns>
    /// <paramref name="seed"/> when it is free, otherwise the seed with the
    /// lowest free numeric suffix.
    /// </returns>
    public static string CreateUniqueId(
        ConfigurationDocument document,
        string seed)
    {
        ArgumentNullException.ThrowIfNull(document);

        string candidate = string.IsNullOrWhiteSpace(seed)
            ? "rule"
            : seed.Trim();
        if (!ContainsId(document, candidate))
        {
            return candidate;
        }

        for (int suffix = 2; ; suffix++)
        {
            string numbered = $"{candidate}-{suffix}";
            if (!ContainsId(document, numbered))
            {
                return numbered;
            }
        }
    }

    /// <summary>
    /// Finds a rule by identifier, comparing the way the duplicate test does.
    /// </summary>
    /// <param name="document">The document to search.</param>
    /// <param name="ruleId">The identifier to find.</param>
    /// <returns>The rule, or <see langword="null"/> when there is none.</returns>
    public static ApplicationRule? Find(
        ConfigurationDocument document,
        string ruleId)
    {
        ArgumentNullException.ThrowIfNull(document);

        int index = IndexOf(document, ruleId);
        return index < 0 ? null : document.ApplicationRules[index];
    }

    private static bool ContainsId(
        ConfigurationDocument document,
        string ruleId) =>
        IndexOf(document, ruleId) >= 0;

    private static int IndexOf(ConfigurationDocument document, string ruleId)
    {
        if (string.IsNullOrWhiteSpace(ruleId))
        {
            return -1;
        }

        ImmutableArray<ApplicationRule> rules = document.ApplicationRules;
        for (int index = 0; index < rules.Length; index++)
        {
            if (string.Equals(
                rules[index].Id,
                ruleId,
                StringComparison.OrdinalIgnoreCase))
            {
                return index;
            }
        }

        return -1;
    }
}

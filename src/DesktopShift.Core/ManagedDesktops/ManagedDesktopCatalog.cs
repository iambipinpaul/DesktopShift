using System.Collections.Immutable;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.Configuration;

namespace DesktopShift.Core.ManagedDesktops;

/// <summary>
/// One Managed Desktop definition joined to everything the user needs to decide
/// what to do with it.
/// </summary>
/// <remarks>
/// The definition is the source of truth for the list. A definition with no
/// runtime mapping is still shown, because a user cannot fix a desktop they
/// cannot see; <see cref="Mapping"/> is null until a reconciliation pass has
/// produced one.
/// </remarks>
/// <param name="Definition">The configured semantic definition.</param>
/// <param name="Mapping">
/// The runtime mapping the last reconciliation produced, or null when that pass
/// did not cover this definition.
/// </param>
/// <param name="AssignedRuleCount">
/// How many Application Rules target this semantic key, or null when the rule
/// set is unknown.
/// </param>
/// <param name="EnabledRuleCount">
/// How many of <paramref name="AssignedRuleCount"/> are enabled, or null when
/// the rule set is unknown.
/// </param>
public sealed record ManagedDesktopCatalogEntry(
    ManagedDesktopDefinition Definition,
    ManagedDesktopRuntimeMapping? Mapping,
    int? AssignedRuleCount,
    int? EnabledRuleCount,
    bool CanMoveEarlier,
    bool CanMoveLater,
    bool CanRecreate,
    string RecreateUnavailableReason)
{
    public string SemanticKey => Definition.SemanticKey;

    public string DisplayName => Definition.DisplayName;

    public int PreferredOrder => Definition.PreferredOrder;

    public bool RecreateWhenMissing => Definition.RecreateWhenMissing;

    public ManagedDesktopMappingStatus? RuntimeStatus => Mapping?.Status;

    public Guid? RuntimeDesktopId => Mapping?.RuntimeDesktopId;

    public int? RuntimePosition => Mapping?.RuntimePosition;

    public bool IsBound => Mapping?.IsBound == true;

    public bool NeedsAttention => !IsBound;
}

/// <summary>
/// The whole Managed Desktops view: definitions, validation, and what the
/// selected provider can do about them.
/// </summary>
public sealed record ManagedDesktopCatalog(
    ImmutableArray<ManagedDesktopCatalogEntry> Entries,
    ImmutableArray<ManagedDesktopValidationMessage> Messages,
    ManagedDesktopMaintenanceAvailability Availability,
    ManagedDesktopReconciliationSnapshot Snapshot)
{
    public static ManagedDesktopCatalog Empty { get; } = new(
        [],
        [],
        ManagedDesktopMaintenanceAvailability.Evaluate(null),
        ManagedDesktopReconciliationSnapshot.NotRun);

    public int BoundCount => Entries.Count(static entry => entry.IsBound);

    public int AttentionCount => Entries.Count(static entry => entry.NeedsAttention);

    public string ProviderSummary => Snapshot.ProviderSummary;

    public string ObservedAt =>
        Snapshot.ObservedAtUtc == DateTimeOffset.MinValue
            ? "Not reconciled yet"
            : $"Updated {Snapshot.ObservedAtUtc.ToLocalTime():g}";

    public string Outcome => Snapshot.Outcome switch
    {
        ManagedDesktopReconciliationOutcome.Succeeded => "Mapped",
        ManagedDesktopReconciliationOutcome.Limited => "Limited",
        ManagedDesktopReconciliationOutcome.Partial => "Needs attention",
        ManagedDesktopReconciliationOutcome.Failed => "Failed",
        _ => Snapshot.Outcome.ToString(),
    };

    public string Summary
    {
        get
        {
            if (Entries.IsEmpty)
            {
                return "No Managed Desktops are defined. Add one to give DesktopShift somewhere to send windows.";
            }

            string managed = Entries.Length == 1
                ? "1 managed desktop"
                : $"{Entries.Length} managed desktops";
            string mapped = $"{managed}, {BoundCount} mapped to a Windows desktop";
            return AttentionCount == 0
                ? $"{mapped}."
                : $"{mapped}, {AttentionCount} needing attention.";
        }
    }
}

/// <summary>
/// Builds the Managed Desktops view from configuration, the last reconciliation
/// pass, and the provider's capabilities.
/// </summary>
public static class ManagedDesktopCatalogProjection
{
    public static ManagedDesktopCatalog Project(
        ConfigurationDocument? configuration,
        ManagedDesktopReconciliationSnapshot snapshot,
        ImmutableArray<ConfigurationValidationIssue> configurationIssues,
        DesktopTopologyProviderState? providerState)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        ManagedDesktopMaintenanceAvailability availability =
            ManagedDesktopMaintenanceAvailability.Evaluate(providerState);
        Dictionary<string, ManagedDesktopRuntimeMapping> mappingsByKey =
            BuildMappingIndex(snapshot);
        IReadOnlyList<ManagedDesktopDefinition> definitions =
            ResolveDefinitions(configuration, snapshot);
        Dictionary<string, RuleCounts> ruleCounts = BuildRuleCounts(configuration);

        ImmutableArray<ManagedDesktopCatalogEntry>.Builder entries =
            ImmutableArray.CreateBuilder<ManagedDesktopCatalogEntry>(definitions.Count);

        for (int index = 0; index < definitions.Count; index++)
        {
            ManagedDesktopDefinition definition = definitions[index];
            _ = mappingsByKey.TryGetValue(
                definition.SemanticKey,
                out ManagedDesktopRuntimeMapping? mapping);
            RuleCounts counts = configuration is null
                ? RuleCounts.Unknown
                : ruleCounts.GetValueOrDefault(definition.SemanticKey, RuleCounts.None);

            entries.Add(new ManagedDesktopCatalogEntry(
                definition,
                mapping,
                counts.Total,
                counts.Enabled,
                CanMoveEarlier: index > 0,
                CanMoveLater: index < definitions.Count - 1,
                CanRecreate: availability.CanRecreate && mapping?.IsBound != true,
                RecreateUnavailableReason: ResolveRecreateReason(availability, mapping)));
        }

        return new ManagedDesktopCatalog(
            entries.ToImmutable(),
            ManagedDesktopValidationProjection.Project(
                configurationIssues,
                snapshot,
                availability),
            availability,
            snapshot);
    }

    private static string ResolveRecreateReason(
        ManagedDesktopMaintenanceAvailability availability,
        ManagedDesktopRuntimeMapping? mapping)
    {
        if (!availability.CanRecreate)
        {
            return availability.RecreateUnavailableReason;
        }

        return mapping?.IsBound == true
            ? "This Managed Desktop is already mapped to a Windows desktop."
            : string.Empty;
    }

    /// <summary>
    /// The definitions to show, newest configuration first.
    /// </summary>
    /// <remarks>
    /// When no configuration is available the snapshot's mappings are the only
    /// record of what was configured, so they are projected back into
    /// definitions. That keeps the list populated in a read-only shell that has
    /// a reconciliation snapshot but no configuration service.
    /// </remarks>
    private static IReadOnlyList<ManagedDesktopDefinition> ResolveDefinitions(
        ConfigurationDocument? configuration,
        ManagedDesktopReconciliationSnapshot snapshot)
    {
        IEnumerable<ManagedDesktopDefinition> source = configuration is null
            ? snapshot.Mappings.Select(static mapping => new ManagedDesktopDefinition(
                mapping.SemanticKey,
                mapping.DisplayName,
                mapping.PreferredOrder,
                mapping.RecreateWhenMissing))
            : configuration.ManagedDesktops;

        return [.. source
            .OrderBy(static definition => definition.PreferredOrder)
            .ThenBy(
                static definition => definition.SemanticKey,
                StringComparer.OrdinalIgnoreCase)];
    }

    private static Dictionary<string, ManagedDesktopRuntimeMapping> BuildMappingIndex(
        ManagedDesktopReconciliationSnapshot snapshot)
    {
        Dictionary<string, ManagedDesktopRuntimeMapping> index =
            new(StringComparer.OrdinalIgnoreCase);
        foreach (ManagedDesktopRuntimeMapping mapping in snapshot.Mappings)
        {
            index[mapping.SemanticKey] = mapping;
        }

        return index;
    }

    private static Dictionary<string, RuleCounts> BuildRuleCounts(
        ConfigurationDocument? configuration)
    {
        Dictionary<string, RuleCounts> counts =
            new(StringComparer.OrdinalIgnoreCase);
        if (configuration is null)
        {
            return counts;
        }

        foreach (ApplicationRule rule in configuration.ApplicationRules)
        {
            if (!rule.MovesWindows)
            {
                // A rule that never moves a window names no desktop, even when
                // it still stores a key, so counting it here would tell a user a
                // desktop is where an application is sent when nothing sends it
                // there.
                continue;
            }

            RuleCounts current = counts.GetValueOrDefault(
                rule.TargetDesktopKey,
                RuleCounts.None);
            counts[rule.TargetDesktopKey] = new RuleCounts(
                current.Total + 1,
                current.Enabled + (rule.IsEnabled ? 1 : 0));
        }

        return counts;
    }

    private readonly record struct RuleCounts(int? Total, int? Enabled)
    {
        public static RuleCounts None { get; } = new(0, 0);

        public static RuleCounts Unknown { get; } = new(null, null);
    }
}

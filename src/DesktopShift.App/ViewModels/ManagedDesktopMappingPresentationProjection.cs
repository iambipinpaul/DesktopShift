using DesktopShift.Core.Configuration;
using DesktopShift.Core.ManagedDesktops;

namespace DesktopShift.App.ViewModels;

public static class ManagedDesktopMappingPresentationProjection
{
    public static ManagedDesktopMappingPresentationSnapshot Project(
        ManagedDesktopReconciliationSnapshot snapshot,
        ConfigurationDocument? configuration)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        // Only a rule that moves a window targets a Managed Desktop. An
        // Anywhere rule and a Show on all desktops rule both store a key they
        // never read, so counting either here would tell a user a desktop is
        // where an application is sent when nothing sends it there.
        IReadOnlyDictionary<string, RuleCounts>? ruleCounts =
            configuration?.ApplicationRules
                .Where(static rule =>
                    !rule.AllowsAnywhere && !rule.ShowsOnAllDesktops)
                .GroupBy(rule => rule.TargetDesktopKey, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => new RuleCounts(
                        group.Count(),
                        group.Count(rule => rule.IsEnabled)),
                    StringComparer.OrdinalIgnoreCase);

        IReadOnlyList<ManagedDesktopMappingItemPresentation> mappings = snapshot.Mappings
            .OrderBy(mapping => mapping.PreferredOrder)
            .ThenBy(mapping => mapping.SemanticKey, StringComparer.OrdinalIgnoreCase)
            .Select(mapping => ProjectMapping(mapping, ruleCounts))
            .ToArray();
        int mappedCount = mappings.Count(mapping => mapping.IsMapped);
        int attentionCount = mappings.Count(mapping => mapping.NeedsAttention);

        return new ManagedDesktopMappingPresentationSnapshot(
            snapshot.ObservedAtUtc,
            FormatOutcome(snapshot.Outcome),
            FormatSummary(snapshot, mappedCount, attentionCount),
            $"{snapshot.ProviderSummary} • {FormatTrigger(snapshot.Trigger)}",
            mappings,
            snapshot.Issues
                .Select(issue => $"{issue.Message} ({issue.Code})")
                .ToArray());
    }

    private static ManagedDesktopMappingItemPresentation ProjectMapping(
        ManagedDesktopRuntimeMapping mapping,
        IReadOnlyDictionary<string, RuleCounts>? ruleCounts)
    {
        (string status, string glyph) = FormatStatus(mapping.Status);
        string runtimeSummary = FormatRuntimeSummary(mapping);
        RuleCounts? counts = ruleCounts is null
            ? null
            : ruleCounts.GetValueOrDefault(mapping.SemanticKey, new RuleCounts(0, 0));
        string ruleSummary = FormatRuleSummary(counts?.Total, counts?.Enabled);
        string recreationPolicy = FormatRecreationPolicy(mapping.RecreateWhenMissing);

        return new ManagedDesktopMappingItemPresentation(
            mapping.PreferredOrder,
            mapping.DisplayName,
            mapping.SemanticKey,
            status,
            glyph,
            runtimeSummary,
            mapping.Explanation,
            ruleSummary,
            recreationPolicy,
            FormatAdvancedDetails(mapping),
            mapping.IsBound,
            !mapping.IsBound);
    }

    private static string FormatSummary(
        ManagedDesktopReconciliationSnapshot snapshot,
        int mappedCount,
        int attentionCount)
    {
        if (snapshot.Mappings.IsEmpty)
        {
            return snapshot.Issues.IsEmpty
                ? "No managed destinations were included in this reconciliation."
                : snapshot.Issues[0].Message;
        }

        string mapped = mappedCount == 1
            ? "1 managed destination is mapped"
            : $"{mappedCount} managed destinations are mapped";
        if (attentionCount == 0)
        {
            return $"{mapped}.";
        }

        string attention = attentionCount == 1
            ? "1 needs attention"
            : $"{attentionCount} need attention";
        return $"{mapped}; {attention}.";
    }

    public static string FormatRuntimeSummary(ManagedDesktopRuntimeMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        if (!mapping.IsBound)
        {
            return mapping.Status == ManagedDesktopMappingStatus.Ambiguous
                ? "No runtime desktop was selected because multiple candidates matched."
                : "No runtime desktop is currently bound.";
        }

        string runtimeName = string.IsNullOrWhiteSpace(mapping.RuntimeDisplayName)
            ? mapping.RuntimePosition is int position
                ? $"Desktop {position + 1}"
                : "Windows desktop"
            : mapping.RuntimeDisplayName.Trim();

        return mapping.RuntimePosition is int runtimePosition
            ? $"Mapped to {runtimeName} at position {runtimePosition + 1}."
            : $"Mapped to {runtimeName}.";
    }

    /// <summary>
    /// Describes how many Application Rules target a Managed Desktop.
    /// </summary>
    /// <remarks>
    /// A null count means the rule set is unknown, which is not the same as
    /// knowing there are none.
    /// </remarks>
    public static string FormatRuleSummary(int? total, int? enabled)
    {
        if (total is not int ruleCount)
        {
            return "Rule count unavailable";
        }

        if (ruleCount == 0)
        {
            return "No application rules";
        }

        string ruleLabel = ruleCount == 1
            ? "1 application rule"
            : $"{ruleCount} application rules";
        return enabled == ruleCount
            ? ruleLabel
            : $"{ruleLabel} ({enabled} enabled)";
    }

    public static string FormatRecreationPolicy(bool recreateWhenMissing) =>
        recreateWhenMissing
            ? "Recreate when missing"
            : "Do not recreate when missing";

    public static string FormatAdvancedDetails(ManagedDesktopRuntimeMapping mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        List<string> lines = [$"Diagnostic: {mapping.Code}"];
        if (mapping.RuntimeDesktopId is Guid runtimeId)
        {
            lines.Add($"Runtime desktop ID: {runtimeId:D}");
        }

        if (!mapping.CandidateDesktopIds.IsEmpty)
        {
            lines.Add("Candidate desktop IDs:");
            lines.AddRange(mapping.CandidateDesktopIds.Select(candidate => $"  {candidate:D}"));
        }

        return string.Join(Environment.NewLine, lines);
    }

    public static (string Label, string Glyph) FormatStatus(
        ManagedDesktopMappingStatus status) =>
        status switch
        {
            ManagedDesktopMappingStatus.ReusedPersistedBinding =>
                ("Mapped", "\uE73E"),
            ManagedDesktopMappingStatus.ReusedCompatibleDesktop =>
                ("Matched", "\uE8FB"),
            ManagedDesktopMappingStatus.Created =>
                ("Created", "\uE710"),
            ManagedDesktopMappingStatus.Missing =>
                ("Missing", "\uE711"),
            ManagedDesktopMappingStatus.Ambiguous =>
                ("Ambiguous", "\uE9CE"),
            ManagedDesktopMappingStatus.Limited =>
                ("Limited", "\uE946"),
            ManagedDesktopMappingStatus.Failed =>
                ("Failed", "\uEA39"),
            _ => (status.ToString(), "\uE946"),
        };

    private static string FormatOutcome(ManagedDesktopReconciliationOutcome outcome) =>
        outcome switch
        {
            ManagedDesktopReconciliationOutcome.Succeeded => "Mapped",
            ManagedDesktopReconciliationOutcome.Limited => "Limited",
            ManagedDesktopReconciliationOutcome.Partial => "Needs attention",
            ManagedDesktopReconciliationOutcome.Failed => "Failed",
            _ => outcome.ToString(),
        };

    private static string FormatTrigger(ManagedDesktopReconciliationTrigger trigger) =>
        trigger switch
        {
            ManagedDesktopReconciliationTrigger.Startup => "Startup reconciliation",
            ManagedDesktopReconciliationTrigger.CompatibilityChanged =>
                "Compatibility refresh",
            ManagedDesktopReconciliationTrigger.ConfigurationAccepted =>
                "Configuration refresh",
            ManagedDesktopReconciliationTrigger.TopologyChanged => "Topology refresh",
            ManagedDesktopReconciliationTrigger.Manual => "Manual refresh",
            _ => trigger.ToString(),
        };

    private readonly record struct RuleCounts(int Total, int Enabled);
}

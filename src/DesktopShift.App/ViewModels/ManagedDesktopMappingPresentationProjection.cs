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

        IReadOnlyDictionary<string, RuleCounts>? ruleCounts =
            configuration?.ApplicationRules
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
        string ruleSummary = FormatRuleSummary(mapping.SemanticKey, ruleCounts);
        string recreationPolicy = mapping.RecreateWhenMissing
            ? "Recreate when missing"
            : "Do not recreate when missing";

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

    private static string FormatRuntimeSummary(ManagedDesktopRuntimeMapping mapping)
    {
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

    private static string FormatRuleSummary(
        string semanticKey,
        IReadOnlyDictionary<string, RuleCounts>? ruleCounts)
    {
        if (ruleCounts is null)
        {
            return "Rule count unavailable";
        }

        if (!ruleCounts.TryGetValue(semanticKey, out RuleCounts? counts) ||
            counts is null ||
            counts.Total == 0)
        {
            return "No application rules";
        }

        string ruleLabel = counts.Total == 1
            ? "1 application rule"
            : $"{counts.Total} application rules";
        return counts.Enabled == counts.Total
            ? ruleLabel
            : $"{ruleLabel} ({counts.Enabled} enabled)";
    }

    private static string FormatAdvancedDetails(ManagedDesktopRuntimeMapping mapping)
    {
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

    private static (string Label, string Glyph) FormatStatus(
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

    private sealed record RuleCounts(int Total, int Enabled);
}

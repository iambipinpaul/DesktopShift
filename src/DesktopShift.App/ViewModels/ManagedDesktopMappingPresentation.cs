namespace DesktopShift.App.ViewModels;

public sealed record ManagedDesktopMappingItemPresentation(
    int PreferredOrder,
    string DisplayName,
    string SemanticKey,
    string Status,
    string StatusGlyph,
    string RuntimeSummary,
    string Explanation,
    string RuleSummary,
    string RecreationPolicy,
    string AdvancedDetails,
    bool IsMapped,
    bool NeedsAttention)
{
    public string Order => PreferredOrder.ToString(
        System.Globalization.CultureInfo.CurrentCulture);

    public string SemanticKeyLabel => $"Semantic key: {SemanticKey}";

    public string AutomationName =>
        $"{DisplayName}, managed desktop {SemanticKey}, preferred order {PreferredOrder}, {Status}. {RuntimeSummary}";
}

public sealed record ManagedDesktopMappingPresentationSnapshot(
    DateTimeOffset ObservedAtUtc,
    string Outcome,
    string Summary,
    string ProviderSummary,
    IReadOnlyList<ManagedDesktopMappingItemPresentation> Mappings,
    IReadOnlyList<string> Issues)
{
    public int MappedCount => Mappings.Count(mapping => mapping.IsMapped);

    public int AttentionCount => Mappings.Count(mapping => mapping.NeedsAttention);

    public string ObservedAt =>
        ObservedAtUtc == DateTimeOffset.MinValue
            ? "Not reconciled yet"
            : $"Updated {ObservedAtUtc.ToLocalTime():g}";

    public string IssueSummary =>
        Issues.Count == 0
            ? string.Empty
            : string.Join(Environment.NewLine, Issues);
}

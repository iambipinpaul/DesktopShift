using DesktopShift.App.ViewModels;
using DesktopShift.Core.ManagedDesktops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Desktops;

/// <summary>
/// One Managed Desktop card on the Desktops page.
/// </summary>
/// <remarks>
/// Everything here is already decided: the strings are formatted and the
/// commands are enabled or disabled. The page binds to it and does no thinking
/// of its own, and the decisions themselves stay in
/// <see cref="ManagedDesktopCatalogProjection"/> where they can be tested.
/// </remarks>
public sealed record ManagedDesktopCatalogItem(
    string SemanticKey,
    string Order,
    string DisplayName,
    string SemanticKeyLabel,
    string Status,
    string StatusGlyph,
    string RuntimeSummary,
    string Explanation,
    string RuleSummary,
    string RecreationPolicy,
    string RecreationToggleLabel,
    string AdvancedDetails,
    string AutomationName,
    bool RecreateWhenMissing,
    bool CanEdit,
    bool CanMoveEarlier,
    bool CanMoveLater,
    bool CanRecreate,
    string RecreateToolTip)
{
    public Visibility EditVisibility =>
        CanEdit ? Visibility.Visible : Visibility.Collapsed;

    public static ManagedDesktopCatalogItem FromEntry(ManagedDesktopCatalogEntry entry)
    {
        ArgumentNullException.ThrowIfNull(entry);

        (string status, string glyph) = entry.Mapping is null
            ? ("Not reconciled", "\uE895")
            : ManagedDesktopMappingPresentationProjection.FormatStatus(
                entry.Mapping.Status);
        string runtimeSummary = entry.Mapping is null
            ? "This managed destination has not been reconciled yet."
            : ManagedDesktopMappingPresentationProjection.FormatRuntimeSummary(
                entry.Mapping);
        string explanation = entry.Mapping?.Explanation ??
            "Reconcile all to map this managed destination to a Windows desktop.";
        string advancedDetails = entry.Mapping is null
            ? "Diagnostic: managed_desktops.not_reconciled"
            : ManagedDesktopMappingPresentationProjection.FormatAdvancedDetails(
                entry.Mapping);

        return new ManagedDesktopCatalogItem(
            entry.SemanticKey,
            entry.PreferredOrder.ToString(
                System.Globalization.CultureInfo.CurrentCulture),
            entry.DisplayName,
            $"Semantic key: {entry.SemanticKey}",
            status,
            glyph,
            runtimeSummary,
            explanation,
            ManagedDesktopMappingPresentationProjection.FormatRuleSummary(
                entry.AssignedRuleCount,
                entry.EnabledRuleCount),
            ManagedDesktopMappingPresentationProjection.FormatRecreationPolicy(
                entry.RecreateWhenMissing),
            entry.RecreateWhenMissing
                ? "Stop recreating when missing"
                : "Recreate when missing",
            advancedDetails,
            $"{entry.DisplayName}, managed desktop {entry.SemanticKey}, preferred position {entry.PreferredOrder}, {status}. {runtimeSummary}",
            entry.RecreateWhenMissing,
            CanEdit: true,
            entry.CanMoveEarlier,
            entry.CanMoveLater,
            entry.CanRecreate,
            entry.CanRecreate
                ? $"Create a Windows desktop for {entry.DisplayName}"
                : entry.RecreateUnavailableReason);
    }

    /// <summary>
    /// Builds a read-only card from a mapping snapshot alone.
    /// </summary>
    /// <remarks>
    /// Used when the page has a reconciliation snapshot but no maintenance
    /// service, so the desktops are still listed and simply cannot be edited.
    /// </remarks>
    public static ManagedDesktopCatalogItem FromMapping(
        ManagedDesktopMappingItemPresentation mapping)
    {
        ArgumentNullException.ThrowIfNull(mapping);

        return new ManagedDesktopCatalogItem(
            mapping.SemanticKey,
            mapping.Order,
            mapping.DisplayName,
            mapping.SemanticKeyLabel,
            mapping.Status,
            mapping.StatusGlyph,
            mapping.RuntimeSummary,
            mapping.Explanation,
            mapping.RuleSummary,
            mapping.RecreationPolicy,
            RecreationToggleLabel: string.Empty,
            mapping.AdvancedDetails,
            mapping.AutomationName,
            RecreateWhenMissing: false,
            CanEdit: false,
            CanMoveEarlier: false,
            CanMoveLater: false,
            CanRecreate: false,
            RecreateToolTip: string.Empty);
    }
}

/// <summary>
/// One actionable Managed Desktop problem, shown as an information bar.
/// </summary>
public sealed record ManagedDesktopValidationItem(
    string Title,
    string Message,
    InfoBarSeverity Severity)
{
    public static ManagedDesktopValidationItem FromMessage(
        ManagedDesktopValidationMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);

        return new ManagedDesktopValidationItem(
            message.Title,
            message.FullText,
            message.Severity switch
            {
                ManagedDesktopValidationSeverity.Error => InfoBarSeverity.Error,
                ManagedDesktopValidationSeverity.Warning => InfoBarSeverity.Warning,
                _ => InfoBarSeverity.Informational,
            });
    }
}

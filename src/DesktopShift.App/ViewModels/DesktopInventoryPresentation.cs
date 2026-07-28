using Microsoft.UI.Xaml;

namespace DesktopShift.App.ViewModels;

public sealed record DesktopInventoryItemPresentation(
    string DisplayName,
    string Position,
    string DesktopId,
    bool IsCurrent)
{
    public string CurrentLabel => IsCurrent ? "Current desktop" : string.Empty;

    public Visibility CurrentVisibility =>
        IsCurrent ? Visibility.Visible : Visibility.Collapsed;

    public string AutomationName =>
        IsCurrent
            ? $"{DisplayName}, {Position}, current desktop"
            : $"{DisplayName}, {Position}";
}

public sealed record DesktopInventoryStatePresentation(
    string Title,
    string Message,
    string ProviderDetails,
    string Glyph,
    bool IsLoading = false);

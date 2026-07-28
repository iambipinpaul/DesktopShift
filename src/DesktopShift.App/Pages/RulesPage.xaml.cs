using DesktopShift.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class RulesPage : Page
{
    public RulesPage()
    {
        InitializeComponent();
    }

    public void Update(IReadOnlyList<RulePresentation> rules)
    {
        ArgumentNullException.ThrowIfNull(rules);

        RuleList.ItemsSource = rules;
        RuleList.Visibility = rules.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        EmptyState.Visibility = rules.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
    }
}

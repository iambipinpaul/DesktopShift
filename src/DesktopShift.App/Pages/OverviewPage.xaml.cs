using DesktopShift.App.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class OverviewPage : Page
{
    private ManagedDesktopMappingPresentationSnapshot? _mappingSnapshot;
    private int _mappingUpdateVersion;

    public OverviewPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    public void Update(OverviewPresentation presentation)
    {
        ArgumentNullException.ThrowIfNull(presentation);

        EnabledRuleCount.Text = presentation.EnabledRuleCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        ManagedDesktopCount.Text = presentation.ManagedDesktopCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        WorkspaceSummary.Text = presentation.IsFirstRunComplete
            ? "Your reviewed configuration is saved. DesktopShift will only act within the capabilities shown below."
            : "Setup has not been completed. DesktopShift will not move any windows until you review and apply the starter mappings.";
        ModeStatus.Text = presentation.Compatibility.Mode;
        ProviderName.Text = presentation.Compatibility.Provider;
        BuildInfo.Text =
            $"{presentation.Compatibility.Build} • {presentation.Compatibility.BuildSupport}";
        TestOutcome.Text = presentation.Compatibility.TestOutcome;
        CompatibilityExplanation.Text = presentation.Compatibility.Explanation;
        CapabilitySummary.Text = presentation.Compatibility.Capabilities;
    }

    public void UpdateMappings(ManagedDesktopMappingPresentationSnapshot snapshot)
    {
        ArgumentNullException.ThrowIfNull(snapshot);

        int version = Interlocked.Increment(ref _mappingUpdateVersion);
        Volatile.Write(ref _mappingSnapshot, snapshot);

        if (DispatcherQueue.HasThreadAccess)
        {
            if (IsLoaded && version == Volatile.Read(ref _mappingUpdateVersion))
            {
                ApplyMappingSnapshot(snapshot);
            }

            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && version == Volatile.Read(ref _mappingUpdateVersion))
            {
                ApplyMappingSnapshot(snapshot);
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        ManagedDesktopMappingPresentationSnapshot? snapshot =
            Volatile.Read(ref _mappingSnapshot);
        if (snapshot is not null)
        {
            ApplyMappingSnapshot(snapshot);
        }
    }

    private void ApplyMappingSnapshot(ManagedDesktopMappingPresentationSnapshot snapshot)
    {
        ManagedMappingSummary.Text = snapshot.Summary;
        ManagedMappingOutcome.Text = snapshot.Outcome;
        MappedDesktopCount.Text = snapshot.MappedCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        MappingAttentionCount.Text = snapshot.AttentionCount.ToString(
            System.Globalization.CultureInfo.CurrentCulture);
        ManagedMappingProvider.Text =
            $"{snapshot.ProviderSummary} • {snapshot.ObservedAt}";
        ManagedMappingCard.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            ManagedMappingCard,
            $"Managed desktop mapping. {snapshot.Outcome}. {snapshot.Summary}");
    }
}

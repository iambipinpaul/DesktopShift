using DesktopShift.App.ViewModels;
using DesktopShift.Core.Compatibility;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class DesktopsPage : Page
{
    private IDesktopTopologyProvider? _provider;
    private DesktopTopologyProviderState? _providerState;
    private CancellationToken _windowCancellationToken;
    private CancellationTokenSource? _refreshCancellation;
    private ManagedDesktopMappingPresentationSnapshot? _mappingSnapshot;
    private int _mappingUpdateVersion;
    private int _refreshVersion;

    public DesktopsPage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Update(
        IDesktopTopologyProvider provider,
        DesktopTopologyProviderState providerState,
        CancellationToken windowCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(provider);
        ArgumentNullException.ThrowIfNull(providerState);

        if (!ReferenceEquals(_provider, provider))
        {
            UnsubscribeFromProvider();
            _provider = provider;

            if (IsLoaded)
            {
                SubscribeToProvider();
            }
        }

        _providerState = providerState;
        _windowCancellationToken = windowCancellationToken;

        if (IsLoaded)
        {
            StartRefresh();
        }
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
        SubscribeToProvider();
        StartRefresh();

        ManagedDesktopMappingPresentationSnapshot? snapshot =
            Volatile.Read(ref _mappingSnapshot);
        if (snapshot is not null)
        {
            ApplyMappingSnapshot(snapshot);
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        CancelRefresh();
        UnsubscribeFromProvider();
    }

    private void SubscribeToProvider()
    {
        if (_provider is not null)
        {
            _provider.TopologyChanged -= OnTopologyChanged;
            _provider.TopologyChanged += OnTopologyChanged;
        }
    }

    private void UnsubscribeFromProvider()
    {
        if (_provider is not null)
        {
            _provider.TopologyChanged -= OnTopologyChanged;
        }
    }

    private void OnTopologyChanged(object? sender, DesktopTopologyChangedEventArgs args)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            if (IsLoaded)
            {
                StartRefresh();
            }

            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded)
            {
                StartRefresh();
            }
        });
    }

    private void StartRefresh()
    {
        CancelRefresh();

        IDesktopTopologyProvider? provider = _provider;
        DesktopTopologyProviderState? providerState = _providerState;
        if (provider is null || providerState is null || _windowCancellationToken.IsCancellationRequested)
        {
            return;
        }

        int version = ++_refreshVersion;
        _refreshCancellation =
            CancellationTokenSource.CreateLinkedTokenSource(_windowCancellationToken);
        CancellationToken cancellationToken = _refreshCancellation.Token;

        if (providerState.Availability == DesktopTopologyProviderAvailability.NotTested)
        {
            ShowStatus(
                new DesktopInventoryStatePresentation(
                    "Waiting for provider validation",
                    "DesktopShift will load the runtime inventory after the Windows compatibility check completes.",
                    FormatProviderDetails(providerState),
                    "\uE895"));
            return;
        }

        if (providerState.Availability == DesktopTopologyProviderAvailability.Failed)
        {
            ShowStatus(
                new DesktopInventoryStatePresentation(
                    "Desktop provider unavailable",
                    providerState.Explanation,
                    FormatProviderDetails(providerState),
                    "\uEA39"));
            return;
        }

        if (!providerState.Capabilities.CanEnumerateDesktops)
        {
            string title = providerState.Identity.Mode == DesktopTopologyProviderMode.Limited
                ? "Limited Mode: desktop inventory unavailable"
                : "Desktop inventory is not supported";
            ShowStatus(
                new DesktopInventoryStatePresentation(
                    title,
                    "This provider cannot enumerate Windows virtual desktops. DesktopShift will not guess or show configured destinations as runtime desktops.",
                    FormatProviderDetails(providerState),
                    "\uE946"));
            return;
        }

        ShowStatus(
            new DesktopInventoryStatePresentation(
                "Loading Windows desktops",
                "Reading the current virtual desktop topology.",
                FormatProviderDetails(providerState),
                "\uE7F4",
                IsLoading: true));
        _ = RefreshAsync(provider, providerState, version, cancellationToken);
    }

    private async Task RefreshAsync(
        IDesktopTopologyProvider provider,
        DesktopTopologyProviderState providerState,
        int version,
        CancellationToken cancellationToken)
    {
        try
        {
            DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result =
                await provider.EnumerateDesktopsAsync(cancellationToken);

            if (cancellationToken.IsCancellationRequested || version != _refreshVersion)
            {
                return;
            }

            await RunOnUiThreadAsync(
                () => ApplyResult(result, providerState, version, cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            await RunOnUiThreadAsync(
                () =>
                {
                    if (CanApply(version, cancellationToken))
                    {
                        ShowStatus(
                            new DesktopInventoryStatePresentation(
                                "Couldn’t load Windows desktops",
                                "The provider returned an unexpected error while reading the desktop inventory.",
                                $"{FormatProviderDetails(providerState)} Error: {exception.Message}",
                                "\uEA39"));
                    }
                });
        }
    }

    private void ApplyResult(
        DesktopTopologyProviderResult<IReadOnlyList<VirtualDesktopDescriptor>> result,
        DesktopTopologyProviderState providerState,
        int version,
        CancellationToken cancellationToken)
    {
        if (!CanApply(version, cancellationToken))
        {
            return;
        }

        if (result.IsSuccess && result.Value is not null)
        {
            IReadOnlyList<DesktopInventoryItemPresentation> desktops = result.Value
                .OrderBy(desktop => desktop.Position)
                .Select(CreatePresentation)
                .ToArray();

            if (desktops.Count == 0)
            {
                ShowStatus(
                    new DesktopInventoryStatePresentation(
                        "No Windows desktops were reported",
                        "The provider completed successfully but returned an empty desktop inventory.",
                        FormatProviderDetails(providerState),
                        "\uE7F4"));
                return;
            }

            DesktopList.ItemsSource = desktops;
            InventoryCount.Text = desktops.Count == 1
                ? "1 desktop"
                : $"{desktops.Count} desktops";
            InventoryPanel.Visibility = Visibility.Visible;
            StatusCard.Visibility = Visibility.Collapsed;
            return;
        }

        if (result.Outcome == DesktopTopologyResultOutcome.Cancelled)
        {
            ShowStatus(
                new DesktopInventoryStatePresentation(
                    "Desktop refresh was cancelled",
                    "The provider did not complete the runtime inventory refresh.",
                    FormatProviderDetails(providerState),
                    "\uE895"));
            return;
        }

        string title = result.Outcome switch
        {
            DesktopTopologyResultOutcome.Unsupported => "Desktop inventory is not supported",
            DesktopTopologyResultOutcome.Unavailable => "Desktop provider unavailable",
            _ => "Couldn’t load Windows desktops",
        };
        string message = result.Error?.Message ??
            "The provider could not return the Windows desktop inventory.";
        string diagnostic = result.Error is null
            ? FormatProviderDetails(providerState)
            : $"{FormatProviderDetails(providerState)} Diagnostic: {result.Error.Code}.";

        ShowStatus(
            new DesktopInventoryStatePresentation(
                title,
                message,
                diagnostic,
                result.Outcome == DesktopTopologyResultOutcome.Unsupported ? "\uE946" : "\uEA39"));
    }

    private static DesktopInventoryItemPresentation CreatePresentation(
        VirtualDesktopDescriptor desktop)
    {
        int displayPosition = desktop.Position + 1;
        string displayName = string.IsNullOrWhiteSpace(desktop.DisplayName)
            ? $"Desktop {displayPosition}"
            : desktop.DisplayName.Trim();

        return new DesktopInventoryItemPresentation(
            displayName,
            $"Position {displayPosition}",
            desktop.Id.ToString("D"),
            desktop.IsCurrent);
    }

    private static string FormatProviderDetails(DesktopTopologyProviderState state) =>
        $"{state.Identity.DisplayName} • {state.Identity.Mode} Mode • {state.Availability}";

    private void ApplyMappingSnapshot(ManagedDesktopMappingPresentationSnapshot snapshot)
    {
        IReadOnlyList<ManagedDesktopMappingItemPresentation> mappings = snapshot.Mappings
            .OrderBy(mapping => mapping.PreferredOrder)
            .ThenBy(mapping => mapping.SemanticKey, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        ManagedMappingSummary.Text = snapshot.Summary;
        ManagedMappingProvider.Text = snapshot.ProviderSummary;
        ManagedMappingOutcome.Text = snapshot.Outcome;
        ManagedMappingObservedAt.Text = snapshot.ObservedAt;
        ManagedMappingList.ItemsSource = mappings;
        ManagedMappingList.Visibility =
            mappings.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ManagedMappingIssues.Message = snapshot.IssueSummary;
        ManagedMappingIssues.IsOpen = snapshot.Issues.Count > 0;
        ManagedMappingsPanel.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            ManagedMappingsPanel,
            $"Managed desktop mappings. {snapshot.Outcome}. {snapshot.Summary}");
    }

    private bool CanApply(int version, CancellationToken cancellationToken) =>
        IsLoaded &&
        version == _refreshVersion &&
        !cancellationToken.IsCancellationRequested &&
        !_windowCancellationToken.IsCancellationRequested;

    private void ShowStatus(DesktopInventoryStatePresentation state)
    {
        DesktopList.ItemsSource = null;
        InventoryPanel.Visibility = Visibility.Collapsed;
        StatusCard.Visibility = Visibility.Visible;
        StatusTitle.Text = state.Title;
        StatusMessage.Text = state.Message;
        StatusProvider.Text = state.ProviderDetails;
        StatusIcon.Glyph = state.Glyph;
        StatusIcon.Visibility = state.IsLoading ? Visibility.Collapsed : Visibility.Visible;
        LoadingIndicator.IsActive = state.IsLoading;
        LoadingIndicator.Visibility = state.IsLoading ? Visibility.Visible : Visibility.Collapsed;
        AutomationProperties.SetName(
            StatusCard,
            $"{state.Title}. {state.Message} {state.ProviderDetails}");
    }

    private Task RunOnUiThreadAsync(Action action)
    {
        if (DispatcherQueue.HasThreadAccess)
        {
            action();
            return Task.CompletedTask;
        }

        TaskCompletionSource completion =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
        if (!DispatcherQueue.TryEnqueue(() =>
            {
                try
                {
                    action();
                    completion.TrySetResult();
                }
                catch (Exception exception)
                {
                    completion.TrySetException(exception);
                }
            }))
        {
            completion.TrySetCanceled();
        }

        return completion.Task;
    }

    private void CancelRefresh()
    {
        _refreshVersion++;
        _refreshCancellation?.Cancel();
        _refreshCancellation?.Dispose();
        _refreshCancellation = null;
    }
}

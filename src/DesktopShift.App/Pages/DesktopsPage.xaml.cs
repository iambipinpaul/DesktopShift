using DesktopShift.App.Desktops;
using DesktopShift.App.ViewModels;
using DesktopShift.Core.Compatibility;
using DesktopShift.Core.ManagedDesktops;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

public sealed partial class DesktopsPage : Page
{
    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        const double horizontalPageMargin = 60;
        DesktopsContent.Width = Math.Max(
            0,
            Math.Min(1000, args.NewSize.Width - horizontalPageMargin));

        bool useStackedHeader = args.NewSize.Width < 620;
        Grid.SetRow(MaintenanceCommands, useStackedHeader ? 1 : 0);
        Grid.SetColumn(MaintenanceCommands, useStackedHeader ? 0 : 1);
        DesktopsHeaderGrid.RowSpacing = useStackedHeader ? 14 : 0;

        bool useStackedSummary = args.NewSize.Width < 560;
        Grid.SetRow(ManagedSummaryStatus, useStackedSummary ? 1 : 0);
        Grid.SetColumn(ManagedSummaryStatus, useStackedSummary ? 1 : 2);
        ManagedSummaryStatus.HorizontalAlignment = useStackedSummary
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        ManagedSummaryStatus.Margin = useStackedSummary
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(0);
    }

    private void OnManagedDesktopCardSizeChanged(
        object sender,
        SizeChangedEventArgs args)
    {
        if (sender is not Grid card ||
            card.FindName("ManagedDesktopCardActions") is not FrameworkElement actions)
        {
            return;
        }

        PositionCardActions(actions, args.NewSize.Width < 620);
    }

    private void OnInventoryRowSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid row ||
            row.FindName("CurrentDesktopIndicator") is not FrameworkElement indicator ||
            row.FindName("CurrentActionColumn") is not ColumnDefinition reservedColumn)
        {
            return;
        }

        bool stack = args.NewSize.Width < 520;
        Grid.SetRow(indicator, stack ? 1 : 0);
        Grid.SetColumn(indicator, stack ? 1 : 2);
        indicator.HorizontalAlignment = stack
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        indicator.Margin = stack
            ? new Thickness(0, 8, 0, 0)
            : new Thickness(0);
        reservedColumn.Width = new GridLength(stack ? 0 : 96);
    }

    private static void PositionCardActions(FrameworkElement actions, bool stack)
    {
        Grid.SetRow(actions, stack ? 1 : 0);
        Grid.SetColumn(actions, stack ? 1 : 2);
        actions.HorizontalAlignment = stack
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        actions.Margin = stack
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(0);
    }

    private IDesktopTopologyProvider? _provider;
    private DesktopTopologyProviderState? _providerState;
    private IManagedDesktopMaintenanceService? _maintenance;
    private CancellationToken _windowCancellationToken;
    private CancellationTokenSource? _refreshCancellation;
    private ManagedDesktopMappingPresentationSnapshot? _mappingSnapshot;
    private IReadOnlyList<ManagedDesktopCatalogItem> _items = [];
    private int _mappingUpdateVersion;
    private int _refreshVersion;
    private bool _isOperationRunning;
    private bool _canReconcile;

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
            RenderManagedDesktops();
        }
    }

    /// <summary>
    /// Supplies the service that maintains the Managed Desktop definitions.
    /// </summary>
    /// <remarks>
    /// Until this is called the page lists the managed destinations read-only,
    /// so an unwired shell degrades to the view it had before rather than to an
    /// empty page.
    /// </remarks>
    public void UpdateMaintenance(
        IManagedDesktopMaintenanceService maintenanceService,
        CancellationToken windowCancellationToken)
    {
        ArgumentNullException.ThrowIfNull(maintenanceService);

        _maintenance = maintenanceService;
        _windowCancellationToken = windowCancellationToken;

        if (IsLoaded)
        {
            RenderManagedDesktops();
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
                RenderManagedDesktops();
            }

            return;
        }

        _ = DispatcherQueue.TryEnqueue(() =>
        {
            if (IsLoaded && version == Volatile.Read(ref _mappingUpdateVersion))
            {
                RenderManagedDesktops();
            }
        });
    }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        SubscribeToProvider();
        StartRefresh();
        RenderManagedDesktops();
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

    /// <summary>
    /// Draws the managed destination list from whichever source the shell wired.
    /// </summary>
    private void RenderManagedDesktops()
    {
        if (_maintenance is not null)
        {
            ApplyCatalog(_maintenance.GetCatalog(_providerState));
            return;
        }

        ManagedDesktopMappingPresentationSnapshot? snapshot =
            Volatile.Read(ref _mappingSnapshot);
        if (snapshot is not null)
        {
            ApplyMappingSnapshot(snapshot);
        }
    }

    private void ApplyCatalog(ManagedDesktopCatalog catalog)
    {
        _items = [.. catalog.Entries.Select(ManagedDesktopCatalogItem.FromEntry)];

        ManagedMappingSummary.Text = catalog.Summary;
        ManagedMappingProvider.Text = catalog.ProviderSummary;
        ManagedMappingOutcome.Text = catalog.Outcome;
        ManagedMappingObservedAt.Text = catalog.ObservedAt;
        ManagedMappingList.ItemsSource = _items;
        ManagedMappingList.Visibility =
            _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ValidationList.ItemsSource = catalog.Messages
            .Select(ManagedDesktopValidationItem.FromMessage)
            .ToArray();
        MaintenanceCommands.Visibility = Visibility.Visible;
        _canReconcile = catalog.Availability.CanReconcile;
        ToolTipService.SetToolTip(
            ReconcileAllButton,
            _canReconcile
                ? "Re-resolve every managed destination against the live Windows desktops."
                : catalog.Availability.ReconcileUnavailableReason);
        ShowManagedDesktops(catalog.Outcome, catalog.Summary);
        ApplyBusyState();
    }

    private void ApplyMappingSnapshot(ManagedDesktopMappingPresentationSnapshot snapshot)
    {
        _items = [.. snapshot.Mappings
            .OrderBy(mapping => mapping.PreferredOrder)
            .ThenBy(mapping => mapping.SemanticKey, StringComparer.OrdinalIgnoreCase)
            .Select(ManagedDesktopCatalogItem.FromMapping)];

        ManagedMappingSummary.Text = snapshot.Summary;
        ManagedMappingProvider.Text = snapshot.ProviderSummary;
        ManagedMappingOutcome.Text = snapshot.Outcome;
        ManagedMappingObservedAt.Text = snapshot.ObservedAt;
        ManagedMappingList.ItemsSource = _items;
        ManagedMappingList.Visibility =
            _items.Count == 0 ? Visibility.Collapsed : Visibility.Visible;
        ValidationList.ItemsSource = snapshot.Issues
            .Select(static issue => new ManagedDesktopValidationItem(
                "Managed desktop reconciliation reported a problem",
                issue,
                InfoBarSeverity.Warning))
            .ToArray();
        MaintenanceCommands.Visibility = Visibility.Collapsed;
        ShowManagedDesktops(snapshot.Outcome, snapshot.Summary);
    }

    private void ShowManagedDesktops(string outcome, string summary)
    {
        ManagedMappingsPanel.Visibility = Visibility.Visible;
        AutomationProperties.SetName(
            ManagedMappingsPanel,
            $"Managed desktop definitions. {outcome}. {summary}");
    }

    private void ApplyBusyState()
    {
        AddDesktopButton.IsEnabled = !_isOperationRunning;
        ReconcileAllButton.IsEnabled = _canReconcile && !_isOperationRunning;
        ManagedMappingList.IsEnabled = !_isOperationRunning;
        MaintenanceProgress.IsActive = _isOperationRunning;
        MaintenanceProgress.Visibility =
            _isOperationRunning ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnAddDesktopClick(object sender, RoutedEventArgs args)
    {
        ManagedDesktopDraft? draft = await ShowAddDialogAsync();
        if (draft is null)
        {
            return;
        }

        await RunOperationAsync(token => _maintenance!.AddAsync(draft, token));
    }

    private async void OnReconcileAllClick(object sender, RoutedEventArgs args)
    {
        await RunOperationAsync(token => _maintenance!.ReconcileAllAsync(token));
    }

    private async void OnRenameClick(object sender, RoutedEventArgs args)
    {
        if (ResolveItem(sender) is not ManagedDesktopCatalogItem item)
        {
            return;
        }

        string? displayName = await ShowRenameDialogAsync(item);
        if (displayName is null)
        {
            return;
        }

        await RunOperationAsync(
            token => _maintenance!.RenameAsync(item.SemanticKey, displayName, token));
    }

    private async void OnMoveEarlierClick(object sender, RoutedEventArgs args)
    {
        if (ResolveItem(sender) is not ManagedDesktopCatalogItem item)
        {
            return;
        }

        await RunOperationAsync(
            token => _maintenance!.MoveAsync(
                item.SemanticKey,
                ManagedDesktopMoveDirection.Earlier,
                token));
    }

    private async void OnMoveLaterClick(object sender, RoutedEventArgs args)
    {
        if (ResolveItem(sender) is not ManagedDesktopCatalogItem item)
        {
            return;
        }

        await RunOperationAsync(
            token => _maintenance!.MoveAsync(
                item.SemanticKey,
                ManagedDesktopMoveDirection.Later,
                token));
    }

    private async void OnToggleRecreationClick(object sender, RoutedEventArgs args)
    {
        if (ResolveItem(sender) is not ManagedDesktopCatalogItem item)
        {
            return;
        }

        await RunOperationAsync(
            token => _maintenance!.SetRecreationPolicyAsync(
                item.SemanticKey,
                !item.RecreateWhenMissing,
                token));
    }

    private async void OnRecreateClick(object sender, RoutedEventArgs args)
    {
        if (ResolveItem(sender) is not ManagedDesktopCatalogItem item)
        {
            return;
        }

        await RunOperationAsync(
            token => _maintenance!.RecreateAsync(item.SemanticKey, token));
    }

    private async void OnRemoveClick(object sender, RoutedEventArgs args)
    {
        if (ResolveItem(sender) is not ManagedDesktopCatalogItem item)
        {
            return;
        }

        if (!await ConfirmRemovalAsync(item))
        {
            return;
        }

        await RunOperationAsync(
            token => _maintenance!.RemoveFromManagementAsync(item.SemanticKey, token));
    }

    private ManagedDesktopCatalogItem? ResolveItem(object sender)
    {
        if (_maintenance is null || _isOperationRunning)
        {
            return null;
        }

        string? semanticKey = (sender as FrameworkElement)?.Tag as string;
        return string.IsNullOrEmpty(semanticKey)
            ? null
            : _items.FirstOrDefault(
                item => string.Equals(
                    item.SemanticKey,
                    semanticKey,
                    StringComparison.OrdinalIgnoreCase));
    }

    private async Task RunOperationAsync(
        Func<CancellationToken, Task<ManagedDesktopMaintenanceResult>> operation)
    {
        if (_maintenance is null || _isOperationRunning)
        {
            return;
        }

        _isOperationRunning = true;
        ApplyBusyState();

        try
        {
            ManagedDesktopMaintenanceResult result =
                await operation(_windowCancellationToken);
            ShowOperationResult(result);
        }
        catch (OperationCanceledException) when (_windowCancellationToken.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            OperationResult.Severity = InfoBarSeverity.Error;
            OperationResult.Title = "That change could not be applied";
            OperationResult.Message = exception.Message;
            OperationResult.IsOpen = true;
        }
        finally
        {
            _isOperationRunning = false;
            RenderManagedDesktops();
        }
    }

    private void ShowOperationResult(ManagedDesktopMaintenanceResult result)
    {
        OperationResult.Severity = result.Outcome switch
        {
            ManagedDesktopMaintenanceOutcome.Applied => InfoBarSeverity.Success,
            ManagedDesktopMaintenanceOutcome.NoChange => InfoBarSeverity.Informational,
            ManagedDesktopMaintenanceOutcome.Rejected => InfoBarSeverity.Error,
            ManagedDesktopMaintenanceOutcome.Failed => InfoBarSeverity.Error,
            _ => InfoBarSeverity.Warning,
        };
        OperationResult.Title = result.Outcome switch
        {
            ManagedDesktopMaintenanceOutcome.Applied => "Change applied",
            ManagedDesktopMaintenanceOutcome.NoChange => "Nothing to change",
            ManagedDesktopMaintenanceOutcome.Rejected => "Change not applied",
            ManagedDesktopMaintenanceOutcome.Limited => "Limited Mode",
            ManagedDesktopMaintenanceOutcome.Partial => "Partly applied",
            _ => "Change failed",
        };
        OperationResult.Message = result.Report is null
            ? result.Summary
            : $"{result.Summary} {result.Report.Summary}";
        OperationResult.IsOpen = true;
    }

    private async Task<ManagedDesktopDraft?> ShowAddDialogAsync()
    {
        TextBox keyBox = new()
        {
            Header = "Semantic key",
            PlaceholderText = "media",
        };
        TextBox nameBox = new()
        {
            Header = "Display name",
            PlaceholderText = "Media",
        };
        CheckBox recreateBox = new()
        {
            Content = "Recreate this Windows desktop when it is missing",
            IsChecked = true,
        };
        TextBlock explanation = new()
        {
            Text = "The semantic key is how Application Rules name this destination. It never changes when you rename the desktop.",
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel panel = new() { Spacing = 12 };
        panel.Children.Add(keyBox);
        panel.Children.Add(nameBox);
        panel.Children.Add(recreateBox);
        panel.Children.Add(explanation);

        // A dialog is hosted outside the page, so it does not inherit the theme
        // the shell applied to its own tree. Without this it renders in whatever
        // Windows is set to, which is visibly wrong the moment someone has asked
        // DesktopShift for light while Windows is dark.
        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Add a managed desktop",
            Content = panel,
            PrimaryButtonText = "Add",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary
            ? new ManagedDesktopDraft(
                keyBox.Text,
                nameBox.Text,
                recreateBox.IsChecked == true)
            : null;
    }

    private async Task<string?> ShowRenameDialogAsync(ManagedDesktopCatalogItem item)
    {
        TextBox nameBox = new()
        {
            Header = "Display name",
            Text = item.DisplayName,
            SelectionStart = item.DisplayName.Length,
        };
        TextBlock explanation = new()
        {
            Text = $"{item.SemanticKeyLabel}. The key does not change, so every Application Rule that targets this destination keeps working.",
            TextWrapping = TextWrapping.Wrap,
        };
        StackPanel panel = new() { Spacing = 12 };
        panel.Children.Add(nameBox);
        panel.Children.Add(explanation);

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Rename {item.DisplayName}",
            Content = panel,
            PrimaryButtonText = "Rename",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Primary,
        };

        ContentDialogResult result = await dialog.ShowAsync();
        return result == ContentDialogResult.Primary ? nameBox.Text : null;
    }

    /// <summary>
    /// Confirms that management is ending and the Windows desktop is not.
    /// </summary>
    /// <remarks>
    /// The confirmation says plainly what this does and what it does not do,
    /// because "remove" is the word a user expects to destroy something.
    /// Deleting a real Windows desktop is a separate destructive action that
    /// DesktopShift does not perform.
    /// </remarks>
    private async Task<bool> ConfirmRemovalAsync(ManagedDesktopCatalogItem item)
    {
        TextBlock explanation = new()
        {
            Text = $"DesktopShift will stop managing '{item.DisplayName}' and will forget which Windows desktop it was mapped to.\n\nThe Windows desktop itself is kept, with every window still on it. DesktopShift does not delete Windows desktops; deleting one is a separate action you take in Task View.",
            TextWrapping = TextWrapping.Wrap,
        };

        ContentDialog dialog = new()
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = $"Stop managing {item.DisplayName}?",
            Content = explanation,
            PrimaryButtonText = "Remove from management",
            CloseButtonText = "Cancel",
            DefaultButton = ContentDialogButton.Close,
        };

        return await dialog.ShowAsync() == ContentDialogResult.Primary;
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

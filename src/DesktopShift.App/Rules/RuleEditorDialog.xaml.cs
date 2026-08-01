using System;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using DesktopShift.Core.Configuration;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Rules;

/// <summary>
/// What the rule editor needs from the rest of the application.
/// </summary>
/// <param name="RunningApplications">Reads the applications a user can pick.</param>
/// <param name="BrowseForExecutableAsync">
/// Opens a file picker and returns the chosen path, or <see langword="null"/>
/// when the user cancels.
/// </param>
/// <param name="SaveAsync">
/// Persists an applied draft and returns the reason it was refused, or
/// <see langword="null"/> when it was accepted.
/// </param>
public sealed record RuleEditorDependencies(
    IRunningApplicationInventory RunningApplications,
    Func<CancellationToken, Task<string?>> BrowseForExecutableAsync,
    Func<ApplicationRuleDraft, CancellationToken, Task<string?>> SaveAsync);

/// <summary>
/// The Application Rule editor.
/// </summary>
/// <remarks>
/// The dialog owns no rules of its own. It reads running windows, hands the
/// draft to the tester, and hands the applied draft to the caller to persist;
/// every decision about what a rule may contain belongs to the draft.
/// </remarks>
public sealed partial class RuleEditorDialog : ContentDialog
{
    private readonly RuleEditorDependencies dependencies;
    private readonly CancellationToken cancellationToken;
    private RunningApplicationSnapshot snapshot =
        RunningApplicationSnapshot.Empty;
    private bool hasReadRunningApplications;

    public RuleEditorDialog(
        RuleEditorViewModel viewModel,
        RuleEditorDependencies dependencies,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(viewModel);
        ArgumentNullException.ThrowIfNull(dependencies);

        ViewModel = viewModel;
        this.dependencies = dependencies;
        this.cancellationToken = cancellationToken;

        InitializeComponent();
        Loaded += OnLoaded;
        SizeChanged += OnDialogSizeChanged;
        Unloaded += OnUnloaded;
    }

    public RuleEditorViewModel ViewModel { get; }

    /// <summary>
    /// ContentDialog is hosted in a popup and does not inherit resource
    /// overrides placed on the window root. Copy the effective app accent into
    /// the popup so its buttons, toggles, checks, and focus visuals match the
    /// page behind it.
    /// </summary>
    public void InheritAccentResources(FrameworkElement owner)
    {
        ArgumentNullException.ThrowIfNull(owner);

        foreach (string key in AccentResourceKeys)
        {
            if (owner.Resources.TryGetValue(key, out object? value))
            {
                Resources[key] = value;
            }
        }
    }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }

        UpdateResponsiveLayout();
        await ReadRunningApplicationsAsync();
    }

    private void OnUnloaded(object sender, RoutedEventArgs args)
    {
        Unloaded -= OnUnloaded;
        SizeChanged -= OnDialogSizeChanged;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed -= OnXamlRootChanged;
        }
    }

    private void OnXamlRootChanged(XamlRoot sender, XamlRootChangedEventArgs args) =>
        UpdateResponsiveLayout();

    private void OnDialogSizeChanged(object sender, SizeChangedEventArgs args)
    {
        UpdateApplicationPickerLayout(args.NewSize.Width);
        CenterInWindow();
    }

    private void UpdateResponsiveLayout()
    {
        if (XamlRoot is null)
        {
            return;
        }

        // The template adds roughly 80px around the content. Preserve a 16px
        // window inset on each side, while retaining the 1040px large layout.
        double targetOuterWidth = Math.Min(1040, XamlRoot.Size.Width - 32);
        Width = Math.Max(320, targetOuterWidth - 80);
        UpdateApplicationPickerLayout(Width);
        CenterInWindow();
    }

    private void UpdateApplicationPickerLayout(double width)
    {
        if (RunningApplicationActionsGrid is null ||
            RunningApplicationStatusPanel is null ||
            RunningApplicationMatchOptionsGrid is null ||
            IncludeWindowClassesCheckBox is null)
        {
            return;
        }

        bool isCompact = width < 660;

        Grid.SetRow(RunningApplicationStatusPanel, isCompact ? 1 : 0);
        Grid.SetColumn(RunningApplicationStatusPanel, isCompact ? 0 : 2);
        Grid.SetColumnSpan(RunningApplicationStatusPanel, isCompact ? 3 : 1);
        RunningApplicationStatusPanel.HorizontalAlignment =
            isCompact ? HorizontalAlignment.Left : HorizontalAlignment.Right;

        Grid.SetRow(IncludeWindowClassesCheckBox, isCompact ? 1 : 0);
        Grid.SetColumn(IncludeWindowClassesCheckBox, isCompact ? 0 : 1);
    }

    private void CenterInWindow()
    {
        if (XamlRoot is null || ActualWidth <= 0)
        {
            return;
        }

        // WinUI's popup starts at the window edge but measures the dialog
        // against the page content. Derive the translation from the live root
        // width so the dialog stays centered at every window size.
        double horizontalOffset = Math.Max(0, (XamlRoot.Size.Width - ActualWidth) / 2);
        Translation = new Vector3((float)horizontalOffset, 0, 0);
    }

    private async void OnRefreshRunningApplicationsClick(
        object sender,
        RoutedEventArgs args)
    {
        await ReadRunningApplicationsAsync();
    }

    private void OnUseRunningApplicationClick(object sender, RoutedEventArgs args)
    {
        if (!ViewModel.ApplySelectedRunningApplication())
        {
            ViewModel.RunningApplicationStatus =
                "Select a running application first.";
        }
    }

    private async void OnBrowseForExecutableClick(
        object sender,
        RoutedEventArgs args)
    {
        try
        {
            string? path = await dependencies.BrowseForExecutableAsync(
                cancellationToken);
            if (!string.IsNullOrWhiteSpace(path))
            {
                ViewModel.ApplyExecutablePath(path);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ViewModel.RunningApplicationStatus =
                $"The executable could not be browsed: {exception.Message}";
        }
    }

    private async void OnTestRuleClick(object sender, RoutedEventArgs args)
    {
        if (!hasReadRunningApplications)
        {
            await ReadRunningApplicationsAsync();
        }

        // A test is deliberately allowed on a draft that will not save. What is
        // wrong with the rule and what the rule would claim are two separate
        // questions, and answering only the first would hide the second.
        _ = ViewModel.Validate();
        ViewModel.SetTestResult(
            ApplicationRuleTester.Test(
                ViewModel.ToDraft().ToRule(),
                snapshot.Windows));
    }

    private async void OnPrimaryButtonClick(
        ContentDialog sender,
        ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        try
        {
            ViewModel.SaveError = string.Empty;
            if (!ViewModel.Validate())
            {
                args.Cancel = true;
                return;
            }

            string? failure = await dependencies.SaveAsync(
                ViewModel.ToDraft(),
                cancellationToken);
            if (failure is not null)
            {
                args.Cancel = true;
                ViewModel.SaveError = failure;
            }
        }
        catch (OperationCanceledException)
        {
            args.Cancel = true;
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            ViewModel.SaveError =
                $"DesktopShift could not save this rule. Your entries are still here. {exception.Message}";
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }

    private async Task ReadRunningApplicationsAsync()
    {
        ViewModel.IsLoadingRunningApplications = true;
        try
        {
            snapshot = await dependencies.RunningApplications.ReadAsync(
                cancellationToken);
            hasReadRunningApplications = true;
            ViewModel.SetRunningApplications(snapshot.Applications);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ViewModel.RunningApplicationStatus =
                $"The running windows could not be read: {exception.Message}";
        }
        finally
        {
            ViewModel.IsLoadingRunningApplications = false;
        }
    }

    private static readonly string[] AccentResourceKeys =
    [
        "DesktopShift.AccentBrush",
        "DesktopShift.AccentStrongBrush",
        "DesktopShift.AccentSoftBrush",
        "AccentFillColorDefaultBrush",
        "AccentFillColorSecondaryBrush",
        "AccentFillColorTertiaryBrush",
        "AccentTextFillColorPrimaryBrush",
        "AccentTextFillColorSecondaryBrush",
        "AccentTextFillColorTertiaryBrush",
        "AccentButtonBackground",
        "AccentButtonBackgroundPointerOver",
        "AccentButtonBackgroundPressed",
        "AccentButtonForeground",
        "AccentButtonForegroundPointerOver",
        "AccentButtonForegroundPressed",
        "ToggleSwitchFillOn",
        "ToggleSwitchFillOnPointerOver",
        "ToggleSwitchFillOnPressed",
        "TextControlBorderBrushFocused",
        "FocusStrokeColorOuterBrush",
        "CheckBoxCheckBackgroundFillChecked",
        "CheckBoxCheckBackgroundFillCheckedPointerOver",
        "CheckBoxCheckBackgroundFillCheckedPressed",
        "CheckBoxCheckBackgroundStrokeChecked",
        "CheckBoxCheckBackgroundStrokeCheckedPointerOver",
        "CheckBoxCheckBackgroundStrokeCheckedPressed",
        "ProgressRingForegroundThemeBrush",
    ];
}

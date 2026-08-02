using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using DesktopShift.App.Rules;
using DesktopShift.Core.Assignments;
using DesktopShift.Core.Configuration;
using DesktopShift.Core.Observation;
using Microsoft.UI;
using Microsoft.UI.Content;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.Pages;

/// <summary>
/// The Application Rules page.
/// </summary>
/// <remarks>
/// <para>
/// The page owns no rules logic. It shows what
/// <see cref="ApplicationRulePresentationProjection"/> produces, hands edits to
/// <see cref="ApplicationRuleCatalog"/> and <see cref="ApplicationRuleDraft"/>,
/// and persists whatever comes back through the configuration service. That is
/// deliberate: XAML code-behind cannot be tested here, so nothing that could be
/// wrong in an interesting way is allowed to live in it.
/// </para>
/// <para>
/// Every edit is written as a candidate. A candidate the validator rejects is
/// still written, so a user who typed six identities and got one of them wrong
/// still has the other five when they come back.
/// </para>
/// </remarks>
public sealed partial class RulesPage : Page
{
    private RulesPageServices? services;
    private CancellationToken cancellationToken = CancellationToken.None;
    private ConfigurationDocument? document;
    private bool isApplyingPresentation;
    private ImmutableArray<ApplicationRulePresentation> allRules = [];

    public IReadOnlyList<WindowsManagedWindowEntry> WindowsManagedWindows { get; } =
        WindowsManagedWindowCatalog.Entries;

    public RulesPage()
    {
        InitializeComponent();
    }

    private void OnPageSizeChanged(object sender, SizeChangedEventArgs args)
    {
        const double horizontalPageMargin = 60;
        RulesContent.Width = Math.Max(
            0,
            Math.Min(1000, args.NewSize.Width - horizontalPageMargin));

        bool useStackedHeader = args.NewSize.Width < 540;
        Grid.SetRow(HeaderActions, useStackedHeader ? 1 : 0);
        Grid.SetColumn(HeaderActions, useStackedHeader ? 0 : 1);
        PageHeaderGrid.RowSpacing = useStackedHeader ? 14 : 0;
    }

    private void OnRuleCardSizeChanged(object sender, SizeChangedEventArgs args)
    {
        if (sender is not Grid card ||
            card.FindName("RuleActions") is not FrameworkElement actions)
        {
            return;
        }

        bool useStackedActions = args.NewSize.Width < 520;
        Grid.SetRow(actions, useStackedActions ? 1 : 0);
        Grid.SetColumn(actions, useStackedActions ? 1 : 2);
        actions.HorizontalAlignment = useStackedActions
            ? HorizontalAlignment.Left
            : HorizontalAlignment.Right;
        actions.Margin = useStackedActions
            ? new Thickness(0, 10, 0, 0)
            : new Thickness(0);
    }

    /// <summary>
    /// Wires the page to the services it edits rules through, and shows the
    /// current rules.
    /// </summary>
    /// <param name="pageServices">The services the page reads and writes with.</param>
    /// <param name="lifetimeCancellation">
    /// Cancelled when the shell window closes, so no edit outlives the window it
    /// was started from.
    /// </param>
    public void Update(
        RulesPageServices pageServices,
        CancellationToken lifetimeCancellation)
    {
        ArgumentNullException.ThrowIfNull(pageServices);

        services = pageServices;
        cancellationToken = lifetimeCancellation;
        Refresh();
    }

    private void Refresh()
    {
        if (services is null)
        {
            return;
        }

        ConfigurationState state = services.ConfigurationService.CurrentState;
        document = state.Candidate;

        ShowRules(ApplicationRulePresentationProjection.Project(
            state.Candidate,
            services.ActivityProjection.Snapshot,
            services.TimeProvider.GetUtcNow(),
            services.IconReader));
        ShowValidationIssues(state.Issues);
    }

    private void ShowRules(
        ImmutableArray<ApplicationRulePresentation> presentations)
    {
        allRules = presentations;
        ApplyRuleFilter();
    }

    private void ApplyRuleFilter()
    {
        string query = RuleSearchBox.Text.Trim();
        ApplicationRulePresentation[] presentations = allRules
            .Where(rule =>
                query.Length == 0 ||
                rule.DisplayName.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                rule.MatchSummary.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                rule.TargetSummary.Contains(query, StringComparison.CurrentCultureIgnoreCase))
            .ToArray();

        // The enabled switches are bound to the items being replaced, so the
        // toggles they raise while the list is rebuilt are the list telling
        // itself what it already knows, not a user asking for a change.
        isApplyingPresentation = true;
        try
        {
            RuleList.ItemsSource = presentations;
            RuleList.Visibility = presentations.Length == 0
                ? Visibility.Collapsed
                : Visibility.Visible;
            EmptyState.Visibility = presentations.Length == 0
                ? Visibility.Visible
                : Visibility.Collapsed;
        }
        finally
        {
            isApplyingPresentation = false;
        }
    }

    private void OnRuleSearchTextChanged(
        AutoSuggestBox sender,
        AutoSuggestBoxTextChangedEventArgs args) => ApplyRuleFilter();

    private void ShowValidationIssues(
        ImmutableArray<ConfigurationValidationIssue> issues)
    {
        if (issues.IsDefaultOrEmpty)
        {
            ValidationBar.IsOpen = false;
            ValidationBar.Message = string.Empty;
            return;
        }

        ValidationBar.Message = string.Join(
            Environment.NewLine,
            issues.Select(static issue => $"{issue.Path}: {issue.Message}"));
        ValidationBar.IsOpen = true;
    }

    private void ShowStatus(string message, InfoBarSeverity severity)
    {
        StatusBar.Severity = severity;
        StatusBar.Message = message;
        StatusBar.Title = severity == InfoBarSeverity.Error
            ? "That did not work"
            : "Done";
        StatusBar.IsOpen = true;
    }

    private void OnRefreshClick(object sender, RoutedEventArgs args)
    {
        Refresh();
    }

    private async void OnNewRuleClick(object sender, RoutedEventArgs args)
    {
        if (document is null)
        {
            return;
        }

        await ShowEditorAsync(ApplicationRuleDraft.ForNewRule(document));
    }

    private async void OnEditRuleClick(object sender, RoutedEventArgs args)
    {
        if (document is null ||
            ReadRuleId(sender) is not string ruleId ||
            ApplicationRuleCatalog.Find(document, ruleId) is not ApplicationRule rule)
        {
            return;
        }

        await ShowEditorAsync(ApplicationRuleDraft.ForExistingRule(rule));
    }

    private async void OnDuplicateRuleClick(object sender, RoutedEventArgs args)
    {
        if (document is null || ReadRuleId(sender) is not string ruleId)
        {
            return;
        }

        (ConfigurationDocument edited, ApplicationRule? duplicate) =
            ApplicationRuleCatalog.Duplicate(document, ruleId);
        if (duplicate is null)
        {
            return;
        }

        string? failure = await PersistAsync(edited);
        ShowStatus(
            failure ??
                $"'{duplicate.DisplayName}' was created, disabled, so you can narrow it before it claims anything.",
            failure is null ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OnDeleteRuleClick(object sender, RoutedEventArgs args)
    {
        if (document is null ||
            ReadRuleId(sender) is not string ruleId ||
            ApplicationRuleCatalog.Find(document, ruleId) is not ApplicationRule rule)
        {
            return;
        }

        ContentDialog confirmation = new()
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
            Title = "Delete this rule?",
            Content =
                $"'{rule.DisplayName}' will stop claiming windows. Windows already assigned stay where they are.",
            PrimaryButtonText = "Delete rule",
            CloseButtonText = "Keep it",
            DefaultButton = ContentDialogButton.Close,
        };

        if (await confirmation.ShowAsync() != ContentDialogResult.Primary)
        {
            return;
        }

        string? failure = await PersistAsync(
            ApplicationRuleCatalog.Remove(document, ruleId));
        ShowStatus(
            failure ?? $"'{rule.DisplayName}' was deleted.",
            failure is null ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OnRuleEnabledToggled(object sender, RoutedEventArgs args)
    {
        if (isApplyingPresentation ||
            document is null ||
            sender is not ToggleSwitch toggle ||
            ReadRuleId(sender) is not string ruleId ||
            ApplicationRuleCatalog.Find(document, ruleId) is not ApplicationRule rule ||
            rule.IsEnabled == toggle.IsOn)
        {
            return;
        }

        string? failure = await PersistAsync(
            ApplicationRuleCatalog.SetEnabled(document, ruleId, toggle.IsOn));
        ShowStatus(
            failure ??
                $"'{rule.DisplayName}' is now {(toggle.IsOn ? "enabled" : "disabled")}.",
            failure is null ? InfoBarSeverity.Success : InfoBarSeverity.Error);
    }

    private async void OnReassignRuleClick(object sender, RoutedEventArgs args)
    {
        if (services is null ||
            document is null ||
            ReadRuleId(sender) is not string ruleId ||
            ApplicationRuleCatalog.Find(document, ruleId) is not ApplicationRule rule)
        {
            return;
        }

        try
        {
            RunningApplicationSnapshot snapshot = await services
                .RunningApplications
                .ReadAsync(cancellationToken);
            ApplicationRuleTestResult test = ApplicationRuleTester.Test(
                rule,
                snapshot.Windows);

            if (test.MatchCount == 0)
            {
                ShowStatus(
                    $"No open window matches '{rule.DisplayName}'. {test.EvaluatedWindowCount} windows were checked.",
                    InfoBarSeverity.Informational);
                return;
            }

            WindowReassignmentBatchResult batch = await services
                .WindowReassignmentService
                .ReassignAllAsync(cancellationToken);
            ApplicationRuleReassignmentSummary summary =
                ApplicationRulePresentationProjection.Summarize(batch, ruleId);

            ShowStatus(
                $"{test.MatchCount} open windows match '{rule.DisplayName}' on {test.StrongestSignal}. {summary.Message}",
                summary.FailedWindowCount > 0
                    ? InfoBarSeverity.Warning
                    : InfoBarSeverity.Success);
            Refresh();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception exception)
        {
            ShowStatus(
                $"'{rule.DisplayName}' could not be reassigned: {exception.Message}",
                InfoBarSeverity.Error);
        }
    }

    private async Task ShowEditorAsync(ApplicationRuleDraft draft)
    {
        if (services is null || document is null)
        {
            return;
        }

        ConfigurationDocument editedDocument = document;
        RuleEditorDialog dialog = new(
            new RuleEditorViewModel(editedDocument, draft),
            new RuleEditorDependencies(
                services.RunningApplications,
                BrowseForExecutableAsync,
                (applied, token) =>
                    PersistAsync(applied.Apply(editedDocument), token)),
            cancellationToken)
        {
            XamlRoot = XamlRoot,
            RequestedTheme = ActualTheme,
        };

        if (XamlRoot.Content is FrameworkElement root)
        {
            dialog.InheritAccentResources(root);
        }

        _ = await dialog.ShowAsync();
    }

    private Task<string?> PersistAsync(ConfigurationDocument candidate) =>
        PersistAsync(candidate, cancellationToken);

    private async Task<string?> PersistAsync(
        ConfigurationDocument candidate,
        CancellationToken token)
    {
        if (services is null)
        {
            return "The Rules page is not connected to the configuration service.";
        }

        ConfigurationSaveResult result = await services.ConfigurationService
            .SaveCandidateAsync(candidate, token);
        Refresh();

        return result.Accepted
            ? null
            : string.Join(
                Environment.NewLine,
                result.State.Issues.Select(
                    static issue => $"{issue.Path}: {issue.Message}"));
    }

    private async Task<string?> BrowseForExecutableAsync(
        CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        nint windowHandle = GetWindowHandle();
        if (windowHandle == 0)
        {
            throw new InvalidOperationException(
                "The file picker needs the shell window, which is not available yet. Type the executable path instead.");
        }

        global::Windows.Storage.Pickers.FileOpenPicker picker = new()
        {
            SuggestedStartLocation =
                global::Windows.Storage.Pickers.PickerLocationId.ComputerFolder,
        };
        picker.FileTypeFilter.Add(".exe");
        WinRT.Interop.InitializeWithWindow.Initialize(picker, windowHandle);

        global::Windows.Storage.StorageFile? file =
            await picker.PickSingleFileAsync();
        return file?.Path;
    }

    /// <summary>
    /// Finds the window the page is hosted in, which the file picker has to be
    /// anchored to or it opens behind the application.
    /// </summary>
    /// <returns>The window handle, or zero when the page is not hosted yet.</returns>
    private nint GetWindowHandle()
    {
        if (XamlRoot?.ContentIslandEnvironment is not ContentIslandEnvironment
            environment)
        {
            return 0;
        }

        return Win32Interop.GetWindowFromWindowId(environment.AppWindowId);
    }

    private static string? ReadRuleId(object sender) =>
        sender is FrameworkElement { Tag: string ruleId } &&
            !string.IsNullOrWhiteSpace(ruleId)
            ? ruleId
            : null;
}

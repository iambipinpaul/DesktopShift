using System;
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
    }

    public RuleEditorViewModel ViewModel { get; }

    private async void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        await ReadRunningApplicationsAsync();
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
}

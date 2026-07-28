using DesktopShift.App.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace DesktopShift.App.FirstRun;

public sealed partial class FirstRunDialog : ContentDialog
{
    private readonly Func<FirstRunViewModel, CancellationToken, Task<FirstRunApplyResult>> _applyAsync;

    public FirstRunDialog(
        FirstRunViewModel viewModel,
        Func<FirstRunViewModel, CancellationToken, Task<FirstRunApplyResult>> applyAsync)
    {
        ViewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        InitializeComponent();
    }

    public FirstRunViewModel ViewModel { get; }

    private async void OnPrimaryButtonClick(ContentDialog sender, ContentDialogButtonClickEventArgs args)
    {
        ContentDialogButtonClickDeferral deferral = args.GetDeferral();
        IsPrimaryButtonEnabled = false;
        try
        {
            FirstRunApplyResult result = await _applyAsync(ViewModel, CancellationToken.None);
            args.Cancel = !result.Accepted;
            ViewModel.ValidationSummary = string.Join(Environment.NewLine, result.ValidationMessages);
        }
        catch (Exception exception)
        {
            args.Cancel = true;
            ViewModel.ValidationSummary =
                $"DesktopShift could not save this configuration. Your edits are still here. {exception.Message}";
        }
        finally
        {
            IsPrimaryButtonEnabled = true;
            deferral.Complete();
        }
    }
}

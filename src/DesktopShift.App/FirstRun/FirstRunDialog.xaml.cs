using System.Numerics;
using DesktopShift.App.ViewModels;
using Microsoft.UI.Xaml;
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
        Loaded += OnLoaded;
        SizeChanged += OnDialogSizeChanged;
        Unloaded += OnUnloaded;
    }

    public FirstRunViewModel ViewModel { get; }

    private void OnLoaded(object sender, RoutedEventArgs args)
    {
        Loaded -= OnLoaded;
        if (XamlRoot is not null)
        {
            XamlRoot.Changed += OnXamlRootChanged;
        }

        CenterInWindow();
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
        CenterInWindow();

    private void OnDialogSizeChanged(object sender, SizeChangedEventArgs args) =>
        CenterInWindow();

    private void CenterInWindow()
    {
        if (XamlRoot is null || ActualWidth <= 0)
        {
            return;
        }

        // WinUI's popup host anchors wide ContentDialogs at the window edge.
        // Translate from that anchor using the measured dialog width so the
        // surface remains centered when the window size or display scale moves.
        double horizontalOffset = Math.Max(0, (XamlRoot.Size.Width - ActualWidth) / 2);
        Translation = new Vector3((float)horizontalOffset, 0, 0);
    }

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

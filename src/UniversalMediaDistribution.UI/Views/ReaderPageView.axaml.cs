using Avalonia;
using Avalonia.Controls;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Views;

public partial class ReaderPageView : UserControl
{
    private ReaderPageViewModel? _activeViewModel;
    private bool _isAttached;

    public ReaderPageView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = true;
        await ActivateCurrentPageAsync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        // Continuous mode virtualizes its item containers. Keep the page bitmap
        // owned by ImageReaderViewModel so scrolling away and back does not
        // dispose and download the same page again.
        _isAttached = false;
        _activeViewModel = null;
    }

    private async void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_isAttached)
        {
            await ActivateCurrentPageAsync();
        }
    }

    private async Task ActivateCurrentPageAsync()
    {
        var viewModel = DataContext as ReaderPageViewModel;
        if (!ReferenceEquals(_activeViewModel, viewModel))
        {
            _activeViewModel = viewModel;
        }

        if (viewModel is null || viewModel.DefersLoadingToReader)
        {
            return;
        }

        await viewModel.LoadAsync();
        if (_isAttached && ReferenceEquals(DataContext, viewModel) &&
            viewModel.Image is null && viewModel.ErrorMessage is null)
        {
            await viewModel.LoadAsync();
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Views;

public partial class EpubContentBlockView : UserControl
{
    private EpubContentBlockViewModel? _activeViewModel;
    private bool _isAttached;

    public EpubContentBlockView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
    }

    private async void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = true;
        await ActivateCurrentBlockAsync();
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = false;
        _activeViewModel?.Release();
        _activeViewModel = null;
    }

    private async void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_isAttached)
        {
            await ActivateCurrentBlockAsync();
        }
    }

    private async Task ActivateCurrentBlockAsync()
    {
        var viewModel = DataContext as EpubContentBlockViewModel;
        if (!ReferenceEquals(_activeViewModel, viewModel))
        {
            _activeViewModel?.Release();
            _activeViewModel = viewModel;
        }

        if (viewModel is null)
        {
            return;
        }

        await viewModel.LoadAsync();
        if (_isAttached &&
            ReferenceEquals(DataContext, viewModel) &&
            viewModel.IsImage &&
            viewModel.Image is null &&
            viewModel.ErrorMessage is null)
        {
            await viewModel.LoadAsync();
        }
    }
}

using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.VisualTree;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Views;

public partial class CatalogArtworkView : UserControl
{
    private CatalogArtworkViewModel? _activeViewModel;
    private bool _isAttached;
    private Rect _viewport;
    private readonly List<Visual> _visibilityAncestors = [];

    public CatalogArtworkView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttachedToVisualTree;
        DetachedFromVisualTree += OnDetachedFromVisualTree;
        DataContextChanged += OnDataContextChanged;
        EffectiveViewportChanged += OnEffectiveViewportChanged;
        PropertyChanged += OnVisibilityChanged;
    }

    private void OnAttachedToVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = true;
        ImageMemoryPolicy.ArtworkActivityChanged += OnArtworkActivityChanged;
        // Wait for layout to supply the viewport. Attachment alone also
        // happens for hidden pages and every off-screen shelf.
        _viewport = default;
        foreach (var ancestor in this.GetVisualAncestors())
        {
            _visibilityAncestors.Add(ancestor);
            ancestor.PropertyChanged += OnVisibilityChanged;
        }
    }

    private void OnDetachedFromVisualTree(object? sender, VisualTreeAttachmentEventArgs eventArgs)
    {
        _isAttached = false;
        ImageMemoryPolicy.ArtworkActivityChanged -= OnArtworkActivityChanged;
        foreach (var ancestor in _visibilityAncestors)
            ancestor.PropertyChanged -= OnVisibilityChanged;
        _visibilityAncestors.Clear();
        _activeViewModel?.ReleaseView();
        _activeViewModel = null;
    }

    private async void OnEffectiveViewportChanged(object? sender, EffectiveViewportChangedEventArgs eventArgs)
    {
        _viewport = eventArgs.EffectiveViewport;
        await ActivateAsync();
    }

    private async void OnVisibilityChanged(object? sender, AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.Property == IsVisibleProperty)
            await ActivateAsync();
    }

    private async void OnArtworkActivityChanged() => await ActivateAsync();

    private async void OnDataContextChanged(object? sender, EventArgs eventArgs)
    {
        if (_isAttached)
        {
            await ActivateAsync();
        }
    }

    private async Task ActivateAsync()
    {
        var viewModel = _isAttached && IsEffectivelyVisible && !ImageMemoryPolicy.IsArtworkSuspended &&
            _viewport.Intersects(new Rect(Bounds.Size))
                ? DataContext as CatalogArtworkViewModel
                : null;
        if (!ReferenceEquals(_activeViewModel, viewModel))
        {
            _activeViewModel?.ReleaseView();
            _activeViewModel = viewModel;
            if (viewModel is not null)
                await viewModel.AcquireViewAsync();
        }
    }
}

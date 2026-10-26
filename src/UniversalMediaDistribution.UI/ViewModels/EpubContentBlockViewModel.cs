using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class EpubContentBlockViewModel : ViewModelBase, IDisposable
{
    private static readonly SemaphoreSlim LoadGate = new(2);
    private readonly PreparedEpubBook _book;
    private readonly PreparedEpubImage? _imageResource;
    private CancellationTokenSource? _loadCancellation;
    private Task? _loadTask;
    private bool _disposed;

    public EpubContentBlockViewModel(
        PreparedEpubContentBlock block,
        PreparedEpubBook book,
        double fontSize)
    {
        ArgumentNullException.ThrowIfNull(block);
        ArgumentNullException.ThrowIfNull(book);
        _book = book;
        _imageResource = block.Image;
        Kind = block.Kind;
        Text = block.Text ?? string.Empty;
        DisplayText = Kind is EpubContentBlockKind.ListItem
            ? "• " + Text
            : Text;
        AlternativeText = block.Image?.AlternativeText ?? string.Empty;
        FontSize = fontSize;
    }

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HeadingFontSize))]
    private double _fontSize;

    public EpubContentBlockKind Kind { get; }

    public string Text { get; }

    public string DisplayText { get; }

    public string AlternativeText { get; }

    public bool IsHeading => Kind is EpubContentBlockKind.Heading;

    public bool IsBodyText => Kind is
        EpubContentBlockKind.Paragraph or EpubContentBlockKind.ListItem;

    public bool IsImage => Kind is EpubContentBlockKind.Image;

    public bool HasAlternativeText => AlternativeText.Length > 0;

    public double HeadingFontSize => Math.Min(42, FontSize + 7);

    public Task LoadAsync()
    {
        if (!IsImage || Image is not null || _disposed)
        {
            return Task.CompletedTask;
        }

        if (_loadTask is { IsCompleted: false } &&
            _loadCancellation is { IsCancellationRequested: false })
        {
            return _loadTask;
        }

        _loadTask = LoadCoreAsync();
        return _loadTask;
    }

    public void Release()
    {
        _loadCancellation?.Cancel();
        var image = Image;
        Image = null;
        image?.Dispose();
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        Release();
        GC.SuppressFinalize(this);
    }

    internal void SetFontSize(double value) => FontSize = value;

    private async Task LoadCoreAsync()
    {
        if (_imageResource is null)
        {
            return;
        }

        var loadCancellation = new CancellationTokenSource();
        _loadCancellation = loadCancellation;
        var cancellationToken = loadCancellation.Token;
        var enteredGate = false;
        Bitmap? pendingImage = null;
        try
        {
            IsLoading = true;
            ErrorMessage = null;
            await LoadGate.WaitAsync(cancellationToken);
            enteredGate = true;
            await using var readHandle = await _book.OpenImageReadAsync(
                _imageResource,
                cancellationToken);
            var source = readHandle.Stream ??
                throw new InvalidOperationException(
                    UiTextCatalog.Get("EpubImageStreamMissing"));
            pendingImage = await Task.Run(
                () => BoundedImageDecoder.Decode(source, 1200, ImageMemoryPolicy.ReaderBitmapBytes),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            if (!_disposed)
            {
                Image = pendingImage;
                pendingImage = null;
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                ErrorMessage = UiTextCatalog.Format("EpubImageLoadFailed", exception.Message);
        }
        finally
        {
            pendingImage?.Dispose();
            if (enteredGate)
                LoadGate.Release();
            if (ReferenceEquals(_loadCancellation, loadCancellation))
            {
                IsLoading = false;
                _loadCancellation = null;
                _loadTask = null;
            }

            loadCancellation.Dispose();
        }
    }
}

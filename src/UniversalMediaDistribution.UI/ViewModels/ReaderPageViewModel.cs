using System.Buffers;
using Avalonia;
using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Reading;
using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class ReaderPageViewModel : ViewModelBase, IDisposable
{
    private static long MaximumDecodedSourceBytes => ImageMemoryPolicy.EncodedReaderBytes;
    private static readonly SemaphoreSlim LoadGate = new(2);
    private static readonly HttpClient RemoteImageClient = new(
        new HttpClientHandler
        {
            AllowAutoRedirect = false
        })
    {
        Timeout = TimeSpan.FromSeconds(30)
    };
    /// <summary>
    /// A scan can be several thousand pixels wide. Decoding it at source size
    /// costs width x height x 4 bytes and is thrown away by the draw anyway, so
    /// pages are decoded at the width they are actually shown at.
    /// </summary>
    private const int MinimumDecodeWidth = 320;

    /// <summary>
    /// Re-decoding is only worth it once the page is shown meaningfully larger
    /// than the copy in hand; below this the existing bitmap is upscaled.
    /// </summary>
    private const double RedecodeThreshold = 1.25;

    private readonly int _position;
    private readonly long _declaredLength;
    private readonly Func<CancellationToken, ValueTask<IMediaReadHandle>> _openReadAsync;
    private CancellationTokenSource? _loadCancellation;
    private Task? _loadTask;
    private bool _disposed;
    private int _decodedWidth;

    /// <summary>This page's own shape, once something has measured it.</summary>
    private double? _measuredAspectRatio;

    /// <summary>
    /// Whether this page's header can be read without paying for a request.
    /// True for anything on disk or inside an archive; false for a remote page.
    /// </summary>
    private readonly bool _canProbeCheaply = true;

    private bool _probed;

    /// <summary>
    /// Whether the reader decides when this page decodes.
    /// <para>
    /// In continuous reading every page has a container whether or not it is
    /// anywhere near the viewport, so a page that loaded itself on being
    /// attached would decode the whole chapter at once. The reader loads what
    /// is near the reading position instead. Paged reading shows one page at a
    /// time and keeps loading itself, since being shown is the whole signal.
    /// </para>
    /// </summary>
    internal bool DefersLoadingToReader { get; set; }

    /// <summary>
    /// What the rest of the chapter has measured, for use until this page has
    /// been decoded itself.
    /// </summary>
    private ReaderPageAspectEstimate? _aspectEstimate;

    public ReaderPageViewModel(
        PreparedImagePage page,
        TorrentMediaSessionService mediaSessionService)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(mediaSessionService);
        _position = page.Position;
        _declaredLength = page.File.Length;
        _openReadAsync = cancellationToken =>
            mediaSessionService.OpenReadAsync(page.File, cancellationToken);
    }

    public ReaderPageViewModel(
        PreparedArchiveImagePage page,
        PreparedImageArchive archive)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(archive);
        _position = page.Position;
        _declaredLength = page.Length;
        _openReadAsync = cancellationToken => archive.OpenReadAsync(page, cancellationToken);
    }

    public ReaderPageViewModel(
        PdfPageDescriptor page,
        IPdfDocument document)
    {
        ArgumentNullException.ThrowIfNull(page);
        ArgumentNullException.ThrowIfNull(document);
        _position = page.Position;
        _declaredLength = 0;
        _openReadAsync = cancellationToken =>
            document.OpenPageReadAsync(page, cancellationToken);
    }

    public ReaderPageViewModel(ApiContentImagePage page)
    {
        ArgumentNullException.ThrowIfNull(page);
        _position = page.Position;
        _declaredLength = 0;
        _openReadAsync = cancellationToken =>
            OpenRemoteImageAsync(
                page.Uri,
                page.Referrer,
                cancellationToken);

        // Measuring this page's header would mean a second request per page
        // over someone's connection, for a number the decode is about to
        // produce anyway. Remote chapters keep the estimate.
        _canProbeCheaply = false;
    }

    // No PlaceholderHeight notification here any more: the reserved height no
    // longer depends on whether the image is loaded, and raising it alongside
    // the bitmap invalidated layout twice for one event on every page a scroll
    // passed through. RememberAspectRatio still raises it, because that is
    // where the reserved height can actually change.
    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private string? _errorMessage;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PlaceholderHeight))]
    private double _displayWidth = 1000;

    /// <summary>
    /// Raised before a bitmap or display-width change can remeasure the
    /// continuous reader. The view captures its visible page while the old
    /// layout is still available.
    /// </summary>
    internal event EventHandler? LayoutMetricsChanging;

    /// <summary>
    /// How tall this page's box is, whether or not its image is currently
    /// loaded.
    /// <para>
    /// A page with no image measured 180px tall, so filling a phone viewport
    /// realized ten or more of them instead of one or two — and every one of
    /// those realizations starts a full-page decode. That burst is what made
    /// scrolling a chapter stutter. Reserving a page-shaped box keeps the
    /// realized set to what actually fits.
    /// </para>
    /// <para>
    /// It stays reserved once the image arrives rather than collapsing to
    /// zero. Dropping to zero left the height depending entirely on the live
    /// bitmap, so a page released while still on screen — which happens as the
    /// reader trims memory — collapsed its box and threw everything below it up
    /// the viewport. Holding the box costs nothing when the image is present,
    /// because the picture is stretched uniformly inside it, and the reserved
    /// height matches what the page actually measured once
    /// <see cref="RememberAspectRatio"/> has seen it.
    /// </para>
    /// <para>
    /// The old 180px floor is gone with it. It existed for the empty case, and
    /// once a real aspect ratio is known it could only disagree with the page's
    /// true height — reintroducing the mismatch the ratio was added to remove.
    /// A width of zero before the first layout pass is the one case still worth
    /// a fallback.
    /// </para>
    /// </summary>
    public double PlaceholderHeight =>
        DisplayWidth > 0
            ? DisplayWidth * CurrentAspectRatio
            : 180;

    /// <summary>
    /// This page's measured shape, or the chapter's average until it has one.
    /// </summary>
    private double CurrentAspectRatio =>
        _measuredAspectRatio ??
        _aspectEstimate?.Ratio ??
        ReaderPageAspectEstimate.TypicalPageAspectRatio;

    /// <summary>
    /// Shares one chapter-wide estimate with this page, so the box it reserves
    /// before being decoded is the shape its neighbours turned out to be.
    /// </summary>
    internal void UseAspectEstimate(ReaderPageAspectEstimate estimate)
    {
        ArgumentNullException.ThrowIfNull(estimate);
        if (_aspectEstimate is not null)
        {
            _aspectEstimate.Changed -= OnAspectEstimateChanged;
        }

        _aspectEstimate = estimate;
        estimate.Changed += OnAspectEstimateChanged;
    }

    /// <summary>
    /// A page that has been decoded knows its own shape and ignores the
    /// estimate; only the ones still reserving space need to measure again.
    /// </summary>
    private void OnAspectEstimateChanged(object? sender, EventArgs eventArgs)
    {
        if (_measuredAspectRatio is not null)
        {
            return;
        }

        LayoutMetricsChanging?.Invoke(this, EventArgs.Empty);
        OnPropertyChanged(nameof(PlaceholderHeight));
    }

    public int Position => _position;

    public int PageNumber => Position + 1;

    public string PageLabel => UiTextCatalog.Format("PageNumber", PageNumber);

    partial void OnImageChanging(Bitmap? value) =>
        LayoutMetricsChanging?.Invoke(this, EventArgs.Empty);

    partial void OnDisplayWidthChanging(double value) =>
        LayoutMetricsChanging?.Invoke(this, EventArgs.Empty);

    public Task LoadAsync()
    {
        if (_disposed || (Image is not null && !NeedsSharperDecode))
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

    /// <summary>
    /// Learns this page's true proportions from its header, so the box it
    /// reserves is right before anything is decoded.
    /// <para>
    /// The alternative is a box sized from the chapter's average, which is
    /// corrected when the picture finally arrives — and correcting the height of
    /// a page at or above the viewport moves what is being read. Reading a
    /// header costs one small read; decoding costs the whole image.
    /// </para>
    /// </summary>
    internal async Task ProbeSizeAsync(CancellationToken cancellationToken)
    {
        if (_probed || !_canProbeCheaply || _measuredAspectRatio is not null)
        {
            return;
        }

        _probed = true;
        try
        {
            await using var readHandle = await _openReadAsync(cancellationToken);
            if (readHandle.Stream is not { } source)
            {
                return;
            }

            // Rented rather than allocated: the header window is 128 KiB, which
            // is over the 85 KiB large-object threshold, so a fresh array per
            // page put one LOH allocation on the heap for every page of a
            // chapter. A few hundred pages of a manga chapter fragmented the
            // 32-bit televisions' address space for no reason - the buffer is
            // dead the moment the header has been read.
            var buffer = ArrayPool<byte>.Shared.Rent(
                ImageDimensionReader.MaximumHeaderBytes);
            try
            {
                var filled = 0;
                while (filled < ImageDimensionReader.MaximumHeaderBytes)
                {
                    var read = await source.ReadAsync(
                        buffer.AsMemory(
                            filled,
                            ImageDimensionReader.MaximumHeaderBytes - filled),
                        cancellationToken);
                    if (read == 0)
                    {
                        break;
                    }

                    filled += read;
                }

                if (ImageDimensionReader.TryRead(buffer.AsSpan(0, filled)) is
                    { } size)
                {
                    ApplyMeasuredSize(size);
                }
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(buffer);
            }
        }
        catch (Exception)
        {
            // A page whose header cannot be read keeps the estimate and is
            // corrected by its decode, exactly as before. Nothing here is worth
            // showing anyone.
        }
    }

    /// <summary>
    /// Tries the page again after a failure.
    /// <para>
    /// A timed-out or refused image is not a permanent state — the host was
    /// slow, the connection dropped, the token expired — but nothing retried
    /// it. The page held its error for the rest of the chapter and the only way
    /// past it was to leave and come back, which reloads every other page too.
    /// </para>
    /// </summary>
    [RelayCommand]
    private Task RetryAsync()
    {
        ErrorMessage = null;
        return LoadAsync();
    }

    /// <summary>
    /// Whether this page failed and is sitting on an error the reader can
    /// offer to retry.
    /// </summary>
    public bool HasFailed => !IsLoading && !string.IsNullOrEmpty(ErrorMessage);

    partial void OnErrorMessageChanged(string? value) =>
        OnPropertyChanged(nameof(HasFailed));

    partial void OnIsLoadingChanged(bool value) =>
        OnPropertyChanged(nameof(HasFailed));

    /// <summary>
    /// True once zoom or a wider window has made the page larger than the
    /// decoded copy, which would otherwise show as a soft, upscaled image.
    /// </summary>
    private bool NeedsSharperDecode =>
        _decodedWidth > 0 &&
        TargetDecodeWidth > _decodedWidth * RedecodeThreshold;

    private int TargetDecodeWidth =>
        (int)Math.Max(MinimumDecodeWidth, Math.Ceiling(DisplayWidth));

    private async Task LoadCoreAsync()
    {
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
            cancellationToken.ThrowIfCancellationRequested();
            await using var readHandle = await _openReadAsync(cancellationToken);
            var source = readHandle.Stream ??
                throw new InvalidOperationException(
                    UiTextCatalog.Get("ImageStreamMissing"));
            await using var buffered = await BufferAsync(source, _declaredLength, cancellationToken);
            var targetWidth = TargetDecodeWidth;
            // Off the UI thread. Every await above resumes on whatever context
            // started the load, and pages are loaded from the view as their
            // containers are realized, so the decode ran on the UI thread —
            // a full-resolution decode plus a rescale, per page, while the
            // scroll that realized it was still moving. A desktop CPU hides
            // that; a phone does not, and it is what made scrolling a chapter
            // stutter there. The bitmap is only published after the await, so
            // it still reaches the binding on the UI thread.
            pendingImage = await Task.Run(
                () => Decode(buffered, targetWidth),
                cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            var previous = Image;
            Image = pendingImage;
            pendingImage = null;
            previous?.Dispose();
            RememberAspectRatio(Image);
            _decodedWidth = targetWidth;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (Exception exception)
        {
            if (!cancellationToken.IsCancellationRequested)
                ErrorMessage = UiTextCatalog.Format("PageLoadFailed", exception.Message);
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

    public void Release()
    {
        _loadCancellation?.Cancel();
        var image = Image;
        Image = null;
        image?.Dispose();
        _decodedWidth = 0;
    }

    /// <summary>
    /// Decodes at the shown width. A page narrower than the target is decoded
    /// as-is rather than being blown up.
    /// </summary>
    private static Bitmap Decode(Stream stream, int width) =>
        BoundedImageDecoder.Decode(stream, width, ImageMemoryPolicy.ReaderBitmapBytes);

    private void RememberAspectRatio(Bitmap bitmap)
    {
        var pixelSize = bitmap.PixelSize;
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            return;
        }

        ApplyMeasuredSize(pixelSize);
    }

    private void ApplyMeasuredSize(PixelSize pixelSize)
    {
        if (pixelSize.Width <= 0 || pixelSize.Height <= 0)
        {
            return;
        }

        var ratio = (double)pixelSize.Height / pixelSize.Width;
        var isFirstMeasurement = _measuredAspectRatio is null;
        if (!isFirstMeasurement &&
            Math.Abs(ratio - _measuredAspectRatio!.Value) < 0.0001)
        {
            return;
        }

        // The box is about to change size, so the view has to capture where it
        // was reading before the new layout replaces it.
        LayoutMetricsChanging?.Invoke(this, EventArgs.Empty);
        _measuredAspectRatio = ratio;

        // Only the first measurement contributes: a header read and the decode
        // that follows describe the same page, and counting both would weight
        // the chapter's average towards whichever pages were measured twice.
        if (isFirstMeasurement)
        {
            _aspectEstimate?.Report(ratio);
        }

        OnPropertyChanged(nameof(PlaceholderHeight));
    }

    public void Dispose()
    {
        _disposed = true;
        if (_aspectEstimate is not null)
        {
            _aspectEstimate.Changed -= OnAspectEstimateChanged;
            _aspectEstimate = null;
        }

        Release();
        GC.SuppressFinalize(this);
    }

    internal void SetDisplayWidth(double value)
    {
        if (Math.Abs(DisplayWidth - value) > 0.5)
        {
            DisplayWidth = value;
        }
    }

    internal static async ValueTask<Stream> BufferAsync(
        Stream source,
        long declaredLength,
        CancellationToken cancellationToken)
    {
        if (declaredLength > MaximumDecodedSourceBytes)
        {
            throw new InvalidOperationException(
                UiTextCatalog.Get("ImageTooLarge"));
        }

        // Keep the encoded page off the large object heap. Remote images often
        // have no declared length, so a growing MemoryStream used to allocate
        // several copies while two pages decoded concurrently.
        try
        {
            return await ArtworkDownloadBuffer.CopyAsync(
                source, MaximumDecodedSourceBytes, cancellationToken).ConfigureAwait(false);
        }
        catch (InvalidDataException exception)
        {
            throw new InvalidOperationException(UiTextCatalog.Get("ImageTooLarge"), exception);
        }
    }

    private static async ValueTask<IMediaReadHandle> OpenRemoteImageAsync(
        Uri uri,
        Uri? referrer,
        CancellationToken cancellationToken)
    {
        using var request = RemoteImageHttpRequest.Create(uri, referrer);
        var response = await RemoteImageClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        try
        {
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > MaximumDecodedSourceBytes)
            {
                throw new InvalidDataException(
                    UiTextCatalog.Get("ImageTooLarge"));
            }

            var contentType = response.Content.Headers.ContentType?.MediaType;
            if (contentType is not null &&
                !contentType.Equals(
                    "image/jpeg",
                    StringComparison.OrdinalIgnoreCase) &&
                !contentType.Equals(
                    "image/png",
                    StringComparison.OrdinalIgnoreCase) &&
                !contentType.Equals(
                    "image/webp",
                    StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidDataException(
                    UiTextCatalog.Get("RemoteImageTypeUnsupported"));
            }

            var stream = await response.Content.ReadAsStreamAsync(
                cancellationToken);
            return new RemoteImageReadHandle(response, stream);
        }
        catch
        {
            response.Dispose();
            throw;
        }
    }

    private sealed class RemoteImageReadHandle(
        HttpResponseMessage response,
        Stream stream) : IMediaReadHandle
    {
        private readonly Stream _stream =
            stream ?? throw new ArgumentNullException(nameof(stream));

        public Stream? Stream => _stream;

        public Uri? PlaybackUri => null;

        public async ValueTask DisposeAsync()
        {
            await _stream.DisposeAsync();
            response.Dispose();
        }
    }
}

using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.UI.ViewModels;

public partial class CatalogArtworkViewModel(
    string reference,
    CatalogMediaSourceDetails? torrentContext,
    TorrentMediaSessionService mediaSessionService,
    IReadOnlyList<Uri>? trustedRemoteOrigins = null,
    int decodeWidth = CatalogArtworkViewModel.PosterDecodeWidth,
    Uri? remoteReferrer = null)
    : ViewModelBase, IDisposable
{
    private const long MaximumArtworkBytes = 24L * 1024 * 1024;

    /// <summary>
    /// Posters are drawn into a 300 px card, so they are decoded at that width
    /// instead of their source resolution. A 1000x1500 cover costs about 6 MB
    /// fully decoded and about 0.5 MB at card width, which is the difference
    /// between a catalog page that fits in memory and one that does not.
    /// </summary>
    public const int PosterDecodeWidth = 300;

    /// <summary>
    /// Chapter and episode rows show a small square, so they decode smaller
    /// still. This matters most in a virtualized list, where rows are built
    /// again every time they scroll back into view.
    /// </summary>
    public const int ThumbnailDecodeWidth = 96;

    /// <summary>
    /// Wide art behind a title page header. Decoded larger than a poster
    /// because it spans the window, and still far below source resolution.
    /// </summary>
    public const int BackdropDecodeWidth = 960;

    /// <summary>
    /// A page can attach a hundred posters at once. Decoding is CPU bound and
    /// fetching is network bound, so both are kept to a handful at a time; the
    /// rest queue instead of competing for the same cores.
    /// </summary>
    private static readonly SemaphoreSlim LoadGate =
        new(ImageMemoryPolicy.ArtworkLoadConcurrency);
    /// <summary>
    /// Artwork hosts redirect constantly — a poster URL recorded as
    /// <c>www.themoviedb.org</c> answers with a 301 to <c>image.tmdb.org</c>,
    /// and provider CDNs move paths the same way. With redirects refused every
    /// one of those became EnsureSuccessStatusCode throwing, which reads to the
    /// viewer as a thumbnail that never arrives. The hop count is small on
    /// purpose, and the response is still only ever decoded as an image.
    /// </summary>
    private static readonly HttpClient RemoteImageClient = new(
        new HttpClientHandler
        {
            AllowAutoRedirect = true,
            MaxAutomaticRedirections = 3
        })
    {
        Timeout = TimeSpan.FromSeconds(20)
    };
    private CancellationTokenSource? _loadCancellation;
    private Task? _loadTask;
    private bool _disposed;
    private int _visibleViews;

    [ObservableProperty]
    private Bitmap? _image;

    [ObservableProperty]
    private bool _isLoading;

    [ObservableProperty]
    private bool _hasError;

    private readonly string CacheKey =
        DecodedArtworkCache.CreateKey(reference, decodeWidth);

    internal Task AcquireViewAsync()
    {
        _visibleViews++;
        return LoadAsync();
    }

    internal void ReleaseView()
    {
        if (_visibleViews > 0 && --_visibleViews == 0)
            Release();
    }

    public Task LoadAsync()
    {
        if (_disposed || Image is not null || HasError)
        {
            return Task.CompletedTask;
        }

        if (_loadTask is { IsCompleted: false } &&
            _loadCancellation is { IsCancellationRequested: false })
        {
            return _loadTask;
        }

        // A row that scrolls back into view gets its image straight back.
        if (DecodedArtworkCache.TryTake(CacheKey) is { } cached)
        {
            Image = cached;
            return Task.CompletedTask;
        }

        _loadTask = LoadCoreAsync();
        return _loadTask;
    }

    /// <summary>
    /// Called when the view detaches. The decoded image is handed to the shared
    /// cache rather than thrown away, because virtualized rows detach and
    /// re-attach constantly while scrolling.
    /// </summary>
    public void Release()
    {
        _loadCancellation?.Cancel();
        var image = Image;
        Image = null;
        if (image is not null)
        {
            DecodedArtworkCache.Return(CacheKey, image);
        }

        // A failed load must not outlive the view that saw it. These view
        // models are deliberately retained across catalog refreshes, so
        // without this a single transient error would leave the poster broken
        // until the app restarts; scrolling back is the retry.
        HasError = false;
    }

    public void Dispose()
    {
        _disposed = true;
        Release();
        GC.SuppressFinalize(this);
    }

    internal static Bitmap DecodeAtWidth(
        Stream stream,
        int width = PosterDecodeWidth) =>
        BoundedImageDecoder.Decode(stream, width, 4L * 1024 * 1024);

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
            // Own the cancellation source before queuing, so detached cards
            // never proceed to download/decode when a slot becomes available.
            await LoadGate.WaitAsync(cancellationToken);
            enteredGate = true;
            cancellationToken.ThrowIfCancellationRequested();
            if (TryGetCacheableRemoteUri(reference, out var cachedRemoteUri))
            {
                pendingImage = await CatalogArtworkCache.TryLoadAsync(
                    cachedRemoteUri,
                    MaximumArtworkBytes,
                    decodeWidth,
                    cancellationToken);
                if (pendingImage is not null)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Image = pendingImage;
                    pendingImage = null;
                    return;
                }
            }

            if (TryGetSupportedRemoteUri(
                    reference,
                    trustedRemoteOrigins,
                    out var remoteUri))
            {
                await using var content = await LoadRemoteImageAsync(
                    remoteUri,
                    remoteReferrer,
                    cancellationToken);
                pendingImage = await Task.Run(() => DecodeAtWidth(content, decodeWidth), cancellationToken);
                await CatalogArtworkCache.StoreAsync(
                    remoteUri,
                    content,
                    cancellationToken);
                cancellationToken.ThrowIfCancellationRequested();
                Image = pendingImage;
                pendingImage = null;
                return;
            }

            if (torrentContext is null)
            {
                throw new InvalidOperationException(
                    "Relative artwork requires a mapped torrent source.");
            }

            var file = await mediaSessionService.PrepareReferencedFileAsync(
                torrentContext,
                reference,
                cancellationToken);
            if (file.Length > MaximumArtworkBytes)
            {
                throw new InvalidOperationException("Artwork is larger than 24 MiB.");
            }

            await using var readHandle = await mediaSessionService.OpenReadAsync(file, cancellationToken);
            var source = readHandle.Stream ??
                throw new InvalidOperationException("The torrent engine did not provide an artwork stream.");
            await using var buffered = await ArtworkDownloadBuffer.CopyAsync(
                source, MaximumArtworkBytes, cancellationToken);
            pendingImage = await Task.Run(() => DecodeAtWidth(buffered, decodeWidth), cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            Image = pendingImage;
            pendingImage = null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            if (!cancellationToken.IsCancellationRequested)
                HasError = true;
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

    internal static bool IsSupportedRemoteReference(
        string reference,
        IReadOnlyList<Uri>? trustedRemoteOrigins = null) =>
        TryGetSupportedRemoteUri(reference, trustedRemoteOrigins, out _);

    internal static bool IsCacheableRemoteReference(string reference) =>
        TryGetCacheableRemoteUri(reference, out _);

    private static bool TryGetSupportedRemoteUri(
        string reference,
        IReadOnlyList<Uri>? trustedRemoteOrigins,
        out Uri uri)
    {
        if (TryGetCacheableRemoteUri(reference, out var candidate) &&
            (IsKnownMetadataArtworkUri(candidate) ||
             trustedRemoteOrigins?.Any(origin =>
                 IsSameOrigin(candidate, origin)) is true))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool TryGetCacheableRemoteUri(
        string reference,
        out Uri uri)
    {
        if (Uri.TryCreate(reference, UriKind.Absolute, out var candidate) &&
            candidate.Scheme is "http" or "https" &&
            candidate.UserInfo.Length == 0)
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsKnownMetadataArtworkUri(Uri candidate) =>
        candidate.Scheme == Uri.UriSchemeHttps &&
        candidate.IsDefaultPort &&
        (string.Equals(
             candidate.Host,
             "image.tmdb.org",
             StringComparison.OrdinalIgnoreCase) ||
         string.Equals(
             candidate.Host,
             "cdn.myanimelist.net",
             StringComparison.OrdinalIgnoreCase));

    private static bool IsSameOrigin(Uri candidate, Uri trustedRemoteOrigin) =>
        candidate.Scheme is "http" or "https" &&
        string.Equals(
            candidate.Scheme,
            trustedRemoteOrigin.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            candidate.IdnHost,
            trustedRemoteOrigin.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        candidate.Port == trustedRemoteOrigin.Port;

    private static async Task<Stream> LoadRemoteImageAsync(
        Uri uri,
        Uri? providerReferrer,
        CancellationToken cancellationToken)
    {
        // The hosts these posters live on serve scraped sites, and several
        // refuse an image request that arrives with no referrer at all. The
        // provider-supplied page wins when its CDN checks hotlinks. Other
        // sources keep the same-origin fallback used before this metadata was
        // available.
        var referrer = providerReferrer ??
            new Uri($"{uri.Scheme}://{uri.Authority}/");
        using var request = RemoteImageHttpRequest.Create(uri, referrer);
        using var response = await WebChallengeGate.SendImageAsync(
            RemoteImageClient,
            request,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumArtworkBytes)
        {
            throw new InvalidDataException("Artwork is larger than 24 MiB.");
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        if (mediaType is not null &&
            !mediaType.Equals("image/jpeg", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Equals("image/png", StringComparison.OrdinalIgnoreCase) &&
            !mediaType.Equals("image/webp", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "The metadata provider returned an unsupported artwork type.");
        }

        await using var source =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        return await ArtworkDownloadBuffer.CopyAsync(
            source, MaximumArtworkBytes, cancellationToken).ConfigureAwait(false);
    }
}

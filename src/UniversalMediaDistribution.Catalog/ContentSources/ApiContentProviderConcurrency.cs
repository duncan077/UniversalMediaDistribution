using System.Collections.Concurrent;
using System.Net;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Coordinates all API-provider work owned by one catalog service. The global
/// gate covers the complete provider operation, including response parsing, so
/// it limits CPU work as well as network fan-out. Each provider also receives a
/// separate request queue for its outbound HTTP traffic.
/// </summary>
internal sealed class ApiContentProviderConcurrencyCoordinator
{
    internal const int MaximumConcurrentProviderOperations = 3;
    internal const int MaximumConcurrentRequestsPerProvider = 4;

    private readonly SemaphoreSlim _providerOperations = new(
        MaximumConcurrentProviderOperations,
        MaximumConcurrentProviderOperations);
    private readonly ConcurrentDictionary<string, ApiProviderRequestQueue>
        _requestQueues = new(StringComparer.Ordinal);

    internal IApiContentProvider Coordinate(IApiContentProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        return new CoordinatedApiContentProvider(provider, this);
    }

    internal async ValueTask<ApiProviderOperationLease> AcquireAsync(
        string providerId,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        var requestQueue = _requestQueues.GetOrAdd(
            providerId,
            static _ => new ApiProviderRequestQueue(
                MaximumConcurrentRequestsPerProvider));
        await _providerOperations.WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return new ApiProviderOperationLease(
            _providerOperations,
            requestQueue);
    }
}

/// <summary>
/// Adds operation admission and the provider request context without changing
/// provider implementations or allowing nested default-interface calls to
/// acquire the global permit again.
/// </summary>
internal sealed class CoordinatedApiContentProvider(
    IApiContentProvider inner,
    ApiContentProviderConcurrencyCoordinator coordinator)
    : IApiContentProvider
{
    private readonly IApiContentProvider _inner =
        inner ?? throw new ArgumentNullException(nameof(inner));
    private readonly ApiContentProviderConcurrencyCoordinator _coordinator =
        coordinator ?? throw new ArgumentNullException(nameof(coordinator));

    public ApiContentProviderDescriptor Descriptor => _inner.Descriptor;

    public bool SupportsDailyUpdates => _inner.SupportsDailyUpdates;

    public ApiContentSearchCapabilities SearchCapabilities =>
        _inner.SearchCapabilities;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) =>
        _inner.GetTrustedArtworkOrigins(baseUri);

    public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri) =>
        _inner.GetArtworkReferrer(baseUri, artworkUri);

    public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit) =>
        _inner.GetKnownUnitRoutes(baseUri, searchItem, unit);

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        LoadDailyUpdatesAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.LoadDailyUpdatesAsync(
            baseUri,
            cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.SearchAsync(
            baseUri,
            query,
            mediaType,
            cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.SearchAsync(
            baseUri,
            request,
            cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.LoadItemAsync(
            baseUri,
            searchItem,
            cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<ApiContentItem> LoadItemForUnitAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.LoadItemForUnitAsync(
            baseUri,
            searchItem,
            unit,
            cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<ApiContentImageSet?> LoadUnitImagesAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.LoadUnitImagesAsync(
            baseUri,
            searchItem,
            unit,
            cancellationToken)
                .ConfigureAwait(false);
    }

    public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        using var operation = await _coordinator.AcquireAsync(
            Descriptor.Id,
            cancellationToken).ConfigureAwait(false);
        using var requestScope = ApiProviderRequestContext.Enter(
            operation.RequestQueue);
        return await _inner.LoadUnitVideosAsync(
            baseUri,
            searchItem,
            unit,
            cancellationToken)
                .ConfigureAwait(false);
    }
}

/// <summary>
/// Central send path for API providers and their hoster extractors. A request
/// permit remains held until the response content is disposed, which preserves
/// the limit for callers using <see cref="HttpCompletionOption.ResponseHeadersRead"/>.
/// </summary>
internal static class ApiProviderHttp
{
    internal static async Task<HttpResponseMessage> SendAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(request);
        ApiProviderRequestMetricsContext.Current?.Increment();
        var response = await SendWithChallengeRetryAsync(
                httpClient,
                request,
                completionOption,
                cancellationToken)
            .ConfigureAwait(false);
        return response;
    }

    /// <summary>
    /// Sends a request, and when the host answers with a browser check rather
    /// than content, obtains clearance once and sends it again.
    /// <para>
    /// Only a challenge triggers this. An ordinary 403 is left alone, so a
    /// genuinely forbidden route does not start a browser on every request.
    /// </para>
    /// </summary>
    private static async Task<HttpResponseMessage>
        SendWithChallengeRetryAsync(
            HttpClient httpClient,
            HttpRequestMessage request,
            HttpCompletionOption completionOption,
            CancellationToken cancellationToken)
    {
        // Captured before sending: a request cannot be sent twice, and its
        // content stream is consumed by the first attempt.
        var retryTemplate = WebChallengeGate.HasSolver
            ? await TryCloneAsync(request, cancellationToken)
                .ConfigureAwait(false)
            : null;
        WebChallengeGate.TryApply(request);
        var response = await SendCoreAsync(
                httpClient,
                request,
                completionOption,
                cancellationToken)
            .ConfigureAwait(false);

        // Every provider request passes through here, which makes it the one
        // place that sees a site announce it has moved.
        ProviderBaseUrlRedirects.Observe(request, response);
        if (retryTemplate is null ||
            request.RequestUri is not { } uri ||
            !WebChallengeGate.IsChallenge(response))
        {
            retryTemplate?.Dispose();
            return response;
        }

        // The page first, because it is what actually transfers. Asking for
        // clearance ahead of it costs a full timeout on every request to a
        // host that issues none — and a host that admits a browser without
        // challenging it never will. Clearance is still worth having when it
        // exists, since it makes later requests ordinary HTTP, so it is tried
        // second rather than not at all.
        var document = request.Method == HttpMethod.Get
            ? await WebChallengeGate
                .TryFetchDocumentAsync(uri, cancellationToken)
                .ConfigureAwait(false)
            : null;
        if (document is not null)
        {
            retryTemplate.Dispose();
            response.Dispose();
            return new HttpResponseMessage(System.Net.HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = document.CreateContent()
            };
        }

        if (!await WebChallengeGate
                .TrySolveAsync(uri, cancellationToken)
                .ConfigureAwait(false))
        {
            retryTemplate.Dispose();
            return response;
        }

        response.Dispose();
        WebChallengeGate.TryApply(retryTemplate);
        return await SendCoreAsync(
                httpClient,
                retryTemplate,
                completionOption,
                cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Copies a request so it can be sent a second time. Returns null when the
    /// body cannot be replayed, which simply means no retry is attempted.
    /// </summary>
    private static async ValueTask<HttpRequestMessage?> TryCloneAsync(
        HttpRequestMessage request,
        CancellationToken cancellationToken)
    {
        try
        {
            var clone = new HttpRequestMessage(
                request.Method,
                request.RequestUri);
            foreach (var header in request.Headers)
            {
                clone.Headers.TryAddWithoutValidation(
                    header.Key,
                    header.Value);
            }

            if (request.Content is { } content)
            {
                var bytes = await content
                    .ReadAsByteArrayAsync(cancellationToken)
                    .ConfigureAwait(false);
                var copy = new ByteArrayContent(bytes);
                foreach (var header in content.Headers)
                {
                    copy.Headers.TryAddWithoutValidation(
                        header.Key,
                        header.Value);
                }

                clone.Content = copy;
            }

            return clone;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private static async Task<HttpResponseMessage> SendCoreAsync(
        HttpClient httpClient,
        HttpRequestMessage request,
        HttpCompletionOption completionOption,
        CancellationToken cancellationToken)
    {
        var queue = ApiProviderRequestContext.Current;
        if (queue is null)
        {
            return await httpClient.SendAsync(
                    request,
                    completionOption,
                    cancellationToken)
                .ConfigureAwait(false);
        }

        ApiProviderRequestLease? lease =
            await queue.AcquireAsync(cancellationToken)
                .ConfigureAwait(false);
        HttpResponseMessage? response = null;
        try
        {
            response = await httpClient.SendAsync(
                    request,
                    completionOption,
                    cancellationToken)
                .ConfigureAwait(false);
            response.Content = new LeaseReleasingHttpContent(
                response.Content,
                lease);
            lease = null;
            return response;
        }
        catch
        {
            response?.Dispose();
            lease?.Dispose();
            throw;
        }
    }

    private sealed class LeaseReleasingHttpContent : HttpContent
    {
        private readonly HttpContent _inner;
        private ApiProviderRequestLease? _lease;

        internal LeaseReleasingHttpContent(
            HttpContent inner,
            ApiProviderRequestLease lease)
        {
            _inner = inner ?? throw new ArgumentNullException(nameof(inner));
            _lease = lease ?? throw new ArgumentNullException(nameof(lease));
            foreach (var header in inner.Headers)
            {
                Headers.TryAddWithoutValidation(header.Key, header.Value);
            }
        }

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context) =>
            _inner.CopyToAsync(stream, context);

        protected override Task SerializeToStreamAsync(
            Stream stream,
            TransportContext? context,
            CancellationToken cancellationToken) =>
            _inner.CopyToAsync(stream, context, cancellationToken);

        protected override Stream CreateContentReadStream(
            CancellationToken cancellationToken) =>
            _inner.ReadAsStream(cancellationToken);

        protected override Task<Stream> CreateContentReadStreamAsync() =>
            _inner.ReadAsStreamAsync();

        protected override Task<Stream> CreateContentReadStreamAsync(
            CancellationToken cancellationToken) =>
            _inner.ReadAsStreamAsync(cancellationToken);

        protected override bool TryComputeLength(out long length)
        {
            if (_inner.Headers.ContentLength is { } contentLength)
            {
                length = contentLength;
                return true;
            }

            length = 0;
            return false;
        }

        protected override void Dispose(bool disposing)
        {
            try
            {
                if (disposing)
                {
                    _inner.Dispose();
                }
            }
            finally
            {
                if (disposing)
                {
                    Interlocked.Exchange(ref _lease, null)?.Dispose();
                }

                base.Dispose(disposing);
            }
        }
    }
}

internal sealed class ApiProviderRequestMetrics
{
    private int _requestCount;

    internal int RequestCount => Volatile.Read(ref _requestCount);

    internal void Increment() => Interlocked.Increment(ref _requestCount);
}

internal static class ApiProviderRequestMetricsContext
{
    private static readonly AsyncLocal<ApiProviderRequestMetrics?> Metrics =
        new();

    internal static ApiProviderRequestMetrics? Current => Metrics.Value;

    internal static ApiProviderRequestMetricsScope Enter(
        ApiProviderRequestMetrics metrics)
    {
        ArgumentNullException.ThrowIfNull(metrics);
        var previous = Metrics.Value;
        Metrics.Value = metrics;
        return new ApiProviderRequestMetricsScope(previous);
    }

    internal readonly struct ApiProviderRequestMetricsScope(
        ApiProviderRequestMetrics? previous)
        : IDisposable
    {
        public void Dispose() => Metrics.Value = previous;
    }
}

internal sealed class ApiProviderRequestQueue(int maximumConcurrency)
{
    private readonly SemaphoreSlim _requests = new(
        maximumConcurrency > 0
            ? maximumConcurrency
            : throw new ArgumentOutOfRangeException(
                nameof(maximumConcurrency)),
        maximumConcurrency);

    internal async ValueTask<ApiProviderRequestLease> AcquireAsync(
        CancellationToken cancellationToken)
    {
        await _requests.WaitAsync(cancellationToken).ConfigureAwait(false);
        return new ApiProviderRequestLease(_requests);
    }
}

internal sealed class ApiProviderRequestLease(SemaphoreSlim requests)
    : IDisposable
{
    private SemaphoreSlim? _requests =
        requests ?? throw new ArgumentNullException(nameof(requests));

    public void Dispose() =>
        Interlocked.Exchange(ref _requests, null)?.Release();
}

internal sealed class ApiProviderOperationLease(
    SemaphoreSlim providerOperations,
    ApiProviderRequestQueue requestQueue)
    : IDisposable
{
    private SemaphoreSlim? _providerOperations =
        providerOperations ??
        throw new ArgumentNullException(nameof(providerOperations));

    internal ApiProviderRequestQueue RequestQueue { get; } =
        requestQueue ?? throw new ArgumentNullException(nameof(requestQueue));

    public void Dispose() =>
        Interlocked.Exchange(ref _providerOperations, null)?.Release();
}

internal static class ApiProviderRequestContext
{
    private static readonly AsyncLocal<ApiProviderRequestQueue?> Queue = new();

    internal static ApiProviderRequestQueue? Current => Queue.Value;

    internal static ApiProviderRequestScope Enter(
        ApiProviderRequestQueue requestQueue)
    {
        ArgumentNullException.ThrowIfNull(requestQueue);
        var previous = Queue.Value;
        Queue.Value = requestQueue;
        return new ApiProviderRequestScope(previous);
    }

    internal readonly struct ApiProviderRequestScope(
        ApiProviderRequestQueue? previous)
        : IDisposable
    {
        public void Dispose() => Queue.Value = previous;
    }
}

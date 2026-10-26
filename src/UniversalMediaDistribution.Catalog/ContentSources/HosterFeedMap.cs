using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Feed map of public hoster extractors ported from
/// speedyconzales/series-scraper (Python). Each entry takes an
/// <see cref="ApiContentVideoSource"/> of kind <c>EmbeddedWeb</c> and
/// returns a <see cref="ApiContentVideoSource"/> of kind <c>DirectMedia</c>
/// pointing at the underlying media file, so the UMD native player can
/// open it without the embedded WebView. The hoster order in
/// <see cref="Default"/> matches the priority used by the original
/// repository, extended with the IronHentai wrappers and
/// Mp4Upload.
/// </summary>
public static class HosterFeedMap
{
    public static IReadOnlyList<IApiVideoHosterExtractor> Default() =>
        [
            new PremiumVipHosterExtractor(),
            new PlusTubeHosterExtractor(),
            new NuploadHosterExtractor(),
            new VoeHosterExtractor(),
            new YourUploadHosterExtractor(),
            new VixcloudHosterExtractor(),
            new UpzoneHosterExtractor(),
            new UqloadHosterExtractor(),
            new UpZurHosterExtractor(),
            new FilemoonHosterExtractor(),
            new FrembedHosterExtractor(),
            new StreamSbHosterExtractor(),
            new StreamlareHosterExtractor(),
            new NinjaStreamHosterExtractor(),
            new UchHosterExtractor(),
            new StreamtapeHosterExtractor(),
            new Mp4UploadHosterExtractor(),
            new DoodstreamHosterExtractor(),
            new VidmolyHosterExtractor(),
            new VidozaHosterExtractor(),
            new LoadXHosterExtractor(),
            new SpeedFilesHosterExtractor(),
            new VidHideHosterExtractor(),
            new StreamWishHosterExtractor(),
            new MediafireHosterExtractor(),
            new MixdropHosterExtractor(),
            new PlayerWrapperHosterExtractor(),
            new ZillaHosterExtractor(),
            new OkRuHosterExtractor(),
            new Html5HosterExtractor(),
            new DPlayerHosterExtractor()
        ];

    internal static bool Supports(Uri playbackUri) =>
        Default().Any(extractor => extractor.Matches(playbackUri));
}

/// <summary>
/// Walks the registered <see cref="IApiVideoHosterExtractor"/> list and
/// rewrites every <see cref="ApiContentVideoSourceKind.EmbeddedWeb"/>
/// source whose host is supported into a
/// <see cref="ApiContentVideoSourceKind.DirectMedia"/> source. Unknown
/// hosters fall back to the original <c>EmbeddedWeb</c> source so the
/// WebView can still render them.
/// </summary>
public sealed class VideoHosterResolver
{
    internal const int MaximumResolvedSourceCacheEntries = 256;
    internal const int MaximumRefreshableSourceEntries = 256;

    /// <summary>
    /// How many hoster pages may be fetched at once while resolving one
    /// title's sources. Enough to hide a slow hoster behind the others,
    /// small enough not to open a connection per server.
    /// </summary>
    private const int MaximumResolveConcurrency = 4;

    /// <summary>
    /// Shared by every resolve, not created per call. Built per invocation it
    /// bounded one title's sources and nothing more, so K concurrent lookups
    /// opened 4K hoster connections at once — and the Live TV path, which
    /// never enters the per-provider request queue, had no other backstop.
    /// </summary>
    private static readonly SemaphoreSlim ResolveConcurrency =
        new(MaximumResolveConcurrency, MaximumResolveConcurrency);

    /// <summary>
    /// How long one source may spend across its whole redirect chain. The hop
    /// cap alone bounded the number of fetches, not the elapsed time.
    /// </summary>
    private static readonly TimeSpan ResolveChainTimeout =
        TimeSpan.FromSeconds(35);
    private static readonly TimeSpan ResolvedSourceLifetime =
        TimeSpan.FromMinutes(5);

    /// <summary>
    /// How long a host that no extractor recognised is left alone. Long enough
    /// that a refresh does not re-read it, short enough that a site which
    /// changes player is picked up the same session.
    /// </summary>
    private static readonly TimeSpan ContentMatchMissLifetime =
        TimeSpan.FromMinutes(10);
    private static readonly TimeSpan UnavailableSourceLifetime =
        TimeSpan.FromMinutes(1);
    private static readonly TimeSpan RefreshableSourceFreshness =
        TimeSpan.FromSeconds(15);
    private readonly Lazy<IReadOnlyDictionary<string, IApiVideoHosterExtractor>>
        _extractors;
    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly Action<HosterResolutionObservation>? _observer;
    private readonly ConcurrentDictionary<string, CachedResolvedSource>
        _resolvedCache = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, RefreshableSource>
        _refreshableSources = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Embed URLs whose pages no extractor recognised, so the read is not
    /// repeated on every refresh. A miss never suppresses another video on
    /// the same host.
    /// </summary>
    private readonly ConcurrentDictionary<string, DateTimeOffset>
        _contentMatchMisses = new(StringComparer.Ordinal);
    private int _cacheWriteCount;
    private long _cacheSequence;
    private long _refreshSequence;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _videoChallenges = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, DateTimeOffset> _videoChallengeAttempts = new(StringComparer.Ordinal);

    public VideoHosterResolver(
        HttpClient httpClient,
        IEnumerable<IApiVideoHosterExtractor>? extractors = null,
        TimeProvider? timeProvider = null,
        Action<HosterResolutionObservation>? observer = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        _httpClient = httpClient;
        _timeProvider = timeProvider ?? TimeProvider.System;
        _observer = observer;
        _extractors = new Lazy<
            IReadOnlyDictionary<string, IApiVideoHosterExtractor>>(
            () => (extractors ?? HosterFeedMap.Default())
                .ToDictionary(
                    static extractor => extractor.Name,
                    StringComparer.OrdinalIgnoreCase),
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public IReadOnlyCollection<string> SupportedHosters =>
        _extractors.Value.Keys.ToArray();

    /// <summary>
    /// Re-resolves a short-lived hoster source immediately before playback
    /// when the signed rendition has been sitting in the server list long
    /// enough to be stale. Sources that do not need refreshing are returned
    /// unchanged.
    /// </summary>
    public async ValueTask<ApiContentVideoSource?> RefreshAsync(
        ApiContentVideoSource source,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(source);
        // This entry point is used only for the selected playback server.
        // Human interaction is outside ResolveAsync's 35-second network budget.
        if (source.IsEmbeddedWeb && WebChallengeGate.HasSolver &&
            (VideoChallengeCapture.SupportsInteractiveCapture(source.PlaybackUri) ||
             (_videoChallenges.TryGetValue(source.PlaybackUri.AbsoluteUri, out var challengeUntil) &&
              challengeUntil > _timeProvider.GetUtcNow())))
        {
            // Declining one video must not suppress capture for another episode
            // hosted on the same service. Keep the URL (including case) in memory only.
            var attemptKey = source.PlaybackUri.AbsoluteUri;
            var interactiveNow = _timeProvider.GetUtcNow();
            if (!_videoChallengeAttempts.TryGetValue(attemptKey, out var attempted) || interactiveNow - attempted > TimeSpan.FromMinutes(2))
            {
                if (_videoChallengeAttempts.Count >= 128) _videoChallengeAttempts.Clear();
                _videoChallengeAttempts[attemptKey] = interactiveNow;
                using var interactive = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                interactive.CancelAfter(TimeSpan.FromMinutes(2));
                try
                {
                    var captured = await WebChallengeGate.ResolveVideoAsync(source, interactive.Token);
                    if (captured is { IsEmbeddedWeb: false, RequiresClearKeyDecryption: false } &&
                        HosterExtractorBase.IsSafePublicHttpUri(captured.PlaybackUri))
                        return captured;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
                catch (Exception exception) when (exception is not OperationCanceledException)
                {
                    Observe(source, null, "Interactive challenge failed; web fallback");
                }
            }
            return source;
        }
        if (!_refreshableSources.TryGetValue(
                source.PlaybackUri.AbsoluteUri,
                out var refreshable))
        {
            return source;
        }

        var now = _timeProvider.GetUtcNow();
        var age = now - refreshable.ResolvedAt;
        if (age >= TimeSpan.Zero &&
            age < RefreshableSourceFreshness)
        {
            return source;
        }

        var refreshed = await ResolveSingleAsync(
            refreshable.OriginalSource,
            new HashSet<string>(StringComparer.OrdinalIgnoreCase),
            cancellationToken);
        if (refreshed is null ||
            string.Equals(
                refreshed.Label,
                source.Label,
                StringComparison.Ordinal))
        {
            return refreshed;
        }

        // Providers often append an ordinal after hoster resolution. Keep
        // that stable UI label while replacing only the expiring rendition.
        return new ApiContentVideoSource(
            source.Label,
            refreshed.PlaybackUri,
            refreshed.MimeType,
            refreshed.Referrer,
            refreshed.Kind,
            refreshed.DecryptionKeys,
            refreshed.HttpOrigin,
            refreshed.UserAgent);
    }

    public async ValueTask<IReadOnlyList<ApiContentVideoSource>> ResolveAsync(
        IEnumerable<ApiContentVideoSource> sources,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        var ordered = sources as IReadOnlyList<ApiContentVideoSource> ??
            sources.ToArray();
        if (ordered.Count == 0)
        {
            return [];
        }

        // Each source resolves independently and carries its own visited set,
        // so they run concurrently: resolving one embed per server serially
        // meant a title page's worst case was the sum of every hoster fetch
        // rather than the slowest one. The cap keeps a page offering many
        // servers from opening a connection per server.
        var results = new ApiContentVideoSource?[ordered.Count];
        var pending = new Task[ordered.Count];
        for (var index = 0; index < ordered.Count; index++)
        {
            pending[index] = ResolveBoundedAsync(index);
        }

        await Task.WhenAll(pending);
        return results
            .OfType<ApiContentVideoSource>()
            .ToArray();

        async Task ResolveBoundedAsync(int position)
        {
            await ResolveConcurrency.WaitAsync(cancellationToken)
                .ConfigureAwait(false);

            // A whole-chain deadline on top of the per-hop timeout. The slot
            // used to be held for the length of the recursion, so a five-hop
            // wrapper chain at the per-hop timeout pinned it for minutes and
            // blew the caller's deadline for every other source in the
            // fan-out; now a slow chain gives up and is simply skipped.
            using var chain = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken);
            chain.CancelAfter(ResolveChainTimeout);
            try
            {
                results[position] = await ResolveSingleAsync(
                    ordered[position],
                    new HashSet<string>(StringComparer.OrdinalIgnoreCase),
                    chain.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                // The extractor ran out of time, not the source. Preserve the
                // original embedded page so a slow native probe cannot erase
                // the only playable server from the provider.
                results[position] = ordered[position];
            }
            finally
            {
                ResolveConcurrency.Release();
            }
        }
    }

    /// <summary>
    /// Offers a page nobody claimed by URL to the extractors that can identify
    /// their own player from it.
    /// <para>
    /// This is what survives a mirror rotation. A hoster that moves to a new
    /// domain keeps serving the same player, so the markup still identifies
    /// it even though every pinned domain list has gone stale. Costs one read
    /// of a source that would otherwise have been handed to the browser
    /// unresolved, and a host that matches nothing is not read again for a
    /// while.
    /// </para>
    /// </summary>
    private async ValueTask<IApiVideoHosterExtractor?> TryMatchByContentAsync(
        ApiContentVideoSource source,
        CancellationToken cancellationToken)
    {
        // A missing player must not suppress other routes on the same host.
        var host = source.PlaybackUri.AbsoluteUri;
        var now = _timeProvider.GetUtcNow();
        if (_contentMatchMisses.TryGetValue(host, out var missedUntil))
        {
            if (missedUntil > now)
            {
                return null;
            }

            _contentMatchMisses.TryRemove(host, out _);
        }

        try
        {
            var page = await HosterExtractorBase
                .FetchForContentMatchAsync(
                    _httpClient,
                    source.PlaybackUri,
                    source.Referrer,
                    cancellationToken)
                .ConfigureAwait(false);
            var matched = _extractors.Value.Values.FirstOrDefault(
                extractor => extractor.MatchesContent(page.Content));
            if (matched is null)
            {
                RememberContentMatchMiss(host, now);
            }

            return matched;
        }
        catch (Exception exception)
            when (exception is not OperationCanceledException)
        {
            // Unreachable or refused: nothing to identify, and the source is
            // still handed on untouched.
            RememberContentMatchMiss(host, now);
            return null;
        }
    }

    private void RememberContentMatchMiss(string host, DateTimeOffset now)
    {
        if (_contentMatchMisses.Count >= MaximumResolvedSourceCacheEntries)
        {
            _contentMatchMisses.Clear();
        }

        _contentMatchMisses[host] = now + ContentMatchMissLifetime;
    }

    private async ValueTask<ApiContentVideoSource?> ResolveSingleAsync(
        ApiContentVideoSource source,
        HashSet<string> visited,
        CancellationToken cancellationToken)
    {
        if (source.Kind is not ApiContentVideoSourceKind.EmbeddedWeb)
        {
            return source;
        }

        if (!HosterExtractorBase.IsSafePublicHttpUri(source.PlaybackUri))
        {
            return source;
        }

        var cacheKey = source.PlaybackUri.AbsoluteUri;
        if (visited.Count >= 5 || !visited.Add(cacheKey))
        {
            return source;
        }

        // A wrapper whose only content is an iframe of another hoster hides
        // that hoster from every extractor here: nothing matches
        // "host/iframe/?url=...", so a page that could have played natively
        // falls back to the embedded browser instead. Unwrapping first is what
        // lets the real hoster be recognized.
        var unwrapped = EmbeddedWebTarget.Unwrap(
            source.PlaybackUri,
            static candidate => true);
        if (!ReferenceEquals(unwrapped, source.PlaybackUri))
        {
            return await ResolveSingleAsync(
                new ApiContentVideoSource(
                    source.Label,
                    unwrapped,
                    source.MimeType,
                    source.Referrer ?? source.PlaybackUri,
                    ApiContentVideoSourceKind.EmbeddedWeb,
                    source.DecryptionKeys,
                    source.HttpOrigin),
                visited,
                cancellationToken);
        }

        var matcher = _extractors.Value.Values
            .FirstOrDefault(extractor =>
                extractor.Matches(source.PlaybackUri));
        matcher ??= await TryMatchByContentAsync(source, cancellationToken)
            .ConfigureAwait(false);
        if (matcher is null)
        {
            Observe(source, null, "No matching extractor");
            return source;
        }

        var now = _timeProvider.GetUtcNow();
        var carriesShortLivedMediaSession =
            matcher is NuploadHosterExtractor;
        if (_resolvedCache.TryGetValue(cacheKey, out var cached) &&
            cached.ExpiresAt > now &&
            // A cached unavailable result is useful: it avoids making every
            // provider repeat the same bounded probe. A successful Nupload
            // result is different. Its signed `s` query is short-lived, so
            // reusing it can hand both the native and web paths an already
            // expired session.
            (!carriesShortLivedMediaSession || cached.Source is null))
        {
            return cached.Source;
        }

        if (cached is not null)
        {
            _resolvedCache.TryRemove(cacheKey, out _);
        }

        ApiContentVideoSource? resolved = null;
        try
        {
            resolved = await matcher.TryResolveAsync(
                source,
                _httpClient,
                cancellationToken);
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (HosterMediaUnavailableException exception)
        {
            // This proves only that the signed rendition decoded from this
            // extractor fetch cannot be handed to the native player. Nupload
            // signs each page response independently, so a later WebView load
            // can receive another session. Never turn a failed optimization
            // into a missing episode.
            System.Diagnostics.Trace.TraceWarning(
                $"Hoster '{matcher.Name}' kept the embedded fallback after " +
                $"an unavailable native media session from " +
                $"'{source.PlaybackUri.IdnHost}': " +
                exception.Message);
            return source;
        }
        catch (Exception exception)
        {
            if (exception is HttpRequestException failure &&
                ((matcher is FilemoonHosterExtractor && failure.StatusCode == HttpStatusCode.PreconditionRequired) ||
                 (matcher is DoodstreamHosterExtractor && failure.StatusCode == HttpStatusCode.Forbidden)))
            {
                if (_videoChallenges.Count >= 256) _videoChallenges.Clear();
                _videoChallenges[source.PlaybackUri.AbsoluteUri] = _timeProvider.GetUtcNow() + TimeSpan.FromMinutes(15);
            }
            Observe(source, matcher.Name, exception is HttpRequestException http
                ? $"HTTP {http.StatusCode?.ToString() ?? http.HttpRequestError.ToString()}; web fallback"
                : $"{exception.GetType().Name}; web fallback");
            // A failing extractor must not break the source list; the
            // WebView fallback remains the source of truth for unknown
            // hosters and for transient network errors.
            return source;
        }

        if (resolved is null)
        {
            Observe(source, matcher.Name, "No direct feed; web fallback");
            return source;
        }

        if (resolved.Kind is ApiContentVideoSourceKind.DirectMedia &&
            !HosterExtractorBase.IsSafePublicHttpUri(resolved.PlaybackUri))
        {
            return source;
        }

        // Some extractors (notably Frembed) resolve a broker URL to another
        // embedded hoster. Follow that hand-off without allowing redirect
        // cycles or unbounded recursion.
        var finalSource = resolved.Kind is ApiContentVideoSourceKind.EmbeddedWeb &&
                          !string.Equals(
                              resolved.PlaybackUri.AbsoluteUri,
                              source.PlaybackUri.AbsoluteUri,
                              StringComparison.OrdinalIgnoreCase)
            ? await ResolveSingleAsync(resolved, visited, cancellationToken)
            : resolved;
        if (carriesShortLivedMediaSession &&
            finalSource?.Kind is ApiContentVideoSourceKind.DirectMedia)
        {
            RememberRefreshableSource(
                finalSource,
                source,
                now);
        }

        if (!carriesShortLivedMediaSession)
        {
            CacheResolvedSource(
                cacheKey,
                finalSource,
                now,
                finalSource is null
                    ? UnavailableSourceLifetime
                    : ResolvedSourceLifetime);
        }

        Observe(source, matcher.Name, finalSource?.Kind is ApiContentVideoSourceKind.DirectMedia
            ? "Direct URL returned (playback unverified)" : "Web fallback");
        return finalSource;
    }

    private void Observe(ApiContentVideoSource source, string? extractor, string outcome)
    {
        // Diagnostics cannot break playback or expose signed URLs and cookies.
        try { _observer?.Invoke(new(source.PlaybackUri.IdnHost, extractor, outcome)); }
        catch { }
    }

    internal int ResolvedSourceCacheCount => _resolvedCache.Count;

    internal int RefreshableSourceCount => _refreshableSources.Count;

    private void RememberRefreshableSource(
        ApiContentVideoSource resolved,
        ApiContentVideoSource original,
        DateTimeOffset resolvedAt)
    {
        _refreshableSources[resolved.PlaybackUri.AbsoluteUri] =
            new RefreshableSource(
                original,
                resolvedAt,
                Interlocked.Increment(ref _refreshSequence));
        var excess =
            _refreshableSources.Count - MaximumRefreshableSourceEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (var entry in _refreshableSources
                     .OrderBy(static entry => entry.Value.Sequence)
                     .Take(excess))
        {
            _refreshableSources.TryRemove(entry.Key, out _);
        }
    }

    private void CacheResolvedSource(
        string cacheKey,
        ApiContentVideoSource? source,
        DateTimeOffset now,
        TimeSpan lifetime)
    {
        _resolvedCache[cacheKey] = new CachedResolvedSource(
            source,
            now + lifetime,
            Interlocked.Increment(ref _cacheSequence));

        var writeCount = Interlocked.Increment(ref _cacheWriteCount);
        if (_resolvedCache.Count > MaximumResolvedSourceCacheEntries ||
            (writeCount & 31) == 0)
        {
            PruneResolvedSourceCache(now);
        }
    }

    private void PruneResolvedSourceCache(DateTimeOffset now)
    {
        foreach (var entry in _resolvedCache)
        {
            if (entry.Value.ExpiresAt <= now)
            {
                _resolvedCache.TryRemove(entry.Key, out _);
            }
        }

        var excess =
            _resolvedCache.Count - MaximumResolvedSourceCacheEntries;
        if (excess <= 0)
        {
            return;
        }

        foreach (var entry in _resolvedCache
                     .OrderBy(static entry => entry.Value.Sequence)
                     .Take(excess))
        {
            _resolvedCache.TryRemove(entry.Key, out _);
        }
    }

    private sealed record CachedResolvedSource(
        ApiContentVideoSource? Source,
        DateTimeOffset ExpiresAt,
        long Sequence);

    private sealed record RefreshableSource(
        ApiContentVideoSource OriginalSource,
        DateTimeOffset ResolvedAt,
        long Sequence);
}

/// <summary>
/// Signals that a hoster successfully decoded a media endpoint but that
/// endpoint cannot currently be handed to the native player. This describes
/// the decoded rendition, not the underlying episode: a fresh embedded-page
/// load may receive another signed session and remains a valid fallback.
/// </summary>
internal sealed class HosterMediaUnavailableException : Exception
{
    public HosterMediaUnavailableException(string message)
        : base(message)
    {
    }
}

/// <summary>
/// Common base class that fetchs a hoster's HTML and exposes the
/// <see cref="HttpClient"/> + <see cref="TimeProvider"/> to subclasses.
/// </summary>
public abstract class HosterExtractorBase : IApiVideoHosterExtractor
{
    protected const int DefaultMaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumHttpRedirects = 5;

    public abstract string Name { get; }

    public abstract bool Matches(Uri playbackUri);

    public virtual bool MatchesContent(string content) => false;

    public abstract ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken);

    protected static async ValueTask<string> FetchHtmlAsync(
        HttpClient httpClient,
        Uri endpoint,
        Uri? referrer,
        string userAgent,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        bool includeIframeNavigationHeaders = false)
    {
        var result = await FetchTextAsync(
            httpClient,
            endpoint,
            referrer,
            userAgent,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            timeout,
            cancellationToken,
            DefaultMaximumResponseBytes,
            includeIframeNavigationHeaders);
        return result.Content;
    }

    /// <summary>
    /// Reads a page so the resolver can offer it to extractors that identify
    /// their own player by markup. Bounded exactly like an extractor's own
    /// fetch, because it is one.
    /// </summary>
    internal static ValueTask<(string Content, Uri FinalUri)> FetchForContentMatchAsync(
        HttpClient httpClient,
        Uri endpoint,
        Uri? referrer,
        CancellationToken cancellationToken) =>
        FetchTextAsync(
            httpClient,
            endpoint,
            referrer,
            HosterBrowserHeaders.UserAgent,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            // Deliberately shorter than an extractor's own budget. This read
            // is a guess about a source that was already going to be handed to
            // the browser, so a slow host must not hold the title up for as
            // long as a hoster being resolved on purpose would.
            TimeSpan.FromSeconds(8),
            cancellationToken,
            DefaultMaximumResponseBytes);

    protected static async ValueTask<(string Content, Uri FinalUri)>
        FetchTextAsync(
            HttpClient httpClient,
            Uri endpoint,
            Uri? referrer,
            string userAgent,
            string accept,
            TimeSpan timeout,
            CancellationToken cancellationToken,
            int maximumResponseBytes,
            bool includeIframeNavigationHeaders = false)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var currentUri = endpoint;
        var currentReferrer = referrer;
        for (var redirectCount = 0;
             redirectCount <= MaximumHttpRedirects;
             redirectCount++)
        {
            if (!IsSafePublicHttpUri(currentUri))
            {
                throw new InvalidDataException(
                    $"{currentUri.Host} returned an unsafe HTTP endpoint.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                currentUri);
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.UserAgent.ParseAdd(userAgent);
            if (currentReferrer is not null)
            {
                request.Headers.Referrer = currentReferrer;
            }

            if (includeIframeNavigationHeaders)
            {
                request.Headers.AcceptLanguage.ParseAdd(
                    "es-AR,es;q=0.9,en;q=0.8");
                request.Headers.TryAddWithoutValidation(
                    "Sec-Fetch-Dest",
                    "iframe");
                request.Headers.TryAddWithoutValidation(
                    "Sec-Fetch-Mode",
                    "navigate");
                request.Headers.TryAddWithoutValidation(
                    "Sec-Fetch-Site",
                    "cross-site");
            }

            using var response = await ApiProviderHttp.SendAsync(
                httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            var responseUri =
                response.RequestMessage?.RequestUri ?? currentUri;
            if (!IsSafePublicHttpUri(responseUri))
            {
                throw new InvalidDataException(
                    $"{responseUri.Host} returned an unsafe HTTP endpoint.");
            }

            if (IsRedirectStatusCode(response.StatusCode))
            {
                if (redirectCount == MaximumHttpRedirects ||
                    response.Headers.Location is null)
                {
                    throw new InvalidDataException(
                        $"{responseUri.Host} returned an invalid or excessive " +
                        "HTTP redirect chain.");
                }

                var nextUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(responseUri, response.Headers.Location);
                if (!IsSafePublicHttpUri(nextUri))
                {
                    throw new InvalidDataException(
                        $"{responseUri.Host} redirected to an unsafe HTTP " +
                        "endpoint.");
                }

                currentReferrer = responseUri;
                currentUri = nextUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            var bytes = await ReadBoundedResponseBytesAsync(
                response.Content,
                responseUri,
                maximumResponseBytes,
                timeoutSource.Token);
            return (Encoding.UTF8.GetString(bytes), responseUri);
        }

        throw new InvalidDataException(
            $"{endpoint.Host} returned an excessive HTTP redirect chain.");
    }

    protected internal static async ValueTask<byte[]> ReadBoundedResponseBytesAsync(
        HttpContent content,
        Uri endpoint,
        int maximumResponseBytes,
        CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumResponseBytes);
        if (content.Headers.ContentLength > maximumResponseBytes)
        {
            throw new InvalidDataException(
                $"{endpoint.Host} response exceeded the " +
                $"{maximumResponseBytes / 1024} KiB limit.");
        }

        await using var input =
            await content.ReadAsStreamAsync(cancellationToken);
        var contentLength = content.Headers.ContentLength;
        using var output = new MemoryStream(
            contentLength >= 0 && contentLength <= maximumResponseBytes
                ? checked((int)contentLength.Value)
                : 0);
        var chunk = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(
                    chunk.AsMemory(0, 16 * 1024),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumResponseBytes)
                {
                    throw new InvalidDataException(
                        $"{endpoint.Host} response exceeded the " +
                        $"{maximumResponseBytes / 1024} KiB limit.");
                }

                await output.WriteAsync(
                    chunk.AsMemory(0, read),
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(chunk);
        }

        return output.Length == output.Capacity
            ? output.GetBuffer()
            : output.ToArray();
    }

    /// <summary>
    /// Walks a redirect chain and returns where it lands, without downloading
    /// the body.
    /// </summary>
    /// <remarks>
    /// Some hosters hand the player a bare origin plus a signed query that
    /// answers 301 with the real playlist. A browser's HLS provider follows
    /// that on its own; libVLC is given a URL and does not, so it sits on the
    /// redirect until the start timeout expires and the server looks dead. The
    /// same bounds and safety checks as the fetch path apply, so a redirect
    /// cannot walk onto a private address or loop.
    /// </remarks>
    protected static async Task<Uri> ResolveRedirectTargetAsync(
        HttpClient httpClient,
        Uri endpoint,
        Uri? referrer,
        string userAgent,
        TimeSpan timeout,
        CancellationToken cancellationToken,
        Uri? origin = null)
    {
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(endpoint);
        if (origin is not null && !IsSafePublicHttpUri(origin))
        {
            throw new InvalidDataException(
                $"{origin.Host} is not a safe HTTP origin.");
        }

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(timeout);

        var currentUri = endpoint;
        var currentReferrer = referrer;
        for (var redirectCount = 0;
             redirectCount <= MaximumHttpRedirects;
             redirectCount++)
        {
            if (!IsSafePublicHttpUri(currentUri))
            {
                throw new InvalidDataException(
                    $"{currentUri.Host} returned an unsafe HTTP endpoint.");
            }

            // GET rather than HEAD: these CDNs routinely answer HEAD with 405
            // or a redirect they do not honour. ResponseHeadersRead means the
            // playlist body is never pulled down.
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                currentUri);
            request.Headers.UserAgent.ParseAdd(userAgent);
            request.Headers.Accept.ParseAdd("*/*");
            if (currentReferrer is not null)
            {
                request.Headers.Referrer = currentReferrer;
            }

            if (origin is not null)
            {
                request.Headers.AcceptLanguage.ParseAdd(
                    "es-AR,es;q=0.9,en;q=0.8");
                request.Headers.TryAddWithoutValidation(
                    "Origin",
                    origin.GetLeftPart(UriPartial.Authority));
                request.Headers.TryAddWithoutValidation(
                    "Sec-Fetch-Dest",
                    "empty");
                request.Headers.TryAddWithoutValidation(
                    "Sec-Fetch-Mode",
                    "cors");
                request.Headers.TryAddWithoutValidation(
                    "Sec-Fetch-Site",
                    "cross-site");
            }

            using var response = await ApiProviderHttp.SendAsync(
                httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                timeoutSource.Token);
            var responseUri =
                response.RequestMessage?.RequestUri ?? currentUri;
            if (!IsSafePublicHttpUri(responseUri))
            {
                throw new InvalidDataException(
                    $"{responseUri.Host} returned an unsafe HTTP endpoint.");
            }

            if (!IsRedirectStatusCode(response.StatusCode))
            {
                // A handler configured to follow redirects has already landed
                // on the target, so report where the request actually ended.
                // A rejected signed session is not a playable target.
                response.EnsureSuccessStatusCode();
                return responseUri;
            }

            if (redirectCount == MaximumHttpRedirects ||
                response.Headers.Location is null)
            {
                throw new InvalidDataException(
                    $"{responseUri.Host} returned an invalid or excessive " +
                    "HTTP redirect chain.");
            }

            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(responseUri, response.Headers.Location);
            if (!IsSafePublicHttpUri(nextUri))
            {
                throw new InvalidDataException(
                    $"{responseUri.Host} redirected to an unsafe HTTP endpoint.");
            }

            currentReferrer = responseUri;
            currentUri = nextUri;
        }

        throw new InvalidDataException(
            $"{endpoint.Host} returned an invalid or excessive HTTP redirect chain.");
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    protected internal static bool IsSafePublicHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0)
        {
            return false;
        }

        var host = uri.IdnHost.TrimEnd('.');
        if (string.Equals(
                host,
                "localhost",
                StringComparison.OrdinalIgnoreCase) ||
            host.EndsWith(
                ".localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(host, out var address))
        {
            return true;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        return bytes.Length switch
        {
            4 => IsPublicIpv4Address(bytes),
            16 => IsPublicIpv6Address(address, bytes),
            _ => false
        };
    }

    private static bool IsPublicIpv4Address(ReadOnlySpan<byte> address)
    {
        var first = address[0];
        var second = address[1];

        return
            // "This network", RFC1918, shared address space, and loopback.
            first != 0 &&
            first != 10 &&
            first != 127 &&
            !(first == 100 && second is >= 64 and <= 127) &&
            // Link-local and RFC1918.
            !(first == 169 && second == 254) &&
            !(first == 172 && second is >= 16 and <= 31) &&
            !(first == 192 && second == 168) &&
            // IETF protocol assignments and documentation networks.
            !(first == 192 && second == 0 && address[2] is 0 or 2) &&
            !(first == 192 && second == 88 && address[2] == 99) &&
            !(first == 198 && second is 18 or 19) &&
            !(first == 198 && second == 51 && address[2] == 100) &&
            !(first == 203 && second == 0 && address[2] == 113) &&
            // Multicast, reserved, and limited broadcast.
            first < 224;
    }

    private static bool IsPublicIpv6Address(
        IPAddress address,
        ReadOnlySpan<byte> bytes) =>
        !address.Equals(IPAddress.IPv6Any) &&
        !address.Equals(IPAddress.IPv6None) &&
        !address.IsIPv6LinkLocal &&
        !address.IsIPv6SiteLocal &&
        !address.IsIPv6Multicast &&
        // RFC4193 unique-local address space (fc00::/7).
        (bytes[0] & 0xfe) != 0xfc;

    protected static bool HostMatchesDomain(Uri uri, string domain) =>
        string.Equals(
            uri.IdnHost,
            domain,
            StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith(
            "." + domain,
            StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// PremiunVIP is one label for the IronHentai MP4 player. The
/// player stores its real <c>hugging.php</c> endpoint in a double-base64
/// script whose intermediate characters are shifted by one.
/// </summary>
public sealed class PremiumVipHosterExtractor : HosterExtractorBase
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "PremiunVIP";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "ironhentai.com") &&
        playbackUri.AbsolutePath.EndsWith(
            "/face.php",
            StringComparison.OrdinalIgnoreCase);

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            FetchTimeout,
            cancellationToken);
        var resolved = IronHentaiPayloadDecoder.TryDecode(
            html,
            static uri =>
                HostMatchesDomain(uri, "ironhentai.com") &&
                uri.AbsolutePath.EndsWith(
                    "/hugging.php",
                    StringComparison.OrdinalIgnoreCase));
        return resolved is null
            ? null
            : new ApiContentVideoSource(
                source.Label,
                resolved,
                "video/mp4",
                source.PlaybackUri,
                ApiContentVideoSourceKind.DirectMedia);
    }
}

/// <summary>
/// PlusTube uses the same IronHentai obfuscation wrapper as PremiunVIP,
/// but its decoded script contains an HLS manifest.
/// </summary>
public sealed class PlusTubeHosterExtractor : HosterExtractorBase
{
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "PlusTube";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "ironhentai.com") &&
        playbackUri.AbsolutePath.EndsWith(
            "/vt.php",
            StringComparison.OrdinalIgnoreCase);

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            FetchTimeout,
            cancellationToken);
        var resolved = IronHentaiPayloadDecoder.TryDecode(
            html,
            static uri =>
                uri.AbsolutePath.EndsWith(
                    ".m3u8",
                    StringComparison.OrdinalIgnoreCase));
        return resolved is null
            ? null
            : new ApiContentVideoSource(
                source.Label,
                resolved,
                "application/vnd.apple.mpegurl",
                source.PlaybackUri,
                ApiContentVideoSourceKind.DirectMedia);
    }
}

internal static class IronHentaiPayloadDecoder
{
    private const int MaximumPayloadLength = 512 * 1024;
    private static readonly Regex PayloadRegex = new(
        """
        atob\s*\(\s*atob\s*\(\s*['"](?<value>[A-Za-z0-9+/=]+)['"]
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex AbsoluteUrlRegex = new(
        """
        https?:\/\/[^\s'"<>\\]+
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public static Uri? TryDecode(
        string html,
        Func<Uri, bool> isExpectedMedia)
    {
        var match = PayloadRegex.Match(html);
        if (!match.Success ||
            match.Groups["value"].Length > MaximumPayloadLength)
        {
            return null;
        }

        string script;
        try
        {
            var shiftedBase64 = Encoding.UTF8.GetString(
                Convert.FromBase64String(match.Groups["value"].Value));
            var base64Buffer = shiftedBase64.ToCharArray();
            for (var index = 0; index < base64Buffer.Length; index++)
            {
                base64Buffer[index] = (char)(base64Buffer[index] - 1);
            }

            script = Encoding.UTF8.GetString(
                Convert.FromBase64String(new string(base64Buffer)));
        }
        catch (FormatException)
        {
            return null;
        }

        foreach (Match urlMatch in AbsoluteUrlRegex.Matches(script))
        {
            var value = WebUtility.HtmlDecode(urlMatch.Value)
                .Replace("\\/", "/", StringComparison.Ordinal);
            if (Uri.TryCreate(value, UriKind.Absolute, out var candidate) &&
                HosterExtractorBase.IsSafePublicHttpUri(candidate) &&
                isExpectedMedia(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}

/// <summary>
/// Nupload builds its JWPlayer HLS URL from an array of Base64 strings.
/// Every decoded string contains a decimal number; subtracting the page's
/// per-request offset from that number yields one character of the URL.
/// </summary>
public sealed class NuploadHosterExtractor : HosterExtractorBase
{
    private static readonly Regex DelegatedFrameRegex = new(
        """<iframe\b[^>]*\ssrc\s*=\s*(?<quote>["'])(?<value>[^"'<>]+)\k<quote>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private const int MaximumArrayCharacters = 256 * 1024;
    private const int MaximumDecodedUrlLength = 4 * 1024;
    private const int MaximumEncodedValueLength = 256;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan MediaRedirectTimeout =
        TimeSpan.FromSeconds(12);
    private static readonly Regex DecoderRegex = new(
        """
        (?<array>[A-Za-z_$][A-Za-z0-9_$]{0,63})
        \s*\.forEach\s*\(
        \s*function
        (?:\s+[A-Za-z_$][A-Za-z0-9_$]{0,63})?
        \s*\(\s*(?<value>[A-Za-z_$][A-Za-z0-9_$]{0,63})\s*\)
        \s*\{
        [\s\S]{0,256}?
        String\s*\.fromCharCode\s*\(
        \s*parseInt\s*\(
        \s*atob\s*\(\s*\k<value>\s*\)
        \s*\.replace\s*\(\s*/\\D/g\s*,\s*["']{2}\s*\)
        \s*\)
        \s*-\s*(?<offset>[0-9]{1,10})
        """,
        RegexOptions.IgnorePatternWhitespace |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private const int MaximumDeclarationRegexes = 64;

    private static readonly ConcurrentDictionary<string, Regex> DeclarationRegexes =
        new(StringComparer.Ordinal);

    private static Regex CreateDeclarationRegex(string arrayName) =>
        new(
            $"""
            \b(?:var|let|const)\s+{Regex.Escape(arrayName)}\s*=\s*\[
            """,
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));

    private static readonly Regex EncodedValueRegex = new(
        """
        (?<quote>["'])(?<value>[A-Za-z0-9+/=]{4,256})\k<quote>
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SessionRegex = new(
        """
        \b(?:var|let|const)\s+sesz\s*=\s*
        (?<quote>["'])(?<value>[A-Za-z0-9_-]{1,256})\k<quote>
        """,
        RegexOptions.IgnorePatternWhitespace |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public override string Name => "Nupload";

    public override bool Matches(Uri playbackUri) =>
        (HostMatchesDomain(playbackUri, "nupload.top") ||
         HostMatchesDomain(playbackUri, "nupload.me")) &&
        playbackUri.AbsolutePath.StartsWith(
            "/watch/",
            StringComparison.OrdinalIgnoreCase);

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            FetchTimeout,
            cancellationToken,
            includeIframeNavigationHeaders: true);
        var resolved = TryDecode(html);
        if (resolved is null)
        {
            // PelisflixHD now publishes /watch/ entries which are themselves
            // wrappers around VOE, rather than the obfuscated Nupload stream.
            // Only hand off to registered players, never an arbitrary ad frame.
            foreach (Match frame in DelegatedFrameRegex.Matches(html))
            {
                if (Uri.TryCreate(source.PlaybackUri, WebUtility.HtmlDecode(frame.Groups["value"].Value), out var target) &&
                    IsSafePublicHttpUri(target) && HosterFeedMap.Supports(target))
                    return new ApiContentVideoSource(source.Label, target, referrer: source.PlaybackUri,
                        kind: ApiContentVideoSourceKind.EmbeddedWeb);
            }
            return null;
        }

        var playbackOrigin = new Uri(
            source.PlaybackUri.GetLeftPart(UriPartial.Authority) + "/");

        // What the page encodes is the origin plus a signed query, and that
        // answers 301 with the playlist. Following it here means the player
        // receives a real .m3u8; handed the redirect itself, libVLC never
        // followed it and stalled until the start timeout, which read as the
        // server being unreachable even though the extraction had worked.
        //
        // A protocol/parsing failure can still fall back to the decoded
        // address. A rejected or slow session must not be handed to libVLC,
        // but it also does not prove the episode is missing: another page load
        // can receive a fresh signed session, so the resolver retains the
        // original EmbeddedWeb source.
        Uri playlistUri;
        try
        {
            playlistUri = await ResolveRedirectTargetAsync(
                httpClient,
                resolved,
                playbackOrigin,
                HosterBrowserHeaders.UserAgent,
                MediaRedirectTimeout,
                cancellationToken,
                playbackOrigin);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is
                HttpStatusCode.NotFound or
                HttpStatusCode.Gone)
        {
            throw new HosterMediaUnavailableException(
                "Nupload rejected the decoded media session.");
        }
        catch (TaskCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            // Do not make libVLC repeat the probe timeout. The resolver catches
            // this and preserves the original embedded page, whose independent
            // load can receive a different signed session.
            throw new HosterMediaUnavailableException(
                "Nupload did not validate the decoded media session in time.");
        }
        catch (Exception exception) when (
            (exception is HttpRequestException or InvalidDataException) &&
            !cancellationToken.IsCancellationRequested)
        {
            playlistUri = resolved;
        }

        return new ApiContentVideoSource(
            source.Label,
            playlistUri,
            "application/vnd.apple.mpegurl",
            playbackOrigin,
            ApiContentVideoSourceKind.DirectMedia,
            httpOrigin: playbackOrigin);
    }

    public static Uri? TryDecode(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var decoder = DecoderRegex.Match(html);
        if (!decoder.Success ||
            !int.TryParse(
                decoder.Groups["offset"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset))
        {
            return null;
        }

        var arrayName = decoder.Groups["array"].Value;

        // Constructing a Regex parses and compiles the pattern, which is the
        // expensive part of using one; every other pattern in this file is a
        // static field for that reason. This one embeds a name read from the
        // page, so it is cached per name instead — bounded, because the name
        // comes from untrusted markup.
        var declarationRegex = DeclarationRegexes.Count < MaximumDeclarationRegexes
            ? DeclarationRegexes.GetOrAdd(arrayName, CreateDeclarationRegex)
            : CreateDeclarationRegex(arrayName);
        var declaration = declarationRegex.Match(html);
        if (!declaration.Success)
        {
            return null;
        }

        var valuesStart = declaration.Index + declaration.Length;
        var valuesEnd = html.IndexOf(']', valuesStart);
        if (valuesEnd < valuesStart ||
            valuesEnd - valuesStart > MaximumArrayCharacters)
        {
            return null;
        }

        var valuesText = html[valuesStart..valuesEnd];
        var values = EncodedValueRegex.Matches(valuesText);
        if (values.Count is 0 or > MaximumDecodedUrlLength)
        {
            return null;
        }

        var decoded = new StringBuilder(values.Count);
        var cursor = 0;
        foreach (Match valueMatch in values)
        {
            if (!ContainsOnlyArraySeparators(
                    valuesText.AsSpan(
                        cursor,
                        valueMatch.Index - cursor)))
            {
                return null;
            }

            cursor = valueMatch.Index + valueMatch.Length;
            var encoded = valueMatch.Groups["value"].Value;
            if (encoded.Length > MaximumEncodedValueLength ||
                !TryDecodeCharacter(encoded, offset, out var character))
            {
                return null;
            }

            decoded.Append(character);
        }

        if (!ContainsOnlyArraySeparators(valuesText.AsSpan(cursor)) ||
            !Uri.TryCreate(
                decoded.ToString(),
                UriKind.Absolute,
                out var candidate) ||
            !IsSafePublicHttpUri(candidate))
        {
            return null;
        }

        var session = SessionRegex.Match(html);
        if (!session.Success)
        {
            return candidate;
        }

        var builder = new UriBuilder(candidate);
        var existingQuery = builder.Query.TrimStart('?');
        builder.Query =
            (existingQuery.Length == 0 ? string.Empty : existingQuery + "&") +
            "s=" +
            Uri.EscapeDataString(session.Groups["value"].Value);
        return IsSafePublicHttpUri(builder.Uri) ? builder.Uri : null;
    }

    private static bool TryDecodeCharacter(
        string encoded,
        int offset,
        out char character)
    {
        character = default;
        string decoded;
        try
        {
            decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return false;
        }

        Span<char> digits = stackalloc char[MaximumEncodedValueLength];
        var digitCount = 0;
        foreach (var value in decoded)
        {
            if (!char.IsAsciiDigit(value))
            {
                continue;
            }

            if (digitCount >= digits.Length)
            {
                return false;
            }

            digits[digitCount++] = value;
        }

        if (digitCount == 0 ||
            !long.TryParse(
                digits[..digitCount],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var encodedNumber))
        {
            return false;
        }

        var characterValue = encodedNumber - offset;
        if (characterValue is < 0x20 or > 0x7e)
        {
            return false;
        }

        character = (char)characterValue;
        return true;
    }

    private static bool ContainsOnlyArraySeparators(
        ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character) && character != ',')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// VOE hoster extractor. VOE embeds an HLS manifest in either a
/// <c>'hls': '…'</c> inline literal or a
/// <c>prompt("Node", "…")</c> call. The value is sometimes a base64
/// blob that has to be decoded before it points at a real
/// <c>https://…m3u8</c>. We deliberately do not implement the
/// Selenium-based fallback from the upstream series-scraper (it would
/// require driving a real browser); the two regex-based paths are
/// tried first.
/// </summary>
public sealed class VoeHosterExtractor : HosterExtractorBase
{
    private static readonly string[] Domains =
    [
        "voe.sx", "jessicachoosemake.com", "matthewhotelscience.com",
        "jilliandescribecompany.com", "mikaylaarealike.com",
        "christopheruntilpoint.com", "walterprettytheir.com",
        "crystaltreatmenteast.com", "lauradaydo.com",
        "lancewhosedifficult.com", "dianaavoidthey.com",
        "jefferycontrolmodel.com", "charlestoughrace.com",
        "richardquestionbuilding.com", "jessicayeahcatch.com",
        "juliewomanwish.com"
    ];
    private static readonly string[] EncodedJunkParts =
        ["@$", "^^", "~@", "%?", "*~", "!!", "#&"];
    private static readonly Regex HlsRegex = new(
        """
        'hls'\s*:\s*'(?<value>[^']+)'
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex NodePromptRegex = new(
        """
        prompt\("Node"\s*,\s*"(?<value>[^"]+)"
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex JsonScriptRegex = new(
        """
        <script\s+type\s*=\s*["']application/json["'][^>]*>(?<value>.*?)</script>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex RedirectRegex = new(
        """
        (?:window\.)?location(?:\.href)?\s*=\s*["'](?<value>https://[^"'<>]+/e/[^"'<>]+)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "VOE";

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            FetchTimeout,
            cancellationToken);
        var resolved = TryReadMediaUri(html);
        if (resolved is null)
        {
            var redirectMatch = RedirectRegex.Match(html);
            if (redirectMatch.Success &&
                Uri.TryCreate(
                    WebUtility.HtmlDecode(
                        redirectMatch.Groups["value"].Value),
                    UriKind.Absolute,
                    out var redirect) &&
                IsSafeVoeRedirect(redirect, source.PlaybackUri))
            {
                var redirectedHtml = await FetchHtmlAsync(
                    httpClient,
                    redirect,
                    source.PlaybackUri,
                    HosterBrowserHeaders.UserAgent,
                    FetchTimeout,
                    cancellationToken);
                resolved = TryReadMediaUri(redirectedHtml);
            }
        }

        return resolved is null
            ? null
            : new ApiContentVideoSource(
                source.Label,
                resolved,
                HosterMimeTypeResolver.MimeTypeFromUrl(resolved),
                source.PlaybackUri,
                ApiContentVideoSourceKind.DirectMedia);
    }

    private static Uri? TryReadMediaUri(string html)
    {
        foreach (Match scriptMatch in JsonScriptRegex.Matches(html))
        {
            var encoded = TryReadJsonString(
                scriptMatch.Groups["value"].Value);
            var decoded = encoded is null
                ? null
                : TryDecodeVoePayload(encoded);
            if (decoded is not null)
            {
                return decoded;
            }
        }

        foreach (var regex in new[] { HlsRegex, NodePromptRegex })
        {
            var match = regex.Match(html);
            if (!match.Success)
            {
                continue;
            }

            var candidate = match.Groups["value"].Value
                .Replace("\\/", "/", StringComparison.Ordinal)
                .Trim();
            var resolved = TryDecodeLegacyHls(candidate);
            if (resolved is not null)
            {
                return resolved;
            }
        }

        return null;
    }

    private static string? TryReadJsonString(string json)
    {
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            var root = document.RootElement;
            if (root.ValueKind is System.Text.Json.JsonValueKind.String)
            {
                return root.GetString();
            }

            if (root.ValueKind is System.Text.Json.JsonValueKind.Array &&
                root.GetArrayLength() > 0 &&
                root[0].ValueKind is System.Text.Json.JsonValueKind.String)
            {
                return root[0].GetString();
            }
        }
        catch (System.Text.Json.JsonException)
        {
        }

        return null;
    }

    private static Uri? TryDecodeVoePayload(string value)
    {
        if (value.Length == 0 || value.Length > 1024 * 1024)
        {
            return null;
        }

        try
        {
            var rotated = RotateAsciiLetters(value, 13);
            foreach (var junkPart in EncodedJunkParts)
            {
                rotated = rotated.Replace(
                    junkPart,
                    string.Empty,
                    StringComparison.Ordinal);
            }

            var shifted = Encoding.UTF8.GetString(
                Convert.FromBase64String(rotated));
            var reversedBase64 = new char[shifted.Length];
            for (var index = 0; index < shifted.Length; index++)
            {
                reversedBase64[shifted.Length - index - 1] =
                    (char)(shifted[index] - 3);
            }

            var json = Encoding.UTF8.GetString(
                Convert.FromBase64String(new string(reversedBase64)));
            using var document = System.Text.Json.JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is not
                    System.Text.Json.JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(
                    "source",
                    out var sourceElement) ||
                sourceElement.ValueKind is not
                    System.Text.Json.JsonValueKind.String)
            {
                return null;
            }

            var source = sourceElement.GetString();
            return Uri.TryCreate(source, UriKind.Absolute, out var resolved) &&
                   IsSafePublicHttpUri(resolved)
                ? resolved
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    private static string RotateAsciiLetters(string value, int offset)
    {
        var buffer = value.ToCharArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            var character = buffer[index];
            if (character is >= 'A' and <= 'Z')
            {
                buffer[index] = (char)(
                    (character - 'A' + offset) % 26 + 'A');
            }
            else if (character is >= 'a' and <= 'z')
            {
                buffer[index] = (char)(
                    (character - 'a' + offset) % 26 + 'a');
            }
        }

        return new string(buffer);
    }

    /// <summary>
    /// Whether a redirect out of the embed page may be followed.
    /// <para>
    /// VOE moves its mirror domains faster than a pinned list can be kept
    /// current, and the published page is now only a shim that forwards to
    /// whichever one is live — so requiring a known host meant every source
    /// stopped at the shim. Following an unknown host stays bounded: it has to
    /// be the same embed this source already asked for, so a redirect cannot
    /// hand the player something unrelated.
    /// </para>
    /// </summary>
    private static bool IsSafeVoeRedirect(Uri redirect, Uri source) =>
        redirect.Scheme == Uri.UriSchemeHttps &&
        IsSafePublicHttpUri(redirect) &&
        (Domains.Any(domain => HostMatchesDomain(redirect, domain)) ||
         string.Equals(
             redirect.AbsolutePath,
             source.AbsolutePath,
             StringComparison.Ordinal));

    private static Uri? TryDecodeLegacyHls(string value)
    {
        if (Uri.TryCreate(value, UriKind.Absolute, out var direct) &&
            direct.Scheme == Uri.UriSchemeHttps &&
            IsSafePublicHttpUri(direct))
        {
            return direct;
        }

        try
        {
            var bytes = Convert.FromBase64String(value);
            var text = Encoding.UTF8.GetString(bytes).Trim();
            if (Uri.TryCreate(text, UriKind.Absolute, out var decoded) &&
                decoded.Scheme == Uri.UriSchemeHttps &&
                IsSafePublicHttpUri(decoded))
            {
                return decoded;
            }
        }
        catch (FormatException)
        {
        }

        return null;
    }
}

/// <summary>
/// Mp4Upload exposes its native MP4 URL in the Video.js
/// <c>sources</c> configuration.
/// </summary>
public sealed class Mp4UploadHosterExtractor : HosterExtractorBase
{
    private static readonly Regex SourceRegex = new(
        """
        (?:src|file)\s*:\s*["'](?<value>https?:\/\/[^"']+\.mp4(?:\?[^"']*)?)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Mp4Upload";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "mp4upload.com");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            FetchTimeout,
            cancellationToken);
        var candidates = new List<string> { html };
        if (JavaScriptPacker.TryUnpackFirst(html, out var unpacked))
        {
            candidates.Add(unpacked);
        }

        foreach (var candidate in candidates)
        {
            foreach (Match match in SourceRegex.Matches(candidate))
            {
                var value = WebUtility.HtmlDecode(
                        match.Groups["value"].Value)
                    .Replace("\\/", "/", StringComparison.Ordinal);
                if (!Uri.TryCreate(
                        value,
                        UriKind.Absolute,
                        out var resolved) ||
                    !IsSafePublicHttpUri(resolved))
                {
                    continue;
                }

                return new ApiContentVideoSource(
                    source.Label,
                    resolved,
                    "video/mp4",
                    source.PlaybackUri,
                    ApiContentVideoSourceKind.DirectMedia);
            }
        }

        return null;
    }
}

/// <summary>
/// Streamtape hoster extractor. The page exposes a
/// <c>get_video?id=…&amp;expires=…&amp;ip=…&amp;token=…</c> query string
/// that, when prepended with the Streamtape origin, points at the
/// direct media URL.
/// </summary>
public sealed class StreamtapeHosterExtractor : HosterExtractorBase
{
    /// <summary>
    /// The assignment that builds the real link, of the shape
    /// <c>robotlink').innerHTML = '//st' + ('xcdreamtape.com/get_video?…')
    /// .substring(2).substring(1)</c>.
    /// <para>
    /// The page also contains several complete-looking <c>get_video</c> URLs
    /// whose tokens differ from this one only in their last characters. They
    /// are decoys: requesting one answers HTTP 200 with an HTML error page,
    /// which a player then tries to decode as video.
    /// </para>
    /// </summary>
    private static readonly Regex RobotLinkRegex = new(
        """
        robotlink'\)\.innerHTML\s*=\s*'(?<prefix>[^']*)'\s*\+\s*
        \('(?<rest>[^']*)'\)(?<ops>(?:\.substring\(\d+\))*)
        """,
        RegexOptions.CultureInvariant | RegexOptions.IgnorePatternWhitespace,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SubstringOffsetRegex = new(
        @"\.substring\((?<offset>\d+)\)",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // '<' is excluded as well: without it the token ran on into the closing
    // tag that follows it, producing a URL ending in "</div>".
    private static readonly Regex GetVideoRegex = new(
        """
        get_video\?id=[^&'"\s<]+&expires=[^&'"\s<]+&ip=[^&'"\s<]+&token=[^&'"\s<]+
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Streamtape";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "streamtape.com") ||
        HostMatchesDomain(playbackUri, "streamtape.to");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            FetchTimeout,
            cancellationToken);
        if (!TryReadRobotLink(html, out var resolved) &&
            !TryReadFirstGetVideoLink(html, out resolved))
        {
            // The original Python extractor retries on miss; we surface
            // null so the WebView fallback kicks in.
            return null;
        }

        return new ApiContentVideoSource(
            source.Label,
            resolved,
            mimeType: null,
            source.PlaybackUri,
            ApiContentVideoSourceKind.DirectMedia);
    }

    /// <summary>
    /// Rebuilds the link the page's own script assembles, which is the only
    /// one carrying a token the server accepts.
    /// </summary>
    private static bool TryReadRobotLink(string html, out Uri resolved)
    {
        resolved = null!;
        var match = RobotLinkRegex.Match(html);
        if (!match.Success)
        {
            return false;
        }

        var rest = match.Groups["rest"].Value;
        var dropped = 0;
        foreach (Match offset in SubstringOffsetRegex.Matches(
                     match.Groups["ops"].Value))
        {
            if (!int.TryParse(
                    offset.Groups["offset"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var count))
            {
                return false;
            }

            dropped += count;
        }

        if (dropped > rest.Length)
        {
            return false;
        }

        var assembled = match.Groups["prefix"].Value + rest[dropped..];
        if (assembled.StartsWith("//", StringComparison.Ordinal))
        {
            assembled = "https:" + assembled;
        }

        return Uri.TryCreate(assembled, UriKind.Absolute, out resolved!) &&
            IsSafePublicHttpUri(resolved);
    }

    /// <summary>
    /// Last resort for a page that no longer carries the script, accepting
    /// that its token may be one of the decoys.
    /// </summary>
    private static bool TryReadFirstGetVideoLink(string html, out Uri resolved)
    {
        resolved = null!;
        var match = GetVideoRegex.Match(html);
        return match.Success &&
            Uri.TryCreate(
                $"https://streamtape.com/{match.Value.TrimEnd('\'', '"')}",
                UriKind.Absolute,
                out resolved!) &&
            IsSafePublicHttpUri(resolved);
    }
}

/// <summary>
/// Doodstream hoster extractor. The page embeds
/// <c>/pass_md5/&lt;hash&gt;/&lt;token&gt;</c> in a script tag. The
/// extractor follows that path with the proper <c>Referer</c> header
/// and returns the body prepended with 10 random alphanumerics plus a
/// <c>token</c>/<c>expiry</c> query string.
/// </summary>
public sealed class DoodstreamHosterExtractor : HosterExtractorBase
{
    private const string DoodOrigin = "https://d0000d.com";
    private const int MaximumMediaUrlLength = 4 * 1024;
    private static readonly Regex PassMd5Regex = new(
        """
        /pass_md5/[\w\-]+/(?<token>[\w\-]+)
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Doodstream";

    public override bool MatchesContent(string content) =>
        content.Contains("/pass_md5/", StringComparison.Ordinal);

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "doodstream.com") ||
        HostMatchesDomain(playbackUri, "dood.li") ||
        HostMatchesDomain(playbackUri, "d0000d.com") ||
        HostMatchesDomain(playbackUri, "playmogo.com") ||
        HostMatchesDomain(playbackUri, "dsvplay.com");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var page = await FetchTextAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            FetchTimeout,
            cancellationToken,
            DefaultMaximumResponseBytes);
        var match = PassMd5Regex.Match(page.Content);
        if (!match.Success)
        {
            return null;
        }

        // The token is minted by whichever mirror actually served the page,
        // and is only redeemable there. Doodstream moves that host regularly,
        // so a pinned origin sent every request to a mirror that had never
        // issued the token and answered nothing usable.
        var origin = HosterSourceReader.GetOrigin(page.FinalUri);

        var passMd5 = match.Value;
        var token = match.Groups["token"].Value;
        var expiry = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()
            .ToString(CultureInfo.InvariantCulture);
        var passUri = new Uri(origin, passMd5);
        var body = await FetchHtmlAsync(
            httpClient,
            passUri,
            page.FinalUri,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            FetchTimeout,
            cancellationToken);
        var baseUrl = body.Trim();
        if (baseUrl.Length is 0 or > MaximumMediaUrlLength)
        {
            return null;
        }

        var randomSuffix = string.Concat(
            Enumerable.Range(0, 10)
                .Select(_ => RandomAlphaNumeric[Random.Shared.Next(RandomAlphaNumeric.Length)]));
        if (!Uri.TryCreate(
                $"{baseUrl}{randomSuffix}?token={token}&expiry={expiry}",
                UriKind.Absolute,
                out var resolved) ||
            !IsSafePublicHttpUri(resolved))
        {
            return null;
        }

        return new ApiContentVideoSource(
            source.Label,
            resolved,
            mimeType: null,
            origin,
            ApiContentVideoSourceKind.DirectMedia);
    }

    private const string RandomAlphaNumeric =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";
}

/// <summary>
/// Vidmoly hoster extractor. The page exposes a single HLS source in
/// a literal: <c>sources: [{file:"…"}]</c>.
/// </summary>
public sealed class VidmolyHosterExtractor : HosterExtractorBase
{
    private static readonly Regex SourcesRegex = new(
        """
        sources\s*:\s*\[\s*\{\s*file\s*:\s*"(?<value>[^"]+)"
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Vidmoly";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "vidmoly.to") ||
        HostMatchesDomain(playbackUri, "vidmoly.me");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            FetchTimeout,
            cancellationToken);
        var match = SourcesRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var url = WebUtility.HtmlDecode(match.Groups["value"].Value)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var resolved) ||
            !IsSafePublicHttpUri(resolved))
        {
            return null;
        }

        return new ApiContentVideoSource(
            source.Label,
            resolved,
            HosterMimeTypeResolver.MimeTypeFromUrl(resolved),
            new Uri(
                $"{source.PlaybackUri.Scheme}://" +
                $"{source.PlaybackUri.Authority}/"),
            ApiContentVideoSourceKind.DirectMedia);
    }
}

/// <summary>
/// Vidoza hoster extractor. The page exposes a single
/// <c>&lt;source src="…"&gt;</c> tag with the direct media URL.
/// </summary>
public sealed class VidozaHosterExtractor : HosterExtractorBase
{
    private static readonly Regex SourceTagRegex = new(
        """
        <source\b(?<attributes>[^>]*\bsrc\s*=\s*"(?<value>[^"]+)"[^>]*)/?>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Vidoza";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "vidoza.net") ||
        HostMatchesDomain(playbackUri, "vidoza.org");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            FetchTimeout,
            cancellationToken);
        var match = SourceTagRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        var url = WebUtility.HtmlDecode(match.Groups["value"].Value)
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Trim();
        if (!Uri.TryCreate(url, UriKind.Absolute, out var resolved) ||
            !IsSafePublicHttpUri(resolved))
        {
            return null;
        }

        return new ApiContentVideoSource(
            source.Label,
            resolved,
            HosterMimeTypeResolver.MimeTypeFromUrl(resolved),
            source.Referrer ?? source.PlaybackUri,
            ApiContentVideoSourceKind.DirectMedia);
    }
}

/// <summary>
/// LoadX hoster extractor. The page hides its media behind a
/// <c>X-Requested-With: XMLHttpRequest</c> POST to
/// <c>/player/index.php?data=&lt;videoId&gt;&amp;do=getVideo</c> that
/// returns a JSON document with a <c>videoSource</c> field.
/// </summary>
public sealed class LoadXHosterExtractor : HosterExtractorBase
{
    private const string LoadXOrigin = "https://loadx.ws";
    private const int MaximumJsonResponseBytes = 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "LoadX";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "loadx.ws") ||
        HostMatchesDomain(playbackUri, "loadx.top");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var path = source.PlaybackUri.AbsolutePath.AsSpan().Trim('/');
        var separator = path.LastIndexOf('/');
        var videoId = (separator >= 0 ? path[(separator + 1)..] : path)
            .ToString();
        if (videoId.Length == 0 || videoId.Length > 128)
        {
            return null;
        }

        var endpoint = new Uri(
            $"{LoadXOrigin}/player/index.php" +
            $"?data={Uri.EscapeDataString(videoId)}&do=getVideo");
        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint);
        request.Headers.Accept.ParseAdd("application/json, */*;q=0.5");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = source.PlaybackUri;
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36");
        request.Content = new StringContent(string.Empty, Encoding.UTF8);

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(FetchTimeout);
        using var response = await ApiProviderHttp.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token);
        if (!response.IsSuccessStatusCode)
        {
            return null;
        }

        var responseUri = response.RequestMessage?.RequestUri ?? endpoint;
        if (!IsSafePublicHttpUri(responseUri))
        {
            return null;
        }

        var json = await ReadBoundedResponseBytesAsync(
            response.Content,
            responseUri,
            MaximumJsonResponseBytes,
            timeoutSource.Token);
        string? videoSource = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind is
                JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    "videoSource",
                    out var videoElement) &&
                videoElement.ValueKind is
                JsonValueKind.String)
            {
                videoSource = videoElement.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(videoSource) ||
            !Uri.TryCreate(videoSource, UriKind.Absolute, out var resolved) ||
            !IsSafePublicHttpUri(resolved))
        {
            return null;
        }

        return new ApiContentVideoSource(
            source.Label,
            resolved,
            HosterMimeTypeResolver.MimeTypeFromUrl(resolved),
            source.Referrer ?? source.PlaybackUri,
            ApiContentVideoSourceKind.DirectMedia);
    }
}

/// <summary>
/// SpeedFiles hoster extractor. The page hides its URL in a
/// <c>var _0x5opu234 = "…";</c> literal that goes through five
/// transformations (base64 → swapcase → reverse → base64 → reverse →
/// chunked-hex → char-3 → swapcase → reverse → base64) to recover
/// the final <c>https://…</c> URL.
/// </summary>
public sealed class SpeedFilesHosterExtractor : HosterExtractorBase
{
    internal const int MaximumPayloadLength = 512 * 1024;
    private static readonly Regex PayloadRegex = new(
        """
        var\s+_0x5opu234\s*=\s*"(?<value>[^"]+)"
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "SpeedFiles";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "speedfiles.net") ||
        HostMatchesDomain(playbackUri, "speedfiles.io");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0 Safari/537.36",
            FetchTimeout,
            cancellationToken);
        var match = PayloadRegex.Match(html);
        if (!match.Success ||
            match.Groups["value"].Length > MaximumPayloadLength)
        {
            return null;
        }

        var resolved = TryDecode(match.Groups["value"].Value);
        if (resolved is null)
        {
            return null;
        }

        return new ApiContentVideoSource(
            source.Label,
            resolved,
            HosterMimeTypeResolver.MimeTypeFromUrl(resolved),
            source.Referrer ?? source.PlaybackUri,
            ApiContentVideoSourceKind.DirectMedia);
    }

    public static Uri? TryDecode(string payload)
    {
        ArgumentNullException.ThrowIfNull(payload);
        if (payload.Length is 0 or > MaximumPayloadLength)
        {
            return null;
        }

        // Step 1: base64.
        var step1 = TryFromBase64(payload.AsSpan());
        if (step1 is null)
        {
            return null;
        }

        // Step 2: swapcase.
        var step2 = SwapCase(step1.AsSpan());

        // Step 3: reverse.
        var step3 = Reverse(step2.AsSpan());

        // Step 4: base64.
        var step4 = TryFromBase64(step3.AsSpan());
        if (step4 is null)
        {
            return null;
        }

        // Step 5: reverse.
        var step5 = Reverse(step4.AsSpan());

        // Step 6: chunked hex → char.
        var step6 = DecodeHexPairs(step5.AsSpan());
        if (step6 is null)
        {
            return null;
        }

        // Step 7: char - 3.
        var step7 = ShiftCharacters(step6.AsSpan(), -3);

        // Step 8: swapcase.
        var step8 = SwapCase(step7.AsSpan());

        // Step 9: reverse.
        var step9 = Reverse(step8.AsSpan());

        // Step 10: base64.
        var final = TryFromBase64(step9.AsSpan());
        if (final is null)
        {
            return null;
        }

        var url = final.Trim();
        if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase) ||
            !Uri.TryCreate(url, UriKind.Absolute, out var resolved) ||
            !IsSafePublicHttpUri(resolved))
        {
            return null;
        }

        return resolved;
    }

    private static string? TryFromBase64(ReadOnlySpan<char> value)
    {
        var bytes = new byte[((value.Length + 3) / 4) * 3];
        if (!Convert.TryFromBase64Chars(value, bytes, out var bytesWritten))
        {
            return null;
        }

        return Encoding.UTF8.GetString(bytes.AsSpan(0, bytesWritten));
    }

    private static string SwapCase(ReadOnlySpan<char> value)
    {
        var buffer = value.ToArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = char.IsUpper(buffer[index])
                ? char.ToLowerInvariant(buffer[index])
                : char.IsLower(buffer[index])
                    ? char.ToUpperInvariant(buffer[index])
                    : buffer[index];
        }

        return new string(buffer);
    }

    private static string Reverse(ReadOnlySpan<char> value)
    {
        var buffer = value.ToArray();
        Array.Reverse(buffer);
        return new string(buffer);
    }

    private static string? DecodeHexPairs(ReadOnlySpan<char> value)
    {
        if (value.Length % 2 != 0)
        {
            return null;
        }

        var builder = new StringBuilder(value.Length / 2);
        for (var index = 0; index < value.Length; index += 2)
        {
            var pair = value.Slice(index, 2);
            if (!byte.TryParse(
                    pair,
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var code))
            {
                return null;
            }

            builder.Append((char)code);
        }

        return builder.ToString();
    }

    private static string ShiftCharacters(
        ReadOnlySpan<char> value,
        int offset)
    {
        var buffer = value.ToArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = (char)(buffer[index] + offset);
        }

        return new string(buffer);
    }
}

/// <summary>
/// YourUpload stores its JWPlayer source in an inline
/// <c>jwplayerOptions</c> object.
/// </summary>
public sealed class YourUploadHosterExtractor : HosterExtractorBase
{
    private static readonly Regex SourceRegex = new(
        """
        \bfile\s*:\s*["'](?<value>https?://[^"']+\.(?:m3u8|mp4)(?:\?[^"']*)?)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public override string Name => "YourUpload";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "yourupload.com") ||
        HostMatchesDomain(playbackUri, "yucache.net");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        return HosterSourceReader.CreateDirectSource(
            source,
            HosterSourceReader.TryReadMediaUri(SourceRegex, html),
            source.PlaybackUri);
    }
}

/// <summary>
/// Upzone either exposes an HLS source directly (sometimes inside a packed
/// script) or delegates playback to another hoster. Delegated sources remain
/// EmbeddedWeb so <see cref="VideoHosterResolver"/> can map them recursively.
/// </summary>
public sealed class UpzoneHosterExtractor : HosterExtractorBase
{
    private static readonly string[] Domains =
    [
        "upzone.cc", "upzone.to", "upzone.net", "upzone.link"
    ];
    private static readonly Regex TaggedDirectMediaRegex = new(
        """
        (?:
            ["']?\b(?:file|src|hls\d*)\b["']?\s*[:=]\s* |
            \bsource\s*=\s*
        )
        ["'](?<value>(?:(?:https?:)?//|/)[^"']+?\.m3u8(?:\?[^"']*)?)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex FallbackDirectMediaRegex = new(
        """
        (?<value>https?://[^\s"'\\<>]+\.m3u8[^\s"'\\<>]*)
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ButtonLinkRegex = new(
        """
        <a\b(?=[^>]*\bclass\s*=\s*["'][^"']*\bbuttonprch\b[^"']*["'])
        [^>]*\bhref\s*=\s*["'](?<value>[^"']+)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex IframeRegex = new(
        """<iframe\b[^>]*\bsrc\s*=\s*["'](?<value>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SourceElementRegex = new(
        """<source\b[^>]*\bsrc\s*=\s*["'](?<value>[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex InlineScriptRegex = new(
        """
        <script\b(?![^>]*\bsrc\s*=)[^>]*>(?<value>[\s\S]*?)</script\s*>
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex AbsoluteUrlRegex = new(
        """https?://[^\s"'\\<>]+""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public override string Name => "Upzone";

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var pageUri = source.PlaybackUri;
        var response = await FetchTextAsync(
            httpClient,
            pageUri,
            GetPageReferrer(pageUri),
            HosterBrowserHeaders.UserAgent,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            TimeSpan.FromSeconds(30),
            cancellationToken,
            DefaultMaximumResponseBytes);
        pageUri = response.FinalUri;
        var html = response.Content;
        var candidates = new List<string> { html };
        if (JavaScriptPacker.TryUnpackFirst(html, out var unpacked))
        {
            candidates.Add(unpacked);
        }

        foreach (var candidate in candidates)
        {
            var media = TryReadDirectMedia(candidate, pageUri);
            if (media is not null)
            {
                return HosterSourceReader.CreateDirectSource(
                    source,
                    media,
                    HosterSourceReader.GetOrigin(pageUri));
            }
        }

        foreach (var candidate in candidates)
        {
            var delegated = TryReadDelegatedUri(candidate, pageUri);
            if (delegated is null ||
                string.Equals(
                    delegated.AbsoluteUri,
                    pageUri.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new ApiContentVideoSource(
                source.Label,
                delegated,
                mimeType: null,
                pageUri,
                ApiContentVideoSourceKind.EmbeddedWeb);
        }

        return null;
    }

    private static Uri GetPageReferrer(Uri playbackUri)
    {
        var query = playbackUri.Query.AsSpan();
        if (!query.IsEmpty && query[0] == '?')
        {
            query = query[1..];
        }

        while (!query.IsEmpty)
        {
            var separator = query.IndexOf('&');
            var pair = separator >= 0 ? query[..separator] : query;
            query = separator >= 0 ? query[(separator + 1)..] : [];
            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex <= 0 ||
                !pair[..equalsIndex].Equals(
                    "reff",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = Uri.UnescapeDataString(
                pair[(equalsIndex + 1)..].ToString().Replace('+', ' ')).Trim();
            if (!value.StartsWith("http", StringComparison.OrdinalIgnoreCase))
            {
                value = "https://" + value.Trim('/') + "/";
            }

            if (Uri.TryCreate(value, UriKind.Absolute, out var referrer) &&
                IsSafePublicHttpUri(referrer))
            {
                return referrer;
            }
        }

        return HosterSourceReader.GetOrigin(playbackUri);
    }

    private static Uri? TryReadDirectMedia(string text, Uri pageUri)
    {
        var normalized = Normalize(text);
        foreach (var regex in new[]
                 {
                     TaggedDirectMediaRegex,
                     FallbackDirectMediaRegex
                 })
        {
            foreach (Match match in regex.Matches(normalized))
            {
                if (TryResolveSafeUri(
                        match.Groups["value"].Value,
                        pageUri,
                        out var media))
                {
                    return media;
                }
            }
        }

        return null;
    }

    private static Uri? TryReadDelegatedUri(string text, Uri pageUri)
    {
        var normalized = Normalize(text);
        var candidates = new List<Uri>();
        foreach (var regex in new[]
                 {
                     ButtonLinkRegex,
                     IframeRegex,
                     SourceElementRegex
                 })
        {
            foreach (Match match in regex.Matches(normalized))
            {
                AddCandidate(match.Groups["value"].Value);
            }
        }

        var redirect = HosterSourceReader.TryReadRedirectUri(normalized);
        if (redirect is not null)
        {
            if (!redirect.IsAbsoluteUri)
            {
                redirect = new Uri(pageUri, redirect);
            }

            if (IsSafePublicHttpUri(redirect))
            {
                AddCandidateUri(redirect);
            }
        }

        foreach (Match script in InlineScriptRegex.Matches(normalized))
        {
            foreach (Match url in AbsoluteUrlRegex.Matches(
                         script.Groups["value"].Value))
            {
                AddCandidate(url.Value);
            }
        }

        return candidates.FirstOrDefault(HosterFeedMap.Supports) ??
               candidates.FirstOrDefault();

        void AddCandidate(string value)
        {
            if (TryResolveSafeUri(value, pageUri, out var candidate))
            {
                AddCandidateUri(candidate);
            }
        }

        void AddCandidateUri(Uri candidate)
        {
            if (!string.Equals(
                    candidate.AbsoluteUri,
                    pageUri.AbsoluteUri,
                    StringComparison.OrdinalIgnoreCase) &&
                !candidates.Any(existing =>
                    string.Equals(
                        existing.AbsoluteUri,
                        candidate.AbsoluteUri,
                        StringComparison.OrdinalIgnoreCase)))
            {
                candidates.Add(candidate);
            }
        }
    }

    private static bool TryResolveSafeUri(
        string value,
        Uri pageUri,
        out Uri uri)
    {
        value = WebUtility.HtmlDecode(value.Trim());
        if (!Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var parsed))
        {
            uri = null!;
            return false;
        }

        uri = parsed.IsAbsoluteUri ? parsed : new Uri(pageUri, parsed);
        return IsSafePublicHttpUri(uri);
    }

    private static string Normalize(string text) =>
        text.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Uqload and several older hosters wrap their player configuration with
/// Dean Edwards' P.A.C.K.E.R. JavaScript compressor.
/// </summary>
public sealed class UqloadHosterExtractor : HosterExtractorBase
{
    public override string Name => "Uqload";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "uqload.cx") ||
        HostMatchesDomain(playbackUri, "uqload.is") ||
        HostMatchesDomain(playbackUri, "uqload.com");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        var media = HosterSourceReader.TryReadMediaUri(html);
        if (media is null &&
            JavaScriptPacker.TryUnpackFirst(html, out var unpacked))
        {
            media = HosterSourceReader.TryReadMediaUri(unpacked);
        }

        return HosterSourceReader.CreateDirectSource(
            source,
            media,
            HosterSourceReader.GetOrigin(source.PlaybackUri));
    }
}

/// <summary>
/// UpZur stores its player markup in a reversed array of JavaScript string
/// fragments whose characters may use \xNN or \uNNNN escapes.
/// </summary>
public sealed class UpZurHosterExtractor : HosterExtractorBase
{
    private const int MaximumArrayLength = 512 * 1024;
    private const int MaximumDecodedLength = 2 * 1024 * 1024;
    private const int MaximumElements = 8 * 1024;
    private static readonly Regex ArrayRegex = new(
        """
        \bvar\s+uHo4sc\s*=\s*\[(?<value>[\s\S]{0,524288}?)\]\s*;?
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ElementRegex = new(
        """
        "(?<double>(?:\\.|[^"\\]){0,65536})"
        |
        '(?<single>(?:\\.|[^'\\]){0,65536})'
        """,
        RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SourceRegex = new(
        """\bsrc\s*=\s*["'](?<value>https?://[^"']+)["']""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public override string Name => "UpZur";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "upzur.com");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer,
            HosterBrowserHeaders.UserAgent,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        return HosterSourceReader.CreateDirectSource(
            source,
            TryDecode(html),
            source.PlaybackUri);
    }

    internal static Uri? TryDecode(string html)
    {
        ArgumentNullException.ThrowIfNull(html);
        var array = ArrayRegex.Match(html);
        if (!array.Success ||
            array.Groups["value"].Length > MaximumArrayLength)
        {
            return null;
        }

        var encoded = array.Groups["value"].Value;
        var elements = ElementRegex.Matches(encoded);
        if (elements.Count is 0 or > MaximumElements)
        {
            return null;
        }

        var cursor = 0;
        foreach (Match element in elements)
        {
            if (!ContainsOnlySeparators(
                    encoded.AsSpan(cursor, element.Index - cursor)))
            {
                return null;
            }

            cursor = element.Index + element.Length;
        }

        if (!ContainsOnlySeparators(encoded.AsSpan(cursor)))
        {
            return null;
        }

        var decoded = new StringBuilder();
        for (var index = elements.Count - 1; index >= 0; index--)
        {
            var element = elements[index];
            var value = element.Groups["double"].Success
                ? element.Groups["double"].Value
                : element.Groups["single"].Value;
            if (!TryAppendDecodedFragment(value, decoded) ||
                decoded.Length > MaximumDecodedLength)
            {
                return null;
            }
        }

        var source = SourceRegex.Match(
            decoded.ToString().Replace("\\/", "/", StringComparison.Ordinal));
        if (!source.Success)
        {
            return null;
        }

        var mediaValue = WebUtility.HtmlDecode(
            source.Groups["value"].Value);
        return Uri.TryCreate(
                   mediaValue,
                   UriKind.Absolute,
                   out var media) &&
               IsSafePublicHttpUri(media)
            ? media
            : null;
    }

    private static bool TryAppendDecodedFragment(
        string value,
        StringBuilder output)
    {
        for (var index = 0; index < value.Length; index++)
        {
            if (value[index] != '\\' || index + 1 >= value.Length)
            {
                output.Append(value[index]);
                continue;
            }

            if (value[index + 1] == 'x' && index + 3 < value.Length &&
                ushort.TryParse(
                    value.AsSpan(index + 2, 2),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var hexCharacter))
            {
                output.Append((char)hexCharacter);
                index += 3;
                continue;
            }

            if (value[index + 1] == 'u' && index + 5 < value.Length &&
                ushort.TryParse(
                    value.AsSpan(index + 2, 4),
                    NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture,
                    out var unicodeCharacter))
            {
                output.Append((char)unicodeCharacter);
                index += 5;
                continue;
            }

            output.Append(value[index]);
        }

        return true;
    }

    private static bool ContainsOnlySeparators(ReadOnlySpan<char> value)
    {
        foreach (var character in value)
        {
            if (!char.IsWhiteSpace(character) && character != ',')
            {
                return false;
            }
        }

        return true;
    }
}

public abstract class GenericPackedHosterExtractor : HosterExtractorBase
{
    protected virtual int MaximumHops => 5;

    /// <summary>
    /// The packed player is the signature: if unpacking it yields a media URL,
    /// this is the shape this extractor handles no matter which mirror served
    /// it.
    /// </summary>
    public override bool MatchesContent(string content) =>
        JavaScriptPacker.TryUnpackFirst(content, out var unpacked) &&
        HosterSourceReader.TryReadMediaUri(unpacked) is not null;

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var current = source.PlaybackUri;
        var referrer = source.Referrer ?? HosterSourceReader.GetOrigin(current);
        for (var hop = 0; hop < MaximumHops; hop++)
        {
            var response = await FetchTextAsync(
                httpClient,
                current,
                referrer,
                HosterBrowserHeaders.UserAgent,
                "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
                TimeSpan.FromSeconds(20),
                cancellationToken,
                DefaultMaximumResponseBytes);
            current = response.FinalUri;
            var html = response.Content;
            var candidates = new List<string> { html };
            if (JavaScriptPacker.TryUnpackFirst(html, out var unpacked))
            {
                candidates.Add(unpacked);
            }

            foreach (var candidate in candidates)
            {
                var media = HosterSourceReader.TryReadMediaUri(candidate, current);
                if (media is not null)
                {
                    return HosterSourceReader.CreateDirectSource(
                        source,
                        media,
                        HosterSourceReader.GetOrigin(current));
                }
            }

            var redirect = candidates
                .Select(HosterSourceReader.TryReadRedirectUri)
                .FirstOrDefault(static uri => uri is not null);
            if (redirect is null)
            {
                return null;
            }

            if (!redirect.IsAbsoluteUri)
            {
                redirect = new Uri(current, redirect);
            }

            if (!IsSafePublicHttpUri(redirect))
            {
                return null;
            }

            referrer = current;
            current = redirect;
        }

        return null;
    }
}

public sealed class StreamSbHosterExtractor : GenericPackedHosterExtractor
{
    private static readonly string[] Domains =
    [
        "streamsb.net", "sbembed.com", "sbplay.org", "sbrapid.com",
        "sbvideo.net", "ssbstream.net", "streamsss.net"
    ];

    public override string Name => "StreamSB";

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));
}

public sealed class StreamlareHosterExtractor : GenericPackedHosterExtractor
{
    public override string Name => "Streamlare";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "streamlare.com");
}

public sealed class NinjaStreamHosterExtractor : GenericPackedHosterExtractor
{
    public override string Name => "NinjaStream";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "ninjastream.to");
}

public sealed class UchHosterExtractor : GenericPackedHosterExtractor
{
    public override string Name => "Uch";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "uch.to");
}

/// <summary>
/// StreamWish serves the ordinary packed player, but only from its delivery
/// mirrors — the headline domain answers with a loader stub that builds the
/// page in script, and there is nothing in it to read.
/// </summary>
/// <summary>
/// Mediafire is a file host rather than a streaming one: the page it serves is
/// the ordinary download page, and the direct link sits in its download anchor
/// already built. Nothing needs unpacking or a second request.
/// <para>
/// The link is matched by where it points rather than by the anchor's id,
/// because the id is written after the href on the element and attribute order
/// is not something worth depending on.
/// </para>
/// </summary>
public sealed class MediafireHosterExtractor : HosterExtractorBase
{
    private static readonly Regex DownloadLinkRegex = new(
        """
        href\s*=\s*(?<quote>["'])(?<value>https://download[0-9]*[.]mediafire[.]com/[^"']+)\k<quote>
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Mediafire";

    public override bool MatchesContent(string content) =>
        DownloadLinkRegex.IsMatch(content);

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "mediafire.com");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var page = await FetchTextAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer ??
            HosterSourceReader.GetOrigin(source.PlaybackUri),
            HosterBrowserHeaders.UserAgent,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            FetchTimeout,
            cancellationToken,
            DefaultMaximumResponseBytes);

        var match = DownloadLinkRegex.Match(page.Content);
        if (!match.Success ||
            !Uri.TryCreate(
                WebUtility.HtmlDecode(match.Groups["value"].Value).Trim(),
                UriKind.Absolute,
                out var media) ||
            !IsSafePublicHttpUri(media))
        {
            return null;
        }

        return HosterSourceReader.CreateDirectSource(
            source,
            media,
            HosterSourceReader.GetOrigin(page.FinalUri));
    }
}

public sealed class StreamWishHosterExtractor : GenericPackedHosterExtractor
{
    private static readonly string[] Domains =
    [
        "sfastwish.com", "flaswish.com", "streamwish.to"
    ];

    public override string Name => "StreamWish";

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));
}

/// <summary>
/// Mixdrop keeps its media URL in the packed player, assigned to a property
/// rather than declared as a source list, so unpacking alone does not surface
/// it — the assignment has to be read directly. The value is protocol
/// relative, which is why a scheme is put back on it here.
/// </summary>
public sealed class MixdropHosterExtractor : HosterExtractorBase
{
    private static readonly string[] Domains =
    [
        "mixdrop.top", "miixdrop.top", "mixdrop.co", "mixdrop.to",
        "mixdrop.club", "mixdrop.ps"
    ];

    private static readonly Regex WrappedUrlRegex = new(
        """
        MDCore\s*[.]\s*wurl\s*=\s*(?<quote>["'])(?<value>[^"']+)\k<quote>
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Mixdrop";

    public override bool MatchesContent(string content) =>
        WrappedUrlRegex.IsMatch(content) ||
        (JavaScriptPacker.TryUnpackFirst(content, out var unpacked) &&
         WrappedUrlRegex.IsMatch(unpacked));

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var page = await FetchTextAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer ??
            HosterSourceReader.GetOrigin(source.PlaybackUri),
            HosterBrowserHeaders.UserAgent,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            FetchTimeout,
            cancellationToken,
            DefaultMaximumResponseBytes);

        var candidates = new List<string> { page.Content };
        if (JavaScriptPacker.TryUnpackFirst(page.Content, out var unpacked))
        {
            candidates.Add(unpacked);
        }

        foreach (var candidate in candidates)
        {
            var match = WrappedUrlRegex.Match(candidate);
            if (!match.Success)
            {
                continue;
            }

            var value = WebUtility.HtmlDecode(match.Groups["value"].Value.Trim());
            if (value.StartsWith("//", StringComparison.Ordinal))
            {
                value = "https:" + value;
            }

            if (!Uri.TryCreate(page.FinalUri, value, out var media) ||
                !IsSafePublicHttpUri(media))
            {
                continue;
            }

            return HosterSourceReader.CreateDirectSource(
                source,
                media,
                HosterSourceReader.GetOrigin(page.FinalUri));
        }

        return null;
    }
}

/// <summary>
/// VidHide serves the ordinary packed-player page, but hands it over on a
/// domain that rotates: an embed published as <c>vidhidepro.com</c> answers
/// from somewhere else entirely. The base class follows that redirect and
/// reads the unpacked source, so only the published entry points are named
/// here — a mirror that appears later needs adding.
/// </summary>
public sealed class VidHideHosterExtractor : GenericPackedHosterExtractor
{
    private static readonly string[] Domains =
    [
        "vidhidepro.com", "callistanise.com", "vidhidevip.com",
        "vidhideplus.com"
    ];

    public override string Name => "VidHide";

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));
}

/// <summary>
/// Some sites publish no hoster links of their own: every server on a title
/// points at a wrapper page they host, which carries the real embed in a plain
/// assignment — either to a variable or straight to the location. The same
/// player software sits behind both sites handled here, which is why one
/// extractor covers them.
/// <para>
/// Returning that as an embedded source lets <see cref="VideoHosterResolver"/>
/// carry on through the normal extractor chain. This is what makes the site's
/// VOE, Doodstream and Streamtape servers resolvable at all — each already had
/// a working extractor, but no source ever reached one, because every playback
/// URL stopped at the player wrapper.
/// </para>
/// </summary>
public sealed class PlayerWrapperHosterExtractor : HosterExtractorBase
{
    private const int MaximumResponseBytes = 512 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    private static readonly Regex PlayerUrlRegex = new(
        """
        (?:(?:var|let|const)\s+url|(?:window\s*\.\s*)?location(?:\s*\.\s*href)?)
        \s*=\s*['"](?<value>https?:\/\/[^'"]+)['"]
        """,
        RegexOptions.IgnorePatternWhitespace |
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public override string Name => "PlayerWrapper";

    public override bool Matches(Uri playbackUri) =>
        (HostMatchesDomain(playbackUri, "poseidonhd2.co") &&
         playbackUri.Host.StartsWith(
             "player.",
             StringComparison.OrdinalIgnoreCase)) ||
        // Only the wrapper route, not the whole site.
        (HostMatchesDomain(playbackUri, "tioplus.app") &&
         playbackUri.AbsolutePath.StartsWith(
             "/player/",
             StringComparison.OrdinalIgnoreCase));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var response = await FetchTextAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer ??
            HosterSourceReader.GetOrigin(source.PlaybackUri),
            HosterBrowserHeaders.UserAgent,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            FetchTimeout,
            cancellationToken,
            MaximumResponseBytes);

        var match = PlayerUrlRegex.Match(response.Content);
        if (!match.Success ||
            !Uri.TryCreate(
                response.FinalUri,
                match.Groups["value"].Value.Trim(),
                out var target) ||
            !IsSafePublicHttpUri(target) ||
            // A wrapper that points at itself would otherwise be walked until
            // the chain's hop cap stopped it.
            Matches(target))
        {
            return null;
        }

        // Keeps the label the title listed, which names the server the viewer
        // picked rather than whatever it forwards to.
        return new ApiContentVideoSource(
            source.Label,
            target,
            referrer: response.FinalUri,
            kind: ApiContentVideoSourceKind.EmbeddedWeb);
    }
}

/// <summary>
/// Vixcloud constructs its HLS URL from <c>window.video</c> and
/// <c>window.masterPlaylist</c> values embedded in the player page.
/// </summary>
public sealed class VixcloudHosterExtractor : HosterExtractorBase
{
    private static readonly Regex VideoIdRegex = new(
        """
        window\.video\s*=\s*\{(?<body>[\s\S]{0,262144}?)\}\s*;
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex MasterPlaylistRegex = new(
        """
        window\.masterPlaylist\s*=\s*\{(?<body>[\s\S]{0,262144}?)\}\s*;
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex IdRegex = new(
        """
        \bid\s*:\s*["']?(?<value>\d{1,20})(?!\d)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex TokenRegex = new(
        """
        \btoken\s*:\s*["'](?<value>[^"']{1,4096})["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex ExpiresRegex = new(
        """
        \bexpires\s*:\s*["'](?<value>[^"']{1,128})["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex PlaylistUrlRegex = new(
        """
        \burl\s*:\s*["'](?<value>[^"']{1,4096})["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    public override string Name => "Vixcloud";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "vixcloud.co");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var origin = HosterSourceReader.GetOrigin(source.PlaybackUri);
        var html = await FetchHtmlAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer ?? origin,
            HosterBrowserHeaders.UserAgent,
            TimeSpan.FromSeconds(20),
            cancellationToken);
        var videoMatch = VideoIdRegex.Match(html);
        var idMatch = videoMatch.Success
            ? IdRegex.Match(videoMatch.Groups["body"].Value)
            : Match.Empty;
        if (!idMatch.Success)
        {
            return null;
        }

        var query = new List<string>();
        var playlistMatch = MasterPlaylistRegex.Match(html);
        var playlistBody = playlistMatch.Success
            ? playlistMatch.Groups["body"].Value
            : string.Empty;
        var token = TokenRegex.Match(playlistBody);
        var expires = ExpiresRegex.Match(playlistBody);
        if (token.Success)
        {
            query.Add("token=" + Uri.EscapeDataString(
                WebUtility.HtmlDecode(token.Groups["value"].Value)));
        }

        if (expires.Success)
        {
            query.Add("expires=" + Uri.EscapeDataString(
                WebUtility.HtmlDecode(expires.Groups["value"].Value)));
        }

        var playlistUrl = PlaylistUrlRegex.Match(playlistBody);
        if (playlistUrl.Success &&
            HasQueryParameter(
                WebUtility.HtmlDecode(
                    playlistUrl.Groups["value"].Value).AsSpan(),
                "b",
                "1"))
        {
            query.Add("b=1");
        }

        if (HasQueryParameter(
                source.PlaybackUri.Query.AsSpan(),
                "canPlayFHD"))
        {
            query.Add("h=1");
        }

        var path = $"playlist/{idMatch.Groups["value"].Value}";
        if (query.Count > 0)
        {
            path += "?" + string.Join("&", query);
        }

        return new ApiContentVideoSource(
            source.Label,
            new Uri(origin, path),
            "application/vnd.apple.mpegurl",
            origin,
            ApiContentVideoSourceKind.DirectMedia);
    }

    private static bool HasQueryParameter(
        ReadOnlySpan<char> value,
        ReadOnlySpan<char> expectedName,
        ReadOnlySpan<char> expectedValue = default)
    {
        var queryStart = value.IndexOf('?');
        if (queryStart >= 0)
        {
            value = value[(queryStart + 1)..];
        }
        else if (!value.IsEmpty && value[0] == '?')
        {
            value = value[1..];
        }

        while (!value.IsEmpty)
        {
            var separator = value.IndexOf('&');
            var pair = separator >= 0 ? value[..separator] : value;
            value = separator >= 0 ? value[(separator + 1)..] : [];
            var equals = pair.IndexOf('=');
            var name = equals >= 0 ? pair[..equals] : pair;
            var parameterValue = equals >= 0 ? pair[(equals + 1)..] : [];
            if (name.Equals(
                    expectedName,
                    StringComparison.OrdinalIgnoreCase) &&
                (expectedValue.IsEmpty ||
                 parameterValue.Equals(
                     expectedValue,
                     StringComparison.Ordinal)))
            {
                return true;
            }
        }

        return false;
    }
}

/// <summary>
/// Frembed is a broker: its API response contains links to other supported
/// hosters. Returning an embedded source lets <see cref="VideoHosterResolver"/>
/// continue through the normal extractor chain.
/// </summary>
public sealed class FrembedHosterExtractor : HosterExtractorBase
{
    private const int MaximumJsonResponseBytes = 1024 * 1024;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);

    public override string Name => "Frembed";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, "frembed.click");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var response = await FetchTextAsync(
            httpClient,
            source.PlaybackUri,
            source.Referrer ??
            HosterSourceReader.GetOrigin(source.PlaybackUri),
            HosterBrowserHeaders.UserAgent,
            "application/json, text/plain;q=0.9, */*;q=0.5",
            FetchTimeout,
            cancellationToken,
            MaximumJsonResponseBytes);
        var finalUri = response.FinalUri;
        if (!Matches(finalUri))
        {
            return new ApiContentVideoSource(
                source.Label,
                finalUri,
                referrer: source.PlaybackUri,
                kind: ApiContentVideoSourceKind.EmbeddedWeb);
        }

        try
        {
            using var document = JsonDocument.Parse(response.Content);
            if (document.RootElement.ValueKind is not JsonValueKind.Object)
            {
                return null;
            }

            var targets = new List<Uri>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!IsNumberedLinkProperty(property.Name.AsSpan()) ||
                    property.Value.ValueKind is not JsonValueKind.String)
                {
                    continue;
                }

                var value = property.Value.GetString();
                if (Uri.TryCreate(
                        source.PlaybackUri,
                        value,
                        out var target) &&
                    IsSafePublicHttpUri(target))
                {
                    targets.Add(target);
                }
            }

            var selected = targets.FirstOrDefault(HosterFeedMap.Supports) ??
                           targets.FirstOrDefault();
            return selected is null
                ? null
                : new ApiContentVideoSource(
                    source.Label,
                    selected,
                    referrer: source.PlaybackUri,
                    kind: ApiContentVideoSourceKind.EmbeddedWeb);
        }
        catch (JsonException)
        {
        }

        return null;
    }

    private static bool IsNumberedLinkProperty(ReadOnlySpan<char> name)
    {
        const string prefix = "link";
        if (name.Length <= prefix.Length ||
            !name[..prefix.Length].Equals(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        foreach (var character in name[prefix.Length..])
        {
            if (character is < '0' or > '9')
            {
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// Filemoon’s current player uses a P-256 attestation followed by an
/// AES-GCM encrypted playback payload.
/// </summary>
public sealed class FilemoonHosterExtractor : HosterExtractorBase
{
    private const int MaximumJsonResponseBytes = 2 * 1024 * 1024;
    private const int MaximumEncryptedPayloadLength = 1024 * 1024;
    private const int MaximumIvLength = 64;
    private const int MaximumKeyPartLength = 128;
    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    private static readonly Regex VideoIdRegex = new(
        @"/(?:e|d)/(?<value>[A-Za-z0-9]+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly string[] Domains =
    [
        "filemoon.site", "bf0skv.org", "bysejikuar.com",
        "moflix-stream.link", "bysezoxexe.com", "bysebuho.com",
        "filemoon.sx", "bysekoze.com", "bysesayeveum.com", "byselapuix.com"
    ];

    public override string Name => "Filemoon";

    public override bool MatchesContent(string content) =>
        content.Contains("<title>Byse Frontend</title>", StringComparison.OrdinalIgnoreCase) &&
        content.Contains("video-embed-mode", StringComparison.Ordinal);

    public override bool Matches(Uri playbackUri) =>
        Domains.Any(domain => HostMatchesDomain(playbackUri, domain));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var match = VideoIdRegex.Match(source.PlaybackUri.AbsolutePath);
        if (!match.Success)
        {
            return null;
        }

        var videoId = match.Groups["value"].Value;
        var sourceOrigin = HosterSourceReader.GetOrigin(source.PlaybackUri);
        using var details = await SendAsync(
            httpClient,
            HttpMethod.Get,
            new Uri(sourceOrigin, $"api/videos/{videoId}/embed/details"),
            body: null,
            referrer: source.PlaybackUri,
            origin: sourceOrigin,
            cancellationToken);
        if (!TryGetString(details, "embed_frame_url", out var embedText) ||
            !Uri.TryCreate(embedText, UriKind.Absolute, out var embedUri) ||
            !IsSafePublicHttpUri(embedUri))
        {
            return null;
        }

        var playbackOrigin = HosterSourceReader.GetOrigin(embedUri);
        using var challenge = await SendAsync(
            httpClient,
            HttpMethod.Post,
            new Uri(playbackOrigin, "api/videos/access/challenge"),
            body: new JsonObject(),
            referrer: embedUri,
            origin: playbackOrigin,
            cancellationToken);
        if (!TryGetString(challenge, "challenge_id", out var challengeId) ||
            !TryGetString(challenge, "nonce", out var nonce))
        {
            return null;
        }

        var viewerId = Guid.NewGuid().ToString("N");
        var deviceId = Guid.NewGuid().ToString("N");
        using var signingKey = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var publicParameters = signingKey.ExportParameters(false);
        var signature = signingKey.SignData(
            Encoding.UTF8.GetBytes(nonce),
            HashAlgorithmName.SHA256,
            DSASignatureFormat.IeeeP1363FixedFieldConcatenation);
        var attestationBody = new JsonObject
        {
            ["viewer_id"] = viewerId,
            ["device_id"] = deviceId,
            ["challenge_id"] = challengeId,
            ["nonce"] = nonce,
            ["signature"] = Base64Url(signature),
            ["public_key"] = new JsonObject
            {
                ["crv"] = "P-256",
                ["ext"] = true,
                ["key_ops"] = new JsonArray("verify"),
                ["kty"] = "EC",
                ["x"] = Base64Url(PadCoordinate(publicParameters.Q.X)),
                ["y"] = Base64Url(PadCoordinate(publicParameters.Q.Y))
            },
            ["client"] = new JsonObject
            {
                ["user_agent"] = HosterBrowserHeaders.UserAgent,
                ["architecture"] = "x86",
                ["bitness"] = "64",
                ["platform"] = "Windows",
                ["platform_version"] = "10.0.0",
                ["pixel_ratio"] = 1.0,
                ["screen_width"] = 1920,
                ["screen_height"] = 1080,
                ["languages"] = new JsonArray("en-US")
            },
            ["storage"] = new JsonObject
            {
                ["cookie"] = viewerId,
                ["local_storage"] = viewerId,
                ["indexed_db"] = $"{viewerId}:{deviceId}",
                ["cache_storage"] = $"{viewerId}:{deviceId}"
            },
            ["attributes"] = new JsonObject { ["entropy"] = "high" }
        };
        using var attestation = await SendAsync(
            httpClient,
            HttpMethod.Post,
            new Uri(playbackOrigin, "api/videos/access/attest"),
            attestationBody,
            embedUri,
            playbackOrigin,
            cancellationToken);
        if (!TryGetString(attestation, "token", out var token) ||
            !TryGetDouble(attestation, "confidence", out var confidence))
        {
            return null;
        }

        viewerId = TryGetString(
            attestation,
            "viewer_id",
            out var returnedViewer)
            ? returnedViewer
            : viewerId;
        deviceId = TryGetString(
            attestation,
            "device_id",
            out var returnedDevice)
            ? returnedDevice
            : deviceId;
        var playbackBody = new JsonObject
        {
            ["fingerprint"] = new JsonObject
            {
                ["token"] = token,
                ["viewer_id"] = viewerId,
                ["device_id"] = deviceId,
                ["confidence"] = confidence
            }
        };
        using var playback = await SendAsync(
            httpClient,
            HttpMethod.Post,
            new Uri(
                playbackOrigin,
                $"api/videos/{videoId}/embed/playback"),
            playbackBody,
            embedUri,
            playbackOrigin,
            cancellationToken,
            ("X-Embed-Parent", source.PlaybackUri.AbsoluteUri));
        if (!playback.RootElement.TryGetProperty(
                "playback",
                out var encrypted) ||
            !TryDecryptPlayback(encrypted, out var decryptedJson))
        {
            return null;
        }

        using (decryptedJson)
        {
            if (!decryptedJson.RootElement.TryGetProperty(
                    "sources",
                    out var sources) ||
                sources.ValueKind is not JsonValueKind.Array ||
                TrySelectMediaUri(sources) is not { } mediaUri)
            {
                return null;
            }

            return new ApiContentVideoSource(
                source.Label,
                mediaUri,
                HosterMimeTypeResolver.MimeTypeFromUrl(mediaUri),
                embedUri,
                ApiContentVideoSourceKind.DirectMedia);
        }
    }

    private static async Task<JsonDocument> SendAsync(
        HttpClient httpClient,
        HttpMethod method,
        Uri endpoint,
        JsonNode? body,
        Uri referrer,
        Uri origin,
        CancellationToken cancellationToken,
        params (string Name, string Value)[] extraHeaders)
    {
        if (!IsSafePublicHttpUri(endpoint))
        {
            throw new InvalidDataException("Filemoon returned an unsafe URL.");
        }

        using var request = new HttpRequestMessage(method, endpoint);
        request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
        request.Headers.Referrer = referrer;
        request.Headers.TryAddWithoutValidation(
            "Origin",
            origin.GetLeftPart(UriPartial.Authority));
        foreach (var (name, value) in extraHeaders)
        {
            request.Headers.TryAddWithoutValidation(name, value);
        }

        if (body is not null)
        {
            request.Content = new StringContent(
                body.ToJsonString(),
                Encoding.UTF8,
                "application/json");
        }

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(FetchTimeout);
        using var response = await ApiProviderHttp.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            timeoutSource.Token);
        response.EnsureSuccessStatusCode();
        var responseUri = response.RequestMessage?.RequestUri ?? endpoint;
        if (!IsSafePublicHttpUri(responseUri))
        {
            throw new InvalidDataException(
                "Filemoon returned an unsafe response URL.");
        }

        var json = await ReadBoundedResponseBytesAsync(
            response.Content,
            responseUri,
            MaximumJsonResponseBytes,
            timeoutSource.Token);
        return JsonDocument.Parse(json);
    }

    private static bool TryDecryptPlayback(
        JsonElement playback,
        out JsonDocument document)
    {
        document = null!;
        try
        {
            if (!TryGetString(playback, "iv", out var ivText) ||
                !TryGetString(playback, "payload", out var payloadText) ||
                ivText.Length > MaximumIvLength ||
                payloadText.Length > MaximumEncryptedPayloadLength ||
                !playback.TryGetProperty(
                    "key_parts",
                    out var parts) ||
                parts.ValueKind is not JsonValueKind.Array ||
                parts.GetArrayLength() is < 2 or > 8 ||
                parts[0].ValueKind is not JsonValueKind.String ||
                parts[1].ValueKind is not JsonValueKind.String)
            {
                return false;
            }

            var firstText = parts[0].GetString() ?? string.Empty;
            var secondText = parts[1].GetString() ?? string.Empty;
            if (firstText.Length > MaximumKeyPartLength ||
                secondText.Length > MaximumKeyPartLength)
            {
                return false;
            }

            var iv = FromBase64Url(ivText);
            var encrypted = FromBase64Url(payloadText);
            var first = FromBase64Url(firstText);
            var second = FromBase64Url(secondText);
            var key = new byte[first.Length + second.Length];
            first.CopyTo(key, 0);
            second.CopyTo(key, first.Length);
            if (encrypted.Length <= 16 ||
                encrypted.Length - 16 > MaximumJsonResponseBytes)
            {
                return false;
            }

            var cipherText = encrypted[..^16];
            var tag = encrypted[^16..];
            var clear = new byte[cipherText.Length];
            using var aes = new AesGcm(key, tag.Length);
            aes.Decrypt(iv, cipherText, tag, clear);
            document = JsonDocument.Parse(clear);
            return true;
        }
        catch (Exception exception)
            when (exception is FormatException or CryptographicException or
                  JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static Uri? TrySelectMediaUri(JsonElement sources)
    {
        Uri? fallback = null;
        foreach (var source in sources.EnumerateArray())
        {
            if (!TryGetString(source, "url", out var mediaText) ||
                !Uri.TryCreate(
                    mediaText,
                    UriKind.Absolute,
                    out var mediaUri) ||
                !IsSafePublicHttpUri(mediaUri))
            {
                continue;
            }

            if (HosterMimeTypeResolver.MimeTypeFromUrl(mediaUri) is not null)
            {
                return mediaUri;
            }

            fallback ??= mediaUri;
        }

        return fallback;
    }

    private static bool TryGetString(
        JsonDocument document,
        string name,
        out string value) =>
        TryGetString(document.RootElement, name, out value);

    private static bool TryGetString(
        JsonElement element,
        string name,
        out string value)
    {
        value = string.Empty;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               property.ValueKind is JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(value = property.GetString()!);
    }

    private static bool TryGetDouble(
        JsonDocument document,
        string name,
        out double value)
    {
        value = 0;
        return document.RootElement.TryGetProperty(name, out var property) &&
               property.TryGetDouble(out value);
    }

    private static byte[] PadCoordinate(byte[]? value)
    {
        if (value is null || value.Length >= 32)
        {
            return value ?? [];
        }

        var padded = new byte[32];
        value.CopyTo(padded, padded.Length - value.Length);
        return padded;
    }

    private static string Base64Url(byte[] value) =>
        Convert.ToBase64String(value)
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');

    private static byte[] FromBase64Url(string value)
    {
        var normalized = value.Replace('-', '+').Replace('_', '/');
        normalized += new string('=', (4 - normalized.Length % 4) % 4);
        return Convert.FromBase64String(normalized);
    }
}

internal static class HosterSourceReader
{
    private static readonly Regex RelativeTaggedMediaRegex = new(
        """
        (?:\b(?:file|src)\b\s*[:=]\s*|\bsources?\b\s*[:=]\s*\[\s*)
        ["'](?<value>(?![a-z]+:)[^"'<>]+\.(?:m3u8|mp4|mpd|webm)(?:[?#][^"'<>]*)?)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex TaggedMediaRegex = new(
        """
        (?:
            \b(?:file|src)\b\s*[:=]\s* |
            \bsources?\b\s*[:=]\s*\[\s*
        )
        ["'](?<value>https?://[^"']+\.(?:m3u8|mp4)(?:\?[^"']*)?)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex FallbackMediaRegex = new(
        """
        ["'](?<value>https?://[^"']+\.(?:m3u8|mp4)(?:\?[^"']*)?)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex RedirectRegex = new(
        """
        (?:
            (?:window\.)?location(?:\.href)?(?:\.replace)?\s*
            (?:=|\()\s* |
            location\.replace\(\s*
        )
        ["'](?<value>[^"']+)["']
        |
        <meta[^>]+http-equiv=["']refresh["'][^>]+
        content=["'][^"']*url=(?<value>[^"'>]+)
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    public static Uri GetOrigin(Uri uri) =>
        new(uri.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);

    public static Uri? TryReadMediaUri(string text, Uri? baseUri = null)
    {
        var normalized = Normalize(text);
        var tagged = TryReadNormalizedMediaUri(TaggedMediaRegex, normalized);
        if (tagged is not null) return tagged;
        if (baseUri is not null)
        {
            foreach (Match match in RelativeTaggedMediaRegex.Matches(normalized))
            {
                if (Uri.TryCreate(baseUri, WebUtility.HtmlDecode(match.Groups["value"].Value), out var uri) &&
                    HosterExtractorBase.IsSafePublicHttpUri(uri)) return uri;
            }
        }
        return TryReadNormalizedMediaUri(FallbackMediaRegex, normalized);
    }

    public static Uri? TryReadMediaUri(Regex regex, string text)
    {
        return TryReadNormalizedMediaUri(regex, Normalize(text));
    }

    private static Uri? TryReadNormalizedMediaUri(
        Regex regex,
        string normalized)
    {
        foreach (Match match in regex.Matches(normalized))
        {
            var value = WebUtility.HtmlDecode(
                match.Groups["value"].Value);
            if (Uri.TryCreate(value, UriKind.Absolute, out var uri) &&
                HosterExtractorBase.IsSafePublicHttpUri(uri))
            {
                return uri;
            }
        }

        return null;
    }

    public static Uri? TryReadRedirectUri(string text)
    {
        var match = RedirectRegex.Match(Normalize(text));
        if (!match.Success)
        {
            return null;
        }

        var value = WebUtility.HtmlDecode(
            match.Groups["value"].Value.Trim());
        return Uri.TryCreate(value, UriKind.RelativeOrAbsolute, out var uri)
            ? uri
            : null;
    }

    public static ApiContentVideoSource? CreateDirectSource(
        ApiContentVideoSource source,
        Uri? mediaUri,
        Uri referrer) =>
        mediaUri is null
            ? null
            : new ApiContentVideoSource(
                source.Label,
                mediaUri,
                HosterMimeTypeResolver.MimeTypeFromUrl(mediaUri),
                referrer,
                ApiContentVideoSourceKind.DirectMedia);

    private static string Normalize(string text) =>
        text.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Small, bounded decoder for the Dean Edwards P.A.C.K.E.R. shape used by
/// several public video-player pages. It does not execute JavaScript.
/// </summary>
internal static class JavaScriptPacker
{
    private const int MaximumPackedLength = 2 * 1024 * 1024;
    private const int MaximumUnpackedLength = 8 * 1024 * 1024;
    private const int MaximumKeywordLength = 64 * 1024;
    private const int MaximumWords = 100_000;
    private const string Digits =
        "0123456789abcdefghijklmnopqrstuvwxyzABCDEFGHIJKLMNOPQRSTUVWXYZ";
    private static readonly Regex DoubleQuotedPackerRegex = new(
        """
        eval\(function\(p,a,c,k,e,(?:d|r)\).*?\}\(\s*
        "(?<payload>(?:\\.|[^"\\])*)"\s*,\s*
        (?<radix>\d+)\s*,\s*(?<count>\d+)\s*,\s*
        "(?<keywords>(?:\\.|[^"\\])*)"\.split\(\s*["']\|["']\s*\)
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SingleQuotedPackerRegex = new(
        """
        eval\(function\(p,a,c,k,e,(?:d|r)\).*?\}\(\s*
        '(?<payload>(?:\\.|[^'\\])*)'\s*,\s*
        (?<radix>\d+)\s*,\s*(?<count>\d+)\s*,\s*
        '(?<keywords>(?:\\.|[^'\\])*)'\.split\(\s*["']\|["']\s*\)
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    public static bool TryUnpackFirst(string text, out string unpacked)
    {
        ArgumentNullException.ThrowIfNull(text);
        unpacked = string.Empty;
        if (text.Length is 0 or > MaximumPackedLength)
        {
            return false;
        }

        var match = DoubleQuotedPackerRegex.Match(text);
        var quote = '"';
        if (!match.Success)
        {
            match = SingleQuotedPackerRegex.Match(text);
            quote = '\'';
        }

        if (!match.Success ||
            !int.TryParse(
                match.Groups["radix"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var radix) ||
            radix is < 2 or > 62 ||
            !int.TryParse(
                match.Groups["count"].Value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var count) ||
            count is < 0 or > MaximumWords)
        {
            return false;
        }

        var payload = DecodeJavaScriptString(
            match.Groups["payload"].Value.AsSpan(),
            quote);
        var keywordText = DecodeJavaScriptString(
            match.Groups["keywords"].Value.AsSpan(),
            quote);
        if (payload is null ||
            keywordText is null ||
            !TryReadKeywords(keywordText.AsSpan(), count, out var keywords) ||
            !TryReplaceWords(
                payload.AsSpan(),
                radix,
                keywords,
                out unpacked))
        {
            unpacked = string.Empty;
            return false;
        }

        return unpacked.Length > 0;
    }

    private static bool TryReadKeywords(
        ReadOnlySpan<char> value,
        int count,
        out string[] keywords)
    {
        keywords = [];
        if (count == 0)
        {
            return value.IsEmpty;
        }

        var result = new List<string>(Math.Min(count, 1024));
        while (true)
        {
            var separator = value.IndexOf('|');
            var keyword = separator >= 0 ? value[..separator] : value;
            if (keyword.Length > MaximumKeywordLength ||
                result.Count >= count ||
                result.Count >= MaximumWords)
            {
                return false;
            }

            result.Add(keyword.ToString());
            if (separator < 0)
            {
                break;
            }

            value = value[(separator + 1)..];
        }

        keywords = result.ToArray();
        return true;
    }

    private static bool TryReplaceWords(
        ReadOnlySpan<char> payload,
        int radix,
        IReadOnlyList<string> keywords,
        out string unpacked)
    {
        var output = new StringBuilder(
            Math.Min(payload.Length, MaximumUnpackedLength));
        var copyStart = 0;
        var cursor = 0;
        while (cursor < payload.Length)
        {
            if (!IsAsciiAlphaNumeric(payload[cursor]) ||
                (cursor > 0 && IsRegexWordCharacter(payload[cursor - 1])))
            {
                cursor++;
                continue;
            }

            var end = cursor + 1;
            while (end < payload.Length &&
                   IsAsciiAlphaNumeric(payload[end]))
            {
                end++;
            }

            if (end < payload.Length &&
                IsRegexWordCharacter(payload[end]))
            {
                cursor = end;
                continue;
            }

            var token = payload[cursor..end];
            var keywordIndex = DecodeBase(token, radix);
            var replacement =
                keywordIndex >= 0 &&
                keywordIndex < keywords.Count &&
                !string.IsNullOrEmpty(keywords[keywordIndex])
                    ? keywords[keywordIndex].AsSpan()
                    : token;
            if (!TryAppendBounded(output, payload[copyStart..cursor]) ||
                !TryAppendBounded(output, replacement))
            {
                unpacked = string.Empty;
                return false;
            }

            copyStart = end;
            cursor = end;
        }

        if (!TryAppendBounded(output, payload[copyStart..]))
        {
            unpacked = string.Empty;
            return false;
        }

        unpacked = output.ToString();
        return true;
    }

    private static bool TryAppendBounded(
        StringBuilder output,
        ReadOnlySpan<char> value)
    {
        if (value.Length > MaximumUnpackedLength - output.Length)
        {
            return false;
        }

        output.Append(value);
        return true;
    }

    private static bool IsAsciiAlphaNumeric(char value) =>
        value is >= '0' and <= '9' or
            >= 'A' and <= 'Z' or
            >= 'a' and <= 'z';

    private static bool IsRegexWordCharacter(char value) =>
        value == '_' || char.IsLetterOrDigit(value);

    private static int DecodeBase(ReadOnlySpan<char> token, int radix)
    {
        long value = 0;
        foreach (var character in token)
        {
            var digit = Digits.IndexOf(character);
            if (digit < 0 || digit >= radix)
            {
                return -1;
            }

            value = (value * radix) + digit;
            if (value >= MaximumWords)
            {
                return -1;
            }
        }

        return (int)value;
    }

    private static string? DecodeJavaScriptString(
        ReadOnlySpan<char> value,
        char quote)
    {
        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var current = value[index];
            if (current != '\\' || index + 1 >= value.Length)
            {
                builder.Append(current);
                continue;
            }

            var escaped = value[++index];
            builder.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                '\\' => '\\',
                '/' => '/',
                _ when escaped == quote => quote,
                _ => escaped
            });
            if (builder.Length > MaximumPackedLength)
            {
                return null;
            }
        }

        return builder.ToString();
    }
}

public static class HosterMimeTypeResolver
{
    public static string? MimeTypeFromUrl(Uri url)
    {
        var path = url.AbsolutePath;
        if (path.EndsWith(".m3u8", StringComparison.OrdinalIgnoreCase))
        {
            return "application/vnd.apple.mpegurl";
        }

        if (path.EndsWith(".mpd", StringComparison.OrdinalIgnoreCase))
        {
            return "application/dash+xml";
        }

        if (path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
        {
            return "video/mp4";
        }

        if (path.EndsWith(".webm", StringComparison.OrdinalIgnoreCase))
        {
            return "video/webm";
        }

        if (path.EndsWith(".mkv", StringComparison.OrdinalIgnoreCase))
        {
            return "video/x-matroska";
        }

        return null;
    }
}

internal static class HosterBrowserHeaders
{
    public const string UserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/124.0 Safari/537.36";
}

/// <summary>
/// Resolves tvlibreonline.tv's <c>/html/fl/?get=…</c> iframe redirector
/// into a DASH manifest URL + ClearKey credentials. The site loads a
/// random player host from <c>/html/cv.json</c> and then asks the player
/// to play a JWPlayer-flavoured DASH stream whose <c>ContentProtection</c>
/// uses the EME "org.w3.clearkey" scheme; the matching kid+key pairs are
/// hard-coded inside the player page in a giant <c>if/else if</c> chain.
/// We pull them out and return a <see cref="ApiContentVideoSource"/> of
/// kind <see cref="ApiContentVideoSourceKind.DirectMedia"/> so the
/// libVLC player can decrypt the stream natively via
/// <c>--dash-clearkey=&lt;kid&gt;:&lt;key&gt;</c> instead of bouncing
/// through the WebView player.
/// </summary>
public sealed class TvLibrePlayerHosterExtractor : HosterExtractorBase
{
    private const string TvLibreHost = "tvlibreonline.tv";
    private const string ChannelListPath = "/html/cv.json";
    private const string PlayerPathSegment = "/cvatt.html";
    private const string PlayerReferer = "https://tvlibreonline.tv/";
    private const string MpdPathTemplate =
        "/live/c{0}eds/{1}/SA_Live_dash_enc_C/{1}.mpd";
    private const string MpdOrigin = "https://cdn.cvattv.com.ar";
    private const int MaxClearKeyPairs = 32;
    private const int MaxSubOriginLength = 32;
    private const int MaximumJsonResponseBytes = 256 * 1024;

    // Captures a `getURL == "<base64>"` block followed by its
    // `keyId = "…";` and `key = "…";` assignments. Comments in the
    // original source may appear anywhere between the values so we
    // match whitespace generously.
    private static readonly Regex ClearKeyBlockRegex = new(
        """
        getURL\s*==\s*"(?<get>[A-Za-z0-9+/=]{4,128})"\s*(?://[^\n]*\n)?\s*\{\s*keyId\s*=\s*"(?<keyId>[0-9a-fA-F]{16,64})"\s*;\s*key\s*=\s*"(?<key>[0-9a-fA-F]{16,64})"
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    // Captures the per-channel "number" the cvatt.html script uses as
    // the "c{number}eds/" segment of the DASH manifest path. The
    // channel count is decided by an `if/else if` chain that tests the
    // base64 channel name against a hard-coded list.
    private static readonly Regex ChannelNumberRegex = new(
        """
        if\s*\(\s*getURL\s*==\s*"(?<target>[A-Za-z0-9+/=]{4,128})"\s*\)\s*number\s*=\s*(?<value>\d+)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly TimeSpan FetchTimeout = TimeSpan.FromSeconds(20);
    private static readonly TimeSpan JsonTimeout = TimeSpan.FromSeconds(10);

    public override string Name => "TVLibrePlayer";

    public override bool Matches(Uri playbackUri) =>
        HostMatchesDomain(playbackUri, TvLibreHost) &&
        playbackUri.AbsolutePath.Trim('/').Equals(
            "html/fl",
            StringComparison.OrdinalIgnoreCase) &&
        GetChannelBase64(playbackUri) is not null;

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var channelBase64 = GetChannelBase64(source.PlaybackUri);
        if (string.IsNullOrEmpty(channelBase64))
        {
            return null;
        }

        var channelName = TryDecodeBase64(channelBase64);
        if (string.IsNullOrEmpty(channelName))
        {
            return null;
        }

        var playerHost = await ResolvePlayerHostAsync(
            source.PlaybackUri,
            httpClient,
            cancellationToken);
        if (string.IsNullOrEmpty(playerHost))
        {
            return null;
        }

        var playerUri = new UriBuilder
        {
            Scheme = Uri.UriSchemeHttps,
            Host = playerHost,
            Path = PlayerPathSegment,
            Query = $"get={Uri.EscapeDataString(channelBase64)}&lang=1"
        }.Uri;
        var html = await FetchHtmlAsync(
            httpClient,
            playerUri,
            new Uri(PlayerReferer),
            HosterBrowserHeaders.UserAgent,
            FetchTimeout,
            cancellationToken);

        if (!TryExtractClearKey(html, channelName, out var keyId, out var key))
        {
            return null;
        }

        if (!TryExtractChannelNumber(html, channelName, out var channelNumber))
        {
            channelNumber = 3;
        }

        var escapedChannelName = Uri.EscapeDataString(channelName);
        var mpdUri = new Uri(
            MpdOrigin +
            string.Format(
                System.Globalization.CultureInfo.InvariantCulture,
                MpdPathTemplate,
                channelNumber,
                escapedChannelName));
        var keyPair = new ApiContentVideoDecryptionKey(keyId, key);
        return new ApiContentVideoSource(
            source.Label,
            mpdUri,
            "application/dash+xml",
            source.Referrer ?? source.PlaybackUri,
            ApiContentVideoSourceKind.DirectMedia,
            [keyPair]);
    }

    private static string? GetChannelBase64(Uri playbackUri)
    {
        var query = playbackUri.Query.AsSpan();
        if (!query.IsEmpty && query[0] == '?')
        {
            query = query[1..];
        }

        if (query.IsEmpty)
        {
            return null;
        }

        while (!query.IsEmpty)
        {
            var separator = query.IndexOf('&');
            var pair = separator >= 0 ? query[..separator] : query;
            query = separator >= 0 ? query[(separator + 1)..] : [];
            var equalsIndex = pair.IndexOf('=');
            if (equalsIndex < 0)
            {
                continue;
            }

            if (pair[..equalsIndex].Equals(
                    "get",
                    StringComparison.OrdinalIgnoreCase))
            {
                var value = Uri.UnescapeDataString(
                    pair[(equalsIndex + 1)..].ToString());
                return value.Length is < 4 or > 128 ? null : value;
            }
        }

        return null;
    }

    private static string? TryDecodeBase64(string value)
    {
        try
        {
            var bytes = Convert.FromBase64String(value);
            var text = Encoding.UTF8.GetString(bytes).Trim();
            return text.Length is < 1 or > 64 ? null : text;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static async ValueTask<string?> ResolvePlayerHostAsync(
        Uri siteUri,
        HttpClient httpClient,
        CancellationToken cancellationToken)
    {
        var jsonUri = new Uri(siteUri, ChannelListPath);
        try
        {
            var response = await FetchTextAsync(
                httpClient,
                jsonUri,
                siteUri,
                HosterBrowserHeaders.UserAgent,
                "application/json, text/plain;q=0.9, */*;q=0.5",
                JsonTimeout,
                cancellationToken,
                MaximumJsonResponseBytes);
            using var document = JsonDocument.Parse(response.Content);
            if (document.RootElement.ValueKind is JsonValueKind.Object &&
                document.RootElement.TryGetProperty(
                    "urls",
                    out var urlsElement) &&
                urlsElement.ValueKind is JsonValueKind.Array)
            {
                foreach (var entry in urlsElement.EnumerateArray())
                {
                    if (entry.ValueKind is not JsonValueKind.String)
                    {
                        continue;
                    }

                    var host = entry.GetString();
                    if (!string.IsNullOrWhiteSpace(host) &&
                        host.Length is <= MaxSubOriginLength &&
                        !host.Contains("://", StringComparison.Ordinal) &&
                        !host.Contains('/', StringComparison.Ordinal) &&
                        Uri.TryCreate(
                            $"https://{host}/",
                            UriKind.Absolute,
                            out var hostUri) &&
                        IsSafePublicHttpUri(hostUri))
                    {
                        return hostUri.IdnHost;
                    }
                }
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
        }

        return null;
    }

    private static bool TryExtractClearKey(
        string html,
        string channelName,
        out string keyId,
        out string key)
    {
        keyId = string.Empty;
        key = string.Empty;

        var blocks = ClearKeyBlockRegex.Matches(html);
        if (blocks.Count == 0 || blocks.Count > MaxClearKeyPairs)
        {
            return false;
        }

        var target = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(channelName));
        foreach (Match match in blocks)
        {
            if (!string.Equals(
                    match.Groups["get"].Value,
                    target,
                    StringComparison.Ordinal))
            {
                continue;
            }

            keyId = match.Groups["keyId"].Value;
            key = match.Groups["key"].Value;
            return true;
        }

        return false;
    }

    private static bool TryExtractChannelNumber(
        string html,
        string channelName,
        out int number)
    {
        number = 0;
        var blocks = ChannelNumberRegex.Matches(html);
        if (blocks.Count == 0)
        {
            return false;
        }

        var target = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(channelName));
        foreach (Match match in blocks)
        {
            if (!string.Equals(
                    match.Groups["target"].Value,
                    target,
                    StringComparison.Ordinal))
            {
                continue;
            }

            return int.TryParse(
                match.Groups["value"].Value,
                System.Globalization.NumberStyles.None,
                System.Globalization.CultureInfo.InvariantCulture,
                out number);
        }

        return false;
    }
}

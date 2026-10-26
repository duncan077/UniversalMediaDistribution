using System.Collections.Concurrent;
using System.Net.Http.Headers;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Proof that a browser passed a host's bot check, and what it takes to keep
/// passing it.
/// </summary>
/// <param name="Host">The host the clearance belongs to.</param>
/// <param name="CookieHeader">
/// The cookie header the browser holds for that host, including the clearance
/// cookie itself.
/// </param>
/// <param name="UserAgent">
/// The user agent that solved the challenge. Clearance cookies are issued
/// against the requesting user agent, so a later request that sends a
/// different one is refused exactly as if it had no cookie at all.
/// </param>
public sealed record WebChallengeClearance(
    string Host,
    string CookieHeader,
    string UserAgent);

/// <summary>
/// A resource a browser was able to load when this application could not.
/// </summary>
/// <param name="Body">
/// What the browser received. For a JSON or plain-text route this is the raw
/// payload, not the viewer markup a browser wraps around it for display.
/// </param>
/// <param name="ContentType">
/// The media type the browser saw. Challenged routes are not all pages —
/// a search endpoint answering JSON is just as likely — and a provider that
/// is handed JSON labelled as HTML will not parse it.
/// </param>
/// <param name="IsBase64">
/// True for bounded raster image bytes; false for the existing text payloads.
/// </param>
public sealed record WebChallengeDocument(string Body, string ContentType, bool IsBase64 = false)
{
    public const int MaximumImageBytes = 4 * 1024 * 1024;

    // Read an already-loaded image document, not its browser-generated HTML
    // wrapper. Canvas encoding is bounded and keeps cookies in the browser.
    public const string ReportExpression = """
        (function () {
            try {
                var type = document.contentType || 'text/html';
                if (/^image\//i.test(type)) {
                    if (!/^image\/(jpeg|png|webp)$/i.test(type)) return null;
                    var image = document.querySelector('img');
                    if (!image || !image.complete || !image.naturalWidth || !image.naturalHeight) return null;
                    var scale = Math.min(1, 1024 / Math.max(image.naturalWidth, image.naturalHeight));
                    var canvas = document.createElement('canvas');
                    canvas.width = Math.max(1, Math.round(image.naturalWidth * scale));
                    canvas.height = Math.max(1, Math.round(image.naturalHeight * scale));
                    canvas.getContext('2d').drawImage(image, 0, 0, canvas.width, canvas.height);
                    var data = canvas.toDataURL('image/png');
                    var body = data.substring(data.indexOf(',') + 1);
                    if (!body || body.length > 5592408) return null;
                    return { contentType: 'image/png', body: body, isBase64: true };
                }
                return {
                    contentType: type,
                    body: type.indexOf('html') < 0
                        ? (document.body ? document.body.textContent : '')
                        : document.documentElement.outerHTML
                };
            } catch (error) { return null; }
        })()
        """;

    public static WebChallengeDocument? ParseReport(string message)
    {
        try
        {
            using var json = System.Text.Json.JsonDocument.Parse(message);
            var root = json.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object ||
                !root.TryGetProperty("body", out var body) ||
                body.ValueKind != System.Text.Json.JsonValueKind.String)
                return null;
            var type = root.TryGetProperty("contentType", out var contentType) &&
                contentType.ValueKind == System.Text.Json.JsonValueKind.String
                    ? contentType.GetString() : null;
            var encoded = root.TryGetProperty("isBase64", out var encoding) &&
                encoding.ValueKind == System.Text.Json.JsonValueKind.True;
            var value = body.GetString() ?? string.Empty;
            if (encoded && (value.Length > ((MaximumImageBytes + 2) / 3) * 4 ||
                !IsSupportedImageType(type)))
                return null;
            return new(value, string.IsNullOrWhiteSpace(type) ? "text/html" : type, encoded);
        }
        catch (System.Text.Json.JsonException)
        {
            return null;
        }
    }

    internal HttpContent CreateContent()
    {
        if (!IsBase64)
            return new StringContent(Body, System.Text.Encoding.UTF8, ContentType);
        if (!IsSupportedImageType(ContentType) ||
            Body.Length > ((MaximumImageBytes + 2) / 3) * 4)
            throw new InvalidDataException("The browser image exceeds its supported type or size.");
        byte[] bytes;
        try { bytes = Convert.FromBase64String(Body); }
        catch (FormatException exception)
        {
            throw new InvalidDataException("The browser returned invalid image bytes.", exception);
        }
        if (bytes.Length == 0 || bytes.Length > MaximumImageBytes)
            throw new InvalidDataException("The browser returned an empty or oversized image.");
        var content = new ByteArrayContent(bytes);
        content.Headers.ContentType = new MediaTypeHeaderValue(ContentType);
        return content;
    }

    private static bool IsSupportedImageType(string? type) =>
        string.Equals(type, "image/png", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "image/jpeg", StringComparison.OrdinalIgnoreCase) ||
        string.Equals(type, "image/webp", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Solves a host's browser check using the platform's browser engine.
/// Without one, the original refusal is surfaced unchanged.
/// </summary>
public interface IWebChallengeSolver
{
    /// <summary>Visible, user-initiated player challenge. Never called during catalog discovery.</summary>
    ValueTask<ApiContentVideoSource?> ResolveVideoAsync(
        ApiContentVideoSource source,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<ApiContentVideoSource?>(null);

    ValueTask<WebChallengeClearance?> SolveAsync(
        Uri endpoint,
        CancellationToken cancellationToken);

    /// <summary>
    /// Loads a page in a browser and returns what it received.
    /// <para>
    /// Some hosts issue no clearance cookie at all: they admit a browser and
    /// refuse this application on the shape of the connection itself, so there
    /// is no credential to copy across. The only thing that transfers is the
    /// page, so a solver that can load it answers here instead.
    /// </para>
    /// </summary>
    ValueTask<WebChallengeDocument?> FetchDocumentAsync(
        Uri endpoint,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult<WebChallengeDocument?>(null);
}

/// <summary>
/// Detects a bot-check refusal, obtains clearance once per host, and replays
/// it on every later request to that host.
/// <para>
/// Providers cannot do this individually: clearance is per host rather than
/// per provider, and it only holds while the user agent matches the one that
/// earned it, which the twenty-odd call sites that set their own user agent
/// would each have to know about. Applying it in the shared send path keeps
/// that in one place.
/// </para>
/// </summary>
public static class WebChallengeGate
{
    /// <summary>
    /// Sends artwork already approved by the caller's trusted-origin policy
    /// through the same challenge handling as the provider's pages.
    /// </summary>
    public static Task<HttpResponseMessage> SendImageAsync(
        HttpClient client, HttpRequestMessage request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Method != HttpMethod.Get || request.Content is not null ||
            request.RequestUri is not { IsAbsoluteUri: true, Scheme: "http" or "https", UserInfo.Length: 0 })
            throw new ArgumentException("Artwork requires a credential-free HTTP GET.", nameof(request));
        return ApiProviderHttp.SendAsync(client, request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
    }

    /// <summary>
    /// How long a clearance is reused before the host is asked again.
    /// Cloudflare's own clearance cookie outlives this comfortably; the bound
    /// exists so a revoked one is not retried forever.
    /// </summary>
    private static readonly TimeSpan ClearanceLifetime = TimeSpan.FromMinutes(30);


    /// <summary>
    /// How long a fetched page answers again without reloading it.
    /// <para>
    /// Short on purpose. A provider commonly reads the same page twice in one
    /// action — once to identify the title and once to open a unit — and
    /// driving a browser twice for it is seconds the user waits for nothing.
    /// Long enough to cover one action, too short to serve stale content to
    /// the next.
    /// </para>
    /// </summary>
    private static readonly TimeSpan DocumentReuseWindow =
        TimeSpan.FromSeconds(60);

    private static readonly ConcurrentDictionary<string, Entry> Entries =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, CachedDocument>
        Documents = new(StringComparer.Ordinal);

    /// <summary>
    /// Hosts that answered a browser without ever challenging it, and so have
    /// no clearance to hand over.
    /// <para>
    /// Whether a host issues a cookie is a property of the host, not of the
    /// moment: one that admits a browser on the shape of the connection alone
    /// will not start issuing one on the next request. Remembering it turns a
    /// full timeout per request into a decision, and is what lets an ordinary
    /// fetch delegate straight to the browser. Only this step is skipped —
    /// the page itself is still fetched every time, so a retry is always a
    /// real retry.
    /// </para>
    /// </summary>
    private static readonly ConcurrentDictionary<string, bool>
        HostsWithoutClearance = new(StringComparer.OrdinalIgnoreCase);

    private static readonly ConcurrentDictionary<string, SemaphoreSlim> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static IWebChallengeSolver? _solver;
    private static TimeProvider _timeProvider = TimeProvider.System;

    internal static bool HasSolver => Volatile.Read(ref _solver) is not null;

    internal static ValueTask<ApiContentVideoSource?> ResolveVideoAsync(
        ApiContentVideoSource source, CancellationToken cancellationToken) =>
        Volatile.Read(ref _solver)?.ResolveVideoAsync(source, cancellationToken) ??
        ValueTask.FromResult<ApiContentVideoSource?>(null);

    public static void Configure(
        IWebChallengeSolver? solver,
        TimeProvider? timeProvider = null)
    {
        Volatile.Write(ref _solver, solver);
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    internal static void Reset()
    {
        Entries.Clear();
        Gates.Clear();
        Documents.Clear();
        HostsWithoutClearance.Clear();
        Volatile.Write(ref _solver, null);
        _timeProvider = TimeProvider.System;
    }

    /// <summary>
    /// Whether a response is a bot check rather than a genuine refusal.
    /// <para>
    /// A plain 403 is not enough on its own — a host that simply forbids the
    /// route would send a browser at it on every request. Cloudflare labels
    /// its interstitials with <c>cf-mitigated: challenge</c>, and the
    /// interstitial page itself is the fallback signal for hosts that do not.
    /// </para>
    /// </summary>
    internal static bool IsChallenge(HttpResponseMessage response)
    {
        ArgumentNullException.ThrowIfNull(response);
        if (response.StatusCode is not (System.Net.HttpStatusCode.Forbidden or
            System.Net.HttpStatusCode.ServiceUnavailable))
        {
            return false;
        }

        // Deliberately narrow. A Cloudflare-fronted host that simply blocks a
        // route also answers 403 with an HTML body from a "cloudflare" server
        // — the media segments of at least one video host do exactly that —
        // and those are refusals no browser will talk its way past. Only the
        // interstitial announces itself with this header, so only it is
        // treated as solvable.
        return response.Headers.TryGetValues("cf-mitigated", out var mitigated) &&
            mitigated.Any(value => string.Equals(
                value?.Trim(),
                "challenge",
                StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Attaches an existing clearance to a request, replacing the caller's own
    /// user agent because the cookie is only accepted alongside the one that
    /// earned it.
    /// </summary>
    internal static bool TryApply(HttpRequestMessage request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.RequestUri is not { } uri ||
            !TryGetClearance(uri.IdnHost, out var clearance))
        {
            return false;
        }

        request.Headers.Remove("Cookie");
        request.Headers.TryAddWithoutValidation(
            "Cookie",
            clearance.CookieHeader);
        request.Headers.UserAgent.Clear();
        request.Headers.UserAgent.ParseAdd(clearance.UserAgent);
        return true;
    }

    /// <summary>
    /// Obtains clearance for a host, at most one attempt at a time and no more
    /// often than the backoff allows.
    /// </summary>
    internal static async ValueTask<bool> TrySolveAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Volatile.Read(ref _solver) is not { } solver)
        {
            return false;
        }

        var host = endpoint.IdnHost;
        if (HostsWithoutClearance.ContainsKey(host))
        {
            return false;
        }

        var gate = Gates.GetOrAdd(host, static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request for the same host may have solved it while this
            // one waited, which is the common case when a page load fans out.
            if (TryGetClearance(host, out _))
            {
                return true;
            }

            var clearance = await solver
                .SolveAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (clearance is null)
            {
                // Not a backoff on the content: the page is still fetched on
                // every request. This records only that asking this host for a
                // cookie is pointless, so the next request spends its time on
                // the fetch that works instead of on a timeout that cannot.
                HostsWithoutClearance[host] = true;
                return false;
            }

            Entries[host] = new Entry(
                clearance,
                _timeProvider.GetUtcNow() + ClearanceLifetime);
            return true;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
        finally
        {
            gate.Release();
        }
    }

    /// <summary>
    /// Asks a browser to load the page this application was refused, for the
    /// hosts where no cookie would have helped.
    /// </summary>
    internal static async ValueTask<WebChallengeDocument?> TryFetchDocumentAsync(
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
        if (Volatile.Read(ref _solver) is not { } solver)
        {
            return null;
        }

        // Serialised per host so one browser runs at a time, but never
        // suppressed: a failed load is not evidence about the next one, and
        // the retry is the user's to make.
        var key = endpoint.AbsoluteUri;
        if (TryGetCachedDocument(key, out var cached))
        {
            return cached;
        }

        var gate = Gates.GetOrAdd(
            "document:" + endpoint.IdnHost,
            static _ => new SemaphoreSlim(1, 1));
        await gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            // Another request may have loaded the same page while this one
            // queued behind the single browser.
            if (TryGetCachedDocument(key, out cached))
            {
                return cached;
            }

            var document = await solver
                .FetchDocumentAsync(endpoint, cancellationToken)
                .ConfigureAwait(false);
            if (document is not null && !document.IsBase64)
            {
                Documents[key] = new CachedDocument(
                    document,
                    _timeProvider.GetUtcNow() + DocumentReuseWindow);
            }

            return document;
        }
        catch (OperationCanceledException)
            when (!cancellationToken.IsCancellationRequested)
        {
            return null;
        }
        finally
        {
            gate.Release();
        }
    }

    private static bool TryGetCachedDocument(
        string key,
        out WebChallengeDocument? document)
    {
        document = null;
        if (!Documents.TryGetValue(key, out var cached))
        {
            return false;
        }

        if (_timeProvider.GetUtcNow() >= cached.ExpiresAt)
        {
            Documents.TryRemove(key, out _);
            return false;
        }

        document = cached.Document;
        return true;
    }

    private sealed record CachedDocument(
        WebChallengeDocument Document,
        DateTimeOffset ExpiresAt);

    private static bool TryGetClearance(
        string host,
        out WebChallengeClearance clearance)
    {
        clearance = null!;
        if (!Entries.TryGetValue(host, out var entry) ||
            entry.Clearance is null ||
            _timeProvider.GetUtcNow() >= entry.ExpiresAt)
        {
            return false;
        }

        clearance = entry.Clearance;
        return true;
    }

    private sealed record Entry(
        WebChallengeClearance? Clearance,
        DateTimeOffset ExpiresAt);
}

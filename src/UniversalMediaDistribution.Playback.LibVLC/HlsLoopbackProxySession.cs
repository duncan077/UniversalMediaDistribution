using System.Buffers;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Server.Kestrel.Core;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Net.Http.Headers;
using UniversalMediaDistribution.UI.Diagnostics;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Playback.LibVLC;

/// <summary>
/// Gives libVLC a private loopback HLS endpoint while this process owns every
/// upstream request. This is how hoster-required headers such as Origin reach
/// playlists, encryption keys, initialization maps, and media segments even
/// though libVLC 3 cannot set arbitrary HTTP headers.
/// </summary>
internal sealed class HlsLoopbackProxySession : IAsyncDisposable
{
    private const int MaximumPlaylistBytes = 4 * 1024 * 1024;
    private const int MaximumMappedResources = 32 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan UpstreamRequestTimeout =
        TimeSpan.FromMinutes(2);
    private static readonly TimeSpan ShutdownTimeout =
        TimeSpan.FromSeconds(3);

    private readonly object _resourceGate = new();
    private readonly Dictionary<string, ProxyResource> _resources =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _resourceIds =
        new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetime = new();
    private readonly HttpClient _httpClient;
    private readonly Uri _httpOrigin;
    private readonly Uri? _referrer;
    private readonly string _userAgent;
    private readonly string _accessToken =
        Convert.ToHexString(RandomNumberGenerator.GetBytes(32))
            .ToLowerInvariant();
    private WebApplication? _application;
    private Uri? _loopbackBaseUri;
    private long _nextResourceId;
    private int _disposeState;

    private HlsLoopbackProxySession(
        Uri httpOrigin,
        Uri? referrer,
        string userAgent,
        HttpMessageHandler upstreamHandler)
    {
        _httpOrigin = httpOrigin;
        _referrer = referrer;
        _userAgent = userAgent;
        _httpClient = new HttpClient(
            upstreamHandler,
            disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    public Uri PlaybackUri { get; private set; } = null!;

    public static ValueTask<HlsLoopbackProxySession> StartAsync(
        PlatformMediaSource source,
        string defaultUserAgent,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentException.ThrowIfNullOrWhiteSpace(defaultUserAgent);
        if (source.PlaybackUri is null || source.HttpOrigin is null)
        {
            throw new ArgumentException(
                "An HLS proxy requires a playback URI and HTTP origin.",
                nameof(source));
        }

        return StartCoreAsync(
            source.PlaybackUri,
            source.HttpOrigin,
            source.Referrer,
            source.UserAgent ?? defaultUserAgent,
            CreateUpstreamHandler(),
            cancellationToken);
    }

    internal static ValueTask<HlsLoopbackProxySession> StartForTestAsync(
        Uri playbackUri,
        Uri httpOrigin,
        Uri? referrer,
        string userAgent,
        HttpMessageHandler upstreamHandler,
        CancellationToken cancellationToken = default) =>
        StartCoreAsync(
            playbackUri,
            httpOrigin,
            referrer,
            userAgent,
            upstreamHandler,
            cancellationToken);

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposeState, 1) != 0)
        {
            return;
        }

        await _lifetime.CancelAsync();
        var application = Interlocked.Exchange(ref _application, null);
        if (application is not null)
        {
            using var shutdown = new CancellationTokenSource(ShutdownTimeout);
            try
            {
                await application.StopAsync(shutdown.Token);
            }
            catch (OperationCanceledException)
            {
                // Disposal must remain bounded if a client disappeared while
                // an upstream response was being copied.
            }

            await application.DisposeAsync();
        }

        _httpClient.Dispose();
        _lifetime.Dispose();
    }

    private static async ValueTask<HlsLoopbackProxySession> StartCoreAsync(
        Uri playbackUri,
        Uri httpOrigin,
        Uri? referrer,
        string userAgent,
        HttpMessageHandler upstreamHandler,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(playbackUri);
        ArgumentNullException.ThrowIfNull(httpOrigin);
        ArgumentException.ThrowIfNullOrWhiteSpace(userAgent);
        ArgumentNullException.ThrowIfNull(upstreamHandler);
        if (!PublicHttpEndpointPolicy.IsSafePublicHttpUri(playbackUri))
        {
            upstreamHandler.Dispose();
            throw new ArgumentException(
                "The proxied playback URI must be a safe public HTTP endpoint.",
                nameof(playbackUri));
        }

        if (!IsHttpOrigin(httpOrigin))
        {
            upstreamHandler.Dispose();
            throw new ArgumentException(
                "The media Origin header must contain only an HTTP origin.",
                nameof(httpOrigin));
        }

        if (referrer is not null &&
            (!referrer.IsAbsoluteUri ||
             referrer.Scheme is not ("http" or "https") ||
             referrer.UserInfo.Length > 0))
        {
            upstreamHandler.Dispose();
            throw new ArgumentException(
                "The media referrer must be an absolute credential-free HTTP URI.",
                nameof(referrer));
        }

        var session = new HlsLoopbackProxySession(
            httpOrigin,
            referrer,
            userAgent,
            upstreamHandler);
        try
        {
            await session.StartServerAsync(
                playbackUri,
                cancellationToken);
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    private async Task StartServerAsync(
        Uri playbackUri,
        CancellationToken cancellationToken)
    {
        var builder = WebApplication.CreateSlimBuilder(
            new WebApplicationOptions
            {
                Args = [],
                ApplicationName = typeof(HlsLoopbackProxySession)
                    .Assembly
                    .GetName()
                    .Name
            });
        builder.Logging.ClearProviders();
        builder.WebHost.UseSetting(
            WebHostDefaults.PreventHostingStartupKey,
            "true");
        builder.WebHost.ConfigureKestrel(options =>
        {
            options.AddServerHeader = false;
            options.Limits.MaxRequestBodySize = 0;
            options.Limits.MaxRequestHeaderCount = 32;
            options.Limits.MaxRequestHeadersTotalSize = 16 * 1024;
            options.Limits.RequestHeadersTimeout = TimeSpan.FromSeconds(10);
            options.Listen(
                IPAddress.Loopback,
                0,
                listen => listen.Protocols = HttpProtocols.Http1);
        });

        var application = builder.Build();
        _application = application;
        application.MapMethods(
            $"/{_accessToken}/{{resourceId}}",
            [HttpMethods.Get, HttpMethods.Head],
            HandleRequestAsync);
        await application.StartAsync(cancellationToken);

        var server = application.Services.GetRequiredService<IServer>();
        var addresses = server.Features
            .Get<IServerAddressesFeature>()?
            .Addresses
            .ToArray();
        if (addresses is not { Length: 1 } ||
            !Uri.TryCreate(
                addresses[0],
                UriKind.Absolute,
                out var listeningUri) ||
            !IPAddress.TryParse(
                listeningUri.Host,
                out var hostAddress) ||
            !IPAddress.IsLoopback(hostAddress))
        {
            throw new InvalidOperationException(
                "The HLS proxy did not bind exclusively to one loopback address.");
        }

        _loopbackBaseUri = new Uri(
            addresses[0].TrimEnd('/') + "/");
        PlaybackUri = RegisterResourceUri(
            playbackUri,
            HlsProxyResourceKind.Playlist);
        LogStarted(playbackUri);
    }

    private async Task HandleRequestAsync(HttpContext context)
    {
        context.Response.Headers.CacheControl = "no-store";
        context.Response.Headers.Pragma = "no-cache";
        if (Volatile.Read(ref _disposeState) != 0)
        {
            context.Response.StatusCode = StatusCodes.Status410Gone;
            return;
        }

        var resourceId = context.Request.RouteValues["resourceId"] as string;
        if (resourceId is null || !TryGetResource(resourceId, out var resource))
        {
            context.Response.StatusCode = StatusCodes.Status404NotFound;
            return;
        }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            context.RequestAborted,
            _lifetime.Token);
        timeout.CancelAfter(UpstreamRequestTimeout);
        try
        {
            using var upstream = await SendUpstreamAsync(
                resource,
                context.Request,
                timeout.Token);
            await CopyResponseAsync(
                context,
                resource,
                upstream,
                timeout.Token);
        }
        catch (OperationCanceledException)
            when (context.RequestAborted.IsCancellationRequested ||
                  _lifetime.IsCancellationRequested)
        {
            // The client or playback session closed. Kestrel owns the aborted
            // response, and no error body should race with it.
        }
        catch (Exception exception) when (
            exception is HttpRequestException or
                InvalidDataException or
                IOException or
                TaskCanceledException or
                UriFormatException)
        {
            LogFailure(resource.UpstreamUri, exception);
            if (!context.Response.HasStarted)
            {
                context.Response.Clear();
                context.Response.StatusCode = StatusCodes.Status502BadGateway;
                context.Response.ContentType = "text/plain; charset=utf-8";
                context.Response.Headers.CacheControl = "no-store";
                await context.Response.WriteAsync(
                    "The upstream media resource could not be loaded.",
                    context.RequestAborted);
            }
            else
            {
                context.Abort();
            }
        }
    }

    private async Task<UpstreamResponse> SendUpstreamAsync(
        ProxyResource resource,
        HttpRequest inbound,
        CancellationToken cancellationToken)
    {
        var currentUri = resource.UpstreamUri;
        for (var redirectCount = 0;
             redirectCount <= MaximumRedirects;
             redirectCount++)
        {
            if (!PublicHttpEndpointPolicy.IsSafePublicHttpUri(currentUri))
            {
                throw new InvalidDataException(
                    "The upstream media endpoint was not public HTTP.");
            }

            using var request = CreateUpstreamRequest(
                currentUri,
                resource.Kind,
                inbound);
            var response = await _httpClient.SendAsync(
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseUri =
                response.RequestMessage?.RequestUri ?? currentUri;
            if (!PublicHttpEndpointPolicy.IsSafePublicHttpUri(responseUri))
            {
                response.Dispose();
                throw new InvalidDataException(
                    "The upstream response endpoint was not public HTTP.");
            }

            if (!IsRedirect(response.StatusCode))
            {
                return new UpstreamResponse(response, responseUri);
            }

            if (redirectCount == MaximumRedirects ||
                response.Headers.Location is null)
            {
                response.Dispose();
                throw new InvalidDataException(
                    "The upstream media redirect chain was invalid or excessive.");
            }

            var nextUri = response.Headers.Location.IsAbsoluteUri
                ? response.Headers.Location
                : new Uri(responseUri, response.Headers.Location);
            response.Dispose();
            if (!PublicHttpEndpointPolicy.IsSafePublicHttpUri(nextUri))
            {
                throw new InvalidDataException(
                    "The upstream media redirect targeted a non-public endpoint.");
            }

            currentUri = nextUri;
        }

        throw new InvalidDataException(
            "The upstream media redirect chain was excessive.");
    }

    private HttpRequestMessage CreateUpstreamRequest(
        Uri uri,
        HlsProxyResourceKind kind,
        HttpRequest inbound)
    {
        var request = new HttpRequestMessage(
            HttpMethods.IsHead(inbound.Method)
                ? HttpMethod.Head
                : HttpMethod.Get,
            uri);
        request.Headers.Accept.ParseAdd("*/*");
        request.Headers.TryAddWithoutValidation(
            HeaderNames.UserAgent,
            _userAgent);
        request.Headers.TryAddWithoutValidation(
            HeaderNames.Origin,
            _httpOrigin.GetLeftPart(UriPartial.Authority));
        if (_referrer is not null)
        {
            request.Headers.Referrer = _referrer;
        }

        request.Headers.AcceptLanguage.ParseAdd(
            "es-AR,es;q=0.9,en;q=0.8");
        request.Headers.TryAddWithoutValidation(
            "Sec-Fetch-Dest",
            "empty");
        request.Headers.TryAddWithoutValidation(
            "Sec-Fetch-Mode",
            "cors");
        request.Headers.TryAddWithoutValidation(
            "Sec-Fetch-Site",
            "cross-site");

        if (kind is not HlsProxyResourceKind.Playlist &&
            inbound.Headers.TryGetValue(HeaderNames.Range, out var range) &&
            System.Net.Http.Headers.RangeHeaderValue.TryParse(
                range.ToString(),
                out var parsedRange) &&
            parsedRange.Ranges.Count == 1)
        {
            request.Headers.Range = parsedRange;
            if (inbound.Headers.TryGetValue(
                    HeaderNames.IfRange,
                    out var ifRange))
            {
                request.Headers.TryAddWithoutValidation(
                    HeaderNames.IfRange,
                    ifRange.ToArray());
            }
        }

        return request;
    }

    private async Task CopyResponseAsync(
        HttpContext context,
        ProxyResource resource,
        UpstreamResponse upstream,
        CancellationToken cancellationToken)
    {
        var response = upstream.Response;
        var rewritePlaylist =
            response.IsSuccessStatusCode &&
            (resource.Kind is HlsProxyResourceKind.Playlist ||
             IsHlsPlaylist(response, upstream.FinalUri));
        if (rewritePlaylist &&
            !HttpMethods.IsHead(context.Request.Method))
        {
            var playlistBytes = await ReadBoundedBytesAsync(
                response.Content,
                MaximumPlaylistBytes,
                cancellationToken);
            var playlist = Encoding.UTF8.GetString(playlistBytes);
            if (!IsHlsDocument(playlist))
            {
                throw new InvalidDataException(
                    "The upstream endpoint did not return an HLS playlist.");
            }

            var rewritten = HlsPlaylistRewriter.Rewrite(
                playlist,
                upstream.FinalUri,
                RegisterResource);
            var output = Encoding.UTF8.GetBytes(rewritten);

            ApplyResponseHeaders(
                context.Response,
                response,
                rewrittenBody: true);
            context.Response.ContentType =
                "application/vnd.apple.mpegurl";
            context.Response.ContentLength = output.Length;
            await context.Response.Body.WriteAsync(
                output,
                cancellationToken);
            return;
        }

        ApplyResponseHeaders(
            context.Response,
            response,
            rewrittenBody: false);
        if (HttpMethods.IsHead(context.Request.Method) ||
            response.StatusCode is HttpStatusCode.NoContent or
                HttpStatusCode.NotModified)
        {
            return;
        }

        await response.Content.CopyToAsync(
            context.Response.Body,
            cancellationToken);
    }

    private static void ApplyResponseHeaders(
        HttpResponse downstream,
        HttpResponseMessage upstream,
        bool rewrittenBody)
    {
        downstream.StatusCode = (int)upstream.StatusCode;
        downstream.Headers.CacheControl = "no-store";
        downstream.Headers.Pragma = "no-cache";
        if (upstream.Content.Headers.ContentType is { } contentType)
        {
            downstream.ContentType = contentType.ToString();
        }

        if (!rewrittenBody)
        {
            downstream.ContentLength =
                upstream.Content.Headers.ContentLength;
            if (upstream.Content.Headers.ContentRange is { } contentRange)
            {
                downstream.Headers.ContentRange = contentRange.ToString();
            }

            if (upstream.Content.Headers.ContentEncoding.Count > 0)
            {
                downstream.Headers.ContentEncoding =
                    upstream.Content.Headers.ContentEncoding.ToArray();
            }
        }

        if (upstream.Headers.AcceptRanges.Count > 0)
        {
            downstream.Headers.AcceptRanges =
                upstream.Headers.AcceptRanges.ToArray();
        }

        if (upstream.Headers.ETag is { } entityTag)
        {
            downstream.Headers.ETag = entityTag.ToString();
        }

        if (upstream.Content.Headers.LastModified is { } lastModified)
        {
            downstream.Headers.LastModified =
                lastModified.ToString("R", CultureInfo.InvariantCulture);
        }
    }

    private Uri RegisterResourceUri(
        Uri upstreamUri,
        HlsProxyResourceKind kind)
    {
        if (!PublicHttpEndpointPolicy.IsSafePublicHttpUri(upstreamUri))
        {
            throw new InvalidDataException(
                "The HLS playlist contained a non-public media endpoint.");
        }

        var key = upstreamUri.AbsoluteUri;
        string resourceId;
        lock (_resourceGate)
        {
            if (_resourceIds.TryGetValue(key, out resourceId!))
            {
                if (kind is HlsProxyResourceKind.Playlist &&
                    _resources[resourceId].Kind is not
                        HlsProxyResourceKind.Playlist)
                {
                    _resources[resourceId] =
                        _resources[resourceId] with { Kind = kind };
                }
            }
            else
            {
                if (_resources.Count >= MaximumMappedResources)
                {
                    throw new InvalidDataException(
                        "The HLS playlist referenced too many resources.");
                }

                resourceId = Interlocked.Increment(ref _nextResourceId)
                    .ToString(CultureInfo.InvariantCulture);
                _resources.Add(
                    resourceId,
                    new ProxyResource(upstreamUri, kind));
                _resourceIds.Add(key, resourceId);
            }
        }

        return BuildLoopbackUri(resourceId);
    }

    private string RegisterResource(
        Uri upstreamUri,
        HlsProxyResourceKind kind) =>
        RegisterResourceUri(upstreamUri, kind).AbsoluteUri;

    private Uri BuildLoopbackUri(string resourceId)
    {
        var loopbackBaseUri = _loopbackBaseUri ??
            throw new InvalidOperationException(
                "The HLS loopback proxy has not started.");
        return new Uri(
            loopbackBaseUri,
            $"{_accessToken}/{resourceId}");
    }

    private bool TryGetResource(
        string resourceId,
        out ProxyResource resource)
    {
        lock (_resourceGate)
        {
            return _resources.TryGetValue(resourceId, out resource!);
        }
    }

    private static SocketsHttpHandler CreateUpstreamHandler() =>
        new()
        {
            AllowAutoRedirect = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectCallback = PublicHttpEndpointPolicy.ConnectAsync,
            ConnectTimeout = TimeSpan.FromSeconds(15),
            MaxConnectionsPerServer = 8,
            MaxResponseHeadersLength = 64,
            PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            UseCookies = false,
            UseProxy = false
        };

    private static bool IsHttpOrigin(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0 &&
        uri.AbsolutePath == "/" &&
        uri.Query.Length == 0 &&
        uri.Fragment.Length == 0;

    private static bool IsHlsPlaylist(
        HttpResponseMessage response,
        Uri uri)
    {
        if (uri.AbsolutePath.EndsWith(
                ".m3u8",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var mediaType = response.Content.Headers.ContentType?.MediaType;
        return mediaType is not null &&
               (mediaType.Equals(
                    "application/vnd.apple.mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals(
                    "application/x-mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals(
                    "audio/mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
                mediaType.Equals(
                    "audio/x-mpegurl",
                    StringComparison.OrdinalIgnoreCase));
    }

    private static bool IsHlsDocument(string value)
    {
        var content = value.AsSpan();
        if (!content.IsEmpty && content[0] == '\uFEFF')
        {
            content = content[1..];
        }

        return content.TrimStart().StartsWith(
            "#EXTM3U",
            StringComparison.Ordinal);
    }

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static async Task<byte[]> ReadBoundedBytesAsync(
        HttpContent content,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                "The HLS playlist exceeded the response limit.");
        }

        await using var input =
            await content.ReadAsStreamAsync(cancellationToken);
        var contentLength = content.Headers.ContentLength;
        var expected = contentLength >= 0 &&
                       contentLength <= maximumBytes
            ? checked((int)contentLength.Value)
            : 0;
        using var output = new MemoryStream(expected);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, 16 * 1024),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException(
                        "The HLS playlist exceeded the response limit.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return output.ToArray();
    }

    private static void LogStarted(Uri upstreamUri)
    {
        try
        {
            ApplicationDiagnosticLog.Information(
                "playback.hls-proxy-started",
                $"upstream={SanitizeUri(upstreamUri)}");
        }
        catch
        {
            // Diagnostics cannot prevent playback.
        }
    }

    private static void LogFailure(
        Uri upstreamUri,
        Exception exception)
    {
        try
        {
            ApplicationDiagnosticLog.Warning(
                "playback.hls-proxy-failed",
                $"upstream={SanitizeUri(upstreamUri)}; " +
                $"error={exception.GetType().Name}");
        }
        catch
        {
            // Diagnostics cannot prevent playback.
        }
    }

    private static string SanitizeUri(Uri uri) =>
        $"{uri.Scheme}://{uri.Host}{uri.AbsolutePath}";

    private sealed record ProxyResource(
        Uri UpstreamUri,
        HlsProxyResourceKind Kind);

    private sealed record UpstreamResponse(
        HttpResponseMessage Response,
        Uri FinalUri) : IDisposable
    {
        public void Dispose() => Response.Dispose();
    }
}

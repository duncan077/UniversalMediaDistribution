using System.Buffers;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;

namespace UniversalMediaDistribution.UI.Playback;

/// <summary>
/// Turns a playlist observed by an Nupload web fallback into a native media
/// request, but only after the same URL succeeds with the headers the native
/// path can preserve.
///
/// This is intentionally not a generic "first m3u8 wins" mechanism. Embedded
/// players routinely request advertisement playlists. Restricting both the
/// document and media hosts keeps an advertisement from replacing the movie
/// while still covering Nupload regardless of which API provider supplied it.
/// </summary>
internal sealed class EmbeddedWebMediaHandoffResolver : IDisposable
{
    private const int MaximumPlaylistProbeBytes = 4 * 1024;
    private const int MaximumRedirects = 5;
    private static readonly TimeSpan ProbeTimeout = TimeSpan.FromSeconds(8);
    private readonly HttpClient _httpClient;
    private bool _disposed;

    internal EmbeddedWebMediaHandoffResolver(
        HttpMessageHandler? messageHandler = null)
    {
        messageHandler ??= CreateDefaultHandler();
        _httpClient = new HttpClient(messageHandler, disposeHandler: true)
        {
            Timeout = Timeout.InfiniteTimeSpan,
            DefaultRequestVersion = HttpVersion.Version11,
            DefaultVersionPolicy = HttpVersionPolicy.RequestVersionOrLower
        };
    }

    internal async ValueTask<PlatformMediaRequest?> TryResolveAsync(
        Uri documentUri,
        string title,
        EmbeddedWebMediaResponse observation,
        CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(documentUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentNullException.ThrowIfNull(observation);

        if (!string.Equals(
                documentUri.AbsoluteUri,
                observation.DocumentUri.AbsoluteUri,
                StringComparison.Ordinal) ||
            !IsNuploadWatchPage(documentUri) ||
            !IsIbraMediaHost(observation.RequestUri) ||
            !IsSafePublicHttpUri(observation.RequestUri) ||
            !IsPotentialNuploadMediaRequest(
                observation.RequestUri,
                observation.ContentType))
        {
            return null;
        }

        if (observation.StatusCode is
            (int)HttpStatusCode.NotFound or
            (int)HttpStatusCode.Gone)
        {
            // A failed media request is not proof that the browser player is
            // unusable. It may retry with cookies, refresh its signed URL, or
            // select another rendition. Handoff is passive: only a confirmed
            // playlist may replace the WebView.
            return null;
        }

        if (observation.StatusCode is { } statusCode &&
            statusCode is < 200 or >= 300)
        {
            return null;
        }

        var origin = ReadOrigin(observation.Origin, documentUri);
        var referrer = ReadReferrer(observation.Referrer, documentUri);
        var userAgent = ReadUserAgent(observation.UserAgent);
        using var timeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(ProbeTimeout);

        var playlist = await ProbePlaylistAsync(
                observation.RequestUri,
                origin,
                referrer,
                userAgent,
                timeout.Token)
            .ConfigureAwait(false);
        if (playlist is null)
        {
            return null;
        }

        return new PlatformMediaRequest(
            new PlatformMediaVariant(
                new PlatformMediaSource(
                    playlist.Uri,
                    openReadAsync: null,
                    referrer,
                    userAgent,
                    httpOrigin: origin),
                "Nupload",
                IsAudio: false,
                playlist.ContentType),
            title,
            StartPositionSeconds: 0,
            Subtitles: [],
            AlternateAudioVariants: []);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _httpClient.Dispose();
    }

    internal static bool IsNuploadWatchPage(Uri uri) =>
        IsSafePublicHttpUri(uri) &&
        (HostMatchesDomain(uri, "nupload.top") ||
         HostMatchesDomain(uri, "nupload.me")) &&
        uri.AbsolutePath.StartsWith(
            "/watch/",
            StringComparison.OrdinalIgnoreCase);

    internal static bool LooksLikeHls(Uri uri, string? contentType)
    {
        if (uri.AbsolutePath.EndsWith(
                ".m3u8",
                StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        var separator = contentType?.IndexOf(';') ?? -1;
        var mediaType = separator >= 0
            ? contentType![..separator]
            : contentType;
        return mediaType?.Trim() is { } value &&
               (value.Equals(
                    "application/vnd.apple.mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "application/x-mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
                value.Equals(
                    "audio/mpegurl",
                    StringComparison.OrdinalIgnoreCase) ||
               value.Equals(
                    "audio/x-mpegurl",
                    StringComparison.OrdinalIgnoreCase));
    }

    internal static bool IsPotentialNuploadMediaRequest(
        Uri uri,
        string? contentType) =>
        LooksLikeHls(uri, contentType) ||
        uri.AbsolutePath == "/" &&
        (uri.Query.StartsWith(
             "?s=",
             StringComparison.Ordinal) ||
         uri.Query.Contains(
             "&s=",
             StringComparison.Ordinal));

    internal static bool IsSafePublicHttpUri(Uri uri)
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

    private async Task<ResolvedPlaylist?> ProbePlaylistAsync(
        Uri endpoint,
        Uri origin,
        Uri referrer,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        var currentUri = endpoint;
        for (var redirectCount = 0;
             redirectCount <= MaximumRedirects;
             redirectCount++)
        {
            if (!IsSafePublicHttpUri(currentUri) ||
                !IsIbraMediaHost(currentUri))
            {
                return null;
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                currentUri);
            request.Headers.Accept.ParseAdd("*/*");
            request.Headers.Referrer = referrer;
            request.Headers.TryAddWithoutValidation(
                "Origin",
                origin.GetLeftPart(UriPartial.Authority));
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
            if (userAgent is not null)
            {
                request.Headers.TryAddWithoutValidation(
                    "User-Agent",
                    userAgent);
            }

            using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken)
                .ConfigureAwait(false);
            var responseUri =
                response.RequestMessage?.RequestUri ?? currentUri;
            if (!IsSafePublicHttpUri(responseUri) ||
                !IsIbraMediaHost(responseUri))
            {
                return null;
            }

            if (IsRedirect(response.StatusCode))
            {
                if (redirectCount == MaximumRedirects ||
                    response.Headers.Location is null)
                {
                    return null;
                }

                currentUri = response.Headers.Location.IsAbsoluteUri
                    ? response.Headers.Location
                    : new Uri(responseUri, response.Headers.Location);
                continue;
            }

            if (response.StatusCode is
                HttpStatusCode.NotFound or
                HttpStatusCode.Gone)
            {
                // The probe deliberately carries no browser cookies. Treating
                // its failure as the WebView's failure closed pages that could
                // still recover or play in their own session.
                return null;
            }

            if (!response.IsSuccessStatusCode ||
                !LooksLikeHls(
                    responseUri,
                    response.Content.Headers.ContentType?.MediaType) ||
                !await StartsWithHlsHeaderAsync(
                        response.Content,
                        cancellationToken)
                    .ConfigureAwait(false))
            {
                return null;
            }

            return new ResolvedPlaylist(
                responseUri,
                NormalizeContentType(
                    response.Content.Headers.ContentType));
        }

        return null;
    }

    private static async ValueTask<bool> StartsWithHlsHeaderAsync(
        HttpContent content,
        CancellationToken cancellationToken)
    {
        await using var stream =
            await content.ReadAsStreamAsync(cancellationToken)
                .ConfigureAwait(false);
        var buffer = ArrayPool<byte>.Shared.Rent(
            MaximumPlaylistProbeBytes);
        try
        {
            var total = 0;
            while (total < MaximumPlaylistProbeBytes)
            {
                var read = await stream.ReadAsync(
                        buffer.AsMemory(
                            total,
                            MaximumPlaylistProbeBytes - total),
                        cancellationToken)
                    .ConfigureAwait(false);
                if (read == 0)
                {
                    break;
                }

                total += read;
                if (total >= 7)
                {
                    var current = Encoding.UTF8
                        .GetString(buffer, 0, total)
                        .AsSpan();
                    if (!current.IsEmpty &&
                        current[0] == '\uFEFF')
                    {
                        current = current[1..];
                    }

                    current = current.TrimStart();
                    if (current.StartsWith(
                            "#EXTM3U",
                            StringComparison.Ordinal))
                    {
                        return true;
                    }

                    // Once a non-whitespace prefix is long enough to differ
                    // from the HLS signature, reading more cannot change it.
                    if (current.Length >= 7)
                    {
                        return false;
                    }
                }
            }

            return false;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static Uri ReadOrigin(string? value, Uri documentUri)
    {
        var fallback = new Uri(
            documentUri.GetLeftPart(UriPartial.Authority) + "/");
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !IsSafePublicHttpUri(candidate) ||
            candidate.AbsolutePath != "/" ||
            candidate.Query.Length > 0 ||
            candidate.Fragment.Length > 0 ||
            !SameAuthority(candidate, documentUri))
        {
            return fallback;
        }

        return candidate;
    }

    private static Uri ReadReferrer(string? value, Uri documentUri)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            !IsSafePublicHttpUri(candidate) ||
            !SameAuthority(candidate, documentUri))
        {
            return documentUri;
        }

        return candidate;
    }

    private static string? ReadUserAgent(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        return trimmed.Length <= 512 &&
               !trimmed.Contains('\r') &&
               !trimmed.Contains('\n')
            ? trimmed
            : null;
    }

    private static string NormalizeContentType(
        MediaTypeHeaderValue? contentType) =>
        contentType?.MediaType is { Length: > 0 } mediaType
            ? mediaType
            : "application/vnd.apple.mpegurl";

    private static bool SameAuthority(Uri left, Uri right) =>
        string.Equals(
            left.GetLeftPart(UriPartial.Authority),
            right.GetLeftPart(UriPartial.Authority),
            StringComparison.OrdinalIgnoreCase);

    private static bool IsIbraMediaHost(Uri uri) =>
        HostMatchesDomain(uri, "ibra.lat");

    private static bool HostMatchesDomain(Uri uri, string domain) =>
        string.Equals(
            uri.IdnHost,
            domain,
            StringComparison.OrdinalIgnoreCase) ||
        uri.IdnHost.EndsWith(
            "." + domain,
            StringComparison.OrdinalIgnoreCase);

    private static bool IsRedirect(HttpStatusCode statusCode) =>
        statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static bool IsPublicIpv4Address(ReadOnlySpan<byte> address)
    {
        var first = address[0];
        var second = address[1];
        return
            first != 0 &&
            first != 10 &&
            first != 127 &&
            !(first == 100 && second is >= 64 and <= 127) &&
            !(first == 169 && second == 254) &&
            !(first == 172 && second is >= 16 and <= 31) &&
            !(first == 192 && second == 168) &&
            !(first == 192 && second == 0 && address[2] is 0 or 2) &&
            !(first == 192 && second == 88 && address[2] == 99) &&
            !(first == 198 && second is 18 or 19) &&
            !(first == 198 && second == 51 && address[2] == 100) &&
            !(first == 203 && second == 0 && address[2] == 113) &&
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
        (bytes[0] & 0xfe) != 0xfc;

    private static HttpMessageHandler CreateDefaultHandler() =>
        new HttpClientHandler
        {
            AllowAutoRedirect = false,
            AutomaticDecompression =
                DecompressionMethods.GZip |
                DecompressionMethods.Deflate |
                DecompressionMethods.Brotli,
            UseCookies = false,
            UseProxy = false
        };

    private sealed record ResolvedPlaylist(
        Uri Uri,
        string ContentType);
}

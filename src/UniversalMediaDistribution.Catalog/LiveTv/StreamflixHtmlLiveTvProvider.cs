using System.Collections.Concurrent;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Catalog.LiveTv;

/// <summary>
/// HTML live-TV provider adapted from the Apache-2.0 Streamflix providers.
/// Channel pages are kept lazy: the catalog loads logos and names first, and
/// the selected channel's iframe chain is resolved only when playback starts.
/// </summary>
public sealed class StreamflixHtmlLiveTvProvider(
    HttpClient httpClient,
    LiveTvProviderDescriptor descriptor,
    VideoHosterResolver? hosterResolver = null,
    TimeProvider? timeProvider = null) : ILiveTvProvider
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumChannels = 2_000;
    private const int MaximumHttpRedirects = 5;
    private const int MaximumPlaybackHops = 6;
    private const int MaximumPlaybackVariants = 16;
    private static readonly TimeSpan ChannelCacheLifetime =
        TimeSpan.FromHours(2);
    private static readonly Regex AnchorRegex = new(
        @"<a\b(?<attributes>[^>]*)>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex ImageRegex = new(
        @"<img\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MediaRegex = new(
        """
        (?:
            ["'](?<value>https?://[^"']+\.(?:m3u8|mp4)(?:\?[^"']*)?)["'] |
            (?:source|file|src)\s*:\s*["'](?<value>https?://[^"']+)["'] |
            var\s+src\s*=\s*["'](?<value>https?://[^"']+)["']
        )
        """,
        RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex IframeRegex = new(
        """
        <iframe\b(?<attributes>[^>]*)>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex AtobRegex = new(
        """
        atob\(\s*["'](?<value>[A-Za-z0-9+/=_-]{8,})["']\s*\)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ServerAnchorRegex = new(
        """
        <a\b(?<attributes>[^>]*)>(?<body>.*?)</a>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex ServerButtonRegex = new(
        """
        <button\b(?<attributes>[^>]*)>(?<body>.*?)</button>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex[] ServerLinkRegexes =
    [
        ServerAnchorRegex,
        ServerButtonRegex
    ];
    private static readonly string[] RejectedArtworkTerms =
    [
        "paypal", "pago", "donar", "pay.png", "cafecito", "qr",
        "mercado", "donate", "telegram", "whatsapp", "facebook",
        "twitter", "instagram", "share", "ads", "banner", "pixel",
        "button", "btn", "favicon"
    ];

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    private readonly VideoHosterResolver? _hosterResolver = hosterResolver;
    private readonly TimeProvider _timeProvider =
        timeProvider ?? TimeProvider.System;
    private readonly SemaphoreSlim _cacheGate = new(1, 1);
    private CachedChannels? _cache;

    public LiveTvProviderDescriptor Descriptor { get; } =
        descriptor ?? throw new ArgumentNullException(nameof(descriptor));

    public async Task<IReadOnlyList<LiveTvChannel>> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        var now = _timeProvider.GetUtcNow();
        if (_cache is { } cached && cached.ExpiresAt > now)
        {
            return cached.Channels;
        }

        await _cacheGate.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_cache is { } current && current.ExpiresAt > now)
            {
                return current.Channels;
            }

            var page = await ReadHtmlAsync(
                Descriptor.PlaylistUri,
                Descriptor.PlaylistUri,
                cancellationToken);
            var channels = ParseChannels(page.Content, page.FinalUri);
            if (channels.Count == 0 &&
                TryReadChannelCatalogUri(
                    page.Content,
                    page.FinalUri,
                    out var catalogUri))
            {
                page = await ReadHtmlAsync(
                    catalogUri,
                    page.FinalUri,
                    cancellationToken);
                channels = ParseChannels(page.Content, page.FinalUri);
            }

            if (channels.Count == 0)
            {
                throw new InvalidDataException(
                    $"{Descriptor.DisplayName} did not return any channels.");
            }

            _cache = new CachedChannels(
                channels,
                now + ChannelCacheLifetime);
            return channels;
        }
        finally
        {
            _cacheGate.Release();
        }
    }

    public async ValueTask<LiveTvPlaybackSource> ResolvePlaybackAsync(
        LiveTvChannel channel,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(channel);
        var channelPage = await ReadHtmlAsync(
            channel.StreamUri,
            channel.Referrer ?? Descriptor.PlaylistUri,
            cancellationToken);
        var servers = ReadServerLinks(
            channelPage.Content,
            channelPage.FinalUri);
        if (servers.Count == 0)
        {
            servers =
            [
                new PlaybackLink(
                    "Automatic",
                    channelPage.FinalUri)
            ];
        }

        var variants = new List<LiveTvPlaybackVariant>();
        Exception? lastFailure = null;
        foreach (var server in servers.Take(MaximumPlaybackVariants))
        {
            try
            {
                var resolved = await ResolveCandidateAsync(
                    channel,
                    server,
                    cancellationToken);
                var qualities = await ExpandMasterPlaylistAsync(
                    resolved,
                    cancellationToken);
                foreach (var variant in qualities)
                {
                    if (variants.Count >= MaximumPlaybackVariants)
                    {
                        break;
                    }

                    if (!variants.Any(existing =>
                            existing.StreamUri == variant.StreamUri))
                    {
                        variants.Add(variant);
                    }
                }
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception exception)
            {
                lastFailure = exception;
            }
        }

        if (variants.Count == 0)
        {
            throw new InvalidDataException(
                $"{Descriptor.DisplayName} did not return a playable stream.",
                lastFailure);
        }

        var initial = variants[0];
        return new LiveTvPlaybackSource(
            initial.StreamUri,
            initial.Referrer,
            initial.UserAgent,
            initial.Label,
            variants.Skip(1).ToArray());
    }

    private async ValueTask<LiveTvPlaybackVariant> ResolveCandidateAsync(
        LiveTvChannel channel,
        PlaybackLink server,
        CancellationToken cancellationToken)
    {
        var current = server.Uri;
        var referrer = channel.StreamUri == current
            ? channel.Referrer ?? Descriptor.PlaylistUri
            : channel.StreamUri;
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var depth = 0; depth < MaximumPlaybackHops; depth++)
        {
            if (!visited.Add(current.AbsoluteUri))
            {
                break;
            }

            if (_hosterResolver is not null)
            {
                var resolved = await _hosterResolver.ResolveAsync(
                    [
                        new ApiContentVideoSource(
                            channel.Name,
                            current,
                            referrer: referrer,
                            kind: ApiContentVideoSourceKind.EmbeddedWeb)
                    ],
                    cancellationToken);
                if (resolved.Count == 0)
                {
                    throw new InvalidDataException(
                        "The hoster reported that this media session is unavailable.");
                }

                var source = resolved[0];
                if (source.Kind is ApiContentVideoSourceKind.DirectMedia)
                {
                    return new LiveTvPlaybackVariant(
                        server.Label,
                        source.PlaybackUri,
                        source.Referrer,
                        BrowserUserAgent);
                }
            }

            var page = await ReadHtmlAsync(
                current,
                referrer,
                cancellationToken);
            var media = TryReadMediaUri(page.Content) ??
                TryReadPackedMediaUri(page.Content) ??
                TryReadBase64MediaUri(page.Content);
            if (media is not null)
            {
                return new LiveTvPlaybackVariant(
                    server.Label,
                    media,
                    page.FinalUri,
                    BrowserUserAgent);
            }

            var next = ReadServerLinks(page.Content, page.FinalUri)
                    .Select(static link => link.Uri)
                    .FirstOrDefault() ??
                TryReadIframe(page.Content, page.FinalUri);
            if (next is null || !IsSafePublicHttpUri(next))
            {
                break;
            }

            referrer = page.FinalUri;
            current = next;
        }

        throw new InvalidDataException(
            $"{Descriptor.DisplayName} did not return a playable stream.");
    }

    internal IReadOnlyList<LiveTvChannel> ParseChannels(
        string html,
        Uri pageUri)
    {
        var channels = new List<LiveTvChannel>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            if (channels.Count >= MaximumChannels)
            {
                break;
            }

            var attributes = anchor.Groups["attributes"].Value;
            var href = ReadAttribute(attributes, "href");
            if (!TryResolveUri(pageUri, href, out var channelUri) ||
                !IsChannelUri(channelUri, pageUri))
            {
                continue;
            }

            var body = anchor.Groups["body"].Value;
            var imageMatch = ImageRegex.Match(body);
            var imageAttributes = imageMatch.Success
                ? imageMatch.Groups["attributes"].Value
                : string.Empty;
            var title = CleanText(body);
            if (string.IsNullOrWhiteSpace(title))
            {
                title = CleanText(
                    ReadAttribute(imageAttributes, "alt") ??
                    ReadAttribute(attributes, "title"));
            }

            if (string.IsNullOrWhiteSpace(title) ||
                title.Contains("Telegram", StringComparison.OrdinalIgnoreCase) ||
                title.Contains("Soporte", StringComparison.OrdinalIgnoreCase) ||
                IsChannelCatalogNavigation(title) ||
                !seen.Add(channelUri.AbsoluteUri))
            {
                continue;
            }

            var artworkText =
                ReadAttribute(imageAttributes, "data-src") ??
                ReadAttribute(imageAttributes, "src");
            Uri? artwork = null;
            if (!RejectedArtworkTerms.Any(term =>
                    artworkText?.Contains(
                        term,
                        StringComparison.OrdinalIgnoreCase) is true) &&
                TryResolveUri(pageUri, artworkText, out var parsedArtwork))
            {
                artwork = parsedArtwork;
            }

            channels.Add(
                new LiveTvChannel(
                    Descriptor.Id,
                    Descriptor.DisplayName,
                    title,
                    channelUri,
                    ChannelId: channelUri.AbsolutePath.Trim('/'),
                    LogoUri: artwork,
                    Group: Classify(title),
                    Referrer: pageUri,
                    UserAgent: BrowserUserAgent));
        }

        return channels;
    }

    private async Task<HtmlPage> ReadHtmlAsync(
        Uri endpoint,
        Uri referrer,
        CancellationToken cancellationToken)
    {
        var rememberedEndpoint = ProviderBaseUrlRedirects.Apply(
            endpoint.AbsoluteUri);
        var currentEndpoint = Uri.TryCreate(
                rememberedEndpoint,
                UriKind.Absolute,
                out var rememberedUri)
            ? rememberedUri
            : endpoint;
        var currentReferrer = referrer;
        for (var redirectCount = 0;
             redirectCount <= MaximumHttpRedirects;
             redirectCount++)
        {
            if (!IsSafePublicHttpUri(currentEndpoint))
            {
                throw new InvalidDataException(
                    $"{Descriptor.DisplayName} returned an unsafe URL.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                currentEndpoint);
            request.Headers.UserAgent.ParseAdd(BrowserUserAgent);
            request.Headers.Referrer = currentReferrer;
            request.Headers.Accept.ParseAdd(
                "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7");
            using var response = await ApiProviderHttp.SendAsync(
                _httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseUri =
                response.RequestMessage?.RequestUri ?? currentEndpoint;
            if (!IsSafePublicHttpUri(responseUri))
            {
                throw new InvalidDataException(
                    $"{Descriptor.DisplayName} returned an unsafe URL.");
            }

            if (IsRedirectStatusCode(response.StatusCode))
            {
                if (redirectCount == MaximumHttpRedirects ||
                    response.Headers.Location is null)
                {
                    throw new InvalidDataException(
                        $"{Descriptor.DisplayName} returned an invalid or " +
                        "excessive HTTP redirect chain.");
                }

                var redirectedEndpoint =
                    response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(responseUri, response.Headers.Location);
                if (!IsSafePublicHttpUri(redirectedEndpoint))
                {
                    throw new InvalidDataException(
                        $"{Descriptor.DisplayName} redirected to an unsafe " +
                        "URL.");
                }

                currentEndpoint = redirectedEndpoint;
                currentReferrer = responseUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            await using var input =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidDataException(
                        $"{Descriptor.DisplayName} response was too large.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }

            return new HtmlPage(
                Encoding.UTF8.GetString(
                    output.GetBuffer(),
                    0,
                    checked((int)output.Length)),
                responseUri);
        }

        throw new InvalidDataException(
            $"{Descriptor.DisplayName} returned an excessive HTTP redirect " +
            "chain.");
    }

    private async ValueTask<IReadOnlyList<LiveTvPlaybackVariant>>
        ExpandMasterPlaylistAsync(
            LiveTvPlaybackVariant source,
            CancellationToken cancellationToken)
    {
        if (!source.StreamUri.AbsolutePath.EndsWith(
                ".m3u8",
                StringComparison.OrdinalIgnoreCase))
        {
            return [source];
        }

        try
        {
            var playlist = await ReadPlaylistAsync(
                source.StreamUri,
                source.Referrer,
                source.UserAgent,
                cancellationToken);
            var variants = ParseMasterPlaylist(playlist, source);
            return variants.Count == 0 ? [source] : variants;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            // A media playlist is already playable. Quality discovery is an
            // enhancement and must never turn it into a playback failure.
            return [source];
        }
    }

    private async Task<string> ReadPlaylistAsync(
        Uri endpoint,
        Uri? referrer,
        string? userAgent,
        CancellationToken cancellationToken)
    {
        if (!IsSafePublicHttpUri(endpoint))
        {
            throw new InvalidDataException(
                $"{Descriptor.DisplayName} returned an unsafe playlist URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.UserAgent.ParseAdd(
            string.IsNullOrWhiteSpace(userAgent)
                ? BrowserUserAgent
                : userAgent);
        request.Headers.Referrer = referrer;
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue(
                "application/vnd.apple.mpegurl"));
        request.Headers.Accept.Add(
            new MediaTypeWithQualityHeaderValue("application/x-mpegURL"));
        using var response = await _httpClient.SendAsync(
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength is > MaximumResponseBytes)
        {
            throw new InvalidDataException(
                $"{Descriptor.DisplayName} playlist was too large.");
        }

        await using var input =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream();
        var buffer = new byte[16 * 1024];
        while (true)
        {
            var read = await input.ReadAsync(buffer, cancellationToken);
            if (read == 0)
            {
                break;
            }

            if (output.Length + read > MaximumResponseBytes)
            {
                throw new InvalidDataException(
                    $"{Descriptor.DisplayName} playlist was too large.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return Encoding.UTF8.GetString(
            output.GetBuffer(),
            0,
            checked((int)output.Length));
    }

    public static IReadOnlyList<LiveTvPlaybackVariant> ParseMasterPlaylist(
        string playlist,
        LiveTvPlaybackVariant source)
    {
        var variants = new List<(LiveTvPlaybackVariant Variant, int Height, long Bandwidth)>();
        string? streamInfo = null;
        using var reader = new StringReader(playlist);
        while (reader.ReadLine() is { } rawLine)
        {
            var line = rawLine.Trim();
            if (line.StartsWith(
                    "#EXT-X-STREAM-INF:",
                    StringComparison.OrdinalIgnoreCase))
            {
                streamInfo = line["#EXT-X-STREAM-INF:".Length..];
                continue;
            }

            if (streamInfo is null || line.Length == 0)
            {
                continue;
            }

            if (line.StartsWith('#'))
            {
                continue;
            }

            if (!TryResolveUri(source.StreamUri, line, out var streamUri))
            {
                streamInfo = null;
                continue;
            }

            var height = ReadNumericAttribute(
                streamInfo,
                @"\bRESOLUTION\s*=\s*\d+x(?<value>\d+)");
            var bandwidth = ReadNumericAttribute(
                streamInfo,
                @"\b(?:AVERAGE-)?BANDWIDTH\s*=\s*(?<value>\d+)");
            var detail = height > 0
                ? $"{height}p"
                : bandwidth > 0
                    ? $"{bandwidth / 1_000_000d:0.#} Mbps"
                    : $"Quality {variants.Count + 1}";
            variants.Add((
                new LiveTvPlaybackVariant(
                    $"{source.Label} · {detail}",
                    streamUri,
                    source.Referrer,
                    source.UserAgent),
                checked((int)Math.Min(height, int.MaxValue)),
                bandwidth));
            streamInfo = null;
        }

        return variants
            .OrderByDescending(static variant => variant.Height)
            .ThenByDescending(static variant => variant.Bandwidth)
            .Select(static variant => variant.Variant)
            .DistinctBy(static variant => variant.StreamUri.AbsoluteUri)
            .Take(MaximumPlaybackVariants)
            .ToArray();
    }

    private static long ReadNumericAttribute(
        string attributes,
        string pattern)
    {
        var match = Regex.Match(
            attributes,
            pattern,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success &&
            long.TryParse(
                match.Groups["value"].Value,
                out var value)
            ? value
            : 0;
    }

    private static Uri? TryReadMediaUri(string text)
    {
        var normalized = NormalizeScript(text);
        foreach (Match match in MediaRegex.Matches(normalized))
        {
            if (Uri.TryCreate(
                    WebUtility.HtmlDecode(
                        match.Groups["value"].Value),
                    UriKind.Absolute,
                    out var media) &&
                IsSafePublicHttpUri(media))
            {
                return media;
            }
        }

        return null;
    }

    private static Uri? TryReadPackedMediaUri(string html) =>
        JavaScriptPacker.TryUnpackFirst(html, out var unpacked)
            ? TryReadMediaUri(unpacked)
            : null;

    private static Uri? TryReadBase64MediaUri(string html)
    {
        foreach (Match match in AtobRegex.Matches(html))
        {
            var candidate = match.Groups["value"].Value;
            for (var layer = 0; layer < 3; layer++)
            {
                try
                {
                    var normalized = candidate
                        .Replace('-', '+')
                        .Replace('_', '/');
                    normalized += new string(
                        '=',
                        (4 - normalized.Length % 4) % 4);
                    candidate = Encoding.UTF8.GetString(
                        Convert.FromBase64String(normalized));
                }
                catch (FormatException)
                {
                    break;
                }

                var media = TryReadMediaUri(candidate);
                if (media is not null)
                {
                    return media;
                }

                if (Uri.TryCreate(
                        candidate.Trim(),
                        UriKind.Absolute,
                        out var direct) &&
                    IsSafePublicHttpUri(direct))
                {
                    return direct;
                }

                var nested = AtobRegex.Match(candidate);
                if (nested.Success)
                {
                    candidate = nested.Groups["value"].Value;
                }
            }
        }

        return null;
    }

    private static IReadOnlyList<PlaybackLink> ReadServerLinks(
        string html,
        Uri pageUri)
    {
        var links = new List<PlaybackLink>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var regex in ServerLinkRegexes)
        {
            foreach (Match match in regex.Matches(html))
            {
                var text = CleanText(match.Groups["body"].Value);
                if (!text.Contains(
                        "Opción",
                        StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains(
                        "Opcion",
                        StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains(
                        "Servidor",
                        StringComparison.OrdinalIgnoreCase) &&
                    !text.Contains(
                        "FHD",
                        StringComparison.OrdinalIgnoreCase))
                {
                    continue;
                }

                var attributes = match.Groups["attributes"].Value;
                var targetValue =
                    ReadAttribute(attributes, "href") ??
                    ReadAttribute(attributes, "data-src") ??
                    ReadAttribute(attributes, "data-url");
                if (TryResolveUri(
                        pageUri,
                        targetValue,
                        out var target) &&
                    seen.Add(target.AbsoluteUri))
                {
                    links.Add(new PlaybackLink(
                        string.IsNullOrWhiteSpace(text)
                            ? $"Server {links.Count + 1}"
                            : text,
                        target));
                }
            }
        }

        return links;
    }

    private static bool TryReadChannelCatalogUri(
        string html,
        Uri pageUri,
        out Uri catalogUri)
    {
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var text = CleanText(anchor.Groups["body"].Value);
            if (!IsChannelCatalogNavigation(text) ||
                !TryResolveUri(
                    pageUri,
                    ReadAttribute(
                        anchor.Groups["attributes"].Value,
                        "href"),
                    out var candidate) ||
                candidate == pageUri ||
                !SameOrigin(candidate, pageUri))
            {
                continue;
            }

            catalogUri = candidate;
            return true;
        }

        catalogUri = null!;
        return false;
    }

    private static bool IsChannelCatalogNavigation(string text) =>
        text.Contains("parrilla", StringComparison.OrdinalIgnoreCase) ||
        (text.Contains("ver", StringComparison.OrdinalIgnoreCase) &&
         text.Contains("canales", StringComparison.OrdinalIgnoreCase));

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(
            left.Scheme,
            right.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.IdnHost,
            right.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static Uri? TryReadIframe(string html, Uri pageUri)
    {
        foreach (Match match in IframeRegex.Matches(html))
        {
            var attributes = match.Groups["attributes"].Value;
            var source =
                ReadAttribute(attributes, "src") ??
                ReadAttribute(attributes, "data-src");
            if (TryResolveUri(pageUri, source, out var target))
            {
                return target;
            }
        }

        return null;
    }

    private static bool IsChannelUri(Uri uri, Uri root)
    {
        if (!string.Equals(
                uri.IdnHost,
                root.IdnHost,
                StringComparison.OrdinalIgnoreCase) ||
            uri.AbsolutePath.Trim('/').Length == 0)
        {
            return false;
        }

        var value = uri.AbsoluteUri;
        return !value.Contains("/category/", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("/tag/", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("paypal.com", StringComparison.OrdinalIgnoreCase) &&
               !value.Contains("linktre.online", StringComparison.OrdinalIgnoreCase);
    }

    private static string? ReadAttribute(
        string attributes,
        string name)
    {
        var match = Regex.Match(
            attributes,
            $"""
             \b{Regex.Escape(name)}\s*=\s*
             (?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s>]+))
             """,
            RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
                RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value.Trim())
            : null;
    }

    private static bool TryResolveUri(
        Uri baseUri,
        string? value,
        out Uri uri)
    {
        if (!string.IsNullOrWhiteSpace(value) &&
            !value.StartsWith('#') &&
            !value.StartsWith("javascript:", StringComparison.OrdinalIgnoreCase) &&
            Uri.TryCreate(baseUri, value.Trim(), out var candidate) &&
            IsSafePublicHttpUri(candidate))
        {
            uri = candidate;
            return true;
        }

        uri = null!;
        return false;
    }

    private static bool IsSafePublicHttpUri(Uri uri) =>
        HosterExtractorBase.IsSafePublicHttpUri(uri);

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static string CleanText(string? value) =>
        WhitespaceRegex.Replace(
                WebUtility.HtmlDecode(TagRegex.Replace(value ?? string.Empty, " ")),
                " ")
            .Trim();

    private static string NormalizeScript(string value) =>
        value.Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("\\u0026", "&", StringComparison.OrdinalIgnoreCase)
            .Replace("&amp;", "&", StringComparison.OrdinalIgnoreCase);

    private static string Classify(string title)
    {
        if (ContainsAny(
                title,
                "sport", "deporte", "espn", "fox", "tyc", "direct",
                "futbol"))
        {
            return "Deportes";
        }

        if (ContainsAny(title, "news", "noticia", "cnn", "24h"))
        {
            return "Noticias";
        }

        if (ContainsAny(
                title,
                "hbo", "max", "cine", "warner", "star", "tnt",
                "film", "movie"))
        {
            return "Cine y series";
        }

        return "General";
    }

    private static bool ContainsAny(
        string text,
        params string[] values) =>
        values.Any(value =>
            text.Contains(value, StringComparison.OrdinalIgnoreCase));

    private const string BrowserUserAgent =
        "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
        "AppleWebKit/537.36 (KHTML, like Gecko) " +
        "Chrome/118.0.0.0 Safari/537.36";

    private sealed record CachedChannels(
        IReadOnlyList<LiveTvChannel> Channels,
        DateTimeOffset ExpiresAt);

    private sealed record HtmlPage(string Content, Uri FinalUri);

    private sealed record PlaybackLink(string Label, Uri Uri);
}

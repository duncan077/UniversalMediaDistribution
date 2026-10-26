using System.Net.Http.Headers;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Shared Zilla route handling; an accessible manifest alone is not playable media.</summary>
public sealed class ZillaHosterExtractor : HosterExtractorBase
{
    public override string Name => "Zilla";
    public override bool Matches(Uri uri) => HostMatchesDomain(uri, "player.zilla-networks.com");

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source, HttpClient httpClient, CancellationToken cancellationToken)
    {
        if (!Matches(source.PlaybackUri) ||
            !TryBuildHlsUri(source.PlaybackUri, out var playlist)) return null;
        if (!await AreSegmentsReachableAsync(httpClient, playlist, source.PlaybackUri, cancellationToken)) return null;
        return new ApiContentVideoSource(source.Label, playlist, "application/vnd.apple.mpegurl",
            source.PlaybackUri, ApiContentVideoSourceKind.DirectMedia,
            httpOrigin: HosterSourceReader.GetOrigin(source.PlaybackUri));
    }

    internal static bool TryBuildHlsUri(
        Uri playbackUri,
        out Uri hlsUri)
    {
        hlsUri = null!;
        if (!string.Equals(
                playbackUri.IdnHost,
                "player.zilla-networks.com",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(playbackUri);
        if (segments.Length != 2 ||
            !string.Equals(
                segments[0],
                "play",
                StringComparison.OrdinalIgnoreCase) ||
            !TmoProviderUri.TryValidateSegment(
                segments[1],
                128,
                static character =>
                    char.IsAsciiLetterOrDigit(character) ||
                    character is '-' or '_',
                out var identifier))
        {
            return false;
        }

        hlsUri = new Uri(
            TmoProviderUri.GetOrigin(playbackUri),
            $"m3u8/{Uri.EscapeDataString(identifier)}");
        return true;
    }

    internal static async ValueTask<bool> AreSegmentsReachableAsync(
        HttpClient client, Uri playlist, Uri referrer, CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            var visited = new HashSet<Uri>();
            for (var hop = 0; hop < 4 && visited.Add(playlist); hop++)
            {
                var page = await FetchTextAsync(client, playlist, referrer, HosterBrowserHeaders.UserAgent,
                    "application/vnd.apple.mpegurl,*/*;q=0.5", TimeSpan.FromSeconds(15), timeout.Token, 2 * 1024 * 1024);
                if (!TryReadTarget(page.Content, out var target, out var childPlaylist) ||
                    !Uri.TryCreate(page.FinalUri, target, out var endpoint) || !IsSafePublicHttpUri(endpoint)) return false;
                if (childPlaylist) { playlist = endpoint; continue; }
                // Never accept an HTTP redirect as proof of playable bytes. Keep
                // the web fallback; the caller's handler must disable redirects.
                using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
                request.Headers.Range = new RangeHeaderValue(0, 0);
                request.Headers.Referrer = referrer;
                request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
                using var response = await ApiProviderHttp.SendAsync(client, request,
                    HttpCompletionOption.ResponseHeadersRead, timeout.Token);
                if (!response.IsSuccessStatusCode ||
                    response.Content.Headers.ContentType?.MediaType is "text/html" or "application/json") return false;
                await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
                return await stream.ReadAsync(new byte[1], timeout.Token) == 1;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested) { }
        catch (HttpRequestException) { }
        catch (InvalidDataException) { }
        return false;
    }

    private static bool TryReadTarget(string text, out string target, out bool childPlaylist)
    {
        target = string.Empty;
        childPlaylist = false;
        var content = text.AsSpan().Trim();
        if (!content.StartsWith("#EXTM3U", StringComparison.Ordinal)) return false;
        foreach (var range in content.Split('\n'))
        {
            var line = content[range].Trim();
            if (line.StartsWith("#EXT-X-STREAM-INF:", StringComparison.Ordinal)) childPlaylist = true;
            if (line.StartsWith("#EXT-X-MAP:", StringComparison.Ordinal))
            {
                var start = line.IndexOf("URI=\"", StringComparison.Ordinal);
                if (start < 0) continue;
                var value = line[(start + 5)..];
                var end = value.IndexOf('"');
                if (end > 0) { target = value[..end].ToString(); return true; }
            }
            if (!line.IsEmpty && line[0] != '#') { target = line.ToString(); return true; }
        }
        return false;
    }
}

/// <summary>Only explicit HTML video/source tags; arbitrary ad URLs are not player sources.</summary>
public sealed class Html5HosterExtractor : HosterExtractorBase
{
    private static readonly Regex VideoTag = new(
        """<(?:video|source)\b[^>]*\bsrc\s*=\s*(?<quote>["'])(?<value>[^"'<>]+)\k<quote>[^>]*>""",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public override string Name => "HTML5";
    public override bool Matches(Uri uri) => false;
    public override bool MatchesContent(string content) => VideoTag.IsMatch(content);

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var page = await FetchTextAsync(httpClient, source.PlaybackUri, source.Referrer,
            HosterBrowserHeaders.UserAgent, "text/html", TimeSpan.FromSeconds(15), cancellationToken, DefaultMaximumResponseBytes);
        foreach (Match match in VideoTag.Matches(page.Content))
        {
            var value = System.Net.WebUtility.HtmlDecode(match.Groups["value"].Value);
            if (Uri.TryCreate(page.FinalUri, value, out var uri) && IsSafePublicHttpUri(uri) &&
                HosterMimeTypeResolver.MimeTypeFromUrl(uri) is not null)
                return HosterSourceReader.CreateDirectSource(source, uri, page.FinalUri);
        }
        return null;
    }
}

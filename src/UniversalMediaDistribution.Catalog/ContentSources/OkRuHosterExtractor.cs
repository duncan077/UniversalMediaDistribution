using System.Net;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Reads public OK.ru player metadata without executing scripts or following login flows.</summary>
public sealed class OkRuHosterExtractor : HosterExtractorBase
{
    private static readonly Regex Options = new(
        """\bdata-options\s*=\s*(?<quote>["'])(?<value>.*?)\k<quote>""",
        RegexOptions.Singleline | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    public override string Name => "OK.ru";
    public override bool Matches(Uri uri) =>
        (HostMatchesDomain(uri, "ok.ru") || HostMatchesDomain(uri, "odnoklassniki.ru")) &&
        (uri.AbsolutePath.StartsWith("/video/", StringComparison.Ordinal) ||
         uri.AbsolutePath.StartsWith("/videoembed/", StringComparison.Ordinal));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var page = await FetchTextAsync(httpClient, source.PlaybackUri, source.Referrer,
            HosterBrowserHeaders.UserAgent, "text/html", TimeSpan.FromSeconds(20), cancellationToken, 2 * 1024 * 1024);
        foreach (Match match in Options.Matches(page.Content))
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                using var options = JsonDocument.Parse(WebUtility.HtmlDecode(match.Groups["value"].Value));
                if (!options.RootElement.TryGetProperty("flashvars", out var flashvars) ||
                    flashvars.ValueKind != JsonValueKind.Object ||
                    !flashvars.TryGetProperty("metadata", out var metadata)) continue;
                using var document = JsonDocument.Parse(metadata.ValueKind == JsonValueKind.String
                    ? metadata.GetString()! : metadata.GetRawText());
                var root = document.RootElement;
                if (root.ValueKind != JsonValueKind.Object) continue;
                foreach (var key in new[] { "hlsManifestUrl", "ondemandHls", "hlsMasterPlaylistUrl" })
                {
                    if (ReadUri(root, key) is { } hls)
                        return new ApiContentVideoSource(source.Label, hls, "application/vnd.apple.mpegurl",
                            page.FinalUri, ApiContentVideoSourceKind.DirectMedia);
                }
                if (!root.TryGetProperty("videos", out var videos) || videos.ValueKind != JsonValueKind.Array) continue;
                var bestRank = -1;
                Uri? best = null;
                foreach (var video in videos.EnumerateArray())
                {
                    if (ReadUri(video, "url") is not { } media) continue;
                    var name = video.TryGetProperty("name", out var n) && n.ValueKind == JsonValueKind.String ? n.GetString() : null;
                    var rank = name switch { "ultra" => 6, "quad" => 5, "full" => 4, "hd" => 3, "sd" => 2, "low" => 1, _ => 0 };
                    if (rank > bestRank) { best = media; bestRank = rank; }
                }
                if (best is not null)
                    return new ApiContentVideoSource(source.Label, best, "video/mp4", page.FinalUri, ApiContentVideoSourceKind.DirectMedia);
            }
            catch (JsonException) { }
            catch (InvalidOperationException) { }
        }
        return null;
    }

    private static Uri? ReadUri(JsonElement element, string key) =>
        element.ValueKind == JsonValueKind.Object && element.TryGetProperty(key, out var value) &&
        value.ValueKind == JsonValueKind.String && Uri.TryCreate(value.GetString(), UriKind.Absolute, out var uri) &&
        IsSafePublicHttpUri(uri) ? uri : null;
}

using System.Net;
using System.Text;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Reads DPlayer's active video configuration, including JKAnime's atob URL.</summary>
public sealed class DPlayerHosterExtractor : HosterExtractorBase
{
    private static readonly Regex Comments = new(@"/\*.*?\*/", RegexOptions.Singleline | RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex EncodedVideo = new(
        """\bvideo\s*:\s*\{\s*url\s*:\s*atob\(\s*["'](?<value>[A-Za-z0-9+/=]{1,16384})["']\s*\)""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    private static readonly Regex PlainVideo = new(
        """\bvideo\s*:\s*\{\s*url\s*:\s*["'](?<value>[^"'<>]+)["']""",
        RegexOptions.CultureInvariant, TimeSpan.FromSeconds(2));
    public override string Name => "DPlayer";
    public override bool Matches(Uri uri) => HostMatchesDomain(uri, "jkanime.net") && uri.AbsolutePath == "/jkplayer/um";
    public override bool MatchesContent(string content) => content.Contains("new DPlayer", StringComparison.Ordinal) &&
        (EncodedVideo.IsMatch(content) || PlainVideo.IsMatch(content));

    public override async ValueTask<ApiContentVideoSource?> TryResolveAsync(
        ApiContentVideoSource source, HttpClient httpClient, CancellationToken cancellationToken)
    {
        var page = await FetchTextAsync(httpClient, source.PlaybackUri, source.Referrer,
            HosterBrowserHeaders.UserAgent, "text/html", TimeSpan.FromSeconds(15), cancellationToken, 2 * 1024 * 1024);
        var text = Comments.Replace(page.Content, string.Empty);
        var match = EncodedVideo.Match(text);
        string value;
        if (match.Success)
        {
            var bytes = new byte[match.Groups["value"].Length];
            if (!Convert.TryFromBase64Chars(match.Groups["value"].ValueSpan, bytes, out var written)) return null;
            value = Encoding.UTF8.GetString(bytes.AsSpan(0, written));
        }
        else
        {
            match = PlainVideo.Match(text);
            if (!match.Success) return null;
            value = match.Groups["value"].Value.Replace("\\/", "/", StringComparison.Ordinal);
        }
        return Uri.TryCreate(page.FinalUri, WebUtility.HtmlDecode(value), out var uri) && IsSafePublicHttpUri(uri) &&
            HosterMimeTypeResolver.MimeTypeFromUrl(uri) is not null
            ? HosterSourceReader.CreateDirectSource(source, uri, page.FinalUri) : null;
    }
}

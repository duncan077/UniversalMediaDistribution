using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Streamtape plants several complete-looking <c>get_video</c> URLs in its
/// embed page. Only the one its own script assembles carries a token the
/// server accepts; the rest answer HTTP 200 with an HTML error page, which a
/// player then tries to decode as video.
/// </summary>
public sealed class StreamtapeHosterExtractorTests
{
    private static readonly Uri EmbedUri =
        new("https://streamtape.com/e/yDVqV49mAvS1RwY/episode.mp4");

    [Fact]
    public async Task ResolvesTheTokenTheScriptAssemblesNotTheDecoys()
    {
        var resolved = await ResolveAsync(EmbedHtml);

        Assert.NotNull(resolved);
        Assert.Equal(
            "https://streamtape.com/get_video?id=yDVqV49mAvS1RwY" +
            "&expires=1786135934&ip=FRuODRyZDROXFxf&token=8sDCwWp4PEYc",
            resolved.PlaybackUri.AbsoluteUri);
        Assert.Equal(
            ApiContentVideoSourceKind.DirectMedia,
            resolved.Kind);
        Assert.Equal(EmbedUri, resolved.Referrer);
    }

    [Fact]
    public async Task TheFallbackNeverCarriesTheTrailingMarkupWithIt()
    {
        // Without the script the extractor has to take a plain match, but the
        // token must still stop at the tag that follows it — a URL ending in
        // "</div>" is rejected by the server just as surely as a wrong token.
        var resolved = await ResolveAsync(EmbedHtmlWithoutScript);

        Assert.NotNull(resolved);
        Assert.DoesNotContain(
            "</div>",
            resolved.PlaybackUri.AbsoluteUri,
            StringComparison.Ordinal);
        Assert.EndsWith(
            "token=8sDCwWp4Pcde",
            resolved.PlaybackUri.AbsoluteUri,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeavesTheEmbedForTheWebViewWhenNothingIsExtractable()
    {
        Assert.Null(await ResolveAsync("<html><body>nothing here</body></html>"));
    }

    private static async Task<ApiContentVideoSource?> ResolveAsync(string html)
    {
        var extractor = new StreamtapeHosterExtractor();
        using var httpClient = new HttpClient(new StubHandler(html));
        Assert.True(extractor.Matches(EmbedUri));
        return await extractor.TryResolveAsync(
            new ApiContentVideoSource(
                "StreamTape",
                EmbedUri,
                mimeType: null,
                referrer: EmbedUri,
                kind: ApiContentVideoSourceKind.EmbeddedWeb),
            httpClient,
            CancellationToken.None);
    }

    /// <summary>
    /// Copied from a live embed page: three decoy links whose tokens differ
    /// from the real one only in their last four characters, each followed
    /// directly by a closing tag, plus the script that builds the real link.
    /// </summary>
    private const string EmbedHtml =
        """
        <html><body>
        <div id="ideoooolink" style="display:none">//streamtape.com/get_video?id=yDVqV49mAvS1RwY&expires=1786135934&ip=FRuODRyZDROXFxf&token=8sDCwWp4Pcde</div>
        <div id="norobotlink" style="display:none">//streamtape.com/get_video?id=yDVqV49mAvS1RwY&expires=1786135934&ip=FRuODRyZDROXFxf&token=8sDCwWp4xyza</div>
        <div id="robotlink" style="display:none"></div>
        <script>
        document.getElementById('robotlink').innerHTML = '//st'+ ('xcdreamtape.com/get_video?id=yDVqV49mAvS1RwY&expires=1786135934&ip=FRuODRyZDROXFxf&token=8sDCwWp4PEYc').substring(2).substring(1);
        </script>
        </body></html>
        """;

    private const string EmbedHtmlWithoutScript =
        """
        <html><body>
        <div id="ideoooolink" style="display:none">//streamtape.com/get_video?id=yDVqV49mAvS1RwY&expires=1786135934&ip=FRuODRyZDROXFxf&token=8sDCwWp4Pcde</div>
        </body></html>
        """;

    private sealed class StubHandler(string html) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
    }
}

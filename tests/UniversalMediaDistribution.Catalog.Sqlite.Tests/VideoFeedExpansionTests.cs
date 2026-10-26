using System.Net;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class VideoFeedExpansionTests
{
    [Theory]
    [InlineData("https://jkanime.net/jkplayer/um?id=sample")]
    [InlineData("https://another-api-player.example/embed/1")]
    public async Task DPlayerDecodesActiveVideoNotCommentedOldPlayer(string embed)
    {
        var encoded = Convert.ToBase64String(System.Text.Encoding.UTF8.GetBytes("https://cdn.example/new.m3u8?token=live"));
        using var client = Client(_ => Html($$$"""
            /* new DPlayer({video: {url: 'https://cdn.example/expired.m3u8'}}); */
            new DPlayer({video: {url: atob('{{{encoded}}}'), type: 'customHls'}});
            """));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source(embed)]));
        Assert.Equal("https://cdn.example/new.m3u8?token=live", result.PlaybackUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://streamwish.to/e/1")]
    [InlineData("https://vidhidevip.com/e/1")]
    [InlineData("https://streamsb.net/e/1")]
    [InlineData("https://streamlare.com/e/1")]
    [InlineData("https://ninjastream.to/e/1")]
    [InlineData("https://uch.to/e/1")]
    public async Task GenericHostersPreferRelativePlayerFeedToAbsoluteAd(string embed)
    {
        using var client = Client(_ => Html("""<script>var ad='https://ads.example/ad.mp4'; player={file:'../media/main.m3u8?x=1&amp;y=2'};</script>"""));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source(embed)]));
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, result.Kind);
        Assert.Equal(new Uri(new Uri(embed), "../media/main.m3u8?x=1&y=2"), result.PlaybackUri);
    }

    [Theory]
    [InlineData("https://mixdrop.top/e/1")]
    [InlineData("https://unlisted-mix.example/e/1")]
    public async Task MixdropRecognizesPackedSingleQuotedAssignment(string embed)
    {
        using var client = Client(_ => Html("""eval(function(p,a,c,k,e,d){return p}("0.1 = '2';",3,3,"MDCore|wurl|//cdn.example/movie.mp4".split('|'),0,{}))"""));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source(embed)]));
        Assert.Equal("https://cdn.example/movie.mp4", result.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task MediafireReadsSingleQuotedAttributeAndEntities()
    {
        using var client = Client(_ => Html("""<a href = 'https://download12.mediafire.com/token/movie.mp4?x=1&amp;y=2'>Download</a>"""));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source("https://mediafire.com/file/1")]));
        Assert.Equal("https://download12.mediafire.com/token/movie.mp4?x=1&y=2", result.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task Html5ResolvesRelativeSourceButNotAdScript()
    {
        using var client = Client(_ => Html("""<script>var ad='https://ads.example/ad.mp4';</script><video><source src='../video/main.webm'></video>"""));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source("https://new-player.example/e/1")]));
        Assert.Equal("https://new-player.example/video/main.webm", result.PlaybackUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("http://192.168.1.1/video.mp4")]
    [InlineData("javascript:alert(1)")]
    [InlineData("blob:https://player.example/video.mp4")]
    public async Task Html5KeepsFallbackForUnsafeSource(string media)
    {
        using var client = Client(_ => Html($"<video src='{media}'></video>"));
        var source = Source("https://new-player.example/e/1");
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([source]));
        Assert.Equal(source, result);
    }

    [Fact]
    public async Task MissingPageDoesNotPoisonOtherVideosOnSameHost()
    {
        using var client = Client(request => Html(request.RequestUri!.AbsolutePath == "/missing"
            ? "not found" : "<video src='https://cdn.example/real.mp4'></video>"));
        var resolver = new VideoHosterResolver(client);
        await resolver.ResolveAsync([Source("https://new-player.example/missing")]);
        var result = Assert.Single(await resolver.ResolveAsync([Source("https://new-player.example/available")]));
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, result.Kind);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task OkRuReadsStringOrObjectMetadataAndChoosesBestVideo(bool stringify)
    {
        var metadata = new
        {
            videos = new[] {
            new { name = "low", url = "https://cdn.example/low" },
            new { name = "full", url = "https://cdn.example/full" },
            new { name = "ultra", url = "http://127.0.0.1/private" } }
        };
        var json = JsonSerializer.Serialize(new
        {
            flashvars = new
            {
                metadata = stringify
            ? (object)JsonSerializer.Serialize(metadata) : metadata
            }
        });
        using var client = Client(_ => Html($"<div data-options=\"{WebUtility.HtmlEncode(json)}\"></div>"));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source("https://ok.ru/videoembed/123")]));
        Assert.Equal("https://cdn.example/full", result.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", result.MimeType);
    }

    [Theory]
    [InlineData("ok.ru.attacker.example")]
    [InlineData("evilok.ru")]
    public void OkRuRejectsLookalikeHosts(string host) =>
        Assert.False(new OkRuHosterExtractor().Matches(new Uri($"https://{host}/videoembed/123")));

    [Fact]
    public void NewByseMirrorCanBeRecognizedWithoutAHostAlias()
    {
        var extractor = new FilemoonHosterExtractor();
        Assert.True(extractor.Matches(new Uri("https://byselapuix.com/e/123")));
        Assert.True(extractor.MatchesContent("<title>Byse Frontend</title><script>root.classList.add('video-embed-mode')</script>"));
        Assert.False(extractor.MatchesContent("An advertisement mentions Byse"));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task ZillaChecksActualSegmentBehindMasterPlaylist(bool accessible)
    {
        var segmentRequests = 0;
        using var client = Client(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/m3u8/abc") return Html("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1000\nchild.m3u8");
            if (request.RequestUri.AbsolutePath == "/m3u8/child.m3u8") return Html("#EXTM3U\n#EXTINF:5,\nsegment.ts");
            segmentRequests++;
            Assert.Equal("bytes=0-0", request.Headers.Range!.ToString());
            return new HttpResponseMessage(accessible ? HttpStatusCode.PartialContent : HttpStatusCode.Forbidden)
            { Content = new ByteArrayContent([0x47]) };
        });
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source("https://player.zilla-networks.com/play/abc")]));
        Assert.Equal(accessible ? ApiContentVideoSourceKind.DirectMedia : ApiContentVideoSourceKind.EmbeddedWeb, result.Kind);
        Assert.Equal(1, segmentRequests);
    }

    [Fact]
    public async Task ZillaRejectsPrivateSegmentBeforeSendingRequest()
    {
        var count = 0;
        using var client = Client(_ => { count++; return Html("#EXTM3U\n#EXTINF:5,\nhttp://169.254.169.254/private"); });
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Source("https://player.zilla-networks.com/play/abc")]));
        Assert.Equal(ApiContentVideoSourceKind.EmbeddedWeb, result.Kind);
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task ObserverFailureCannotBreakPlayback()
    {
        using var client = Client(_ => Html("<video src='https://cdn.example/video.mp4'></video>"));
        var resolver = new VideoHosterResolver(client, observer: _ => throw new InvalidOperationException());
        var result = Assert.Single(await resolver.ResolveAsync([Source("https://new-player.example/e/1")]));
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, result.Kind);
    }

    private static ApiContentVideoSource Source(string uri) => new("Server", new Uri(uri), kind: ApiContentVideoSourceKind.EmbeddedWeb);
    private static HttpResponseMessage Html(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text) };
    private static HttpClient Client(Func<HttpRequestMessage, HttpResponseMessage> response) => new(new Handler(response));
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> response) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = response(request);
            result.RequestMessage = request;
            return Task.FromResult(result);
        }
    }
}

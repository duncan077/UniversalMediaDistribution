using System.Net;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

[CollectionDefinition("Web challenge gate", DisableParallelization = true)]
public sealed class WebChallengeCollection;

[Collection("Web challenge gate")]
public sealed class VideoChallengeTests : IDisposable
{
    public void Dispose() => WebChallengeGate.Reset();
    private static ApiContentVideoSource Embed(string uri = "https://bysekoze.com/e/abc") =>
        new("Selected server", new Uri(uri), kind: ApiContentVideoSourceKind.EmbeddedWeb);

    [Theory]
    [InlineData("https://bysekoze.com/e/abc", 428)]
    [InlineData("https://dsvplay.com/e/abc", 403)]
    public async Task OnlySelectedServerOpensInteractiveChallenge(string uri, int status)
    {
        var solver = new Solver();
        WebChallengeGate.Configure(solver);
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage((HttpStatusCode)status)));
        var resolver = new VideoHosterResolver(client);
        var original = Embed(uri);
        var offered = Assert.Single(await resolver.ResolveAsync([original]));
        Assert.Equal(original, offered);
        Assert.Equal(0, solver.Calls);
        var captured = await resolver.RefreshAsync(offered);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, captured!.Kind);
        Assert.Equal(1, solver.Calls);
        // No repeated dialogs while retrying the same declined/challenged host.
        Assert.Equal(original, await resolver.RefreshAsync(original));
        Assert.Equal(1, solver.Calls);
    }

    [Fact]
    public async Task UnrelatedForbiddenHostDoesNotOpenAChallenge()
    {
        var solver = new Solver(); WebChallengeGate.Configure(solver);
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.Forbidden)));
        var resolver = new VideoHosterResolver(client);
        var source = Embed("https://private-video.example/e/1");
        await resolver.ResolveAsync([source]);
        Assert.Equal(source, await resolver.RefreshAsync(source));
        Assert.Equal(0, solver.Calls);
    }

    [Theory]
    [InlineData("https://animeav1.uns.bio/#video")]
    [InlineData("https://pelisplus.upns.pro/#video")]
    [InlineData("https://byselapuix.com/e/video")]
    [InlineData("https://framextv.xyz/embed/movie/27205")]
    public async Task JavascriptHostCanCaptureWithoutChallengeAndWithoutSuppressingOtherVideos(string uri)
    {
        var solver = new Solver(); WebChallengeGate.Configure(solver);
        using var client = new HttpClient(new Handler(_ => Text("<html>JavaScript player</html>")));
        var resolver = new VideoHosterResolver(client);
        var offered = Assert.Single(await resolver.ResolveAsync([Embed(uri)]));
        Assert.Equal(0, solver.Calls);
        Assert.False((await resolver.RefreshAsync(offered))!.IsEmbeddedWeb);
        Assert.Equal(offered, await resolver.RefreshAsync(offered));
        Assert.False((await resolver.RefreshAsync(Embed(uri + "2")))!.IsEmbeddedWeb);
        Assert.Equal(2, solver.Calls);
    }

    [Theory]
    [InlineData("https://uns.bio.attacker.example/#video")]
    [InlineData("https://fakeupns.pro/#video")]
    [InlineData("https://framextv.xyz.attacker.example/embed/movie/27205")]
    [InlineData("https://framextv.xyz/search")]
    [InlineData("http://127.0.0.1/video")]
    public void JavascriptHostOptInDoesNotMatchLookalikesOrPrivateUrls(string uri) =>
        Assert.False(VideoChallengeCapture.SupportsInteractiveCapture(new Uri(uri)));

    [Fact]
    public async Task JavascriptCaptureNeverOpensForAlreadyDirectMedia()
    {
        var solver = new Solver(); WebChallengeGate.Configure(solver);
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException()));
        var direct = new ApiContentVideoSource("Native", new Uri("https://animeav1.uns.bio/video.m3u8"));
        Assert.Equal(direct, await new VideoHosterResolver(client).RefreshAsync(direct));
        Assert.Equal(0, solver.Calls);
    }

    [Fact]
    public async Task CancelledDialogPreservesWebSource()
    {
        WebChallengeGate.Configure(new Solver { Decline = true });
        using var client = new HttpClient(new Handler(_ => new HttpResponseMessage(HttpStatusCode.PreconditionRequired)));
        var resolver = new VideoHosterResolver(client);
        var original = Assert.Single(await resolver.ResolveAsync([Embed()]));
        Assert.Equal(original, await resolver.RefreshAsync(original));
    }

    [Theory]
    [InlineData("https://cdn.example/movie.m3u8", true)]
    [InlineData("https://cdn.example/stream", true)]
    [InlineData("http://127.0.0.1/video.mp4", false)]
    [InlineData("http://169.254.169.254/video.mp4", false)]
    [InlineData("blob:https://cdn.example/id", false)]
    [InlineData("file:///C:/video.mp4", false)]
    public void CaptureValidatesMediaUrlAndPreservesHeaders(string url, bool valid)
    {
        var json = JsonSerializer.Serialize(new { umdVideoChallenge = true, url, document = "https://frame.example/e/1", userAgent = "Browser/1.0", type = "video/mp4" });
        var result = VideoChallengeCapture.Parse(json, Embed());
        Assert.Equal(valid, result is not null);
        if (result is not null)
        {
            Assert.Equal("Browser/1.0", result.UserAgent);
            Assert.Equal("Selected server", result.Label);
            Assert.Equal(result.MimeType == "application/vnd.apple.mpegurl" ? "https://frame.example/" : null, result.HttpOrigin?.AbsoluteUri);
        }
    }

    [Fact]
    public void CaptureKeepsEmbeddedAndObservedSubtitleTracksWithoutPrivateUrls()
    {
        var original = new ApiContentVideoSource("Framex", new("https://framextv.xyz/embed/movie/550"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb,
            subtitles: [new(new("https://subs.example/es.srt"), "Español", "es")]);
        var captured = VideoChallengeCapture.Parse("""
            {"umdVideoChallenge":true,"url":"https://cdn.example/master.m3u8",
             "document":"https://framextv.xyz/embed/movie/550","userAgent":"Browser/1.0",
             "subtitles":[{"url":"https://subs.example/en.vtt","label":"English","lang":"en"},
             {"url":"http://192.168.0.1/private.srt","lang":"en"}]}
            """, original);
        Assert.NotNull(captured);
        Assert.Equal(["en", "es"], captured.Subtitles.Select(t => t.Language));
    }

    [Fact]
    public void CaptureRejectsMalformedOversizedAndHeaderInjection()
    {
        Assert.Null(VideoChallengeCapture.Parse("[]", Embed()));
        Assert.Null(VideoChallengeCapture.Parse(new string(' ', 16385), Embed()));
        var json = JsonSerializer.Serialize(new { umdVideoChallenge = true, url = "https://cdn.example/video.mp4", document = "https://frame.example/", userAgent = "Browser\r\nAuthorization: secret", type = "video/mp4" });
        Assert.Null(VideoChallengeCapture.Parse(json, Embed()));
        Assert.False(VideoChallengeCapture.AllowsNavigation(Embed().PlaybackUri, new Uri("https://ad-popup.example/")));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task NativeProbeRequiresReachableSegmentWithoutCookies(bool reachable)
    {
        var requests = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            requests++;
            Assert.False(request.Headers.Contains("Cookie"));
            Assert.Equal("Browser/1.0", request.Headers.UserAgent.ToString());
            if (request.RequestUri!.AbsolutePath == "/master.m3u8") return Text("#EXTM3U\n#EXT-X-STREAM-INF:BANDWIDTH=1\nchild.m3u8");
            if (request.RequestUri.AbsolutePath == "/child.m3u8") return Text("#EXTM3U\n#EXTINF:4,\nsegment.ts");
            Assert.Equal("bytes=0-7", request.Headers.Range!.ToString());
            return new HttpResponseMessage(reachable ? HttpStatusCode.PartialContent : HttpStatusCode.Forbidden) { Content = new ByteArrayContent([0x47, 1, 2]) };
        }));
        var source = new ApiContentVideoSource("video", new Uri("https://cdn.example/master.m3u8"), "application/vnd.apple.mpegurl", userAgent: "Browser/1.0");
        Assert.Equal(reachable, await VideoChallengeMediaProbe.VerifyAsync(client, source, default));
        Assert.Equal(3, requests);
    }

    [Fact]
    public async Task NativeProbeAcceptsFeatureLengthPlaylistButStillBoundsHostileResponses()
    {
        var segment = "https://cdn.example/segment.ts?token=" + new string('a', 200);
        var playlist = "#EXTM3U\n" + string.Concat(Enumerable.Repeat("#EXTINF:6,\n" + segment + "\n", 1200));
        Assert.True(Encoding.UTF8.GetByteCount(playlist) > 128 * 1024);
        using var valid = new HttpClient(new Handler(request => request.RequestUri!.AbsolutePath == "/film.m3u8"
            ? Text(playlist) : new(HttpStatusCode.PartialContent) { Content = new ByteArrayContent([0x47, 1, 2]) }));
        var source = new ApiContentVideoSource("Film", new("https://cdn.example/film.m3u8"), "application/vnd.apple.mpegurl");
        Assert.True(await VideoChallengeMediaProbe.VerifyAsync(valid, source, default));
        using var oversized = new HttpClient(new Handler(_ => Text("#EXTM3U\n" + new string('a', 1024 * 1024))));
        Assert.False(await VideoChallengeMediaProbe.VerifyAsync(oversized, source, default));
    }

    [Fact]
    public async Task NativeProbeRejectsUnsafeRedirectBeforeSendingIt()
    {
        var count = 0;
        using var client = new HttpClient(new Handler(_ => { count++; return new HttpResponseMessage(HttpStatusCode.Redirect) { Headers = { Location = new Uri("http://192.168.1.1/movie.mp4") } }; }));
        Assert.False(await VideoChallengeMediaProbe.VerifyAsync(client, new("video", new Uri("https://cdn.example/video.mp4")), default));
        Assert.Equal(1, count);
    }

    [Fact]
    public async Task NuploadWatchWrapperDelegatesToVoeNotAdIframe()
    {
        using var client = new HttpClient(new Handler(request => request.RequestUri!.Host == "nupload.top"
            ? Text("<iframe src='https://ad.example/player'></iframe><iframe src='https://voe.sx/e/actual'></iframe>")
            : Text("<script>var sources = {'hls':'https://cdn.example/movie.m3u8'};</script>")));
        var result = Assert.Single(await new VideoHosterResolver(client).ResolveAsync([Embed("https://nupload.top/watch/new-wrapper")]));
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, result.Kind);
        Assert.Equal("https://cdn.example/movie.m3u8", result.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task PelisflixNewOriginAcceptsRetiredCatalogLinksButPreservesArtworkOrigin()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("pelisflixhd1.top", request.RequestUri!.Host);
            return Text("<a href='https://pelisflixhd.blog/pelicula/example/'><img src='https://s.pelisflixhd.blog/poster.jpg' alt='Poster Example'><p>Example</p></a>");
        }));
        var provider = new PelisflixHdApiContentProvider(client);
        Assert.True(provider.Descriptor.IsRetiredBaseUrl("https://pelisflixhd.blog/"));
        var result = Assert.Single(await provider.LoadDailyUpdatesAsync(new Uri(provider.Descriptor.DefaultBaseUrl)));
        Assert.Equal("pelicula/example/", result.ExternalId);
        Assert.Equal("https://s.pelisflixhd.blog/poster.jpg", result.PreviewItem.ThumbnailRef);
    }

    private static HttpResponseMessage Text(string text) => new(HttpStatusCode.OK) { Content = new StringContent(text, Encoding.UTF8) };
    private sealed class Handler(Func<HttpRequestMessage, HttpResponseMessage> run) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var result = run(request); result.RequestMessage = request; return Task.FromResult(result);
        }
    }
    private sealed class Solver : IWebChallengeSolver
    {
        internal int Calls;
        internal bool Decline;
        public ValueTask<WebChallengeClearance?> SolveAsync(Uri endpoint, CancellationToken token) => ValueTask.FromResult<WebChallengeClearance?>(null);
        public ValueTask<ApiContentVideoSource?> ResolveVideoAsync(ApiContentVideoSource source, CancellationToken token)
        {
            Calls++;
            return ValueTask.FromResult<ApiContentVideoSource?>(Decline ? null : new(source.Label, new Uri("https://cdn.example/confirmed.mp4"), "video/mp4"));
        }
    }
}

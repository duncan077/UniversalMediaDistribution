using System.Net;
using System.Net.Http.Headers;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class EmbeddedWebMediaHandoffResolverTests
{
    private static readonly Uri Document =
        new("https://nupload.top/watch/video-id");
    private static readonly Uri Playlist =
        new("https://sv2.ibra.lat/aa/test/master.m3u8?token=secret");

    [Fact]
    public async Task SuccessfulPlaylistBecomesNativeRequestWithBrowserHeaders()
    {
        var handler = new RecordingHandler(
            static _ => HlsResponse());
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            Observation(statusCode: 200),
            CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal(Playlist, request.InitialVariant.Source.PlaybackUri);
        Assert.Equal(
            new Uri("https://nupload.top/watch/video-id"),
            request.InitialVariant.Source.Referrer);
        Assert.Equal(
            new Uri("https://nupload.top/"),
            request.InitialVariant.Source.HttpOrigin);
        Assert.Equal(
            "Test Browser",
            request.InitialVariant.Source.UserAgent);
        Assert.Equal(
            "application/vnd.apple.mpegurl",
            request.InitialVariant.MimeType);
        Assert.Equal(1, handler.RequestCount);
        Assert.Equal(
            "https://nupload.top",
            handler.LastOrigin);
        Assert.Equal(
            "https://nupload.top/watch/video-id",
            handler.LastReferrer);
        Assert.Equal("Test Browser", handler.LastUserAgent);
    }

    [Fact]
    public async Task AndroidRequestWithoutStatusIsValidatedBeforeHandoff()
    {
        var handler = new RecordingHandler(
            static _ => HlsResponse());
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            Observation(statusCode: null),
            CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task MissingBrowserResponseLeavesWebPlayerWithoutProbe()
    {
        var handler = new RecordingHandler(
            static _ => HlsResponse());
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            Observation(statusCode: 404),
            CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task AndroidSignedRootFollowsRedirectToPlaylist()
    {
        var signedRoot =
            new Uri("https://sv2.ibra.lat/?s=session");
        var handler = new RecordingHandler(
            request => request.RequestUri == signedRoot
                ? new HttpResponseMessage(HttpStatusCode.Found)
                {
                    Headers =
                    {
                        Location = Playlist
                    }
                }
                : HlsResponse());
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);
        var observation = Observation(statusCode: null) with
        {
            RequestUri = signedRoot,
            ContentType = null
        };

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            observation,
            CancellationToken.None);

        Assert.NotNull(request);
        Assert.Equal(Playlist, request.InitialVariant.Source.PlaybackUri);
        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task AndroidSignedRoot404LeavesWebPlayerOpen()
    {
        var signedRoot =
            new Uri("https://sv3.ibra.lat/?s=missing-session");
        var handler = new RecordingHandler(
            static _ => new HttpResponseMessage(
                HttpStatusCode.NotFound));
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);
        var observation = Observation(statusCode: null) with
        {
            RequestUri = signedRoot,
            ContentType = null
        };

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            observation,
            CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task SuccessfulHtmlBodyDoesNotReplaceWebPlayer()
    {
        var handler = new RecordingHandler(
            static _ =>
            {
                var response = new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        "<!doctype html><title>Not found</title>")
                };
                response.Content.Headers.ContentType =
                    new MediaTypeHeaderValue(
                        "application/vnd.apple.mpegurl");
                return response;
            });
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            Observation(statusCode: 200),
            CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task RedirectOutsideNuploadMediaCdnIsRejected()
    {
        var handler = new RecordingHandler(
            static _ => new HttpResponseMessage(HttpStatusCode.Found)
            {
                Headers =
                {
                    Location = new Uri(
                        "http://127.0.0.1/private/master.m3u8")
                }
            });
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            Observation(statusCode: 200),
            CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(1, handler.RequestCount);
    }

    [Fact]
    public async Task UnrelatedPlayerPlaylistIsNeverProbed()
    {
        var handler = new RecordingHandler(
            static _ => HlsResponse());
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);
        var unrelated = Observation(statusCode: 200) with
        {
            RequestUri =
                new Uri("https://ads.example/preroll/master.m3u8")
        };

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            unrelated,
            CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(0, handler.RequestCount);
    }

    [Fact]
    public async Task ResponseFromPreviousWebDocumentIsNeverProbed()
    {
        var handler = new RecordingHandler(
            static _ => HlsResponse());
        using var resolver =
            new EmbeddedWebMediaHandoffResolver(handler);
        var stale = Observation(statusCode: 200) with
        {
            DocumentUri =
                new Uri("https://nupload.top/watch/previous-video")
        };

        var request = await resolver.TryResolveAsync(
            Document,
            "Movie",
            stale,
            CancellationToken.None);

        Assert.Null(request);
        Assert.Equal(0, handler.RequestCount);
    }

    [Theory]
    [InlineData("http://localhost/master.m3u8")]
    [InlineData("http://127.0.0.1/master.m3u8")]
    [InlineData("http://10.0.0.1/master.m3u8")]
    [InlineData("http://192.168.1.1/master.m3u8")]
    [InlineData("http://[::ffff:192.168.1.1]/master.m3u8")]
    [InlineData("http://[fc00::1]/master.m3u8")]
    public void PrivateEndpointIsNotSafe(string value)
    {
        Assert.False(
            EmbeddedWebMediaHandoffResolver.IsSafePublicHttpUri(
                new Uri(value)));
    }

    private static EmbeddedWebMediaResponse Observation(
        int? statusCode) =>
        new(
            Document,
            Playlist,
            statusCode,
            "application/vnd.apple.mpegurl",
            "https://nupload.top/watch/video-id",
            "https://nupload.top",
            "Test Browser");

    private static HttpResponseMessage HlsResponse()
    {
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "#EXTM3U\n#EXT-X-VERSION:3\n" +
                "#EXTINF:10,\nsegment0.ts\n")
        };
        response.Content.Headers.ContentType =
            new MediaTypeHeaderValue(
                "application/vnd.apple.mpegurl");
        return response;
    }

    private sealed class RecordingHandler(
        Func<HttpRequestMessage, HttpResponseMessage> respond) :
        HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        public string? LastOrigin { get; private set; }

        public string? LastReferrer { get; private set; }

        public string? LastUserAgent { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastOrigin = request.Headers.TryGetValues(
                    "Origin",
                    out var origin)
                ? Assert.Single(origin)
                : null;
            LastReferrer = request.Headers.Referrer?.AbsoluteUri;
            LastUserAgent = request.Headers.TryGetValues(
                    "User-Agent",
                    out var userAgent)
                ? string.Join(" ", userAgent)
                : null;
            return Task.FromResult(respond(request));
        }
    }
}

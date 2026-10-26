using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;
using System.Text;
using UniversalMediaDistribution.Playback.LibVLC;
using UniversalMediaDistribution.UI.Playback;

namespace UniversalMediaDistribution.Desktop.Tests;

public sealed class HlsLoopbackProxySessionTests
{
    private static readonly Uri PlaybackUri =
        new("https://sv2.ibra.lat/?s=fixture-session-secret");
    private static readonly Uri Origin =
        new("https://nupload.top/");

    [Fact]
    public async Task RewritesAndStreamsTheWholeHlsGraphWithOrigin()
    {
        var upstream = new NuploadHlsHandler();
        await using var proxy =
            await HlsLoopbackProxySession.StartForTestAsync(
                PlaybackUri,
                Origin,
                Origin,
                "Test Browser",
                upstream);
        using var client = CreateLoopbackClient();

        var master = await client.GetStringAsync(proxy.PlaybackUri);

        AssertLoopbackDocument(master);
        var audioUri = ReadQuotedUri(master, "#EXT-X-MEDIA:");
        var variantUri = ReadStandaloneUri(master);
        AssertLoopbackUri(audioUri);
        AssertLoopbackUri(variantUri);

        var variant = await client.GetStringAsync(variantUri);
        AssertLoopbackDocument(variant);
        var keyUri = ReadQuotedUri(variant, "#EXT-X-KEY:");
        var segmentUri = ReadStandaloneUri(variant);
        AssertLoopbackUri(keyUri);
        AssertLoopbackUri(segmentUri);

        var key = await client.GetByteArrayAsync(keyUri);
        Assert.Equal(16, key.Length);

        using var segmentRequest = new HttpRequestMessage(
            HttpMethod.Get,
            segmentUri);
        segmentRequest.Headers.Range = new RangeHeaderValue(0, 2);
        using var segmentResponse = await client.SendAsync(segmentRequest);
        var segment = await segmentResponse.Content.ReadAsByteArrayAsync();

        Assert.Equal(HttpStatusCode.PartialContent, segmentResponse.StatusCode);
        Assert.Equal([0x47, 0x01, 0x02], segment);
        Assert.Equal("bytes 0-2/1880", segmentResponse.Content.Headers.ContentRange?.ToString());
        Assert.All(
            upstream.Requests,
            request =>
            {
                Assert.Equal("https://nupload.top", request.Origin);
                Assert.Equal("https://nupload.top/", request.Referrer);
                Assert.Equal("Test Browser", request.UserAgent);
            });
        Assert.Contains(
            upstream.Requests,
            request =>
                request.Path == "/media/segment.ts" &&
                request.Range == "bytes=0-2");
        Assert.Contains(
            upstream.Requests,
            request =>
                request.Path == "/" &&
                request.Query == "?s=fixture-session-secret");
    }

    [Fact]
    public async Task RejectsPrivateUrisFromAnUntrustedPlaylist()
    {
        var upstream = new UnsafePlaylistHandler();
        await using var proxy =
            await HlsLoopbackProxySession.StartForTestAsync(
                new Uri("https://media.example/master.m3u8"),
                Origin,
                Origin,
                "Test Browser",
                upstream);
        using var client = CreateLoopbackClient();

        using var response = await client.GetAsync(proxy.PlaybackUri);
        var error = await response.Content.ReadAsStringAsync();

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
        Assert.Equal(1, upstream.RequestCount);
        Assert.DoesNotContain("127.0.0.1", error, StringComparison.Ordinal);
        Assert.DoesNotContain("media.example", error, StringComparison.Ordinal);
    }

    [Fact]
    public async Task RejectsAHosterDecoyThatReturnsSuccessWithoutHls()
    {
        await using var proxy =
            await HlsLoopbackProxySession.StartForTestAsync(
                new Uri("https://media.example/master.m3u8"),
                Origin,
                Origin,
                "Test Browser",
                new DecoyPlaylistHandler());
        using var client = CreateLoopbackClient();

        using var response = await client.GetAsync(proxy.PlaybackUri);

        Assert.Equal(HttpStatusCode.BadGateway, response.StatusCode);
    }

    [Fact]
    public void NativeVlcUsesTheProxyForAnyOriginProtectedHlsSource()
    {
        var source = new PlatformMediaSource(
            PlaybackUri,
            openReadAsync: null,
            referrer: Origin,
            httpOrigin: Origin);
        var variant = new PlatformMediaVariant(
            source,
            "Nupload",
            IsAudio: false,
            "application/vnd.apple.mpegurl");

        Assert.True(
            LibVlcPlatformMediaPlayer.RequiresHlsProxy(variant));
        Assert.False(
            LibVlcPlatformMediaPlayer.RequiresHlsProxy(
                variant with
                {
                    Source = new PlatformMediaSource(
                        PlaybackUri,
                        openReadAsync: null,
                        referrer: Origin)
                }));
    }

    [Theory]
    [InlineData("127.0.0.1", false)]
    [InlineData("10.0.0.1", false)]
    [InlineData("169.254.169.254", false)]
    [InlineData("192.168.1.10", false)]
    [InlineData("::1", false)]
    [InlineData("::2", false)]
    [InlineData("64:ff9b::c0a8:101", false)]
    [InlineData("100::1", false)]
    [InlineData("2001:db8::1", false)]
    [InlineData("2002:c0a8:101::1", false)]
    [InlineData("3fff::1", false)]
    [InlineData("fc00::1", false)]
    [InlineData("fe80::1", false)]
    [InlineData("1.1.1.1", true)]
    [InlineData("2606:4700:4700::1111", true)]
    public void PublicEndpointPolicyClassifiesResolvedAddresses(
        string address,
        bool expected)
    {
        Assert.Equal(
            expected,
            PublicHttpEndpointPolicy.IsPublicAddress(
                IPAddress.Parse(address)));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://sub.localhost/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("file:///windows/win.ini")]
    [InlineData("https://user:password@example.com/media")]
    public void PublicEndpointPolicyRejectsUnsafeUris(string value)
    {
        Assert.False(
            PublicHttpEndpointPolicy.IsSafePublicHttpUri(new Uri(value)));
    }

    private static HttpClient CreateLoopbackClient() =>
        new(new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = false
        });

    private static void AssertLoopbackDocument(string playlist)
    {
        Assert.StartsWith("#EXTM3U", playlist, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sv2.ibra.lat",
            playlist,
            StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            "secret",
            playlist,
            StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertLoopbackUri(Uri uri)
    {
        Assert.Equal("http", uri.Scheme);
        Assert.Equal(IPAddress.Loopback.ToString(), uri.Host);
        Assert.True(uri.IsLoopback);
        Assert.Empty(uri.Query);
    }

    private static Uri ReadStandaloneUri(string playlist)
    {
        var value = playlist
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .First(line => !line.StartsWith('#'));
        return new Uri(value);
    }

    private static Uri ReadQuotedUri(string playlist, string tag)
    {
        var line = playlist
            .Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries)
            .Single(value => value.StartsWith(
                tag,
                StringComparison.OrdinalIgnoreCase));
        const string marker = "URI=\"";
        var start = line.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase) + marker.Length;
        var end = line.IndexOf('"', start);
        return new Uri(line[start..end]);
    }

    private sealed class NuploadHlsHandler : HttpMessageHandler
    {
        private readonly object _gate = new();
        private readonly List<RequestSnapshot> _requests = [];

        public IReadOnlyList<RequestSnapshot> Requests
        {
            get
            {
                lock (_gate)
                {
                    return _requests.ToArray();
                }
            }
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            lock (_gate)
            {
                _requests.Add(new RequestSnapshot(
                    uri.AbsolutePath,
                    uri.Query,
                    request.Headers.TryGetValues("Origin", out var origins)
                        ? origins.Single()
                        : null,
                    request.Headers.Referrer?.AbsoluteUri,
                    request.Headers.UserAgent.ToString(),
                    request.Headers.Range?.ToString()));
            }

            var response = uri.AbsolutePath switch
            {
                "/" => Redirect("/media/master.m3u8"),
                "/media/master.m3u8" => Playlist(
                    """
                    #EXTM3U
                    #EXT-X-CONTENT-STEERING:SERVER-URI="steering.json?token=steering-secret",PATHWAY-ID="cdn-a"
                    #EXT-X-MEDIA:TYPE=AUDIO,GROUP-ID="audio",URI="audio.m3u8?token=audio-secret"
                    #EXT-X-STREAM-INF:BANDWIDTH=1280000,AUDIO="audio"
                    video.m3u8?token=variant-secret
                    """),
                "/media/video.m3u8" => Playlist(
                    """
                    #EXTM3U
                    #EXT-X-TARGETDURATION:6
                    #EXT-X-KEY:METHOD=AES-128,URI="key.bin?token=key-secret"
                    #EXTINF:6,
                    segment.ts?token=segment-secret
                    #EXT-X-ENDLIST
                    """),
                "/media/audio.m3u8" => Playlist(
                    """
                    #EXTM3U
                    #EXTINF:6,
                    audio.ts
                    #EXT-X-ENDLIST
                    """),
                "/media/key.bin" => Bytes(
                    Enumerable.Range(0, 16)
                        .Select(static value => (byte)value)
                        .ToArray()),
                "/media/segment.ts" => PartialSegment(),
                _ => new HttpResponseMessage(HttpStatusCode.NotFound)
            };
            response.RequestMessage = request;
            return Task.FromResult(response);
        }

        private static HttpResponseMessage Redirect(string location)
        {
            var response = new HttpResponseMessage(
                HttpStatusCode.MovedPermanently);
            response.Headers.Location = new Uri(location, UriKind.Relative);
            return response;
        }

        private static HttpResponseMessage Playlist(string value) =>
            new(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    value,
                    Encoding.UTF8,
                    "application/vnd.apple.mpegurl")
            };

        private static HttpResponseMessage Bytes(byte[] value) =>
            new(HttpStatusCode.OK)
            {
                Content = new ByteArrayContent(value)
            };

        private static HttpResponseMessage PartialSegment()
        {
            var content = new ByteArrayContent([0x47, 0x01, 0x02]);
            content.Headers.ContentType =
                new MediaTypeHeaderValue("video/mp2t");
            content.Headers.ContentRange =
                new ContentRangeHeaderValue(0, 2, 1880);
            var response = new HttpResponseMessage(
                HttpStatusCode.PartialContent)
            {
                Content = content
            };
            response.Headers.AcceptRanges.Add("bytes");
            return response;
        }
    }

    private sealed class UnsafePlaylistHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    """
                    #EXTM3U
                    #EXTINF:6,
                    http://127.0.0.1:8080/private.ts
                    """,
                    Encoding.UTF8,
                    "application/vnd.apple.mpegurl")
            });
        }
    }

    private sealed class DecoyPlaylistHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    "<html>media access denied</html>",
                    Encoding.UTF8,
                    "text/html")
            });
    }

    private sealed record RequestSnapshot(
        string Path,
        string Query,
        string? Origin,
        string? Referrer,
        string UserAgent,
        string? Range);
}

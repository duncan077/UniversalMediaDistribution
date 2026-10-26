using System.Collections.Concurrent;
using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Composition;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class VideoHosterResolverTests
{
    [Fact]
    public void SupportedHostersIncludesAllDefaultExtractors()
    {
        var resolver = new VideoHosterResolver(new HttpClient());

        Assert.Contains("PremiunVIP", resolver.SupportedHosters);
        Assert.Contains("PlusTube", resolver.SupportedHosters);
        Assert.Contains("Nupload", resolver.SupportedHosters);
        Assert.Contains("VOE", resolver.SupportedHosters);
        Assert.Contains("YourUpload", resolver.SupportedHosters);
        Assert.Contains("Vixcloud", resolver.SupportedHosters);
        Assert.Contains("Upzone", resolver.SupportedHosters);
        Assert.Contains("Uqload", resolver.SupportedHosters);
        Assert.Contains("UpZur", resolver.SupportedHosters);
        Assert.Contains("Filemoon", resolver.SupportedHosters);
        Assert.Contains("Frembed", resolver.SupportedHosters);
        Assert.Contains("StreamSB", resolver.SupportedHosters);
        Assert.Contains("Streamlare", resolver.SupportedHosters);
        Assert.Contains("NinjaStream", resolver.SupportedHosters);
        Assert.Contains("Uch", resolver.SupportedHosters);
        Assert.Contains("Streamtape", resolver.SupportedHosters);
        Assert.Contains("Mp4Upload", resolver.SupportedHosters);
        Assert.Contains("Doodstream", resolver.SupportedHosters);
        Assert.Contains("Vidmoly", resolver.SupportedHosters);
        Assert.Contains("Vidoza", resolver.SupportedHosters);
        Assert.Contains("LoadX", resolver.SupportedHosters);
        Assert.Contains("SpeedFiles", resolver.SupportedHosters);
        Assert.DoesNotContain("TVLibrePlayer", resolver.SupportedHosters);
    }

    [Fact]
    public void UnencryptedDashMimeTypeRemainsSupported()
    {
        var mimeType = HosterMimeTypeResolver.MimeTypeFromUrl(
            new Uri("https://media.example/live/manifest.mpd"));

        Assert.Equal("application/dash+xml", mimeType);
    }

    [Theory]
    [InlineData("http://0.0.0.0/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://100.64.0.1/")]
    [InlineData("http://127.0.0.1/")]
    [InlineData("http://169.254.169.254/latest/meta-data/")]
    [InlineData("http://172.16.0.1/")]
    [InlineData("http://192.168.1.1/")]
    [InlineData("http://198.18.0.1/")]
    [InlineData("http://224.0.0.1/")]
    [InlineData("http://[::]/")]
    [InlineData("http://[fc00::1]/")]
    [InlineData("http://[fd12:3456:789a::1]/")]
    [InlineData("http://[fe80::1]/")]
    [InlineData("http://[ff02::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://[::ffff:192.168.1.1]/")]
    [InlineData("http://localhost./")]
    [InlineData("http://player.localhost/")]
    public void PublicHttpUriPolicyRejectsNonPublicTargets(string value)
    {
        Assert.False(
            PublicUriProbeExtractor.IsSafe(new Uri(value)));
    }

    [Theory]
    [InlineData("https://video.example.com/e/1")]
    [InlineData("https://1.1.1.1/")]
    [InlineData("https://[2606:4700:4700::1111]/")]
    public void PublicHttpUriPolicyAllowsPublicTargets(string value)
    {
        Assert.True(
            PublicUriProbeExtractor.IsSafe(new Uri(value)));
    }

    [Fact]
    public async Task ResolverDoesNotInvokeExtractorForNonPublicTarget()
    {
        var extractor = new PublicUriProbeExtractor();
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ => throw new InvalidOperationException(
                    "The unsafe target must not be requested.")));
        var resolver = new VideoHosterResolver(httpClient, [extractor]);
        var source = new ApiContentVideoSource(
            "Unsafe",
            new Uri("http://192.168.1.20/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.Equal(0, extractor.ResolveCount);
    }

    [Fact]
    public async Task ResolverRejectsNonPublicDirectMediaReturnedByExtractor()
    {
        var extractor = new UnsafeResultExtractor();
        using var httpClient = new HttpClient(
            new StaticHandler(_ => EmptyResponse()));
        var resolver = new VideoHosterResolver(httpClient, [extractor]);
        var source = new ApiContentVideoSource(
            "Unsafe result",
            new Uri("https://video.example.com/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
    }

    [Fact]
    public void DefaultContentSourceHandlerDisablesAutomaticRedirects()
    {
        using var handler =
            ApplicationComposition.CreateContentSourceHttpHandler();

        Assert.False(handler.AllowAutoRedirect);
    }

    [Fact]
    public void EveryHosterRegexHasAtMostTwoSecondMatchTimeout()
    {
        var regexFields = typeof(HosterFeedMap).Assembly
            .GetTypes()
            .Where(type =>
                typeof(HosterExtractorBase).IsAssignableFrom(type) ||
                type.Name is "HosterSourceReader" or "JavaScriptPacker")
            .SelectMany(type => type.GetFields(
                BindingFlags.Public |
                BindingFlags.NonPublic |
                BindingFlags.Static))
            .Where(field => field.FieldType == typeof(Regex))
            .ToArray();

        Assert.NotEmpty(regexFields);
        foreach (var field in regexFields)
        {
            var regex = Assert.IsType<Regex>(field.GetValue(null));
            Assert.True(
                regex.MatchTimeout > TimeSpan.Zero &&
                regex.MatchTimeout <= TimeSpan.FromSeconds(2),
                $"{field.DeclaringType!.Name}.{field.Name} has an unsafe " +
                $"match timeout of {regex.MatchTimeout}.");
        }
    }

    [Fact]
    public async Task GenericHosterRejectsPrivateRedirectBeforeSecondRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ =>
                {
                    Interlocked.Increment(ref requestCount);
                    return HtmlResponse(
                        """
                        <meta http-equiv="refresh"
                              content="0;url=http://192.168.1.25/player">
                        """);
                }));
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "StreamSB",
            new Uri("https://streamsb.net/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GenericHosterFollowsSafeHttpRedirectWithReferrer()
    {
        const string media =
            "https://delivery.example/hls/redirected.m3u8";
        var requestCount = 0;
        using var httpClient = new HttpClient(
            new StaticHandler(
                request =>
                {
                    requestCount++;
                    if (requestCount == 1)
                    {
                        return RedirectResponse(
                            "https://streamsb.net/player/final");
                    }

                    Assert.Equal(
                        "https://streamsb.net/e/abcdef",
                        request.Headers.Referrer?.AbsoluteUri);
                    return HtmlResponse(
                        $$"""<script>const player={file:"{{media}}"};</script>""");
                }));
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "StreamSB",
            new Uri("https://streamsb.net/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var direct = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, direct.Kind);
        Assert.Equal(media, direct.PlaybackUri.AbsoluteUri);
        Assert.Equal("https://streamsb.net/", direct.Referrer?.AbsoluteUri);
        Assert.Equal(2, requestCount);
    }

    [Fact]
    public async Task GenericHosterRejectsPrivateHttpRedirectBeforeSecondRequest()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ =>
                {
                    requestCount++;
                    return RedirectResponse("http://192.168.1.25/player");
                }));
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "StreamSB",
            new Uri("https://streamsb.net/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.Equal(1, requestCount);
    }

    [Fact]
    public async Task GenericHosterBoundsHttpRedirectChain()
    {
        var requestCount = 0;
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ =>
                {
                    requestCount++;
                    return RedirectResponse(
                        $"https://streamsb.net/redirect/{requestCount}");
                }));
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "StreamSB",
            new Uri("https://streamsb.net/e/redirect-loop"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.Equal(6, requestCount);
    }

    [Theory]
    [InlineData("https://vixcloud.evil.example/embed/1", "Vixcloud")]
    [InlineData("https://frembed.evil.example/embed/1", "Frembed")]
    [InlineData("https://evil-ninjastream.to/embed/1", "NinjaStream")]
    [InlineData("https://uch.attacker.example/embed/1", "Uch")]
    public void HosterMatchersRejectLookalikeDomains(
        string url,
        string extractorName)
    {
        var extractor = HosterFeedMap.Default()
            .Single(candidate => candidate.Name == extractorName);

        Assert.False(extractor.Matches(new Uri(url)));
    }

    [Fact]
    public async Task AnIframeWrapperIsUnwrappedSoTheRealHosterIsRecognized()
    {
        // The address a site hands out is often a wrapper whose only content
        // is an iframe of the real hoster. Nothing matches the wrapper, so a
        // title that could have played in the native player fell back to the
        // embedded browser — where the player sits in a cross-origin frame
        // that cannot even be filtered.
        const string media = "https://delivery.example/hls/master.m3u8";
        var html = $$"""
            <script>
              var sources = {'hls': '{{media}}'};
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Nupload",
                    new Uri(
                        "https://nupload.top/iframe/?url=" +
                        "https%3A%2F%2Fvoe.sx%2Fe%2Fdxaecmu83h94"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task AnAddressThatIsNotAWrapperIsResolvedAsBefore()
    {
        const string media = "https://delivery.example/hls/master.m3u8";
        var html = $$"""
            <script>
              var sources = {'hls': '{{media}}'};
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "VOE",
                    new Uri("https://voe.sx/e/dxaecmu83h94"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task YourUploadResolvesJwPlayerMedia()
    {
        const string media =
            "https://cdn.yucache.net/videos/example/master.m3u8";
        var html = $$"""
            <script>
              const jwplayerOptions = { file: '{{media}}' };
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "YourUpload",
                    new Uri("https://www.yourupload.com/embed/example"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", source.MimeType);
    }

    [Fact]
    public async Task UqloadUnpacksPackedPlayerSource()
    {
        const string media =
            "https://cdn.uqload.is/video/example.mp4";
        var html = $$"""
            <script>
            eval(function(p,a,c,k,e,d){return p}(
              "0:'1'",2,2,"file|{{media}}".split('|'),0,{}))
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Uqload",
                    new Uri("https://uqload.cx/embed-example.html"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", source.MimeType);
    }

    [Fact]
    public async Task GenericHosterPrefersTaggedPlayerSourceOverEarlierAdMedia()
    {
        const string ad = "https://ads.example/preload.mp4";
        const string media = "https://delivery.example/hls/master.m3u8";
        var html = $$"""
            <link rel="preload" href="{{ad}}">
            <script>const player = { file: "{{media}}" };</script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Streamlare",
                    new Uri("https://streamlare.com/e/example"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Theory]
    [InlineData("https://streamlare.com/e/example")]
    [InlineData("https://ninjastream.to/e/example")]
    [InlineData("https://uch.to/e/example")]
    public async Task GenericPackedHostersResolveTaggedMedia(string embedUrl)
    {
        const string media = "https://delivery.example/video/example.mp4";
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ => HtmlResponse(
                    $$"""<script>const player={src:"{{media}}"};</script>""")));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Generic",
                    new Uri(embedUrl),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task UpzoneResolvesRelativeHlsAndUsesReffQuery()
    {
        const string media =
            "https://upzone.cc/streams/master.m3u8?token=abc";
        var html = """
            <script>
              const player = { hls2: '/streams/master.m3u8?token=abc' };
            </script>
            """;
        var handler = new StaticHandler(
            request =>
            {
                Assert.Equal(
                    "https://pelisplus.autos/",
                    request.Headers.Referrer?.AbsoluteUri);
                return HtmlResponse(html);
            },
            hostFilter: "upzone.cc");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Upzone",
                    new Uri(
                        "https://upzone.cc/e/example" +
                        "?reff=pelisplus.autos"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", source.MimeType);
        Assert.Equal("https://upzone.cc/", source.Referrer?.AbsoluteUri);
    }

    [Fact]
    public async Task UpzoneDelegatesIframeToExistingUqloadExtractor()
    {
        const string media =
            "https://cdn.uqload.is/video/delegated.mp4";
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri?.IdnHost == "upzone.to")
            {
                return HtmlResponse(
                    """
                    <iframe src="https://uqload.is/embed-delegated.html">
                    </iframe>
                    """);
            }

            return HtmlResponse(
                $$"""
                <script type="text/javascript">
                eval(function(p,a,c,k,e,d){return p}(
                  "0:'1'",2,2,"file|{{media}}".split('|'),0,{}))
                </script>
                """);
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Upzone",
                    new Uri("https://upzone.to/e/delegated"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", source.MimeType);
        Assert.Equal("https://uqload.is/", source.Referrer?.AbsoluteUri);
    }

    [Fact]
    public async Task UpzonePrefersSupportedDelegatedHosterOverEarlierAdFrame()
    {
        const string media =
            "https://cdn.uqload.is/video/delegated-priority.mp4";
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri?.IdnHost == "upzone.to")
            {
                return HtmlResponse(
                    """
                    <iframe src="https://ads.example/player"></iframe>
                    <iframe src="https://uqload.is/embed-priority.html"></iframe>
                    """);
            }

            return HtmlResponse(
                $$"""<script>const player={file:"{{media}}"};</script>""");
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Upzone",
                    new Uri("https://upzone.to/e/priority"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task UpZurDecodesReversedEscapedArray()
    {
        const string media =
            "https://cdn.upzur.example/live/master.m3u8";
        var html = """
            <script>
              var uHo4sc = [
                "\x2em3u8\x22",
                "//cdn.upzur.example/live/master",
                "src\u003d\x22https:"
              ];
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ => HtmlResponse(html),
                hostFilter: "upzur.com"));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "UpZur",
                    new Uri("https://upzur.com/e/example"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", source.MimeType);
        Assert.Equal(
            "https://upzur.com/e/example",
            source.Referrer?.AbsoluteUri);
    }

    [Fact]
    public async Task VixcloudBuildsTokenizedPlaylist()
    {
        var html = """
            <script>
              window.video = { id: 712, filename: 'episode.mp4' };
              window.masterPlaylist = {
                url: '/playlist/712?b=1',
                params: { token: "abc", expires: "12345" },
              };
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Vixcloud",
                    new Uri(
                        "https://vixcloud.co/embed/712?canPlayFHD=1"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(
            "https://vixcloud.co/playlist/712" +
            "?token=abc&expires=12345&b=1&h=1",
            source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task VixcloudScopesPlaylistParametersAndMatchesExactQueryNames()
    {
        var html = """
            <script>
              const ad = { token: "wrong", expires: "wrong", url: "?b=1" };
              window.video = { id: 713, filename: 'episode.mp4' };
              window.masterPlaylist = {
                url: '/playlist/713',
                params: { token: "right", expires: "67890" },
              };
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Vixcloud",
                    new Uri(
                        "https://vixcloud.co/embed/713" +
                        "?notcanPlayFHDvalue=1"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(
            "https://vixcloud.co/playlist/713?token=right&expires=67890",
            source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task FrembedHandsOffToRegisteredHoster()
    {
        const string media =
            "https://cdn.yucache.net/videos/example.mp4";
        var handler = new StaticHandler(
            request =>
            {
                if (request.RequestUri!.IdnHost.Contains(
                        "frembed",
                        StringComparison.OrdinalIgnoreCase))
                {
                    return JsonResponse(
                        """{"link1":"https://www.yourupload.com/embed/example"}""");
                }

                return HtmlResponse(
                    $$"""<script>const jwplayerOptions={file:'{{media}}'};</script>""");
            });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Frembed",
                    new Uri("https://frembed.click/api/films?id=1"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task FrembedPrefersNumberedLinkForRegisteredHoster()
    {
        const string media =
            "https://cdn.yucache.net/videos/preferred.mp4";
        var handler = new StaticHandler(
            request =>
            {
                if (request.RequestUri!.IdnHost == "frembed.click")
                {
                    return JsonResponse(
                        """
                        {
                          "linkPreview":"https://ads.example/player",
                          "link1":"https://ads.example/landing",
                          "link2":"https://www.yourupload.com/embed/preferred"
                        }
                        """);
                }

                return HtmlResponse(
                    $$"""<script>const player={file:"{{media}}"};</script>""");
            });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Frembed",
                    new Uri("https://frembed.click/api/films?id=2"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task FilemoonCompletesAttestationAndDecryptsPlayback()
    {
        var handler = new FilemoonHandler();
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Filemoon",
                    new Uri("https://filemoon.site/e/abc123"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(
            FilemoonHandler.MediaUri,
            source.PlaybackUri.AbsoluteUri);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Method == HttpMethod.Post &&
                request.RequestUri!.AbsolutePath.EndsWith(
                    "/access/attest",
                    StringComparison.Ordinal));
    }

    [Fact]
    public async Task FrembedRejectsOversizedJsonResponse()
    {
        const int limit = 1024 * 1024;
        var handler = new OversizedStreamingHandler(4 * limit);
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Frembed",
            new Uri("https://frembed.click/api/films?id=oversized"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.InRange(handler.Stream.BytesRead, limit + 1, limit + (16 * 1024));
    }

    [Fact]
    public async Task FilemoonRejectsOversizedJsonResponse()
    {
        const int limit = 2 * 1024 * 1024;
        var handler = new OversizedStreamingHandler(2 * limit);
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Filemoon",
            new Uri("https://filemoon.site/e/oversized"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.InRange(handler.Stream.BytesRead, limit + 1, limit + (16 * 1024));
    }

    [Fact]
    public async Task DisabledTvLibreExtractorStillParsesBoundedNativeDashFeed()
    {
        const string channel = "Canal1";
        var channelBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(channel));
        const string keyId = "00112233445566778899aabbccddeeff";
        const string key = "ffeeddccbbaa99887766554433221100";
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri!.AbsolutePath == "/html/cv.json")
            {
                return JsonResponse(
                    """{"urls":["player.example.com"]}""");
            }

            return HtmlResponse(
                $$"""
                <script>
                if (getURL == "{{channelBase64}}") number = 5;
                getURL == "{{channelBase64}}"
                {
                    keyId = "{{keyId}}";
                    key = "{{key}}";
                }
                </script>
                """);
        });
        using var httpClient = new HttpClient(handler);
        var extractor = new TvLibrePlayerHosterExtractor();
        var source = new ApiContentVideoSource(
            "TV",
            new Uri(
                "https://tvlibreonline.tv/html/fl/?get=" +
                Uri.EscapeDataString(channelBase64)),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await extractor.TryResolveAsync(
            source,
            httpClient,
            CancellationToken.None);

        Assert.NotNull(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, resolved.Kind);
        Assert.Equal(
            "https://cdn.cvattv.com.ar/live/c5eds/Canal1/" +
            "SA_Live_dash_enc_C/Canal1.mpd",
            resolved.PlaybackUri.AbsoluteUri);
        var decryptionKey = Assert.Single(resolved.DecryptionKeys);
        Assert.Equal(keyId, decryptionKey.KeyId);
        Assert.Equal(key, decryptionKey.Key);
    }

    [Fact]
    public async Task DisabledTvLibreExtractorRejectsOversizedServerList()
    {
        const int limit = 256 * 1024;
        var handler = new OversizedStreamingHandler(4 * limit);
        using var httpClient = new HttpClient(handler);
        var extractor = new TvLibrePlayerHosterExtractor();
        var channelBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("Canal1"));
        var source = new ApiContentVideoSource(
            "TV",
            new Uri(
                "https://tvlibreonline.tv/html/fl/?get=" +
                Uri.EscapeDataString(channelBase64)),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await extractor.TryResolveAsync(
            source,
            httpClient,
            CancellationToken.None);

        Assert.Null(resolved);
        Assert.InRange(handler.Stream.BytesRead, limit + 1, limit + (16 * 1024));
    }

    [Fact]
    public async Task DisabledTvLibreExtractorPropagatesCallerCancellation()
    {
        using var httpClient = new HttpClient(new CancellationHandler());
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var extractor = new TvLibrePlayerHosterExtractor();
        var channelBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes("Canal1"));
        var source = new ApiContentVideoSource(
            "TV",
            new Uri(
                "https://tvlibreonline.tv/html/fl/?get=" +
                Uri.EscapeDataString(channelBase64)),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await extractor.TryResolveAsync(
                source,
                httpClient,
                cancellation.Token));
    }

    [Fact]
    public async Task LeavesUnknownHostersAsEmbeddedWebSources()
    {
        using var httpClient = new HttpClient(new StaticHandler(_ => EmptyResponse()));
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Streamwish",
            new Uri("https://streamwish.to/e/abcdef"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.EmbeddedWeb, only.Kind);
        Assert.Equal("https://streamwish.to/e/abcdef", only.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task LeavesDirectMediaSourcesUntouched()
    {
        using var httpClient = new HttpClient(new StaticHandler(_ => EmptyResponse()));
        var resolver = new VideoHosterResolver(httpClient);
        var direct = new ApiContentVideoSource(
            "Direct",
            new Uri("https://cdn.example.com/stream.m3u8"),
            mimeType: "application/vnd.apple.mpegurl",
            referrer: null,
            ApiContentVideoSourceKind.DirectMedia);

        var resolved = await resolver.ResolveAsync([direct]);

        var only = Assert.Single(resolved);
        Assert.Same(direct, only);
    }

    [Fact]
    public async Task PremiumVipDecodesIronHentaiMp4Endpoint()
    {
        var mediaUrl =
            "https://re.ironhentai.com/hugging.php?id=episode-3";
        var html = BuildIronHentaiPage(
            $"var videoId = '{mediaUrl}'; video.src = videoId;");
        var handler = new StaticHandler(
            _ => HtmlResponse(html),
            hostFilter: "ironhentai.com");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "PremiunVIP",
            new Uri("https://re.ironhentai.com/face.php?id=episode-3"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(mediaUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", only.MimeType);
        Assert.Equal(source.PlaybackUri, only.Referrer);
    }

    [Fact]
    public async Task PlusTubeDecodesIronHentaiHlsManifest()
    {
        var mediaUrl =
            "https://str12.vtube.network/hls/episode/master.m3u8";
        var html = BuildIronHentaiPage(
            $"hls.loadSource('{mediaUrl}');");
        var handler = new StaticHandler(
            _ => HtmlResponse(html),
            hostFilter: "ironhentai.com");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "PlusTube",
            new Uri("https://re.ironhentai.com/vt.php?id=episode-3"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(mediaUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
        Assert.Equal(source.PlaybackUri, only.Referrer);
    }

    [Fact]
    public async Task VoeHosterResolvesHlsManifest()
    {
        var hlsUrl = "https://hls.voe-network.net/stream/abc/master.m3u8";
        var html = $$"""
            <html><head><title>VOE</title></head>
            <body>
              <script>
                var player = { 'hls': '{{hlsUrl}}' };
              </script>
            </body></html>
            """;
        var handler = new StaticHandler(
            request => HtmlResponse(html),
            hostFilter: "voe.sx");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "VOE",
            new Uri("https://voe.sx/e/abcdef"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(hlsUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
    }

    [Fact]
    public async Task VoeHosterDecodesBase64Hls()
    {
        var hlsUrl = "https://hls.voe-network.net/encoded/master.m3u8";
        var encoded = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(hlsUrl));
        var html = $$"""
            <html><body>
            <script>var data = { 'hls': '{{encoded}}' };</script>
            </body></html>
            """;
        var handler = new StaticHandler(
            request => HtmlResponse(html),
            hostFilter: "voe.sx");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "VOE",
            new Uri("https://voe.sx/e/abcdef"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(hlsUrl, only.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task VoexFollowsRotatingDomainAndDecodesCurrentPayload()
    {
        var mediaUrl =
            "https://voe-cdn.example.com/engine/master.m3u8?token=abc";
        var encoded = BuildVoePayload(mediaUrl);
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri?.IdnHost == "voe.sx")
            {
                return HtmlResponse(
                    """
                    <script>
                    window.location.href =
                        'https://matthewhotelscience.com/e/episode3';
                    </script>
                    """);
            }

            return HtmlResponse(
                $$"""
                <script type="application/json">["{{encoded}}"]</script>
                """);
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Voex",
            new Uri("https://voe.sx/e/episode3"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(mediaUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
        Assert.Equal(source.PlaybackUri, only.Referrer);
    }

    [Fact]
    public async Task StreamtapeHosterBuildsGetVideoUrl()
    {
        var html = """
            <html><body>
            <script>
              var video = '/get_video?id=abc123&expires=1700000000&ip=1.2.3.4&token=deadbeef';
            </script>
            </body></html>
            """;
        var handler = new StaticHandler(
            request => HtmlResponse(html),
            hostFilter: "streamtape.com");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Streamtape",
            new Uri("https://streamtape.com/v/abc123/abc"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(
            "https://streamtape.com/get_video?id=abc123&expires=1700000000&ip=1.2.3.4&token=deadbeef",
            only.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task Mp4UploadReadsVideoJsSource()
    {
        var mediaUrl =
            "https://a3.mp4upload.com:183/d/episode/video.mp4";
        var html = $$"""
            <html><body><script>
            player.src({
                type: "video/mp4",
                src: "{{mediaUrl}}"
            });
            </script></body></html>
            """;
        var handler = new StaticHandler(
            _ => HtmlResponse(html),
            hostFilter: "mp4upload.com");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Mp4Upload",
            new Uri("https://www.mp4upload.com/embed-episode.html"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(mediaUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", only.MimeType);
        Assert.Equal(source.PlaybackUri, only.Referrer);
    }

    [Fact]
    public async Task DoodstreamHosterFollowsPassMd5AndAppendsToken()
    {
        var passResponse = "https://d0000d.com/stream/abcdef";
        var handler = new DoodstreamHandler(passResponse);
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Doodstream",
            new Uri("https://doodstream.com/d/abcdef"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.StartsWith(
            "https://d0000d.com/stream/abcdef",
            only.PlaybackUri.AbsoluteUri);
        Assert.Contains("?token=passToken&expiry=", only.PlaybackUri.Query);

        // Redeemed where it was minted. The token belongs to whichever mirror
        // served the embed page, so a pinned origin sent every request to a
        // host that had never issued it — which is what a rotation to a new
        // mirror broke in the field.
        var pass = Assert.Single(
            handler.Requests,
            uri => uri.AbsolutePath.StartsWith(
                "/pass_md5",
                StringComparison.Ordinal));
        Assert.Equal("doodstream.com", pass.Host);
        Assert.Equal(
            new Uri("https://doodstream.com/"),
            only.Referrer);
    }

    [Fact]
    public async Task DoodstreamRejectsPrivatePassMd5Result()
    {
        var handler = new DoodstreamHandler(
            "http://192.168.1.25/private/video/");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Doodstream",
            new Uri("https://doodstream.com/d/private"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.Equal(2, handler.Requests.Count);
    }

    [Fact]
    public async Task VidmolyHosterParsesSourcesFileRegex()
    {
        var url = "https://vidmoly.example.com/stream/abc.m3u8";
        var html = $$"""
            <html><body>
            <script>
            jwplayer("v").setup({
                sources: [{file:"{{url}}"}]
            });
            </script>
            </body></html>
            """;
        var handler = new StaticHandler(
            request => HtmlResponse(html),
            hostFilter: "vidmoly.to");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Vidmoly",
            new Uri("https://vidmoly.to/embed/abc"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(url, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
        Assert.Equal(
            new Uri("https://vidmoly.to/"),
            only.Referrer);
    }

    [Fact]
    public async Task DoesNotMatchAHostNameThatOnlyEndsWithTheHosterName()
    {
        using var httpClient = new HttpClient(
            new StaticHandler(_ =>
                throw new InvalidOperationException(
                    "A suffix-only host must not be requested.")));
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Fake VOE",
            new Uri("https://evilvoe.sx/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
    }

    [Fact]
    public async Task VidozaHosterReadsSourceTag()
    {
        var url = "https://vidoza.example.com/stream.mp4";
        var html = $$"""
            <html><body>
              <video>
                <source src="{{url}}" type="video/mp4" />
              </video>
            </body></html>
            """;
        var handler = new StaticHandler(
            request => HtmlResponse(html),
            hostFilter: "vidoza.net");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Vidoza",
            new Uri("https://vidoza.net/v/abc"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(url, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", only.MimeType);
    }

    [Fact]
    public async Task VidozaDecodesHtmlEntitiesInSourceUrl()
    {
        const string encoded =
            "https://vidoza.example.com/stream.mp4?token=abc&amp;part=2";
        const string expected =
            "https://vidoza.example.com/stream.mp4?token=abc&part=2";
        using var httpClient = new HttpClient(
            new StaticHandler(
                _ => HtmlResponse(
                    $$"""<video><source src="{{encoded}}"></video>"""),
                hostFilter: "vidoza.net"));
        var resolver = new VideoHosterResolver(httpClient);

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Vidoza",
                    new Uri("https://vidoza.net/v/entities"),
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        Assert.Equal(expected, Assert.Single(resolved).PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task LoadXHosterPostsForVideoSource()
    {
        var videoSource = "https://loadx.example.com/stream.mp4";
        var handler = new LoadXHandler(videoSource);
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "LoadX",
            new Uri("https://loadx.ws/abc123"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(videoSource, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("video/mp4", only.MimeType);
    }

    [Fact]
    public async Task LoadXRejectsOversizedJsonResponse()
    {
        const int limit = 1024 * 1024;
        var handler = new OversizedStreamingHandler(4 * limit);
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "LoadX",
            new Uri("https://loadx.ws/oversized"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
        Assert.InRange(handler.Stream.BytesRead, limit + 1, limit + (16 * 1024));
    }

    [Fact]
    public void SpeedFilesDecoderRoundTripsTheReferencePipeline()
    {
        // The pipeline is mirror-image per series-scraper: base64 →
        // swapcase → reverse → base64 → reverse → hex-pair → char-3 →
        // swapcase → reverse → base64. We build a target URL and
        // invert the steps to produce a payload that decodes to it.
        var targetUrl = "https://speedfiles.example.com/stream.mp4";
        var payload = BuildSpeedFilesPayload(targetUrl);

        var resolved = SpeedFilesHosterExtractor.TryDecode(payload);

        Assert.NotNull(resolved);
        Assert.Equal(targetUrl, resolved!.AbsoluteUri);
    }

    [Fact]
    public void SpeedFilesDecoderRejectsOversizedPayload()
    {
        var payload = new string('A', (512 * 1024) + 1);

        Assert.Null(SpeedFilesHosterExtractor.TryDecode(payload));
    }

    [Fact]
    public void JavaScriptPackerRejectsMoreKeywordsThanDeclared()
    {
        const string packed = """
            eval(function(p,a,c,k,e,d){return p}(
              "0",2,1,"first|second".split('|'),0,{}))
            """;

        Assert.False(TryUnpackJavaScript(packed, out var unpacked));
        Assert.Empty(unpacked);
    }

    [Fact]
    public void JavaScriptPackerRejectsOutputExpansionBeyondLimit()
    {
        var payload = string.Join(' ', Enumerable.Repeat("0", 129));
        var keyword = new string('x', 64 * 1024);
        var packed = $$"""
            eval(function(p,a,c,k,e,d){return p}(
              "{{payload}}",2,1,"{{keyword}}".split('|'),0,{}))
            """;

        Assert.False(TryUnpackJavaScript(packed, out var unpacked));
        Assert.Empty(unpacked);
    }

    [Fact]
    public async Task SpeedFilesHosterResolvesThroughRealPage()
    {
        var targetUrl = "https://speedfiles.example.com/stream.mp4";
        var payload = BuildSpeedFilesPayload(targetUrl);
        var html = $$"""
            <html><body>
              <script>var _0x5opu234 = "{{payload}}";</script>
            </body></html>
            """;
        var handler = new StaticHandler(
            request => HtmlResponse(html),
            hostFilter: "speedfiles.net");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "SpeedFiles",
            new Uri("https://speedfiles.net/v/abc"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(targetUrl, only.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task NuploadHosterDecodesObfuscatedHls()
    {
        var targetUrl = "https://media.nupload.example/video/master.m3u8";
        var html = BuildNuploadPage(targetUrl);
        // The decoded address is now requested as well, to follow the redirect
        // the hoster answers it with, so the media host has to reply too.
        var handler = new StaticHandler(request =>
            request.RequestUri!.Host == "nupload.top"
                ? HtmlResponse(html)
                : new HttpResponseMessage(HttpStatusCode.OK));
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Option 1 - Latino",
            new Uri(
                "https://nupload.top/watch/" +
                "masters-of-the-universe-latino"),
            mimeType: null,
            referrer: new Uri(
                "https://pelisplus.autos/pelicula/amos-del-universo/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(targetUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
        Assert.Equal("https://nupload.top/", only.Referrer?.AbsoluteUri);
        Assert.Equal("https://nupload.top/", only.HttpOrigin?.AbsoluteUri);
    }

    /// <summary>
    /// The page encodes the origin plus a signed query, and that answers 301
    /// with the playlist. Returning the redirect itself looked like a success
    /// but gave libVLC a URL it does not follow, so playback simply timed out
    /// and the server was reported as unreachable.
    /// </summary>
    [Fact]
    public async Task NuploadHosterFollowsTheRedirectToThePlaylist()
    {
        const string redirectStub =
            "https://sv2.ibra.lat/?s=EKCsvDViaPelhNaJTetnCaejVrVShozW";
        const string playlistUrl =
            "https://sv2.ibra.lat/aa/af/aaf9e8a659e.m3u8";
        var html = BuildNuploadPage(redirectStub);
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri!.Host == "nupload.top")
            {
                return HtmlResponse(html);
            }

            if (request.RequestUri.AbsoluteUri == redirectStub)
            {
                Assert.Equal(
                    "https://nupload.top/",
                    request.Headers.Referrer?.AbsoluteUri);
                Assert.Equal(
                    "https://nupload.top",
                    request.Headers.GetValues("Origin").Single());
                Assert.Contains(
                    "empty",
                    request.Headers.GetValues("Sec-Fetch-Dest"));
                Assert.Contains(
                    "cors",
                    request.Headers.GetValues("Sec-Fetch-Mode"));
                Assert.Contains(
                    "cross-site",
                    request.Headers.GetValues("Sec-Fetch-Site"));
                var redirect = new HttpResponseMessage(
                    HttpStatusCode.MovedPermanently);
                redirect.Headers.Location = new Uri(playlistUrl);
                return redirect;
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "nupload.top 1",
            new Uri("https://nupload.top/watch/Ugtoze7kz7p5mpzuH7kz7TuOoIL"),
            mimeType: null,
            referrer: new Uri("https://seriesflixhd.ink/episodio/wrath-1x1/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(playlistUrl, only.PlaybackUri.AbsoluteUri);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
        Assert.Equal("https://nupload.top/", only.Referrer?.AbsoluteUri);
        Assert.Equal("https://nupload.top/", only.HttpOrigin?.AbsoluteUri);
    }

    [Fact]
    public async Task NuploadHosterKeepsWebFallbackForMissingMediaSession()
    {
        const string redirectStub =
            "https://sv3.ibra.lat/?s=expired-session";
        var html = BuildNuploadPage(redirectStub);
        var requestCount = 0;
        var handler = new StaticHandler(request =>
        {
            requestCount++;
            return request.RequestUri!.Host == "nupload.top"
                ? HtmlResponse(html)
                : new HttpResponseMessage(HttpStatusCode.NotFound);
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "nupload.top 1",
            new Uri("https://nupload.top/watch/expired-session"),
            mimeType: null,
            referrer: new Uri("https://seriesflixhd.ink/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var first = await resolver.ResolveAsync([source]);
        var second = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(first));
        Assert.Same(source, Assert.Single(second));
        Assert.Equal(4, requestCount);
    }

    [Fact]
    public async Task NuploadHosterKeepsWebFallbackWhenMediaProbeTimesOut()
    {
        const string redirectStub =
            "https://sv3.ibra.lat/?s=slow-session";
        var html = BuildNuploadPage(redirectStub);
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri!.Host == "nupload.top")
            {
                return HtmlResponse(html);
            }

            throw new TaskCanceledException("The test media probe timed out.");
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "nupload.top 1",
            new Uri("https://nupload.top/watch/slow-session"),
            mimeType: null,
            referrer: new Uri("https://seriesflixhd.ink/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        Assert.Same(source, Assert.Single(resolved));
    }

    [Fact]
    public async Task NuploadHosterDoesNotCacheSignedPlaylist()
    {
        var pageRequestCount = 0;
        var mediaRootRequestCount = 0;
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri!.Host == "nupload.top")
            {
                pageRequestCount++;
                return HtmlResponse(
                    BuildNuploadPage(
                        $"https://sv2.ibra.lat/?generation={pageRequestCount}"));
            }

            if (request.RequestUri.AbsolutePath == "/")
            {
                mediaRootRequestCount++;
                return RedirectResponse(
                    $"https://sv2.ibra.lat/aa/video-" +
                    $"{mediaRootRequestCount}.m3u8");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "nupload.top 1",
            new Uri("https://nupload.top/watch/fresh-each-time"),
            mimeType: null,
            referrer: new Uri("https://seriesflixhd.ink/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var first = Assert.Single(await resolver.ResolveAsync([source]));
        var second = Assert.Single(await resolver.ResolveAsync([source]));

        Assert.NotEqual(first.PlaybackUri, second.PlaybackUri);
        Assert.Equal(2, pageRequestCount);
        Assert.Equal(2, mediaRootRequestCount);
    }

    [Fact]
    public async Task NuploadHosterRefreshesAStalePlaylistAtPlaybackTime()
    {
        var pageRequestCount = 0;
        var mediaRootRequestCount = 0;
        var handler = new StaticHandler(request =>
        {
            if (request.RequestUri!.Host == "nupload.top")
            {
                pageRequestCount++;
                return HtmlResponse(
                    BuildNuploadPage(
                        $"https://sv2.ibra.lat/?generation={pageRequestCount}"));
            }

            if (request.RequestUri.AbsolutePath == "/")
            {
                mediaRootRequestCount++;
                return RedirectResponse(
                    $"https://sv2.ibra.lat/aa/refresh-" +
                    $"{mediaRootRequestCount}.m3u8");
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        using var httpClient = new HttpClient(handler);
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                30,
                12,
                0,
                0,
                TimeSpan.Zero));
        var resolver = new VideoHosterResolver(
            httpClient,
            timeProvider: timeProvider);
        var original = new ApiContentVideoSource(
            "nupload.top",
            new Uri("https://nupload.top/watch/refresh-at-play"),
            mimeType: null,
            referrer: new Uri("https://seriesflixhd.ink/"),
            ApiContentVideoSourceKind.EmbeddedWeb);
        var initiallyResolved = Assert.Single(
            await resolver.ResolveAsync([original]));
        var providerRelabelled = new ApiContentVideoSource(
            "nupload.top 1",
            initiallyResolved.PlaybackUri,
            initiallyResolved.MimeType,
            initiallyResolved.Referrer,
            initiallyResolved.Kind,
            initiallyResolved.DecryptionKeys,
            initiallyResolved.HttpOrigin);

        var stillFresh = await resolver.RefreshAsync(providerRelabelled);

        Assert.Same(providerRelabelled, stillFresh);
        Assert.Equal(1, pageRequestCount);

        timeProvider.Advance(TimeSpan.FromMinutes(1));
        var refreshed = await resolver.RefreshAsync(providerRelabelled);

        Assert.NotNull(refreshed);
        Assert.Equal("nupload.top 1", refreshed.Label);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, refreshed.Kind);
        Assert.NotEqual(
            providerRelabelled.PlaybackUri,
            refreshed.PlaybackUri);
        Assert.Equal(2, pageRequestCount);
        Assert.Equal(2, mediaRootRequestCount);
    }

    [Fact]
    public async Task NuploadHosterUsesIframeHeadersAndAppendsSession()
    {
        const string mediaOrigin = "https://sv5.ibra.lat/";
        const string session =
            "hHtevYJzEVgUG99YKDPdgiD81FljFmLX4ebzcV9RRpLxyVwjzlwPEaYGQjZwGKtKYFk";
        var html = BuildNuploadPage(mediaOrigin, session);
        var handler = new StaticHandler(
            request =>
            {
                if (request.RequestUri!.Host != "nupload.top")
                {
                    Assert.Equal(
                        "https://nupload.top/",
                        request.Headers.Referrer?.AbsoluteUri);
                    Assert.Equal(
                        "https://nupload.top",
                        request.Headers.GetValues("Origin").Single());
                    Assert.Contains(
                        "empty",
                        request.Headers.GetValues("Sec-Fetch-Dest"));
                    Assert.Contains(
                        "cors",
                        request.Headers.GetValues("Sec-Fetch-Mode"));
                    Assert.Contains(
                        "cross-site",
                        request.Headers.GetValues("Sec-Fetch-Site"));
                    return new HttpResponseMessage(HttpStatusCode.OK);
                }

                Assert.Contains(
                    "iframe",
                    request.Headers.GetValues("Sec-Fetch-Dest"));
                Assert.Contains(
                    "navigate",
                    request.Headers.GetValues("Sec-Fetch-Mode"));
                Assert.Contains(
                    "cross-site",
                    request.Headers.GetValues("Sec-Fetch-Site"));
                Assert.Contains(
                    request.Headers.AcceptLanguage,
                    language => language.Value == "es-AR");
                return HtmlResponse(html);
            });
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "Option 1 - Latino",
            new Uri(
                "https://nupload.top/watch/" +
                "zuxPHra3MXHulxgiRTBfO4hIEvg5ohy3jz3fKLeiME7kz7zZs"),
            mimeType: null,
            referrer: new Uri(
                "https://pelisplus.autos/pelicula/amos-del-universo/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, only.Kind);
        Assert.Equal(
            mediaOrigin + "?s=" + session,
            only.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", only.MimeType);
        Assert.Equal("https://nupload.top/", only.Referrer?.AbsoluteUri);
        Assert.Equal("https://nupload.top/", only.HttpOrigin?.AbsoluteUri);
    }

    [Fact]
    public void NuploadDecoderRejectsUnsafeMediaUrl()
    {
        var html = BuildNuploadPage(
            "http://127.0.0.1/private/master.m3u8");

        Assert.Null(NuploadHosterExtractor.TryDecode(html));
    }
    [Fact]
    public async Task ResolverCachesResolvedSource()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "VOE",
            new Uri("https://voe.sx/e/abcdef"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var first = await resolver.ResolveAsync([source]);
        var second = await resolver.ResolveAsync([source]);

        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, Assert.Single(first).Kind);
        Assert.Equal(Assert.Single(first).PlaybackUri, Assert.Single(second).PlaybackUri);
        Assert.True(
            handler.RequestCount <= 2,
            "The resolver should not refetch the hoster page on the second call.");
    }

    [Fact]
    public async Task ResolverBoundsOneOffResolvedSources()
    {
        const int cacheCapacity = 256;
        var extractor = new CountingExtractor();
        using var httpClient = new HttpClient(
            new StaticHandler(_ => EmptyResponse()));
        var resolver = new VideoHosterResolver(
            httpClient,
            [extractor],
            new AdjustableTimeProvider(
                new DateTimeOffset(
                    2026,
                    7,
                    26,
                    0,
                    0,
                    0,
                    TimeSpan.Zero)));
        var sources = Enumerable.Range(0, cacheCapacity + 1)
            .Select(index => new ApiContentVideoSource(
                "Test",
                new Uri($"https://video.example.com/embed/{index}"),
                kind: ApiContentVideoSourceKind.EmbeddedWeb))
            .ToArray();

        foreach (var source in sources)
        {
            await resolver.ResolveAsync([source]);
        }

        await resolver.ResolveAsync([sources[0]]);
        await resolver.ResolveAsync([sources[^1]]);

        Assert.Equal(cacheCapacity + 2, extractor.ResolveCount);
    }

    [Fact]
    public async Task ResolverRefetchesExpiringDirectSources()
    {
        var handler = new CountingHandler();
        using var httpClient = new HttpClient(handler);
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                25,
                12,
                0,
                0,
                TimeSpan.Zero));
        var resolver = new VideoHosterResolver(
            httpClient,
            timeProvider: timeProvider);
        var source = new ApiContentVideoSource(
            "VOE",
            new Uri("https://voe.sx/e/abcdef"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        await resolver.ResolveAsync([source]);
        await resolver.ResolveAsync([source]);
        Assert.Equal(1, handler.RequestCount);

        timeProvider.Advance(TimeSpan.FromMinutes(6));
        await resolver.ResolveAsync([source]);

        Assert.Equal(2, handler.RequestCount);
    }

    [Fact]
    public async Task ResolverKeepsEmbeddedFallbackWhenExtractorReturnsNull()
    {
        var handler = new StaticHandler(
            _ => HtmlResponse("<html><body>no hls here</body></html>"),
            hostFilter: "voe.sx");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var source = new ApiContentVideoSource(
            "VOE",
            new Uri("https://voe.sx/e/abcdef"),
            mimeType: null,
            referrer: new Uri("https://example.com/"),
            ApiContentVideoSourceKind.EmbeddedWeb);

        var resolved = await resolver.ResolveAsync([source]);

        var only = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.EmbeddedWeb, only.Kind);
        Assert.Equal("https://voe.sx/e/abcdef", only.PlaybackUri.AbsoluteUri);
    }

    private static string BuildSpeedFilesPayload(string targetUrl)
    {
        // Reverse the series-scraper pipeline so the payload decodes to
        // the target URL via SpeedFilesHosterExtractor.TryDecode. The
        // decoder does base64 → swapcase → reverse → base64 → reverse
        // → hex-pair → char-3 → swapcase → reverse → base64; we walk
        // those steps backwards here.
        var s1 = Convert.ToBase64String(Encoding.UTF8.GetBytes(targetUrl));
        var s2 = new string(s1.Reverse().ToArray());
        var s3 = SwapCase(s2);
        var s4 = new string(s3.Select(c => (char)(c + 3)).ToArray());
        var s5 = string.Concat(s4.Select(c => ((int)c).ToString("X2")));
        var s6 = new string(s5.Reverse().ToArray());
        var s7 = Convert.ToBase64String(Encoding.UTF8.GetBytes(s6));
        var s8 = new string(s7.Reverse().ToArray());
        var s9 = SwapCase(s8);
        return Convert.ToBase64String(Encoding.UTF8.GetBytes(s9));
    }

    private static bool TryUnpackJavaScript(
        string text,
        out string unpacked)
    {
        var packerType = typeof(HosterFeedMap).Assembly.GetType(
            "UniversalMediaDistribution.Catalog.ContentSources.JavaScriptPacker",
            throwOnError: true)!;
        var method = packerType.GetMethod(
            "TryUnpackFirst",
            BindingFlags.Public | BindingFlags.Static)!;
        object?[] arguments = [text, null];
        var result = Assert.IsType<bool>(method.Invoke(null, arguments));
        unpacked = Assert.IsType<string>(arguments[1]);
        return result;
    }

    private static string BuildNuploadPage(
        string targetUrl,
        string? session = null)
    {
        // This is the same decoder shape and offset size captured in the
        // SeriesFlix Nupload HAR, with the signed values replaced by fixtures.
        const int offset = 9_916_480;
        var values = string.Join(
            ", ",
            targetUrl.Select(character =>
            {
                var decoratedNumber =
                    $"prefix{offset + character}suffix";
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(decoratedNumber));
                return $"\"{encoded}\"";
            }));
        return $$"""
            <html><body>
              <script>var encodedSource = [{{values}}];</script>
              <script>
                var mediaSource = "";
                encodedSource.forEach(function Oym(value) {
                  mediaSource += String.fromCharCode(
                    parseInt(atob(value).replace(/\D/g,'')) - {{offset}});
                });
                var sesz="{{session ?? string.Empty}}";
                jwplayer("player").setup({
                  file: mediaSource + "?s=" + sesz,
                  type: "hls"
                });
              </script>
            </body></html>
            """;
    }
    private static string BuildIronHentaiPage(string decodedScript)
    {
        var innerBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(decodedScript));
        var shifted = new string(
            innerBase64.Select(character => (char)(character + 1)).ToArray());
        var payload = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(shifted));
        return
            $"<script>eval(atob(atob('{payload}').split('')" +
            ".map(c => String.fromCharCode(c.charCodeAt(0) - 1))" +
            ".join('')));</script>";
    }

    private static string BuildVoePayload(string source)
    {
        var json = $$"""{"source":"{{source}}"}""";
        var reversedBase64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(json))
            .Reverse()
            .Select(character => (char)(character + 3))
            .ToArray();
        var rotated = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(reversedBase64));
        return RotateAsciiLetters(rotated, 13);
    }

    private static string RotateAsciiLetters(string value, int offset)
    {
        var buffer = value.ToCharArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            if (buffer[index] is >= 'A' and <= 'Z')
            {
                buffer[index] = (char)(
                    (buffer[index] - 'A' + offset) % 26 + 'A');
            }
            else if (buffer[index] is >= 'a' and <= 'z')
            {
                buffer[index] = (char)(
                    (buffer[index] - 'a' + offset) % 26 + 'a');
            }
        }

        return new string(buffer);
    }

    private static string SwapCase(string value)
    {
        var buffer = value.ToCharArray();
        for (var index = 0; index < buffer.Length; index++)
        {
            buffer[index] = char.IsUpper(buffer[index])
                ? char.ToLowerInvariant(buffer[index])
                : char.IsLower(buffer[index])
                    ? char.ToUpperInvariant(buffer[index])
                    : buffer[index];
        }

        return new string(buffer);
    }

    private static HttpResponseMessage EmptyResponse() =>
        new(HttpStatusCode.NotFound);

    private static HttpResponseMessage HtmlResponse(string html) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                html,
                Encoding.UTF8,
                "text/html")
        };

    private static HttpResponseMessage JsonResponse(string json) =>
        new(HttpStatusCode.OK)
        {
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };

    private static HttpResponseMessage RedirectResponse(string location) =>
        new(HttpStatusCode.Found)
        {
            Headers =
            {
                Location = new Uri(location)
            }
        };

    [Fact]
    public async Task APackedPlayerOnAnUnknownMirrorIsStillResolved()
    {
        // What a domain rotation actually looks like: the host appears in no
        // pinned list, so nothing claims the URL, but the player behind it is
        // unchanged and still identifies the hoster.
        const string media = "https://cdn.example/video/rotated.mp4";
        var html = $$"""
            <script>
            eval(function(p,a,c,k,e,d){return p}(
              "0:'1'",2,2,"file|{{media}}".split('|'),0,{}))
            </script>
            """;
        using var httpClient = new HttpClient(
            new StaticHandler(_ => HtmlResponse(html)));
        var resolver = new VideoHosterResolver(httpClient);
        var rotated = new Uri("https://brand-new-mirror.example/e/abc");
        Assert.DoesNotContain(
            HosterFeedMap.Default(),
            extractor => extractor.Matches(rotated));

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Rotated",
                    rotated,
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(media, source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task ADoodstreamMirrorIsRecognisedByItsTokenRoute()
    {
        // Doodstream carries no player in the page, so the packed-player
        // signature cannot find it. Its token route is the signature instead.
        var handler = new DoodstreamHandler("https://cdn.example/stream/abc");
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(httpClient);
        var rotated = new Uri("https://unlisted-dood.example/e/abc");
        Assert.DoesNotContain(
            HosterFeedMap.Default(),
            extractor => extractor.Matches(rotated));

        var resolved = await resolver.ResolveAsync(
            [
                new ApiContentVideoSource(
                    "Dood",
                    rotated,
                    kind: ApiContentVideoSourceKind.EmbeddedWeb)
            ]);

        var source = Assert.Single(resolved);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.StartsWith(
            "https://cdn.example/stream/abc",
            source.PlaybackUri.AbsoluteUri);

        // Redeemed on the mirror that served the page, not on a pinned origin.
        Assert.Equal(
            "unlisted-dood.example",
            Assert.Single(
                handler.Requests,
                uri => uri.AbsolutePath.StartsWith(
                    "/pass_md5",
                    StringComparison.Ordinal)).Host);
    }

    [Fact]
    public async Task APageNothingRecognisesIsLeftAloneAndNotReadTwice()
    {
        // The fallback must not become a read of every unresolvable source on
        // every refresh.
        var reads = 0;
        using var httpClient = new HttpClient(
            new StaticHandler(_ =>
            {
                Interlocked.Increment(ref reads);
                return HtmlResponse("<html><body>a player we cannot read</body></html>");
            }));
        var resolver = new VideoHosterResolver(httpClient);
        var unknown = new ApiContentVideoSource(
            "Unknown",
            new Uri("https://unknown-player.example/e/abc"),
            kind: ApiContentVideoSourceKind.EmbeddedWeb);

        var first = await resolver.ResolveAsync([unknown]);
        var second = await resolver.ResolveAsync([unknown]);

        Assert.Same(unknown, Assert.Single(first));
        Assert.Same(unknown, Assert.Single(second));
        Assert.Equal(1, reads);
    }

    private sealed class StaticHandler : HttpMessageHandler
    {
        private readonly Func<HttpRequestMessage, HttpResponseMessage> _responder;
        private readonly string? _hostFilter;

        public StaticHandler(
            Func<HttpRequestMessage, HttpResponseMessage> responder,
            string? hostFilter = null)
        {
            _responder = responder;
            _hostFilter = hostFilter;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (_hostFilter is not null &&
                request.RequestUri is { } uri &&
                !uri.IdnHost.EndsWith(_hostFilter, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Unexpected host: {uri.Host}");
            }

            return Task.FromResult(_responder(request));
        }
    }

    private sealed class CancellationHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("Unreachable.");
        }
    }

    private sealed class OversizedStreamingHandler : HttpMessageHandler
    {
        public OversizedStreamingHandler(int length)
        {
            Stream = new RepeatingReadStream(length);
        }

        public RepeatingReadStream Stream { get; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StreamContent(Stream)
                });
    }

    private sealed class RepeatingReadStream(long length) : Stream
    {
        private long _remaining = length;

        public long BytesRead { get; private set; }

        public override bool CanRead => true;

        public override bool CanSeek => false;

        public override bool CanWrite => false;

        public override long Length => throw new NotSupportedException();

        public override long Position
        {
            get => throw new NotSupportedException();
            set => throw new NotSupportedException();
        }

        public override int Read(byte[] buffer, int offset, int count) =>
            Read(buffer.AsSpan(offset, count));

        public override int Read(Span<byte> buffer)
        {
            var count = (int)Math.Min(buffer.Length, _remaining);
            if (count == 0)
            {
                return 0;
            }

            buffer[..count].Fill((byte)'x');
            _remaining -= count;
            BytesRead += count;
            return count;
        }

        public override ValueTask<int> ReadAsync(
            Memory<byte> buffer,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(Read(buffer.Span));
        }

        public override void Flush()
        {
        }

        public override long Seek(long offset, SeekOrigin origin) =>
            throw new NotSupportedException();

        public override void SetLength(long value) =>
            throw new NotSupportedException();

        public override void Write(
            byte[] buffer,
            int offset,
            int count) =>
            throw new NotSupportedException();
    }

    private sealed class FilemoonHandler : HttpMessageHandler
    {
        public const string MediaUri =
            "https://cdn.filemoon.site/videos/abc123/master.m3u8";
        private readonly string _playbackResponse = BuildPlaybackResponse();

        public ConcurrentBag<HttpRequestMessage> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            var path = request.RequestUri!.AbsolutePath;
            var json = path switch
            {
                "/api/videos/abc123/embed/details" =>
                    """{"embed_frame_url":"https://play.filemoon.site/embed/abc123"}""",
                "/api/videos/access/challenge" =>
                    """{"challenge_id":"challenge-1","nonce":"nonce-1"}""",
                "/api/videos/access/attest" =>
                    """{"token":"token-1","viewer_id":"viewer-1","device_id":"device-1","confidence":0.99}""",
                "/api/videos/abc123/embed/playback" =>
                    _playbackResponse,
                _ => throw new InvalidOperationException(
                    $"Unexpected Filemoon request: {request.Method} {path}")
            };
            return Task.FromResult(JsonResponse(json));
        }

        private static string BuildPlaybackResponse()
        {
            var first = Enumerable.Range(1, 16)
                .Select(static value => (byte)value)
                .ToArray();
            var second = Enumerable.Range(17, 16)
                .Select(static value => (byte)value)
                .ToArray();
            var key = first.Concat(second).ToArray();
            var iv = Enumerable.Range(40, 12)
                .Select(static value => (byte)value)
                .ToArray();
            var clear = Encoding.UTF8.GetBytes(
                $$"""
                {
                  "sources":[
                    {"url":"https://ads.example/tracker"},
                    {"url":"{{MediaUri}}"}
                  ]
                }
                """);
            var cipherText = new byte[clear.Length];
            var tag = new byte[16];
            using (var aes = new AesGcm(key, tag.Length))
            {
                aes.Encrypt(iv, clear, cipherText, tag);
            }

            return $$$"""
                {"playback":{
                  "iv":"{{{Base64Url(iv)}}}",
                  "payload":"{{{Base64Url(cipherText.Concat(tag).ToArray())}}}",
                  "key_parts":["{{{Base64Url(first)}}}","{{{Base64Url(second)}}}"]
                }}
                """;
        }

        private static string Base64Url(byte[] value) =>
            Convert.ToBase64String(value)
                .TrimEnd('=')
                .Replace('+', '-')
                .Replace('/', '_');
    }

    private sealed class DoodstreamHandler : HttpMessageHandler
    {
        private readonly string _passResponse;
        public ConcurrentBag<Uri> Requests { get; } = [];

        public DoodstreamHandler(string passResponse)
        {
            _passResponse = passResponse;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");
            Requests.Add(uri);
            if (uri.AbsolutePath.StartsWith("/pass_md5", StringComparison.Ordinal))
            {
                // The original series-scraper treats the body as the
                // resolved media URL. We do the same: no HTML wrapping.
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(_passResponse, Encoding.UTF8, "text/plain")
                });
            }

            var initialHtml = $$"""
                <html><body>
                  <script>var data = '/pass_md5/abc123/{{"passToken"}}';</script>
                </body></html>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    initialHtml,
                    Encoding.UTF8,
                    "text/html")
            });
        }
    }

    private sealed class LoadXHandler : HttpMessageHandler
    {
        private readonly string _videoSource;

        public LoadXHandler(string videoSource)
        {
            _videoSource = videoSource;
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Post &&
                request.RequestUri is { } uri &&
                uri.AbsolutePath == "/player/index.php")
            {
                var json = $$"""{"videoSource":"{{_videoSource}}"}""";
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    Content = new StringContent(
                        json,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            throw new InvalidOperationException(
                $"Unexpected LoadX request: {request.Method} {request.RequestUri}");
        }
    }

    private sealed class CountingHandler : HttpMessageHandler
    {
        public int RequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            var hlsUrl = "https://hls.voe-network.net/stream/abc/master.m3u8";
            var html = $$"""
                <html><body>
                  <script>var data = { 'hls': '{{hlsUrl}}' };</script>
                </body></html>
                """;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    html,
                    Encoding.UTF8,
                    "text/html")
            });
        }
    }

    private sealed class CountingExtractor : IApiVideoHosterExtractor
    {
        public string Name => "Test";

        public int ResolveCount { get; private set; }

        public bool Matches(Uri playbackUri) =>
            playbackUri.IdnHost == "video.example.com";

        public ValueTask<ApiContentVideoSource?> TryResolveAsync(
            ApiContentVideoSource source,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return ValueTask.FromResult<ApiContentVideoSource?>(
                new ApiContentVideoSource(
                    source.Label,
                    new Uri(
                        $"https://cdn.example.com{source.PlaybackUri.AbsolutePath}.m3u8"),
                    mimeType: "application/vnd.apple.mpegurl",
                    referrer: source.Referrer,
                    ApiContentVideoSourceKind.DirectMedia));
        }
    }

    private sealed class PublicUriProbeExtractor : HosterExtractorBase
    {
        public override string Name => "Public URI probe";

        public int ResolveCount { get; private set; }

        public static bool IsSafe(Uri uri) => IsSafePublicHttpUri(uri);

        public override bool Matches(Uri playbackUri) => true;

        public override ValueTask<ApiContentVideoSource?> TryResolveAsync(
            ApiContentVideoSource source,
            HttpClient httpClient,
            CancellationToken cancellationToken)
        {
            ResolveCount++;
            return ValueTask.FromResult<ApiContentVideoSource?>(null);
        }
    }

    private sealed class UnsafeResultExtractor : IApiVideoHosterExtractor
    {
        public string Name => "Unsafe result";

        public bool Matches(Uri playbackUri) => true;

        public ValueTask<ApiContentVideoSource?> TryResolveAsync(
            ApiContentVideoSource source,
            HttpClient httpClient,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ApiContentVideoSource?>(
                new ApiContentVideoSource(
                    source.Label,
                    new Uri("http://192.168.1.25/private/video.m3u8"),
                    kind: ApiContentVideoSourceKind.DirectMedia));
    }

    private sealed class AdjustableTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) =>
            _utcNow += duration;
    }
}

using System.Net;
using System.Text;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.LiveTv;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class LiveTvCatalogServiceTests
{
    [Fact]
    public async Task HtmlProviderLoadsChannelsAndResolvesSelectedIframe()
    {
        var handler = new HtmlTvHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new StreamflixHtmlLiveTvProvider(
            httpClient,
            new LiveTvProviderDescriptor(
                "html-tv",
                "HTML TV",
                new Uri("https://tv.example/")));
        var service = new LiveTvCatalogService([provider]);

        var catalog = await service.LoadAsync();
        var channel = Assert.Single(catalog.Channels);
        Assert.Equal("Canal Deportes", channel.Name);
        Assert.Equal("Deportes", channel.Group);
        Assert.Equal(
            "https://tv.example/logos/deportes.png",
            channel.LogoUri?.AbsoluteUri);

        var playback = await service.ResolvePlaybackAsync(channel);

        Assert.Equal(
            "https://cdn.example/live/deportes/master.m3u8",
            playback.StreamUri.AbsoluteUri);
        Assert.Equal(
            "https://player.example/embed/deportes",
            playback.Referrer?.AbsoluteUri);
        var alternate = Assert.Single(playback.AlternateVariants!);
        Assert.Equal("Opción 2", alternate.Label);
        Assert.Equal(
            "https://backup.example/live/deportes.m3u8",
            alternate.StreamUri.AbsoluteUri);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.AbsoluteUri ==
                "https://player.example/embed/deportes");
    }

    [Fact]
    public async Task HtmlProviderFollowsRotationLandingAndButtonPlayers()
    {
        var handler = new RotatedHtmlTvHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new StreamflixHtmlLiveTvProvider(
            httpClient,
            new LiveTvProviderDescriptor(
                "rotated-html-tv",
                "Rotated HTML TV",
                new Uri("https://old-live-tv.example/")));
        var service = new LiveTvCatalogService([provider]);

        var catalog = await service.LoadAsync();

        var channel = Assert.Single(catalog.Channels);
        Assert.Equal("EN VIVO ESPN", channel.Name);
        Assert.Equal(
            "https://new-live-tv.example/espn-en-vivo.php",
            channel.StreamUri.AbsoluteUri);
        Assert.Equal(
            "https://new-live-tv.example/parrilla-directo.php",
            channel.Referrer?.AbsoluteUri);

        var playback = await service.ResolvePlaybackAsync(channel);

        Assert.Equal(
            "https://cdn.example/playlist.php?id=espn",
            playback.StreamUri.AbsoluteUri);
        Assert.Equal(
            "https://player.example/stream?canal=espn",
            playback.Referrer?.AbsoluteUri);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.AbsoluteUri ==
                    "https://new-live-tv.example/live/core.php?canal=espn" &&
                request.Referrer?.AbsoluteUri ==
                    "https://new-live-tv.example/espn-en-vivo.php");
        Assert.Equal(
            [
                "https://old-live-tv.example/",
                "https://new-live-tv.example/",
                "https://new-live-tv.example/parrilla-directo.php"
            ],
            handler.Requests
                .Take(3)
                .Select(static request => request.Uri.AbsoluteUri));
    }

    [Fact]
    public async Task ParsesChannelMetadataPlaybackHeadersAndSafeUrls()
    {
        const string playlist = """
            #EXTM3U
            #EXTINF:-1 tvg-id="news.ar" tvg-logo="/logos/news.png" group-title="Noticias",Canal Noticias
            #EXTVLCOPT:http-user-agent=iPhone
            #EXTVLCOPT:http-referrer=https://player.example/watch/
            https://streams.example/live/news.m3u8
            #EXTINF:-1 tvg-name="Canal Dos" group-title="General" http-user-agent="Custom Player",Canal Dos
            https://streams.example/live/two.mpd
            #EXTINF:-1 tvg-id="private",Private address
            http://192.168.1.20/live
            #EXTINF:-1 tvg-id="duplicate",Duplicate
            https://streams.example/live/news.m3u8
            """;
        var handler = new PlaylistHandler(playlist);
        using var httpClient = new HttpClient(handler);
        var provider = new M3uLiveTvProvider(
            httpClient,
            new LiveTvProviderDescriptor(
                "test",
                "Test provider",
                new Uri("https://feed.example/argentina.m3u")));

        var channels = await provider.LoadAsync();

        Assert.Equal(2, channels.Count);
        var news = Assert.Single(
            channels,
            static channel => channel.ChannelId == "news.ar");
        Assert.Equal("Canal Noticias", news.Name);
        Assert.Equal("Noticias", news.Group);
        Assert.Equal(
            "https://feed.example/logos/news.png",
            news.LogoUri?.AbsoluteUri);
        Assert.Equal(
            "https://player.example/watch/",
            news.Referrer?.AbsoluteUri);
        Assert.Equal("iPhone", news.UserAgent);
        Assert.Equal(
            "https://streams.example/live/news.m3u8",
            news.StreamUri.AbsoluteUri);

        var second = Assert.Single(
            channels,
            static channel => channel.Name == "Canal Dos");
        Assert.Equal("Custom Player", second.UserAgent);
        Assert.NotNull(handler.LastRequest);
        Assert.Contains(
            "UniversalMediaDistribution",
            handler.LastRequest!.Headers.UserAgent.ToString(),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task KeepsWorkingWhenOneProviderFails()
    {
        var first = new StubProvider(
            new LiveTvProviderDescriptor(
                "working",
                "Working",
                new Uri("https://working.example/list.m3u")),
            [
                new LiveTvChannel(
                    "working",
                    "Working",
                    "Zulu",
                    new Uri("https://stream.example/zulu.m3u8")),
                new LiveTvChannel(
                    "working",
                    "Working",
                    "Alpha",
                    new Uri("https://stream.example/alpha.m3u8"))
            ]);
        var failed = new StubProvider(
            new LiveTvProviderDescriptor(
                "failed",
                "Failed",
                new Uri("https://failed.example/list.m3u")),
            new HttpRequestException("Timed out"));
        var service = new LiveTvCatalogService([first, failed]);

        var result = await service.LoadAsync();

        Assert.Equal(1, result.LoadedProviderCount);
        Assert.Equal(["Alpha", "Zulu"], result.Channels.Select(
            static channel => channel.Name));
        var failure = Assert.Single(result.Failures);
        Assert.Equal("failed", failure.ProviderId);
        Assert.Contains(
            "Timed out",
            failure.Message,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task DisabledProviderIsNotContacted()
    {
        var descriptor = new LiveTvProviderDescriptor(
            "disabled",
            "Disabled",
            new Uri("https://disabled.example/list.m3u"));
        var provider = new StubProvider(descriptor, []);
        var settingsStore = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers =
                [
                    new ContentSourceProviderConfiguration
                    {
                        Id = descriptor.Id,
                        DisplayName = descriptor.DisplayName,
                        Kind = ContentSourceProviderKind.LiveTv,
                        IsEnabled = false,
                        BaseUrl = descriptor.PlaylistUri.AbsoluteUri
                    }
                ]
            });
        var service = new LiveTvCatalogService([provider], settingsStore);

        var result = await service.LoadAsync();

        Assert.Empty(result.Channels);
        Assert.Empty(result.Failures);
        Assert.Equal(0, result.LoadedProviderCount);
        Assert.Equal(0, provider.LoadCount);
    }

    [Fact]
    public void MasterPlaylistQualitiesAreSortedAndLabeled()
    {
        var source = new LiveTvPlaybackVariant(
            "Opción 1",
            new Uri("https://cdn.example/live/master.m3u8"),
            new Uri("https://player.example/watch"));
        const string playlist = """
            #EXTM3U
            #EXT-X-STREAM-INF:BANDWIDTH=1600000,RESOLUTION=1280x720
            720/playlist.m3u8
            #EXT-X-STREAM-INF:BANDWIDTH=4200000,RESOLUTION=1920x1080
            1080/playlist.m3u8
            """;

        var variants =
            StreamflixHtmlLiveTvProvider.ParseMasterPlaylist(
                playlist,
                source);

        Assert.Equal(2, variants.Count);
        Assert.Equal("Opción 1 · 1080p", variants[0].Label);
        Assert.Equal(
            "https://cdn.example/live/1080/playlist.m3u8",
            variants[0].StreamUri.AbsoluteUri);
        Assert.Equal("Opción 1 · 720p", variants[1].Label);
    }

    private sealed class PlaylistHandler(string playlist) : HttpMessageHandler
    {
        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        playlist,
                        Encoding.UTF8,
                        "audio/x-mpegurl")
                });
        }
    }

    private sealed class HtmlTvHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var html = uri.IdnHost switch
            {
                "tv.example" when uri.AbsolutePath == "/" => """
                    <script>
                    const homeChannels = `
                      <a href="/canal-deportes">
                        <img src="/logos/deportes.png"
                             alt="Canal Deportes">
                      </a>`;
                    </script>
                    """,
                "tv.example" => """
                    <a href="https://player.example/embed/deportes">
                      Opción 1
                    </a>
                    <a href="https://backup.example/embed/deportes">
                      Opción 2
                    </a>
                    """,
                "player.example" => """
                    <script>
                      const player = {
                        source: "https://cdn.example/live/deportes/master.m3u8"
                      };
                    </script>
                    """,
                "backup.example" => """
                    <video src="https://backup.example/live/deportes.m3u8">
                    </video>
                    """,
                _ => throw new InvalidOperationException(
                    $"Unexpected TV request: {uri}")
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        html,
                        Encoding.UTF8,
                        "text/html")
                });
        }
    }

    private sealed class RotatedHtmlTvHandler : HttpMessageHandler
    {
        public List<(Uri Uri, Uri? Referrer)> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add((uri, request.Headers.Referrer));
            if (uri.IdnHost == "old-live-tv.example")
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.MovedPermanently)
                {
                    RequestMessage = request,
                    Headers =
                    {
                        Location = new Uri("https://new-live-tv.example/")
                    }
                });
            }

            var html = (uri.IdnHost, uri.AbsolutePath) switch
            {
                ("new-live-tv.example", "/") =>
                    """
                    <a href="/parrilla-directo.php">
                      VER PARRILLA DE CANALES
                    </a>
                    """,
                ("new-live-tv.example", "/parrilla-directo.php") =>
                    """
                    <a href="/espn-en-vivo.php">
                      <img src="/imge/espn.webp">
                      EN VIVO ESPN
                    </a>
                    """,
                ("new-live-tv.example", "/espn-en-vivo.php") =>
                    """
                    <button class="option"
                            data-src="/live/core.php?canal=espn">
                      Opción 1
                    </button>
                    <iframe src="/chat.php"></iframe>
                    """,
                ("new-live-tv.example", "/live/core.php") =>
                    """
                    <iframe src="https://player.example/stream?canal=espn">
                    </iframe>
                    """,
                ("player.example", "/stream") =>
                    """
                    <script>
                      var src =
                        "https:\/\/cdn.example\/playlist.php?id=espn";
                    </script>
                    """,
                _ => throw new InvalidOperationException(
                    $"Unexpected rotated TV request: {uri}")
            };
            return Task.FromResult(
                new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        html,
                        Encoding.UTF8,
                        "text/html")
                });
        }
    }

    private sealed class StubProvider : ILiveTvProvider
    {
        private readonly IReadOnlyList<LiveTvChannel>? _channels;
        private readonly Exception? _exception;

        public StubProvider(
            LiveTvProviderDescriptor descriptor,
            IReadOnlyList<LiveTvChannel> channels)
        {
            Descriptor = descriptor;
            _channels = channels;
        }

        public StubProvider(
            LiveTvProviderDescriptor descriptor,
            Exception exception)
        {
            Descriptor = descriptor;
            _exception = exception;
        }

        public LiveTvProviderDescriptor Descriptor { get; }

        public int LoadCount { get; private set; }

        public Task<IReadOnlyList<LiveTvChannel>> LoadAsync(
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            LoadCount++;
            return _exception is null
                ? Task.FromResult(_channels!)
                : Task.FromException<IReadOnlyList<LiveTvChannel>>(
                    _exception);
        }
    }
}

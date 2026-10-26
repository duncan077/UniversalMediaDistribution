using System.Net;
using System.Text;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class StreamflixApiContentProviderTests
{
    [Fact]
    public async Task PelisflixSearchesLoadsArtworkAndResolvesMovieServer()
    {
        var handler = new PelisflixHandler();
        using var httpClient = new HttpClient(handler);
        var resolver = new VideoHosterResolver(
            httpClient,
            [new ClearKeyHosterExtractor()]);
        var provider = new StreamflixHtmlApiContentProvider(
            httpClient,
            new ApiContentProviderDescriptor(
                "test-pelisflix",
                "Test Pelisflix",
                "https://catalog.example/"),
            StreamflixCatalogProfile.PelisflixHd,
            resolver);

        var results = await provider.SearchAsync(
            new Uri("https://catalog.example/"),
            "movie");
        var searchItem = Assert.Single(results);
        Assert.Equal("pelicula/test-movie", searchItem.ExternalId);
        Assert.Equal("Test Movie", searchItem.PreviewItem.Name);
        Assert.Equal(
            "https://images.example/test.jpg",
            searchItem.PreviewItem.ThumbnailRef);

        var item = await provider.LoadItemAsync(
            new Uri("https://catalog.example/"),
            searchItem);

        Assert.Equal("A test movie description long enough to use.", item.CatalogItem.Description);
        Assert.Contains("Acción", item.CatalogItem.Genres);
        var videoSet = Assert.Single(item.VideoSets);
        var source = Assert.Single(videoSet.Sources);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(
            "https://cdn.example/encrypted.mpd",
            source.PlaybackUri.AbsoluteUri);
        Assert.True(source.RequiresClearKeyDecryption);
        Assert.Single(source.DecryptionKeys);
        Assert.Equal(
            "https://www.yourupload.com/",
            source.HttpOrigin?.AbsoluteUri);
    }

    [Fact]
    public async Task SeriesFlixLoadsSeasonEpisodesAndLazyPlayback()
    {
        var handler = new SeriesFlixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new StreamflixHtmlApiContentProvider(
            httpClient,
            new ApiContentProviderDescriptor(
                "test-seriesflix",
                "Test SeriesFlix",
                "https://series.example/"),
            StreamflixCatalogProfile.SeriesFlix);
        var searchItem = new ApiContentSearchItem(
            "serie/my-show",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "My Show"
            });

        var item = await provider.LoadItemAsync(
            new Uri("https://series.example/"),
            searchItem);

        var season = Assert.Single(item.CatalogItem.Seasons);
        Assert.Equal(1, season.Number);
        var episode = Assert.Single(season.Units);
        Assert.Equal("2", episode.DisplayNumber);

        var videos = await provider.LoadUnitVideosAsync(
            new Uri("https://series.example/"),
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "02",
                1));

        var source = Assert.Single(videos!.Sources);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(
            "https://cdn.example/show/s01e02/master.m3u8",
            source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task SeriesFlixPrefersDelegatedVoeOverBareNupload()
    {
        var handler = new SeriesFlixDecoyFirstHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new StreamflixHtmlApiContentProvider(
            httpClient,
            new ApiContentProviderDescriptor(
                "test-seriesflix-priority",
                "Test SeriesFlix priority",
                "https://series.example/"),
            StreamflixCatalogProfile.SeriesFlix);
        var searchItem = new ApiContentSearchItem(
            "serie/my-show",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "My Show"
            });

        _ = await provider.LoadItemAsync(
            new Uri("https://series.example/"),
            searchItem);
        var videos = await provider.LoadUnitVideosAsync(
            new Uri("https://series.example/"),
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "2",
                1));

        Assert.Collection(
            videos!.Sources,
            source => Assert.Equal(
                SeriesFlixDecoyFirstHandler.DelegatedVoe,
                source.PlaybackUri.AbsoluteUri),
            source => Assert.Equal(
                SeriesFlixDecoyFirstHandler.BareNupload,
                source.PlaybackUri.AbsoluteUri));
    }

    [Fact]
    public async Task SeriesFlixReusesRememberedEpisodeRouteBeforeSeasonRoute()
    {
        var handler = new RememberedSeasonSeriesFlixHandler();
        using var httpClient = new HttpClient(handler);
        var descriptor = new ApiContentProviderDescriptor(
            "test-seriesflix-route",
            "Test SeriesFlix route",
            "https://series.example/");
        var root = new Uri(descriptor.DefaultBaseUrl);
        var searchItem = new ApiContentSearchItem(
            "serie/my-show",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "My Show"
            });
        var request = new ApiContentUnitRequest(
            ContentUnitKind.Episode,
            "2",
            seasonNumber: 1);
        var firstProvider = new StreamflixHtmlApiContentProvider(
            httpClient,
            descriptor,
            StreamflixCatalogProfile.SeriesFlix);

        _ = await firstProvider.LoadItemAsync(root, searchItem);
        var routes =
            firstProvider.GetKnownUnitRoutes(root, searchItem, request);
        Assert.Collection(
            routes,
            route =>
            {
                Assert.Equal("2", route.DisplayNumber);
                Assert.Equal(1, route.SeasonNumber);
                Assert.Equal("/episodio/my-show-1x2", route.Route);
            },
            route =>
            {
                Assert.Null(route.DisplayNumber);
                Assert.Equal(1, route.SeasonNumber);
                Assert.Equal("/temporada/my-show-1", route.Route);
            });

        handler.Requests.Clear();
        handler.ExposeSeasonLink = false;
        var rememberedSearchItem = new ApiContentSearchItem(
            searchItem.ExternalId,
            searchItem.PreviewItem,
            routes);
        var secondProvider = new StreamflixHtmlApiContentProvider(
            httpClient,
            descriptor,
            StreamflixCatalogProfile.SeriesFlix);

        var item = await secondProvider.LoadItemAsync(
            root,
            rememberedSearchItem);

        Assert.Equal(
            "2",
            Assert.Single(
                Assert.Single(item.CatalogItem.Seasons).Units).DisplayNumber);
        Assert.Equal(
            ["/serie/my-show"],
            handler.Requests.Select(static uri => uri.AbsolutePath));

        var videos = await secondProvider.LoadUnitVideosAsync(
            root,
            rememberedSearchItem,
            request);
        Assert.Equal(
            "https://cdn.example/remembered.m3u8",
            Assert.Single(videos!.Sources).PlaybackUri.AbsoluteUri);
        Assert.Equal(
            ["/serie/my-show", "/episodio/my-show-1x2"],
            handler.Requests.Select(static uri => uri.AbsolutePath));
    }

    [Fact]
    public async Task SeriesFlixCapturedGenreCardsKeepTitleYearAndPaging()
    {
        var handler = new CapturedSeriesFlixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new SeriesFlixApiContentProvider(httpClient);
        var root = new Uri(provider.Descriptor.DefaultBaseUrl);

        var first = await provider.SearchAsync(
            root,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Adventure,
                releaseYear: 2024));

        // The published domain redirects here; pinning the destination
        // keeps every request from paying a redirect it does not need.
        Assert.Equal(
            "https://seriesflixhd.pw/",
            provider.Descriptor.DefaultBaseUrl);
        var item = Assert.Single(first.Items);
        Assert.Equal("X-Men '97", item.PreviewItem.Name);
        Assert.Equal("2024", item.PreviewItem.ReleaseDate);
        Assert.Equal(
            "https://s.seriesflixhd.ink/p/v2/w0/x-men.webp",
            item.PreviewItem.ThumbnailRef);
        Assert.Equal("2", first.ContinuationToken);

        var second = await provider.SearchAsync(
            root,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Adventure,
                releaseYear: 2024,
                continuationToken: first.ContinuationToken));

        Assert.Equal(
            "Second Adventure",
            Assert.Single(second.Items).PreviewItem.Name);
        Assert.Null(second.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/genero/aventura/page/2");
    }

    [Fact]
    public async Task SeriesFlixCapturedDetailKeepsYearGenresAndSeason()
    {
        using var httpClient = new HttpClient(
            new CapturedSeriesFlixHandler());
        var provider = new SeriesFlixApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "serie/cuentos-asombrosos-zlrp/",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "Cuentos asombrosos"
            });

        var item = await provider.LoadItemAsync(
            new Uri(provider.Descriptor.DefaultBaseUrl),
            searchItem);

        Assert.Equal("Cuentos asombrosos", item.CatalogItem.Name);
        Assert.Equal("Amazing Stories", item.CatalogItem.OriginalName);
        var originalName = Assert.Single(
            item.CatalogItem.AlternativeNames);
        Assert.Equal("Amazing Stories", originalName.Name);
        Assert.Equal("original", originalName.AliasType);
        Assert.Equal("2020", item.CatalogItem.ReleaseDate);
        Assert.Equal(
            ["Acción", "Aventura", "C. Ficción", "Fantasía"],
            item.CatalogItem.Genres);
        var season = Assert.Single(item.CatalogItem.Seasons);
        Assert.Equal(1, season.Number);
        Assert.Equal(
            "1",
            Assert.Single(season.Units).DisplayNumber);
    }

    [Fact]
    public async Task DoramasflixUsesGraphQlSeasonsAndEpisodeServers()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(
            httpClient,
            new VideoHosterResolver(
                httpClient,
                [new ClearKeyHosterExtractor()]));

        var results = await provider.SearchAsync(
            new Uri("https://doramas.example/"),
            "Moon");
        var searchItem = Assert.Single(results);
        Assert.Equal("doramas-online/moon-story", searchItem.ExternalId);
        Assert.Equal("Moon Story", searchItem.PreviewItem.Name);
        var localizedName = Assert.Single(
            searchItem.PreviewItem.AlternativeNames);
        Assert.Equal("Historia lunar", localizedName.Name);
        Assert.Equal("es", localizedName.Language);
        Assert.Equal("localized", localizedName.AliasType);

        var localizedResults = await provider.SearchAsync(
            new Uri("https://doramas.example/"),
            "Historia");
        Assert.Equal(
            "Moon Story",
            Assert.Single(localizedResults).PreviewItem.Name);

        var item = await provider.LoadItemAsync(
            new Uri("https://doramas.example/"),
            new ApiContentSearchItem(
                searchItem.ExternalId,
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "series",
                    Name = "Stale preview"
                }));
        Assert.Equal("Moon Story", item.CatalogItem.Name);
        Assert.Equal("문 스토리", item.CatalogItem.OriginalName);
        Assert.Contains(
            item.CatalogItem.AlternativeNames,
            static alias =>
                alias.Name == "Historia lunar" &&
                alias.Language == "es" &&
                alias.AliasType == "localized");
        Assert.Contains(
            item.CatalogItem.AlternativeNames,
            static alias =>
                alias.Name == "문 스토리" &&
                alias.AliasType == "original");
        Assert.True(
            handler.GraphQlBodies.Any(static body =>
                body.Contains(
                    "\"operationName\":\"listSeasons\"",
                    StringComparison.Ordinal)),
            string.Join(Environment.NewLine, handler.GraphQlBodies));
        var season = Assert.Single(item.CatalogItem.Seasons);
        var episode = Assert.Single(season.Units);
        Assert.Equal("3", episode.DisplayNumber);

        var videos = await provider.LoadUnitVideosAsync(
            new Uri("https://doramas.example/"),
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "03",
                1));

        var source = Assert.Single(videos!.Sources);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, source.Kind);
        Assert.Equal(
            "https://cdn.example/encrypted.mpd",
            source.PlaybackUri.AbsoluteUri);
        Assert.True(source.RequiresClearKeyDecryption);
        Assert.Single(source.DecryptionKeys);
    }

    [Fact]
    public async Task DoramasflixUsesCurrentGraphQlEpisodeLinksWithoutNextData()
    {
        var handler = new DoramasflixHandler
        {
            UseCurrentVideoLinks = true
        };
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(httpClient);
        var searchItem = Assert.Single(await provider.SearchAsync(
            new Uri("https://doramas.example/"),
            "Moon"));
        var item = await provider.LoadItemAsync(
            new Uri("https://doramas.example/"),
            searchItem);
        var episode = Assert.Single(
            Assert.Single(item.CatalogItem.Seasons).Units);

        var videos = await provider.LoadUnitVideosAsync(
            new Uri("https://doramas.example/"),
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                episode.DisplayNumber,
                1));

        var source = Assert.Single(videos!.Sources);
        Assert.Equal(ApiContentVideoSourceKind.EmbeddedWeb, source.Kind);
        Assert.Equal(
            "https://uqload.com/embed-current",
            source.PlaybackUri.AbsoluteUri);
        Assert.Equal(
            "https://fkplayer.xyz/e/current-token",
            source.Referrer?.AbsoluteUri);
        Assert.True(handler.SawDecodePlayerAction);
        Assert.Contains(
            handler.GraphQlBodies,
            static body =>
                body.Contains(
                    "\"operationName\":\"EpisodeLinksOnline\"",
                    StringComparison.Ordinal) &&
                body.Contains(
                    "com.asiapp.doramasgo",
                    StringComparison.Ordinal) &&
                body.Contains("MongoID!", StringComparison.Ordinal));
    }

    [Fact]
    public async Task PoseidonUsesTitleCoordinateWhenEpisodeNumberIsMissing()
    {
        using var httpClient = new HttpClient(
            new PoseidonTitleCoordinateHandler());
        var provider = new StreamflixHtmlApiContentProvider(
            httpClient,
            new ApiContentProviderDescriptor(
                "test-poseidon",
                "Test Poseidon",
                "https://poseidon.example/"),
            StreamflixCatalogProfile.PoseidonHd2);
        var searchItem = new ApiContentSearchItem(
            "serie/1999/the-comeback",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "The Comeback"
            });

        var item = await provider.LoadItemAsync(
            new Uri("https://poseidon.example/"),
            searchItem);

        var season = Assert.Single(item.CatalogItem.Seasons);
        Assert.Equal(3, season.Number);
        var episode = Assert.Single(season.Units);
        Assert.Equal("7", episode.DisplayNumber);
        Assert.Equal("The Comeback 3x7", episode.Name);
    }

    [Fact]
    public async Task SeriesFlixScopesEpisodeCacheToBaseUrl()
    {
        var handler = new BaseSwitchSeriesFlixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new StreamflixHtmlApiContentProvider(
            httpClient,
            new ApiContentProviderDescriptor(
                "test-seriesflix-cache",
                "Test SeriesFlix Cache",
                "https://series-a.example/"),
            StreamflixCatalogProfile.SeriesFlix);
        var searchItem = new ApiContentSearchItem(
            "serie/my-show",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "My Show"
            });

        _ = await provider.LoadItemAsync(
            new Uri("https://series-a.example/"),
            searchItem);
        var videos = await provider.LoadUnitVideosAsync(
            new Uri("https://series-b.example/"),
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                1));

        var source = Assert.Single(videos!.Sources);
        Assert.Equal(
            "https://cdn.example/series-b.example.m3u8",
            source.PlaybackUri.AbsoluteUri);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.IdnHost == "series-b.example" &&
                uri.AbsolutePath == "/episodio/my-show-1x1");
    }

    [Fact]
    public async Task DoramasflixDailyUpdatesUseUpdatedSort()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(httpClient);

        var updates = await provider.LoadDailyUpdatesAsync(
            new Uri("https://doramas.example/"));

        Assert.Equal(2, updates.Count);
        Assert.All(
            handler.GraphQlBodies.Where(static body =>
                body.Contains(
                    "\"operationName\":\"list",
                    StringComparison.Ordinal)),
            static body => Assert.Contains("UPDATEDAT_DESC", body));
    }

    [Fact]
    public async Task DoramasflixRejectsOversizedGraphQlResponse()
    {
        using var httpClient = new HttpClient(
            new OversizedGraphQlHandler());
        var provider = new DoramasflixApiContentProvider(httpClient);

        var exception = await Assert.ThrowsAsync<InvalidDataException>(
            async () => await provider.SearchAsync(
                new Uri("https://doramas.example/"),
                "Moon"));

        Assert.Contains(
            "exceeded",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
    }

    private sealed class PelisflixHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            string html;
            if (uri.IdnHost == "catalog.example" &&
                uri.AbsolutePath.StartsWith(
                    "/busqueda/",
                    StringComparison.Ordinal))
            {
                html = """
                    <a href="/pelicula/test-movie">
                      <img src="https://images.example/test.jpg"
                           alt="Poster Test Movie">
                    </a>
                    """;
            }
            else if (uri.IdnHost == "catalog.example")
            {
                var encoded = Convert.ToBase64String(
                    Encoding.UTF8.GetBytes(
                        "https://www.yourupload.com/embed/movie"));
                html = $$"""
                    <h1>Ver Película Test Movie</h1>
                    <meta property="og:image"
                          content="https://images.example/test.jpg">
                    <meta property="og:description"
                          content="A test movie description long enough to use.">
                    Género: <a href="/genero/accion/">Acción</a>
                    <div id="player">
                      <li data-server="{{encoded}}">YourUpload</li>
                    </div>
                    """;
            }
            else if (uri.IdnHost.EndsWith(
                         "yourupload.com",
                         StringComparison.OrdinalIgnoreCase))
            {
                html = """
                    <script>
                      const jwplayerOptions = {
                        file: 'https://cdn.example/movie.mp4'
                      };
                    </script>
                    """;
            }
            else
            {
                throw new InvalidOperationException(
                    $"Unexpected request: {uri}");
            }

            return Task.FromResult(Html(html, request));
        }
    }

    private sealed class CapturedSeriesFlixHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ??
                throw new InvalidOperationException("Missing request URI.");
            Requests.Add(uri);
            var html = uri.AbsolutePath switch
            {
                "/" => """
                    <nav>
                      <a href="/genero/aventura/">Aventura</a>
                      <a href="/genero/accion/">Acción</a>
                    </nav>
                    """,
                "/genero/aventura/" => """
                    <ul class="MovieList">
                      <li>
                        <article class="TPost B">
                          <a href="/serie/tomb-raider-king/">
                            <div class="Image">
                              <img data-src="//s.seriesflixhd.ink/p/tomb.webp">
                              <span class="Qlty Yr">2026</span>
                            </div>
                            <h2 class="Title">Tomb Raider King</h2>
                          </a>
                        </article>
                      </li>
                      <li>
                        <article class="TPost B">
                          <a href="/serie/x-men-97-zndb/">
                            <div class="Image">
                              <img data-src="//s.seriesflixhd.ink/p/v2/w0/x-men.webp">
                              <span class="Qlty Yr">2024</span>
                            </div>
                            <h2 class="Title">X-Men '97</h2>
                          </a>
                        </article>
                      </li>
                    </ul>
                    <a href="/genero/aventura/page/2">2</a>
                    """,
                "/genero/aventura/page/2" => """
                    <article class="TPost B">
                      <a href="/serie/second-adventure/">
                        <div class="Image">
                          <img data-src="/p/second.webp">
                          <span class="Qlty Yr">2024</span>
                        </div>
                        <h2 class="Title">Second Adventure</h2>
                      </a>
                    </article>
                    """,
                "/serie/cuentos-asombrosos-zlrp/" => """
                    <main>
                      <h1 class="Title">Serie Cuentos asombrosos</h1>
                      <div class="Info">
                        <p>Titulo Original: Amazing Stories</p>
                        <span class="Date">2020</span>
                        <span class="Time">55min</span>
                      </div>
                      <div class="Description">
                        <p>
                          Steven Spielberg revive la antología que nació en los
                          años ochenta con nuevas historias de ciencia ficción.
                        </p>
                        <p class="Genre">
                          <span>Género:</span>
                          <a href="/genero/accion/">Acción,</a>
                          <a href="/genero/aventura/">Aventura,</a>
                          <a href="/genero/ciencia-ficcion/">C. Ficción,</a>
                          <a href="/genero/fantasia/">Fantasía</a>
                        </p>
                      </div>
                      <a href="/temporada/cuentos-asombrosos-zlrp-1/">
                        Temporada <span>1</span>
                      </a>
                    </main>
                    """,
                "/temporada/cuentos-asombrosos-zlrp-1/" => """
                    <a href="/episodio/cuentos-asombrosos-zlrp-1x1/">
                      Episodio 1
                    </a>
                    """,
                _ => throw new InvalidOperationException(
                    $"Unexpected captured SeriesFlix request: {uri}")
            };

            return Task.FromResult(Html(html, request));
        }
    }

    private sealed class SeriesFlixHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var encodedStream = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    "https://cdn.example/show/s01e02/master.m3u8"));
            var html = uri.AbsolutePath switch
            {
                "/serie/my-show" => """
                    <h1>Serie My Show</h1>
                    <a href="/temporada/my-show-1">Temporada 1</a>
                    <a href="https://ads.example/temporada/ad-9">Temporada 9</a>
                    """,
                "/temporada/my-show-1" => """
                    <a href="/episodio/my-show-1x2">
                      <img src="https://images.example/episode-2.jpg"
                           alt="Episode 2">
                      Episodio 2
                    </a>
                    <a href="https://ads.example/episodio/ad-9x9">
                      Episodio 9
                    </a>
                    """,
                "/episodio/my-show-1x2" => $$"""
                    <button data-url="{{encodedStream}}">Server</button>
                    """,
                _ => throw new InvalidOperationException(
                    $"Unexpected request: {uri}")
            };
            return Task.FromResult(Html(html, request));
        }
    }

    private sealed class SeriesFlixDecoyFirstHandler : HttpMessageHandler
    {
        public const string BareNupload =
            "https://nupload.top/watch/principal-decoy";
        public const string DelegatedVoe =
            "https://nupload.top/iframe/?url=" +
            "https%3A%2F%2Fvoe.sx%2Fe%2Freal-player";

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            var bareNupload = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(BareNupload));
            var delegatedVoe = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(DelegatedVoe));
            var html = uri.AbsolutePath switch
            {
                "/serie/my-show" => """
                    <h1>Serie My Show</h1>
                    <a href="/temporada/my-show-1">Temporada 1</a>
                    """,
                "/temporada/my-show-1" => """
                    <a href="/episodio/my-show-1x2">Episodio 2</a>
                    """,
                "/episodio/my-show-1x2" => $$"""
                    <div data-url="{{bareNupload}}">
                      01 CASTELLANO HD Principal
                    </div>
                    <div data-url="{{delegatedVoe}}">
                      02 CASTELLANO HD Waaw
                    </div>
                    """,
                _ => throw new InvalidOperationException(
                    $"Unexpected request: {uri}")
            };
            return Task.FromResult(Html(html, request));
        }
    }

    private sealed class DoramasflixHandler : HttpMessageHandler
    {
        public List<string> GraphQlBodies { get; } = [];
        public bool UseCurrentVideoLinks { get; init; }
        public bool SawDecodePlayerAction { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            if (request.Method == HttpMethod.Post &&
                uri.IdnHost == "sv1.fluxcedene.net")
            {
                var body = await request.Content!.ReadAsStringAsync(
                    cancellationToken);
                GraphQlBodies.Add(body);
                if (body.Contains(
                        "\"operationName\":\"searchAll\"",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{
                          "searchDorama":[{
                            "_id":"635dd960f42dac8b1d9a4603",
                            "slug":"moon-story",
                            "name":"Moon Story",
                            "name_es":"Historia lunar",
                            "poster_path":"/moon.jpg"
                          }],
                          "searchMovie":[]
                        }}
                        """,
                        request);
                }

                if (body.Contains(
                        "\"operationName\":\"listSeasons\"",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{"listSeasons":[{
                          "slug":"moon-story-season-1",
                          "season_number":1,
                          "poster_path":"/moon-s1.jpg"
                        }]}}
                        """,
                        request);
                }

                if (body.Contains(
                        "\"operationName\":\"listEpisodes\"",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{"listEpisodes":[{
                          "_id":"episode-3",
                          "name":"The letter",
                          "slug":"moon-story-episode-3",
                          "episode_number":3,
                          "season_number":1,
                          "still_path":"/moon-e3.jpg"
                        }]}}
                        """,
                        request);
                }

                if (UseCurrentVideoLinks &&
                    body.Contains(
                        "\"operationName\":\"EpisodeLinksOnline\"",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{"getEpisodeLinks":{"links_online":[{
                          "server":"1233",
                          "lang":"13109",
                          "link":"https://fkplayer.xyz/e/current-token"
                        }]}}}
                        """,
                        request);
                }

                if (body.Contains(
                        "\"operationName\":\"listDoramas\"",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{"paginationDorama":{"items":[{
                          "_id":"dorama-latest",
                          "slug":"latest-dorama",
                          "name":"Latest Dorama"
                        }]}}}
                        """,
                        request);
                }

                if (body.Contains(
                        "\"operationName\":\"listMovies\"",
                        StringComparison.Ordinal))
                {
                    return Json(
                        """
                        {"data":{"paginationMovie":{"items":[{
                          "_id":"movie-latest",
                          "slug":"latest-movie",
                          "name":"Latest Movie"
                        }]}}}
                        """,
                        request);
                }
            }

            if (UseCurrentVideoLinks &&
                uri.IdnHost == "fkplayer.xyz" &&
                request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/e/current-token")
            {
                return Html(
                    """
                    <html><body>
                    <script src="/_next/static/chunks/player.js"></script>
                    <script>self.__next_f.push([1,"player"])</script>
                    </body></html>
                    """,
                    request);
            }

            if (UseCurrentVideoLinks &&
                uri.IdnHost == "fkplayer.xyz" &&
                request.Method == HttpMethod.Get &&
                uri.AbsolutePath == "/_next/static/chunks/player.js")
            {
                return Html(
                    """
                    var decode=(0,a.createServerReference)("4082a28b2fb3eb29fde4ec3df198a5afd10f8edc32",a.callServer,void 0,a.findSourceMapURL,"decodePlayerToken");
                    """,
                    request);
            }

            if (UseCurrentVideoLinks &&
                uri.IdnHost == "fkplayer.xyz" &&
                request.Method == HttpMethod.Post &&
                uri.AbsolutePath == "/e/current-token")
            {
                SawDecodePlayerAction = request.Headers.Contains("Next-Action");
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        """
                        0:{"a":"$@1","f":"","b":"build"}
                        1:{"success":true,"iframeUrl":"https://uqload.com/embed-current"}
                        """,
                        Encoding.UTF8,
                        "text/x-component")
                };
            }

            if (uri.IdnHost == "doramas.example" &&
                uri.AbsolutePath == "/doramas-online/moon-story")
            {
                return Html(
                    """
                    <main>
                      <h1>Moon Story</h1>
                      <p>Historia lunar</p>
                      <dl>
                        <dt>Título original</dt>
                        <dd>문 스토리</dd>
                      </dl>
                    </main>
                    <script>
                    self.__next_f.push(
                      [1,"{\"serie_id\":\"635dd960f42dac8b1d9a4603\"}"])
                    </script>
                    """,
                    request);
            }

            if (uri.IdnHost == "doramas.example" &&
                uri.AbsolutePath == "/episodios/moon-story-episode-3")
            {
                return Html(
                    UseCurrentVideoLinks
                        ? """
                          <main><h1>Moon Story - Episode 3</h1></main>
                          <script>
                          self.__next_f.push([1,"episode-3"])
                          </script>
                          """
                        : """
                    <script id="__NEXT_DATA__" type="application/json">
                    {"props":{"pageProps":{"apolloState":{
                      "Episode:episode-3":{
                        "_id":"episode-3",
                        "links_online":{"json":[{
                          "link":"https://www.yourupload.com/embed/dorama-3",
                          "lang":"38"
                        }]}
                      }
                    }}}}
                    </script>
                    """,
                    request);
            }

            if (uri.IdnHost.EndsWith(
                    "yourupload.com",
                    StringComparison.OrdinalIgnoreCase))
            {
                return Html(
                    """
                    <script>
                      const jwplayerOptions = {
                        file: 'https://cdn.example/dorama-3.mp4'
                      };
                    </script>
                    """,
                    request);
            }

            throw new InvalidOperationException(
                $"Unexpected Doramasflix request: {request.Method} {uri}");
        }
    }

    private sealed class PoseidonTitleCoordinateHandler :
        HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.RequestUri?.AbsolutePath !=
                "/serie/1999/the-comeback")
            {
                throw new InvalidOperationException(
                    $"Unexpected Poseidon request: {request.RequestUri}");
            }

            return Task.FromResult(
                Html(
                    """
                    <h1>Serie The Comeback</h1>
                    <script id="__NEXT_DATA__" type="application/json">
                    {"props":{"pageProps":{"thisSerie":{"seasons":[{
                      "number":3,
                      "episodes":[{
                        "title":"The Comeback 3x7",
                        "image":"https://images.example/comeback-3x7.jpg",
                        "releaseDate":"2026-07-01",
                        "slug":"series/1999/the-comeback/seasons/3/episodes/7"
                      }]
                    }]}}}}
                    </script>
                    """,
                    request));
        }
    }

    private sealed class BaseSwitchSeriesFlixHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var encodedStream = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    $"https://cdn.example/{uri.IdnHost}.m3u8"));
            var html = uri.AbsolutePath switch
            {
                "/serie/my-show" => """
                    <h1>Serie My Show</h1>
                    <a href="/temporada/my-show-1">Temporada 1</a>
                    """,
                "/temporada/my-show-1" => """
                    <a href="/episodio/my-show-1x1">Episodio 1</a>
                    """,
                "/episodio/my-show-1x1" =>
                    $"<button data-url=\"{encodedStream}\">Server</button>",
                _ => throw new InvalidOperationException(
                    $"Unexpected request: {uri}")
            };
            return Task.FromResult(Html(html, request));
        }
    }

    private sealed class RememberedSeasonSeriesFlixHandler :
        HttpMessageHandler
    {
        public bool ExposeSeasonLink { get; set; } = true;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri!;
            Requests.Add(uri);
            var encodedStream = Convert.ToBase64String(
                Encoding.UTF8.GetBytes(
                    "https://cdn.example/remembered.m3u8"));
            var html = uri.AbsolutePath switch
            {
                "/serie/my-show" => ExposeSeasonLink
                    ? """
                      <h1>Serie My Show</h1>
                      <a href="/temporada/my-show-1">Temporada 1</a>
                      """
                    : "<h1>Serie My Show</h1>",
                "/temporada/my-show-1" => """
                    <a href="/episodio/my-show-1x2">Episodio 2</a>
                    """,
                "/episodio/my-show-1x2" =>
                    $"<button data-url=\"{encodedStream}\">Server</button>",
                _ => throw new InvalidOperationException(
                    $"Unexpected request: {uri}")
            };
            return Task.FromResult(Html(html, request));
        }
    }

    private sealed class OversizedGraphQlHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var content = new StringContent(
                "{}",
                Encoding.UTF8,
                "application/json");
            content.Headers.ContentLength = (8 * 1024 * 1024) + 1;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = content
            });
        }
    }

    private sealed class ClearKeyHosterExtractor :
        IApiVideoHosterExtractor
    {
        public string Name => "test-clearkey";

        public bool Matches(Uri playbackUri) =>
            playbackUri.IdnHost.EndsWith(
                "yourupload.com",
                StringComparison.OrdinalIgnoreCase);

        public ValueTask<ApiContentVideoSource?> TryResolveAsync(
            ApiContentVideoSource source,
            HttpClient httpClient,
            CancellationToken cancellationToken) =>
            ValueTask.FromResult<ApiContentVideoSource?>(
                new ApiContentVideoSource(
                    source.Label,
                    new Uri("https://cdn.example/encrypted.mpd"),
                    "application/dash+xml",
                    source.Referrer,
                    ApiContentVideoSourceKind.DirectMedia,
                    [
                        new ApiContentVideoDecryptionKey(
                            "00112233445566778899aabbccddeeff",
                            "ffeeddccbbaa99887766554433221100")
                    ],
                    new Uri("https://www.yourupload.com/")));
    }

    private static HttpResponseMessage Html(
        string html,
        HttpRequestMessage request) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(
                html,
                Encoding.UTF8,
                "text/html")
        };

    private static HttpResponseMessage Json(
        string json,
        HttpRequestMessage request) =>
        new(HttpStatusCode.OK)
        {
            RequestMessage = request,
            Content = new StringContent(
                json,
                Encoding.UTF8,
                "application/json")
        };
}

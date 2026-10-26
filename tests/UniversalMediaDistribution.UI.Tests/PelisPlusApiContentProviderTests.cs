using System.Collections.Concurrent;
using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class PelisPlusApiContentProviderTests
{
    [Fact]
    public async Task SearchesSeriesLoadsSeasonsAndReturnsEmbeddedServers()
    {
        var handler = new PelisPlusHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new PelisPlusApiContentProvider(httpClient);
        var root = new Uri("https://pelisplus.autos/");

        var results = await provider.SearchAsync(
            root,
            "fire",
            MediaType.Movie);

        var result = Assert.Single(results);
        Assert.Equal("serie/fire-country", result.ExternalId);
        Assert.Equal("Fire Country", result.PreviewItem.Name);
        Assert.Equal("series", result.PreviewItem.Subtype);
        Assert.Equal(
            "https://pelisplus.autos/cdn/p/fire-country.webp",
            result.PreviewItem.ThumbnailRef);

        var item = await provider.LoadItemAsync(root, result);

        Assert.Equal("Fire Country", item.CatalogItem.Name);
        Assert.Equal("series", item.CatalogItem.Subtype);
        Assert.Equal("2022", item.CatalogItem.ReleaseDate);
        Assert.Contains("Drama", item.CatalogItem.Genres);
        Assert.Equal([1, 2], item.CatalogItem.Seasons.Select(
            static season => season.Number));
        Assert.Equal(
            ["1", "2"],
            item.CatalogItem.Seasons[0].Units.Select(
                static unit => unit.DisplayNumber));
        Assert.Equal(
            "The Fresh Prince of Edgewater",
            item.CatalogItem.Seasons[0].Units[1].Name);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });

        var videos = await provider.LoadUnitVideosAsync(
            root,
            result,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "2",
                seasonNumber: 1));

        Assert.NotNull(videos);
        Assert.Equal(1, videos.SeasonNumber);
        Assert.Equal("2", videos.DisplayNumber);
        Assert.Equal(
            ["Opción 1 · Latino", "Opción 2 · Latino"],
            videos.Sources.Select(static source => source.Label));
        Assert.All(
            videos.Sources,
            static source => Assert.Equal(
                ApiContentVideoSourceKind.EmbeddedWeb,
                source.Kind));
        Assert.Equal(
            "https://nupload.top/watch/fire-country-1x2",
            videos.Sources[0].PlaybackUri.AbsoluteUri);
        Assert.Equal(
            "https://nupload.top/iframe/?url=https%3A%2F%2Fvoe.sx%2Fe%2Fabc",
            videos.Sources[1].PlaybackUri.AbsoluteUri);
        Assert.Equal(
            "https://pelisplus.autos/episodio/fire-country-1x2/",
            videos.Sources[0].Referrer?.AbsoluteUri);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/temporada/fire-country-1/");
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/episodio/fire-country-1x2/");
    }

    [Fact]
    public async Task UnitDiscoveryLoadsOnlyTheRequestedSeason()
    {
        var handler = new PelisPlusHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new PelisPlusApiContentProvider(httpClient);
        var root = new Uri("https://pelisplus.autos/");
        var searchItem = new ApiContentSearchItem(
            "serie/fire-country",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "Fire Country"
            });

        var item = await provider.LoadItemForUnitAsync(
            root,
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: 2));

        Assert.Equal(2, Assert.Single(item.CatalogItem.Seasons).Number);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/temporada/fire-country-2/");
        Assert.DoesNotContain(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/temporada/fire-country-1/");
        Assert.Empty(item.VideoSets);
    }

    [Fact]
    public async Task RememberedEpisodeRouteSkipsTheSeasonPage()
    {
        var handler = new PelisPlusHandler();
        using var httpClient = new HttpClient(handler);
        var root = new Uri("https://pelisplus.autos/");
        var searchItem = new ApiContentSearchItem(
            "serie/fire-country",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "Fire Country"
            });
        var request = new ApiContentUnitRequest(
            ContentUnitKind.Episode,
            "2",
            seasonNumber: 1);
        var firstProvider = new PelisPlusApiContentProvider(httpClient);

        _ = await firstProvider.LoadItemForUnitAsync(
            root,
            searchItem,
            request);
        var routes =
            firstProvider.GetKnownUnitRoutes(root, searchItem, request);
        Assert.Collection(
            routes,
            route =>
            {
                Assert.Equal("2", route.DisplayNumber);
                Assert.Equal(
                    "/episodio/fire-country-1x2/",
                    route.Route);
            },
            route =>
            {
                Assert.Null(route.DisplayNumber);
                Assert.Equal(
                    "/temporada/fire-country-1/",
                    route.Route);
            });

        handler.Requests.Clear();
        var rememberedSearchItem = new ApiContentSearchItem(
            searchItem.ExternalId,
            searchItem.PreviewItem,
            routes);
        var secondProvider = new PelisPlusApiContentProvider(httpClient);

        var item = await secondProvider.LoadItemForUnitAsync(
            root,
            rememberedSearchItem,
            request);

        Assert.Equal(1, Assert.Single(item.CatalogItem.Seasons).Number);
        Assert.DoesNotContain(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/temporada/fire-country-1/");

        var videos = await secondProvider.LoadUnitVideosAsync(
            root,
            rememberedSearchItem,
            request);
        Assert.NotNull(videos);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/episodio/fire-country-1x2/");
    }

    [Fact]
    public async Task LoadsMovieMetadataAndCaptureStylePlaybackServers()
    {
        using var httpClient = new HttpClient(new PelisPlusHandler());
        var provider = new PelisPlusApiContentProvider(httpClient);
        var result = new ApiContentSearchItem(
            "pelicula/amos-del-universo",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "movie",
                Name = "Amos del Universo"
            });

        var item = await provider.LoadItemAsync(
            new Uri("https://pelisplus.autos/"),
            result);

        Assert.Equal("Amos del Universo", item.CatalogItem.Name);
        Assert.Equal("movie", item.CatalogItem.Subtype);
        Assert.Equal("2026", item.CatalogItem.ReleaseDate);
        Assert.Equal(8_460, item.CatalogItem.RuntimeSeconds);
        Assert.Equal(
            "https://pelisplus.autos/cdn/p/amos.webp",
            item.CatalogItem.ThumbnailRef);
        Assert.Equal(
            "https://pelisplus.autos/cdn/b/amos.webp",
            item.CatalogItem.BackdropRef);
        Assert.Contains("C. Ficción", item.CatalogItem.Genres);
        var unit = Assert.Single(item.CatalogItem.Units);
        Assert.Equal("1", unit.DisplayNumber);
        var videos = Assert.Single(item.VideoSets);
        Assert.Equal(ContentUnitKind.Episode, videos.UnitKind);
        Assert.Null(videos.SeasonNumber);
        Assert.Equal(
            ["Opción 1 · Latino", "Opción 1 · Español Castellano"],
            videos.Sources.Select(static source => source.Label));
        Assert.All(
            videos.Sources,
            static source => Assert.True(source.IsEmbeddedWeb));
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
    }

    [Fact]
    public async Task DailyUpdatesMergeLatestEpisodeIntoCanonicalSeries()
    {
        using var httpClient = new HttpClient(new PelisPlusHandler());
        var provider = new PelisPlusApiContentProvider(httpClient);

        var updates = await provider.LoadDailyUpdatesAsync(
            new Uri("https://pelisplus.autos/"));

        Assert.Equal(3, updates.Count);
        var series = Assert.Single(
            updates,
            static update =>
                update.ExternalId == "serie/fire-country");
        Assert.Equal("Fire Country", series.PreviewItem.Name);
        var season = Assert.Single(series.PreviewItem.Seasons);
        Assert.Equal(3, season.Number);
        var episode = Assert.Single(season.Units);
        Assert.Equal("7", episode.DisplayNumber);
        var movie = Assert.Single(
            updates,
            static update =>
                update.ExternalId == "pelicula/amos-del-universo");
        var movieUnit = Assert.Single(movie.PreviewItem.Units);
        Assert.Equal("1", movieUnit.DisplayNumber);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = updates.Select(static update => update.PreviewItem).ToArray()
        });
    }

    [Fact]
    public async Task ReportsBrowserChallengeWithoutRetrying()
    {
        var handler = new PelisPlusHandler(rejectRequests: true);
        using var httpClient = new HttpClient(handler);
        var provider = new PelisPlusApiContentProvider(httpClient);

        var exception = await Assert.ThrowsAsync<HttpRequestException>(
            async () => await provider.SearchAsync(
                new Uri("https://pelisplus.autos/"),
                "fire",
                MediaType.Movie));

        Assert.Contains(
            "browser challenge",
            exception.Message,
            StringComparison.OrdinalIgnoreCase);
        Assert.Single(handler.Requests);
    }

    private sealed class PelisPlusHandler(bool rejectRequests = false) :
        HttpMessageHandler
    {
        public ConcurrentBag<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = Assert.IsType<Uri>(request.RequestUri);
            Requests.Add(uri);
            Assert.NotNull(request.Headers.UserAgent);
            if (rejectRequests)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.Forbidden)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        "<title>Just a moment...</title>",
                        Encoding.UTF8,
                        "text/html")
                });
            }

            var html = (uri.AbsolutePath, uri.Query) switch
            {
                ("/busqueda/fire", "") => SearchHtml,
                ("/", "") => HomepageHtml,
                ("/serie/fire-country/", "") => SeriesHtml,
                ("/temporada/fire-country-1/", "") => SeasonOneHtml,
                ("/temporada/fire-country-2/", "") => SeasonTwoHtml,
                ("/episodio/fire-country-1x2/", "") => EpisodeHtml,
                ("/pelicula/amos-del-universo/", "") => MovieHtml,
                _ => throw new InvalidOperationException(
                    $"Unexpected PelisPlus test request: {uri}")
            };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    html,
                    Encoding.UTF8,
                    "text/html")
            });
        }

        private const string SearchHtml = """
            <main>
              <h2 class="section-title">Resultados de búsqueda para: fire</h2>
              <div class="movie-item">
                <a href="/serie/fire-country/">
                  <div class="item-picture">
                    <figure>
                      <img class="poster"
                           src="//pelisplus.autos/cdn/p/fire-country.webp"
                           alt="Fire Country">
                      <span class="year text-center"></span>
                    </figure>
                  </div>
                  <div class="item-detail"><p>Fire Country</p></div>
                </a>
              </div>
              <div class="movie-item">
                <a href="/pelicula/water/">
                  <img src="/cdn/p/water.webp" alt="Water">
                  <div class="item-detail"><p>Water</p></div>
                </a>
              </div>
            </main>
            """;

        private const string HomepageHtml = """
            <main>
              <dl>
                <dt class="section-title">Películas de estreno</dt>
                <dd>
                  <div class="movie-item">
                    <a href="/pelicula/amos-del-universo/">
                      <div class="item-picture">
                        <figure>
                          <img class="poster" src="/cdn/p/amos.webp"
                               alt="Poster Amos del Universo">
                          <span class="year text-center">2026</span>
                        </figure>
                      </div>
                      <div class="item-detail"><p>Amos del Universo</p></div>
                    </a>
                  </div>
                </dd>
              </dl>
              <dl>
                <dt class="section-title">Series de estreno</dt>
                <dd>
                  <div class="movie-item">
                    <a href="/serie/fire-country/">
                      <div class="item-picture">
                        <figure>
                          <img class="poster" src="/cdn/p/fire-country.webp"
                               alt="Poster Fire Country">
                          <span class="year text-center">2022</span>
                        </figure>
                      </div>
                      <div class="item-detail"><p>Fire Country</p></div>
                    </a>
                  </div>
                </dd>
              </dl>
              <dl>
                <dt class="section-title">Últimos Episodios</dt>
                <dd>
                  <div class="movie-item episode-item">
                    <a href="/serie/fire-country/">
                      <div class="item-picture">
                        <figure>
                          <img class="poster" src="/cdn/p/fire-country.webp"
                               alt="Poster Fire Country">
                        </figure>
                      </div>
                      <div class="item-detail"><p>Fire Country 3x7</p></div>
                    </a>
                  </div>
                  <div class="movie-item episode-item">
                    <a href="/serie/chainsmoker-cat/">
                      <img class="poster" src="/cdn/p/chainsmoker.webp"
                           alt="Poster Chainsmoker Cat">
                      <div class="item-detail"><p>Chainsmoker Cat 1x1</p></div>
                    </a>
                  </div>
                  <div class="movie-item episode-item">
                    <a href="/serie/fire-country/">
                      <img class="poster" src="/cdn/p/fire-country.webp"
                           alt="Poster Fire Country">
                      <div class="item-detail"><p>Fire Country 2x10</p></div>
                    </a>
                  </div>
                </dd>
              </dl>
            </main>
            """;

        private const string SeriesHtml = """
            <div class="backdrop-image"
                 style="background-image: url('//pelisplus.autos/cdn/b/fire-country.webp')">
            </div>
            <article class="backdrop-info"
                     itemtype="http://schema.org/TVSeries">
              <figure class="poster">
                <img itemprop="image"
                     src="//pelisplus.autos/cdn/p/fire-country.webp">
              </figure>
              <header class="info-header">
                <h1 class="title">
                  <span itemprop="name">Ver Serie Fire Country</span>
                </h1>
              </header>
              <p class="meta">
                <span itemprop="datePublished">2022</span>
              </p>
              <div class="info">
                <a href="/genero/drama/">
                  <span itemprop="genre">Drama</span>
                </a>
              </div>
              <div class="description" itemprop="description">
                <h2>Fire Country es una serie gratis online:</h2>
                <p>Un joven convicto se une a un programa de bomberos.</p>
              </div>
              <a class="btn" href="/temporada/fire-country-1/">
                Ver temporada 1
              </a>
              <ul itemprop="containsSeason">
                <li itemtype="https://schema.org/TVSeason">
                  <a href="/temporada/fire-country-1/">
                    <span itemprop="name">Temporada 1</span>
                  </a>
                </li>
                <li itemtype="https://schema.org/TVSeason">
                  <a href="/temporada/fire-country-2/">
                    <span itemprop="name">Temporada 2</span>
                  </a>
                </li>
              </ul>
            </article>
            """;

        private const string SeasonOneHtml = """
            <article itemtype="http://schema.org/TVSeries">
              <h1 class="title">
                <span itemprop="name">Ver Serie Fire Country</span>
              </h1>
              <div class="item-season-episodes"
                   itemprop="episode"
                   itemtype="https://schema.org/TVEpisode">
                <a href="/episodio/fire-country-1x1/">
                  <img src="/cdn/e/fire-country-1x1.webp">
                  <span itemprop="name">Pilot</span>
                  <span>1x1</span>
                </a>
              </div>
              <div class="item-season-episodes"
                   itemprop="episode"
                   itemtype="https://schema.org/TVEpisode">
                <a href="/episodio/fire-country-1x2/">
                  <img src="/cdn/e/fire-country-1x2.webp">
                  <span itemprop="name">The Fresh Prince of Edgewater</span>
                  <span>1x2</span>
                </a>
              </div>
            </article>
            """;

        private const string SeasonTwoHtml = """
            <article itemtype="http://schema.org/TVSeries">
              <div class="item-season-episodes" itemprop="episode">
                <a href="/episodio/fire-country-2x1/">
                  <img src="/cdn/e/fire-country-2x1.webp">
                  <span itemprop="name">Something's Coming</span>
                  <span>2x1</span>
                </a>
              </div>
            </article>
            """;

        private const string EpisodeHtml = """
            <div class="video-sandbox"></div>
            <ul class="tab-video">
              <li class="tab-video-item">
                <div class="tab-item-name">Latino</div>
                <ul>
                  <li data-server="aHR0cHM6Ly9udXBsb2FkLnRvcC93YXRjaC9maXJlLWNvdW50cnktMXgy">
                    <span>Opción 1 · Latino</span>
                    <span>Reproducir</span>
                  </li>
                  <li data-server="aHR0cHM6Ly9udXBsb2FkLnRvcC9pZnJhbWUvP3VybD1odHRwcyUzQSUyRiUyRnZvZS5zeCUyRmUlMkZhYmM=">
                    <span>Opción 2 · Latino</span>
                    <span>Reproducir</span>
                  </li>
                  <li data-server="aHR0cDovLzEyNy4wLjAuMS9hZG1pbg==">
                    <span>Unsafe local server</span>
                    <span>Reproducir</span>
                  </li>
                </ul>
              </li>
            </ul>
            """;

        private const string MovieHtml = """
            <div class="backdrop-image"
                 style="background-image: linear-gradient(#000, #111), url('//pelisplus.autos/cdn/b/amos.webp')">
            </div>
            <article class="backdrop-info"
                     itemtype="http://schema.org/Movie">
              <figure class="poster">
                <img itemprop="image"
                     src="//pelisplus.autos/cdn/p/amos.webp">
              </figure>
              <header class="info-header">
                <h1 class="title">
                  <span itemprop="name">Ver Película Amos del Universo</span>
                </h1>
              </header>
              <p class="meta">
                <span itemprop="duration">2h 21min</span>
                <span itemprop="datePublished">2026</span>
              </p>
              <div class="info">
                <a href="/genero/accion/">
                  <span itemprop="genre">Acción, </span>
                </a>
                <a href="/genero/ciencia-ficcion/">
                  <span itemprop="genre">C. Ficción</span>
                </a>
              </div>
              <div class="description" itemprop="description">
                <h2>Amos del Universo es una película gratis en HD:</h2>
                <p>El reino de Eternia está amenazado por Skeletor.</p>
              </div>
            </article>
            <ul class="tab-video">
              <li class="tab-video-item">
                <ul>
                  <li data-server="aHR0cHM6Ly9udXBsb2FkLnRvcC93YXRjaC9tYXN0ZXJzLW9mLXRoZS11bml2ZXJzZS1sYXRpbm8=">
                    <span>Opción 1 · Latino</span><span>Reproducir</span>
                  </li>
                </ul>
              </li>
              <li class="tab-video-item">
                <ul>
                  <li data-server="aHR0cHM6Ly9udXBsb2FkLnRvcC93YXRjaC9tYXN0ZXJzLW9mLXRoZS11bml2ZXJzZS1jYXN0ZWxsYW5v">
                    <span>Opción 1 · Español Castellano</span>
                    <span>Reproducir</span>
                  </li>
                </ul>
              </li>
            </ul>
            """;
    }
}

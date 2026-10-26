using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class JkAnimeApiContentProviderTests
{
    private static readonly Uri BaseUri =
        new("https://jkanime.example/");

    [Fact]
    public async Task SearchReturnsAnimeSeriesAndMoviesFromDirectoryIndex()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);

        var seriesResults = await provider.SearchAsync(
            BaseUri,
            "zombie",
            MediaType.Movie);
        var series = Assert.Single(seriesResults);
        Assert.Equal("anime_series", series.PreviewItem.Subtype);
        Assert.Equal("zombie-land-saga", series.ExternalId);
        Assert.Equal("Zombie Land Saga", series.PreviewItem.Name);
        Assert.Equal(
            "https://cdn.jkdesa.com/assets/images/animes/image/zombie.webp",
            series.PreviewItem.ThumbnailRef);
        Assert.Contains("Anime", series.PreviewItem.Categories);

        var movieResults = await provider.SearchAsync(
            BaseUri,
            "akira",
            MediaType.Movie);
        var movie = Assert.Single(movieResults);
        Assert.Equal("anime_movie", movie.PreviewItem.Subtype);
        Assert.Equal("akira", movie.ExternalId);
        Assert.Equal("Akira", movie.PreviewItem.Name);
        Assert.Equal(
            "https://cdn.jkdesa.com/assets/images/animes/image/akira.webp",
            movie.PreviewItem.ThumbnailRef);
    }

    [Fact]
    public async Task SearchIgnoresNonMovieMediaTypesAndEmptyQueries()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);

        var nonMovie = await provider.SearchAsync(
            BaseUri,
            "zombie",
            MediaType.Book);
        Assert.Empty(nonMovie);
        Assert.Empty(handler.Requests);
    }

    [Fact]
    public async Task DirectoryFiltersGenreAndYearNativelyAndPages()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new JkAnimeApiContentProvider(httpClient);

        var first = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 2020));

        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.Genre);
        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.ReleaseYear);
        Assert.True(provider.SearchCapabilities.SupportsFilterOnlySearch);
        var firstItem = Assert.Single(first.Items);
        Assert.Equal("tower-of-god", firstItem.ExternalId);
        Assert.Equal(2020, firstItem.PreviewItem.ReleaseYear);
        Assert.Equal("En emision", firstItem.PreviewItem.PublicationStatus);
        Assert.Contains("accion", firstItem.PreviewItem.Genres);
        Assert.Equal("2", first.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.AbsolutePath == "/directorio" &&
                request.Uri.Query.Contains(
                    "genero=accion",
                    StringComparison.Ordinal) &&
                request.Uri.Query.Contains(
                    "fecha=2020",
                    StringComparison.Ordinal));

        var second = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 2020,
                continuationToken: first.ContinuationToken));

        Assert.Equal(
            "jujutsu-kaisen",
            Assert.Single(second.Items).ExternalId);
        Assert.Null(second.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.Query.Contains("p=2", StringComparison.Ordinal));
    }

    [Fact]
    public async Task DirectoryGenreMapIsReadFromThePublishedSelect()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new JkAnimeApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Western));

        Assert.Empty(page.Items);
        Assert.Single(
            handler.Requests,
            static request =>
                request.Uri.AbsolutePath == "/directorio" &&
                request.Uri.Query.Length == 0);
    }

    [Fact]
    public async Task DirectoryReadsCurrentScriptedAnimePayload()
    {
        var handler = new JkAnimeHandler
        {
            DirectoryHtmlOverride = JkAnimeHandler.ScriptedDirectoryHtml
        };
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider = new JkAnimeApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(releaseYear: 2026));

        var item = Assert.Single(page.Items);
        Assert.Equal("bleach-final-arc", item.ExternalId);
        Assert.Equal("Bleach: Final Arc", item.PreviewItem.Name);
        Assert.Equal("anime_series", item.PreviewItem.Subtype);
        Assert.Equal("Concluido", item.PreviewItem.PublicationStatus);
        Assert.Equal(
            "https://cdn.jkdesa.com/assets/images/animes/image/bleach-final.jpg",
            item.PreviewItem.ThumbnailRef);
        Assert.Empty(item.PreviewItem.Genres);
    }

    [Fact]
    public async Task LoadItemReturnsSeriesMetadataAndEpisodeUnits()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "zombie-land-saga",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_series",
                Name = "Zombie Land Saga"
            });

        var item = await provider.LoadItemAsync(BaseUri, searchItem);

        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });

        Assert.Equal("Zombie Land Saga", item.CatalogItem.Name);
        Assert.Equal("anime_series", item.CatalogItem.Subtype);
        Assert.Equal("2018", item.CatalogItem.ReleaseDate);
        Assert.Equal(24 * 60, item.CatalogItem.RuntimeSeconds);
        Assert.Equal("HD", item.CatalogItem.ContentRating);
        Assert.Equal("Concluido", item.CatalogItem.PublicationStatus);
        Assert.Equal("Comedia", Assert.Single(item.CatalogItem.Genres));
        Assert.Equal(
            "Sakura Minamoto dreams of becoming an idol until a zombie apocalypse disrupts her plans.",
            item.CatalogItem.Description);
        Assert.Equal(
            "https://cdn.jkdesa.com/assets/images/animes/image/zombie.webp",
            item.CatalogItem.ThumbnailRef);
        Assert.Equal(
            ["1", "2", "3"],
            item.CatalogItem.Units.Select(
                static unit => unit.DisplayNumber));
        Assert.All(
            item.CatalogItem.Units,
            static unit => Assert.Equal("episode", unit.Kind));
    }

    [Fact]
    public async Task LoadItemReturnsPlayableMovieUnit()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "akira",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_movie",
                Name = "Akira"
            });

        var item = await provider.LoadItemAsync(BaseUri, searchItem);

        Assert.Equal("Akira", item.CatalogItem.Name);
        Assert.Equal("anime_movie", item.CatalogItem.Subtype);
        Assert.Equal("1988", item.CatalogItem.ReleaseDate);
        Assert.Equal(124 * 60, item.CatalogItem.RuntimeSeconds);
        Assert.Equal(
            "A biker leader tries to save his friend from a government experiment.",
            item.CatalogItem.Description);
        var unit = Assert.Single(item.CatalogItem.Units);
        Assert.Equal("1", unit.DisplayNumber);

        var videos = await provider.LoadUnitVideosAsync(
            BaseUri,
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: null));
        var source = Assert.Single(videos!.Sources);
        Assert.Equal(
            "https://hls.jkdesa.com/stream/akira/master.m3u8",
            source.PlaybackUri.AbsoluteUri);
    }

    [Fact]
    public async Task LoadUnitVideosExposesJkPlayerAtobSourceAsDirectMedia()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "zombie-land-saga",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_series",
                Name = "Zombie Land Saga"
            });

        var videos = await provider.LoadUnitVideosAsync(
            BaseUri,
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: null));

        Assert.NotNull(videos);
        var jkPlayer = Assert.Single(
            videos.Sources,
            static source => source.Label == "JKPlayer");
        Assert.Equal(
            "https://hls.jkdesa.com/stream/zombie-land-saga-1/master.m3u8",
            jkPlayer.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", jkPlayer.MimeType);
        Assert.Equal(ApiContentVideoSourceKind.DirectMedia, jkPlayer.Kind);
        Assert.Equal(
            "https://jkanime.example/jkplayer/",
            jkPlayer.Referrer?.AbsoluteUri);
        Assert.Contains(
            videos.Sources,
            static source =>
                source.Label == "VOE" &&
                source.PlaybackUri.Host == "voe.sx");
        Assert.Contains(
            videos.Sources,
            static source =>
                source.Label == "Streamwish" &&
                source.PlaybackUri.Host == "streamwish.to");
        Assert.DoesNotContain(
            videos.Sources,
            static source => source.PlaybackUri.Host == "0.0.0.0");
    }

    [Fact]
    public async Task LoadUnitVideosReadsCurrentRawServerArrayAndIframeLiteral()
    {
        var handler = new JkAnimeHandler
        {
            EpisodeHtmlOverride = JkAnimeHandler.CurrentEpisodeHtml
        };
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "zombie-land-saga",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_series",
                Name = "Zombie Land Saga"
            });

        var videos = await provider.LoadUnitVideosAsync(
            BaseUri,
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: null));

        Assert.NotNull(videos);
        Assert.Contains(
            videos.Sources,
            static source =>
                source.Label == "JKPlayer 0" &&
                source.PlaybackUri.AbsoluteUri ==
                    "https://jkanime.example/jkplayer/current-episode");
        Assert.Contains(
            videos.Sources,
            static source =>
                source.Label == "VOE" &&
                source.PlaybackUri.AbsoluteUri ==
                    "https://voe.sx/e/abcdef");
        Assert.Contains(
            videos.Sources,
            static source =>
                source.Label == "Streamwish" &&
                source.PlaybackUri.AbsoluteUri ==
                    "https://streamwish.to/e/abcdef");
    }

    [Fact]
    public async Task LoadUnitVideosReadsHlsFromCurrentJkPlayerDocument()
    {
        var handler = new JkAnimeHandler
        {
            EpisodeHtmlOverride = JkAnimeHandler.NestedPlayerEpisodeHtml,
            PlayerHtmlOverride = JkAnimeHandler.NestedPlayerHtml
        };
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "zombie-land-saga",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_series",
                Name = "Zombie Land Saga"
            });

        var videos = await provider.LoadUnitVideosAsync(
            BaseUri,
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: null));

        var source = Assert.Single(
            videos!.Sources,
            static candidate => candidate.Label == "JKPlayer");
        Assert.Equal(
            "https://nika.playmudos.com/signed/master.m3u8?st=1&e=2",
            source.PlaybackUri.AbsoluteUri);
        Assert.Equal(
            "https://jkanime.example/jkplayer/um?e=1&t=2&op=3",
            source.Referrer?.AbsoluteUri);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.AbsolutePath == "/jkplayer/um" &&
                request.Referrer?.AbsolutePath ==
                    "/zombie-land-saga/1/");
    }

    [Fact]
    public async Task LoadUnitVideosFallsBackToRawM3u8WhenAtobIsMissing()
    {
        var handler = new JkAnimeHandler
        {
            EpisodeHtmlOverride = JkAnimeHandler.RawHlsEpisodeHtml
        };
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "zombie-land-saga",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_series",
                Name = "Zombie Land Saga"
            });

        var videos = await provider.LoadUnitVideosAsync(
            BaseUri,
            searchItem,
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "2",
                seasonNumber: null));

        var jkPlayer = Assert.Single(
            videos!.Sources,
            static source => source.Label == "JKPlayer");
        Assert.Equal(
            "https://hls.jkdesa.com/stream/zombie-land-saga-2/master.m3u8",
            jkPlayer.PlaybackUri.AbsoluteUri);
        Assert.Equal("application/vnd.apple.mpegurl", jkPlayer.MimeType);
    }

    [Fact]
    public async Task DailyUpdatesReturnLatestHomepageEpisodes()
    {
        var handler = new JkAnimeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);

        var update = Assert.Single(
            updates,
            static item => item.ExternalId == "zombie-land-saga");
        Assert.Equal("Zombie Land Saga", update.PreviewItem.Name);
        Assert.Equal(
            ["12"],
            update.PreviewItem.Units.Select(
                static unit => unit.DisplayNumber));
    }

    [Fact]
    public async Task PostEndpointIncludesCsrfTokenAndRecoversAfter419()
    {
        var handler = new JkAnimeHandler
        {
            ForceAjaxTokenExpired = true
        };
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);

        var results = await provider.SearchAsync(
            BaseUri,
            "akira",
            MediaType.Movie);

        Assert.NotEmpty(results);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.AbsolutePath == "/" &&
                request.Method == HttpMethod.Get);
        Assert.Contains(
            handler.AjaxRequests,
            static request =>
                request.Body != null &&
                (request.Body.Contains("\"_token\"=test-csrf-token") ||
                 request.Body.Contains("_token=test-csrf-token")));
        Assert.Equal(2, handler.HomepageRequestCount);
        Assert.Contains(
            handler.AjaxRequests,
            static request =>
                request.Body?.Contains(
                    "_token=refreshed-csrf-token",
                    StringComparison.Ordinal) is true);
    }

    [Fact]
    public void TrustedArtworkOriginsIncludeJkanimeAndJkdesaCdn()
    {
        var provider = new JkAnimeApiContentProvider(new HttpClient());

        var origins = provider.GetTrustedArtworkOrigins(BaseUri);

        Assert.Contains(
            origins,
            static uri =>
                uri.Host == "jkanime.example" &&
                uri.AbsoluteUri.EndsWith("/"));
        Assert.Contains(
            origins,
            static uri => uri.Host == "cdn.jkdesa.com");
    }

    private sealed class JkAnimeHandler : HttpMessageHandler
    {
        private const string EncodedAtob =
            "aHR0cHM6Ly9obHMuamtkZXNhLmNvbS9zdHJlYW0vem9tYmllLWxhbmQtc2FnYS0xL21hc3Rlci5tM3U4";
        private const string DecodedAtob =
            "https://hls.jkdesa.com/stream/zombie-land-saga-1/master.m3u8";
        private const string VoeRemote =
            "aHR0cHM6Ly92b2Uuc3gvZS9hYmNkZWY=";
        private const string StreamwishRemote =
            "aHR0cHM6Ly9zdHJlYW13aXNoLnRvL2UvYWJjZGVm";

        public ConcurrentBag<RecordedRequest> Requests { get; } = [];
        public ConcurrentBag<RecordedRequest> AjaxRequests { get; } = [];
        public bool ForceAjaxTokenExpired { get; init; }
        public string? EpisodeHtmlOverride { get; init; }
        public string? PlayerHtmlOverride { get; init; }
        public string? DirectoryHtmlOverride { get; init; }
        public int HomepageRequestCount { get; private set; }
        public int SearchCallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri
                ?? throw new InvalidOperationException("Missing request URI.");
            string? body = null;
            if (request.Content is not null)
            {
                body = await request.Content.ReadAsStringAsync(cancellationToken);
            }

            Requests.Add(new RecordedRequest(
                request.Method,
                uri,
                body,
                request.Headers.Referrer));

            if (uri.IdnHost is "hls.jkdesa.com" or "nika.playmudos.com")
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new ByteArrayContent(
                        Encoding.UTF8.GetBytes("#EXTM3U\n#EXT-X-ENDLIST\n"))
                };
            }

            if (request.Method == HttpMethod.Post)
            {
                return await HandlePostAsync(request, uri, body, cancellationToken);
            }

            return HandleGet(uri);
        }

        private async Task<HttpResponseMessage> HandlePostAsync(
            HttpRequestMessage request,
            Uri uri,
            string? body,
            CancellationToken cancellationToken)
        {
            AjaxRequests.Add(new RecordedRequest(
                request.Method,
                uri,
                body,
                request.Headers.Referrer));
            if (uri.AbsolutePath.StartsWith("/ajax/episodes/", StringComparison.OrdinalIgnoreCase))
            {
                var parts = uri.AbsolutePath.Split(
                    '/',
                    StringSplitOptions.RemoveEmptyEntries);
                var page = int.Parse(
                    parts[^1],
                    CultureInfo.InvariantCulture);
                return JsonResponse(BuildEpisodePageJson(page));
            }

            if (uri.AbsolutePath == "/ajax_search")
            {
                SearchCallCount++;
                var query = ExtractFormValue(body, "q");
                if (ForceAjaxTokenExpired && SearchCallCount == 1)
                {
                    return new HttpResponseMessage(
                        (HttpStatusCode)419)
                    {
                        RequestMessage = request
                    };
                }

                if (ForceAjaxTokenExpired &&
                    body?.Contains(
                        "_token=refreshed-csrf-token",
                        StringComparison.Ordinal) is not true)
                {
                    return new HttpResponseMessage((HttpStatusCode)419)
                    {
                        RequestMessage = request
                    };
                }

                return JsonResponse(BuildSearchJson(query));
            }

            throw new InvalidOperationException(
                $"Unexpected JKAnime POST: {uri}");
        }

        private static string? ExtractFormValue(string? body, string key)
        {
            if (string.IsNullOrEmpty(body))
            {
                return null;
            }

            foreach (var pair in body.Split('&'))
            {
                var equalsIndex = pair.IndexOf('=');
                if (equalsIndex < 0)
                {
                    continue;
                }

                var name = Uri.UnescapeDataString(pair[..equalsIndex]);
                if (string.Equals(name, key, StringComparison.Ordinal))
                {
                    return Uri.UnescapeDataString(
                        pair[(equalsIndex + 1)..]
                            .Replace('+', ' '));
                }
            }

            return null;
        }

        private static string BuildSearchJson(string? query)
        {
            var normalized = query?.Trim() ?? string.Empty;
            if (normalized.Length == 0)
            {
                return "[]";
            }

            var matches = normalized.Contains("zombie", StringComparison.OrdinalIgnoreCase)
                ? new[] { ("zombie-land-saga", "Zombie Land Saga", "zombie.webp", "Anime") }
                : Array.Empty<(string, string, string, string)>();
            if (normalized.Contains("akira", StringComparison.OrdinalIgnoreCase))
            {
                matches = matches
                    .Append(("akira", "Akira", "akira.webp", "Pelicula"))
                    .ToArray();
            }

            if (matches.Length == 0)
            {
                return "[]";
            }

            var entries = string.Join(
                ",",
                matches.Select(static match =>
                    $$"""
                    { "slug": "{{match.Item1}}", "title": "{{match.Item2}}", "image": "https://cdn.jkdesa.com/assets/images/animes/image/{{match.Item3}}", "type": "{{match.Item4}}" }
                    """));
            return $"[ {entries} ]";
        }

        private HttpResponseMessage HandleGet(Uri uri)
        {
            var html = (uri.IdnHost, uri.AbsolutePath, uri.Query) switch
            {
                ("jkanime.example", "/", _) => GetHomepageHtml(),
                ("jkanime.example", "/directorio", var query)
                    when query.Contains("p=2", StringComparison.Ordinal)
                        => DirectoryPageTwoHtml,
                ("jkanime.example", "/directorio", _)
                    => DirectoryHtmlOverride ?? DirectoryHtml,
                ("jkanime.example", "/jkplayer/um", _)
                    => PlayerHtmlOverride ?? "<html></html>",
                ("jkanime.example", "/jkplayer/current-episode", _)
                    => PlayerHtmlOverride ?? "<html></html>",
                ("jkanime.example", "/zombie-land-saga/", "")
                    => SeriesHtml,
                ("jkanime.example", "/akira/", "")
                    => MovieHtml,
                ("jkanime.example", "/akira/1/", "")
                    => MovieEpisodeHtml,
                ("jkanime.example", "/zombie-land-saga/1/", "")
                    when EpisodeHtmlOverride is not null
                        => EpisodeHtmlOverride,
                ("jkanime.example", "/zombie-land-saga/2/", "")
                    when EpisodeHtmlOverride is not null
                        => EpisodeHtmlOverride,
                ("jkanime.example", "/zombie-land-saga/1/", "")
                    => EpisodeHtml,
                ("jkanime.example", "/zombie-land-saga/2/", "")
                    => RawHlsEpisodeHtml,
                _ => throw new InvalidOperationException(
                    $"Unexpected JKAnime GET: {uri}")
            };
            return HtmlResponse(html);
        }

        private string GetHomepageHtml()
        {
            HomepageRequestCount++;
            var token = HomepageRequestCount == 1
                ? "test-csrf-token"
                : "refreshed-csrf-token";
            return HomepageHtml.Replace(
                "test-csrf-token",
                token,
                StringComparison.Ordinal);
        }

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

        private static string BuildEpisodePageJson(int page)
        {
            return page switch
            {
                1 => """
                    {
                      "data": [
                        { "number": 1, "title": "Good Morning", "image": "zombie-1.webp", "timestamp": "2018-10-04 12:00:00" },
                        { "number": 2, "title": "I, Kakeru", "image": "zombie-2.webp", "timestamp": "2018-10-11 12:00:00" },
                        { "number": 3, "title": "Yes, Dead or Alive", "image": "zombie-3.webp", "timestamp": "2018-10-18 12:00:00" }
                      ],
                      "last_page": 1,
                      "total": 3
                    }
                    """,
                _ => """{ "data": [], "last_page": 1, "total": 3 }"""
            };
        }

        private const string HomepageHtml = """
            <!doctype html>
            <html>
              <head>
                <meta name="csrf-token" content="test-csrf-token">
                <title>JKAnime</title>
              </head>
              <body>
                <section class="hero"></section>
                <section class="latest">
                  <article class="card">
                    <a href="/zombie-land-saga/12/">
                      <h3 class="title">Zombie Land Saga</h3>
                      <img data-src="https://cdn.jkdesa.com/assets/images/animes/image/zombie.webp" alt="Zombie Land Saga">
                    </a>
                  </article>
                </section>
              </body>
            </html>
            """;

        private const string DirectoryHtml = """
            <!doctype html>
            <html>
              <head>
                <meta name="csrf-token" content="test-csrf-token">
              </head>
              <body>
                <form action="/directorio" method="get">
                  <select name="genero">
                    <option value="">Todos</option>
                    <option value="accion">Acción</option>
                    <option value="comedia">Comedia</option>
                    <option value="cosas-de-la-vida">Cosas de la vida</option>
                  </select>
                  <select name="fecha">
                    <option value="2020">2020</option>
                  </select>
                </form>
                <a href="/tower-of-god/">
                  <div class="d-thumb">
                    <img src="https://cdn.jkdesa.com/assets/images/animes/image/tower.webp"
                         alt="Tower of God">
                    <span class="badge">Serie</span>
                    <span class="badge">En emision</span>
                    <span class="year">2020</span>
                  </div>
                  <h5 class="card-title">Tower of God</h5>
                </a>
                <a href="?genero=accion&amp;fecha=2020&amp;p=2">2</a>
              </body>
            </html>
            """;

        private const string DirectoryPageTwoHtml = """
            <!doctype html>
            <html>
              <body>
                <a href="/jujutsu-kaisen/">
                  <div class="d-thumb">
                    <img data-src="https://cdn.jkdesa.com/assets/images/animes/image/jujutsu.webp"
                         alt="Jujutsu Kaisen">
                    <span class="badge">Serie</span>
                    <span class="badge">Concluido</span>
                    <span class="year">2020</span>
                  </div>
                  <h5 class="card-title">Jujutsu Kaisen</h5>
                </a>
                <a href="?genero=accion&amp;fecha=2020&amp;p=1">1</a>
              </body>
            </html>
            """;

        public const string ScriptedDirectoryHtml = """
            <!doctype html>
            <html>
              <head><meta name="csrf-token" content="test-csrf-token"></head>
              <body>
                <select name="genero">
                  <option value="accion">AcciÃ³n</option>
                </select>
                <div id="anime-list"></div>
                <script>
                var animes = {"current_page":1,"data":[{
                  "id":4760,
                  "title":"Bleach: Final Arc",
                  "image":"https:\/\/cdn.jkdesa.com\/assets\/images\/animes\/image\/bleach-final.jpg",
                  "slug":"bleach-final-arc",
                  "type":"TV",
                  "tipo":"Serie",
                  "status":"Concluido"
                }],"next_page_url":null};
                </script>
              </body>
            </html>
            """;

        private const string SeriesHtml = """
            <!doctype html>
            <html>
              <head>
                <meta name="csrf-token" content="test-csrf-token">
                <meta property="og:title" content="Zombie Land Saga">
                <meta property="og:image" content="https://cdn.jkdesa.com/assets/images/animes/image/zombie.webp">
                <meta name="description" content="Sakura Minamoto dreams of becoming an idol until a zombie apocalypse disrupts her plans.">
              </head>
              <body>
                <div class="anime_pic">
                  <img class="poster" src="https://cdn.jkdesa.com/assets/images/animes/image/zombie.webp" alt="Zombie Land Saga">
                </div>
                <div class="anime_info">
                  <h3 class="titulo">Zombie Land Saga</h3>
                  <p class="sinopsis">Sakura Minamoto dreams of becoming an idol until a zombie apocalypse disrupts her plans.</p>
                </div>
                <ul class="anime_data">
                  <li><span>Tipo</span>Anime</li>
                  <li><span>Generos</span><a href="/genero/comedia">Comedia</a></li>
                  <li><span>Duracion</span>24 min</li>
                  <li><span>Calidad</span>HD</li>
                  <li><span>Estreno</span>2018</li>
                  <li><span>Estado</span>Concluido</li>
                </ul>
                <span class="guardar_anime" data-anime="4242"></span>
              </body>
            </html>
            """;

        private const string MovieHtml = """
            <!doctype html>
            <html>
              <head>
                <meta name="csrf-token" content="test-csrf-token">
                <meta property="og:title" content="Akira">
                <meta name="description" content="A biker leader tries to save his friend from a government experiment.">
              </head>
              <body>
                <div class="movpic">
                  <img class="poster" src="https://cdn.jkdesa.com/assets/images/animes/image/akira.webp" alt="Akira">
                </div>
                <div class="anime_info">
                  <h3 class="titulo">Akira</h3>
                  <p class="sinopsis">A biker leader tries to save his friend from a government experiment.</p>
                </div>
                <ul class="anime_data">
                  <li><span>Tipo</span>Pelicula</li>
                  <li><span>Duracion</span>124 min</li>
                  <li><span>Calidad</span>HD</li>
                  <li><span>Estreno</span>1988</li>
                </ul>
                <span class="guardar_anime" data-anime="1001"></span>
              </body>
            </html>
            """;

        private const string EpisodeHtml = $$"""
            <!doctype html>
            <html>
              <head>
                <meta name="csrf-token" content="test-csrf-token">
              </head>
              <body>
                <script>
                var servers = '[{"server":"VOE","remote":"{{VoeRemote}}"},{"server":"Streamwish","remote":"{{StreamwishRemote}}"}]';
                video[7] = { html: '<iframe src="http://0.0.0.0/admin"></iframe>' };
                video[8] = { html: '<iframe src="http://["></iframe>' };
                </script>
                <script>
                jwplayer("player").setup({
                    sources: [{ url: atob('{{EncodedAtob}}') }]
                });
                </script>
                <video id="player"></video>
              </body>
            </html>
            """;

        private const string MovieEpisodeHtml = """
            <!doctype html>
            <html>
              <body>
                <script>
                var servers = '[]';
                var sources = [{
                    file: 'https://hls.jkdesa.com/stream/akira/master.m3u8',
                    type: 'hls'
                }];
                </script>
              </body>
            </html>
            """;

        public const string RawHlsEpisodeHtml = $$"""
            <!doctype html>
            <html>
              <head>
                <meta name="csrf-token" content="test-csrf-token">
              </head>
              <body>
                <script>
                var servers = '[]';
                var sources = [{
                    file: 'https://hls.jkdesa.com/stream/zombie-land-saga-2/master.m3u8',
                    type: 'hls'
                }];
                </script>
              </body>
            </html>
            """;

        public const string CurrentEpisodeHtml = $$"""
            <!doctype html>
            <html>
              <body>
                <script>
                video[0] = '<iframe src="https://jkanime.example/jkplayer/current-episode"></iframe>';
                var servers = [{"remote":"{{VoeRemote}}","server":"VOE","slug":"voe"},{"remote":"{{StreamwishRemote}}","server":"Streamwish","slug":"streamwish"}];
                </script>
              </body>
            </html>
            """;

        public const string NestedPlayerEpisodeHtml = """
            <!doctype html>
            <html>
              <body>
                <script>
                var video = [];
                video[0] = '<iframe class="player_conte" src="https://jkanime.example/jkplayer/um?e=1&t=2&op=3"></iframe>';
                </script>
              </body>
            </html>
            """;

        public const string NestedPlayerHtml = """
            <!doctype html>
            <html>
              <body>
                <script>
                window.dp = new DPlayer({
                  video: {
                    url: 'https://nika.playmudos.com/signed/master.m3u8?st=1&e=2',
                    type: 'customHls'
                  }
                });
                </script>
              </body>
            </html>
            """;
    }

    public sealed record RecordedRequest(
        HttpMethod Method,
        Uri Uri,
        string? Body,
        Uri? Referrer);
}

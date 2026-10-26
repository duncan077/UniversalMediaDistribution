using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Guards the provider shapes that broke against what the live sites actually
/// serve, using markup and payloads copied from 2026-08-01 captures.
/// <para>
/// The existing per-provider suites kept passing while every one of these
/// failed in the field, because their fixtures were written from older
/// captures and encoded the contract the sites had already left. Each test
/// here pins one of those differences, so the next drift fails a test instead
/// of only failing a user.
/// </para>
/// </summary>
public sealed class ProviderCapturedShapeRegressionTests
{
    [Fact]
    public async Task ZonaTmoImportsAMangaTypeTitleWithoutDuplicateCategories()
    {
        // The captured detail page renders the badge upper cased, so the type
        // label collides with the literal "Manga" category the provider adds.
        // The old fixture used MANHWA, which does not collide, so this only
        // ever failed on the site's dominant type.
        var handler = new ZonaTmoMangaHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new ZonaTmoApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            new Uri("https://zonatmo.example/"),
            new ApiContentSearchRequest(query: "tensei", pageSize: 10));
        var result = Assert.Single(page.Items);

        var item = await provider.LoadItemAsync(
            new Uri("https://zonatmo.example/"),
            result);

        // Would throw "categories[1] is duplicated" before the fix.
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal(
            ["Manga", "Shounen"],
            item.CatalogItem.Categories);
    }

    [Fact]
    public void ZonaTmoGenreMapPrefersTheTaxonomyTitlesAreActuallyTagatedWith()
    {
        // The checkbox list is alphabetical, so the later English import
        // ("Adventure" = 77) precedes the original Spanish entry
        // ("Aventura" = 2) in document order. Real title pages only ever link
        // the low-numbered ids, so document order picked a token that matches
        // nothing.
        var map = ZonaTmoApiContentProvider.ParseGenreMap(
            """
            <form id="form-filters">
              <input type="checkbox" name="genders[]" id="gender_1" value="1">
              <label for="gender_1">Acción</label>
              <input type="checkbox" name="genders[]" id="gender_77" value="77">
              <label for="gender_77">Adventure</label>
              <input type="checkbox" name="genders[]" id="gender_2" value="2">
              <label for="gender_2">Aventura</label>
              <input type="checkbox" name="genders[]" id="gender_46" value="46">
              <label for="gender_46">Deporte</label>
              <input type="checkbox" name="genders[]" id="gender_11" value="11">
              <label for="gender_11">Deportes</label>
            </form>
            """);

        Assert.True(map.TryGetToken(ApiContentGenre.Adventure, out var adventure));
        Assert.Equal("2", adventure);
        Assert.True(map.TryGetToken(ApiContentGenre.Sports, out var sports));
        Assert.Equal("11", sports);
    }

    [Fact]
    public async Task JkAnimeReadsDailyUpdatesFromAbsoluteEpisodeLinks()
    {
        // The home page switched to absolute hrefs. Splitting one of those
        // directly put "jkanime.example" where the episode number was read
        // from, so every card was skipped and the feed came back empty.
        var handler = new JkAnimeHomeHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);

        var updates = await provider.LoadDailyUpdatesAsync(
            new Uri("https://jkanime.example/"));

        var update = Assert.Single(updates);
        Assert.Equal("kami-no-shizuku", update.ExternalId);
        Assert.Equal("Kami no Shizuku", update.PreviewItem.Name);
    }

    [Fact]
    public async Task UchuujinImportsAManhwaAsARegisteredSubtype()
    {
        // "manhwa" and "manhua" are not registered book subtypes, so returning
        // them verbatim made every Korean or Chinese series throw on import.
        var handler = new UchuujinManhwaHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new UchuujinMangasApiContentProvider(httpClient);

        var results = await provider.SearchAsync(
            new Uri("https://uchuujin.example/"),
            "Prometida");
        var item = await provider.LoadItemAsync(
            new Uri("https://uchuujin.example/"),
            Assert.Single(results));

        // Would throw "Unknown subtype for media type 'Book'" before the fix.
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("manga", item.CatalogItem.Subtype);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task DoramasflixFilteredSearchSendsValidGraphQl(
        bool byGenre,
        bool byYear)
    {
        // The query declared $genre and $year unconditionally but referenced
        // only the filters that were supplied. GraphQL rejects an operation
        // carrying a variable it never uses, so filtering by genre alone or
        // year alone failed validation and the catalogue came back empty.
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            new Uri("https://doramasflix.example/"),
            new ApiContentSearchRequest(
                query: null,
                genre: byGenre ? ApiContentGenre.Drama : null,
                releaseYear: byYear ? 2024 : null,
                pageSize: 20));

        Assert.Empty(handler.ValidationErrors);
        Assert.NotEmpty(page.Items);
    }

    [Fact]
    public async Task DoramasflixGenreLookupOnlyAsksForFieldsGenreStillHas()
    {
        // Genre lost name_es. Asking for a field a type no longer has fails
        // the whole query with HTTP 400 rather than returning null, so the
        // genre map came back empty and every genre filter threw.
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(httpClient);

        _ = await provider.SearchAsync(
            new Uri("https://doramasflix.example/"),
            new ApiContentSearchRequest(
                query: null,
                genre: ApiContentGenre.Drama,
                pageSize: 20));

        var genreQuery = Assert.Single(
            handler.Queries,
            static query => query.Contains("listGenres", StringComparison.Ordinal));
        Assert.DoesNotContain("name_es", genreQuery, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(true, "DirectMedia", "https://player.zilla-networks.com/m3u8/abc123")]
    [InlineData(false, "EmbeddedWeb", "https://player.zilla-networks.com/play/abc123")]
    public async Task AnimeTmoOnlyOffersDirectHlsWhenItsSegmentsCanBeRead(
        bool segmentsReachable,
        string expectedKind,
        string expectedUri)
    {
        // The host serves the playlist to anyone but puts the segments behind
        // a Cloudflare block. A player then shows the full duration and stalls
        // forever, because every segment answers 403 — a video that loads but
        // never starts.
        var handler = new AnimeTmoHlsHandler
        {
            SegmentsReachable = segmentsReachable
        };
        using var httpClient = new HttpClient(handler);
        var provider = new AnimeTmoApiContentProvider(httpClient);
        var baseUri = new Uri("https://animetmo.example/");

        var results = await provider.SearchAsync(baseUri, "Akira");
        var videos = await provider.LoadUnitVideosAsync(
            baseUri,
            Assert.Single(results),
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: null));

        Assert.NotNull(videos);
        var hls = Assert.Single(videos.Sources);
        Assert.Equal(expectedKind, hls.Kind.ToString());
        Assert.Equal(expectedUri, hls.PlaybackUri.AbsoluteUri);
    }

    private sealed class AnimeTmoHlsHandler : HttpMessageHandler
    {
        public bool SegmentsReachable { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            if (uri.AbsolutePath.StartsWith("/segs/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(
                    SegmentsReachable
                        ? HttpStatusCode.PartialContent
                        : HttpStatusCode.Forbidden)
                {
                    RequestMessage = request,
                    Content = new StringContent("x", Encoding.UTF8, "video/mp4")
                });
            }

            var (content, mediaType) = uri.AbsolutePath switch
            {
                "/wp-content/uploads/az-catalog.json" => (
                    CatalogJson, "application/json"),
                "/anime/akira/" => (DetailHtml, "text/html"),
                "/ver/akira/1" => (EpisodeHtml, "text/html"),
                "/m3u8/abc123" => (
                    PlaylistText, "application/vnd.apple.mpegurl"),
                _ => (null, null)
            };
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                }
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        content,
                        Encoding.UTF8,
                        mediaType!)
                });
        }

        private const string CatalogJson =
            """
            [{
              "id": 1,
              "title": "Akira",
              "slug": "akira",
              "url": "https://animetmo.example/anime/akira/",
              "cover": "https://animetmo.example/covers/akira.webp",
              "year": "1988",
              "type_name": "TV",
              "type_slug": "tv",
              "stat_name": "Finalizado",
              "genres": ["accion"]
            }]
            """;

        private const string DetailHtml =
            """
            <!doctype html>
            <h1 class="element-title">Akira</h1>
            <h1 class="book-type bg-tv">TV</h1>
            <p class="element-description" id="anime-synopsis">A biker gang.</p>
            <a class="az-ep-card" data-episode-number="1" href="/ver/akira/1">Episodio 1</a>
            """;

        private const string EpisodeHtml =
            """
            <!doctype html>
            <div class="az-servers-box">
              <button class="az-srv-btn active"
                      data-url="https://player.zilla-networks.com/play/abc123"
                      data-type="SUB">HLS</button>
            </div>
            """;

        // The real playlist names its segments with a .html extension.
        private const string PlaylistText =
            """
            #EXTM3U
            #EXT-X-VERSION:7
            #EXT-X-PLAYLIST-TYPE:VOD
            #EXT-X-MAP:URI="https://player.zilla-networks.com/segs/abc123/init.html"
            #EXTINF:10.0,
            https://player.zilla-networks.com/segs/abc123/000.html
            #EXT-X-ENDLIST
            """;
    }

    [Fact]
    public async Task DoramasflixOffersEveryMovieServerNotOnePerLanguage()
    {
        // getMovieLinks returns a single link per language, so a movie with
        // three Spanish servers surfaced one of them. The movie record's own
        // links_online carries the full set, which is what the site lists.
        var handler = new DoramasflixMovieLinksHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(httpClient);

        var videos = await provider.LoadUnitVideosAsync(
            new Uri("https://doramasflix.example/"),
            new ApiContentSearchItem(
                "peliculas-online/wild-sing",
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "movie",
                    Name = "Wild Sing"
                }),
            new ApiContentUnitRequest(
                ContentUnitKind.Episode,
                "1",
                seasonNumber: null));

        Assert.NotNull(videos);
        Assert.Equal(
            [
                "https://bysekoze.com/e/mfnm9lxswgoh",
                "https://dood.stream/e/q975amqpej96",
                "https://primeload.co/embed/BAdrrPl8rfRq",
                "https://streamtape.com/e/L3zXGW1roKtRQ6j"
            ],
            videos.Sources
                .Select(static source => source.PlaybackUri.AbsoluteUri)
                .OrderBy(static uri => uri, StringComparer.Ordinal)
                .ToArray());
    }

    /// <summary>
    /// Answers the movie flow the way the live gateway does: the links
    /// resolver collapses to one server per language, while the movie record
    /// carries them all.
    /// </summary>
    private sealed class DoramasflixMovieLinksHandler : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            var content = body.Contains("paginationMovie", StringComparison.Ordinal)
                ? CatalogLinksJson
                : body.Contains("getMovieLinks", StringComparison.Ordinal)
                    ? ResolverLinksJson
                    : body.Contains("detailMovie", StringComparison.Ordinal)
                        ? DetailJson
                        : EmptyJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private const string DetailJson =
            """
            {"data":{"detailMovie":{"_id":"6a6cd010237a66370f9a75fd","slug":"wild-sing"}}}
            """;

        // One per language, which is the whole defect.
        private const string ResolverLinksJson =
            """
            {"data":{"getMovieLinks":{"links_online":[
              {"server":"4721","lang":"13109","link":"https://primeload.co/embed/BAdrrPl8rfRq"},
              {"server":"958695","lang":"38","link":"https://bysekoze.com/e/mfnm9lxswgoh"}
            ]}}}
            """;

        private const string CatalogLinksJson =
            """
            {"data":{"paginationMovie":{"items":[{"links_online":[
              {"server":"7286","lang":"13109","link":"https://dood.stream/e/q975amqpej96"},
              {"server":"4721","lang":"13109","link":"https://primeload.co/embed/BAdrrPl8rfRq"},
              {"server":"8309","lang":"13109","link":"https://streamtape.com/e/L3zXGW1roKtRQ6j"},
              {"server":"958695","lang":"38","link":"https://bysekoze.com/e/mfnm9lxswgoh"}
            ]}]}}}
            """;

        private const string EmptyJson = """{"data":{}}""";
    }

    /// <summary>
    /// Stands in for the GraphQL gateway, enforcing the one rule the real
    /// server enforces that a permissive stub would hide: an operation may not
    /// declare a variable it never references.
    /// </summary>
    private sealed class DoramasflixHandler : HttpMessageHandler
    {
        private static readonly Regex DeclaredVariables = new(
            @"query\s+\w+\s*\((?<declarations>[^)]*)\)",
            RegexOptions.Singleline);

        public List<string> Queries { get; } = [];

        public List<string> ValidationErrors { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var body = request.Content is null
                ? "{}"
                : await request.Content.ReadAsStringAsync(cancellationToken);
            using var payload = JsonDocument.Parse(body);
            var query = payload.RootElement.TryGetProperty("query", out var q)
                ? q.GetString() ?? string.Empty
                : string.Empty;
            Queries.Add(query);

            var declarations = DeclaredVariables.Match(query);
            if (declarations.Success)
            {
                var signature = declarations.Groups["declarations"].Value;
                var body_ = query[(declarations.Index + declarations.Length)..];
                foreach (var declaration in signature.Split(
                             ',',
                             StringSplitOptions.RemoveEmptyEntries |
                                 StringSplitOptions.TrimEntries))
                {
                    var name = declaration.Split(':')[0].Trim();
                    if (name.Length > 1 &&
                        !body_.Contains(name, StringComparison.Ordinal))
                    {
                        ValidationErrors.Add(
                            $"Variable \"{name}\" is never used.");
                    }
                }
            }

            if (ValidationErrors.Count > 0)
            {
                return new HttpResponseMessage(HttpStatusCode.BadRequest)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        """{"errors":[{"message":"validation failed"}]}""",
                        Encoding.UTF8,
                        "application/json")
                };
            }

            var content = query.Contains("listGenres", StringComparison.Ordinal)
                ? GenresJson
                : query.Contains("paginationMovie", StringComparison.Ordinal)
                    ? MoviesJson
                    : DoramasJson;
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    "application/json")
            };
        }

        private const string GenresJson =
            """
            {"data":{"listGenres":[
              {"_id":"5f0630afeb20933e4aeb66e0","name":"Drama","slug":"drama"}
            ]}}
            """;

        private const string DoramasJson =
            """
            {"data":{"paginationDorama":{
              "items":[{"_id":"1","slug":"match-point","name":"Match Point","poster_path":"/a.jpg"}],
              "pageInfo":{"hasNextPage":false}}}}
            """;

        private const string MoviesJson =
            """
            {"data":{"paginationMovie":{
              "items":[{"_id":"2","slug":"wild-sing","name":"Wild Sing","poster_path":"/b.jpg"}],
              "pageInfo":{"hasNextPage":false}}}}
            """;
    }

    [Fact]
    public async Task JkAnimeTakesTheTitleFromThePageNotTheSeoTag()
    {
        // ".anime_info h3" was collapsed to the literal class name "h3", so the
        // selector matched nothing and the name fell through to og:title —
        // which carries the site's SEO suffix and ends up as the catalog name.
        var handler = new JkAnimeDetailHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new JkAnimeApiContentProvider(httpClient);
        var baseUri = new Uri("https://jkanime.example/");

        var results = await provider.SearchAsync(baseUri, "One Piece");
        var item = await provider.LoadItemAsync(baseUri, Assert.Single(results));

        Assert.Equal("One Piece", item.CatalogItem.Name);
    }

    private sealed class JkAnimeDetailHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            if (uri.AbsolutePath == "/ajax_search" ||
                uri.AbsolutePath.StartsWith("/ajax/", StringComparison.Ordinal))
            {
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        uri.AbsolutePath == "/ajax_search"
                            ? SearchJson
                            : EpisodesJson,
                        Encoding.UTF8,
                        "application/json")
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    uri.AbsolutePath == "/one-piece/" ? DetailHtml : ShellHtml,
                    Encoding.UTF8,
                    "text/html")
            });
        }

        private const string ShellHtml =
            """
            <!doctype html>
            <meta name="csrf-token" content="tok">
            <html><body></body></html>
            """;

        private const string SearchJson =
            """
            [{"id":1,"slug":"one-piece","title":"One Piece","image":"one-piece.jpg"}]
            """;

        private const string EpisodesJson =
            """
            {"current_page":1,"data":[{"id":1,"number":1,"title":"One Piece - 1","image":"jkvideo_abc123.jpg"}],"last_page":1}
            """;

        // The real page carries the title only inside .anime_info, while
        // og:title carries the SEO string.
        private const string DetailHtml =
            """
            <!doctype html>
            <meta name="csrf-token" content="tok">
            <meta property="og:title" content="One Piece - anime One Piece online JkAnime">
            <div id="guardar-anime" data-anime="4242"></div>
            <div class="anime_info">
              <h3>One Piece</h3>
              <p class="scroll">Pirates seek a treasure.</p>
            </div>
            <script>$.ajax({ url: 'https://jkanime.example/ajax/episodes/4242/'+pag });</script>
            """;
    }

    [Fact]
    public async Task CapibaraReadsEveryChapterAndScopesTheTenantHeader()
    {
        // Two separate breaks. The detail page server-renders only the newest
        // ten-chapter block, so scraping it capped long series at ~10 units.
        // And routes keyed only on a manga slug resolve their tenant from
        // x-organization, answering HTTP 500 without it — but the site-wide
        // catalog call must NOT carry it, or the catalog collapses to one
        // scanlation group.
        var handler = new CapibaraHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new CapibaraTraductorApiContentProvider(httpClient);
        var baseUri = new Uri("https://capibara.example/");

        var page = await provider.SearchAsync(
            baseUri,
            new ApiContentSearchRequest(query: "Blue Lock", pageSize: 10));
        var result = Assert.Single(page.Items);

        var item = await provider.LoadItemAsync(baseUri, result);

        // Chapter 3 is scheduled, so it must not be offered at all.
        Assert.Equal(
            ["1", "2"],
            item.CatalogItem.Units.Select(static unit => unit.DisplayNumber));

        Assert.Contains(
            handler.Requests,
            static request => request.Path == "/api/manga-custom");
        Assert.All(
            handler.Requests.Where(
                static request => request.Path == "/api/manga-custom"),
            static request => Assert.Null(request.Organization));

        var workCall = Assert.Single(
            handler.Requests,
            request => request.Path == "/api/manga-custom/blue-lock");
        Assert.Equal("senshimanga", workCall.Organization);

        var images = await provider.LoadUnitImagesAsync(
            baseUri,
            result,
            new ApiContentUnitRequest(
                ContentUnitKind.Chapter,
                "1",
                seasonNumber: null));
        Assert.NotNull(images);
        Assert.Equal(
            "senshimanga",
            Assert.Single(
                handler.Requests,
                request => request.Path.EndsWith(
                    "/chapter/1/pages",
                    StringComparison.Ordinal))
                .Organization);
    }

    [Fact]
    public async Task CapibaraReportsAnEarlyAccessChapterAsUnavailable()
    {
        // The pages route answers HTTP 200 with a status:false envelope for a
        // chapter that is not out yet. Treating that as a schema error sent
        // the reader an "invalid response" message for a perfectly normal
        // state.
        var handler = new CapibaraHandler { LockChapterOne = true };
        using var httpClient = new HttpClient(handler);
        var provider = new CapibaraTraductorApiContentProvider(httpClient);
        var baseUri = new Uri("https://capibara.example/");

        var page = await provider.SearchAsync(
            baseUri,
            new ApiContentSearchRequest(query: "Blue Lock", pageSize: 10));
        var result = Assert.Single(page.Items);
        _ = await provider.LoadItemAsync(baseUri, result);

        var images = await provider.LoadUnitImagesAsync(
            baseUri,
            result,
            new ApiContentUnitRequest(
                ContentUnitKind.Chapter,
                "1",
                seasonNumber: null));

        Assert.Null(images);
    }

    private sealed record CapturedRequest(string Path, string? Organization);

    private sealed class CapibaraHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        public bool LockChapterOne { get; init; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            var organization =
                request.Headers.TryGetValues("x-organization", out var values)
                    ? values.FirstOrDefault()
                    : null;
            Requests.Add(new CapturedRequest(uri.AbsolutePath, organization));

            var (content, mediaType) = uri.AbsolutePath switch
            {
                "/api/manga-custom" => (CatalogJson, "application/json"),
                "/api/manga-custom/blue-lock" => (WorkJson, "application/json"),
                "/api/manga-custom/blue-lock/chapter/1/pages" => (
                    LockChapterOne ? NotReleasedJson : PagesJson,
                    "application/json"),
                "/senshimanga/manga/blue-lock" => (DetailHtml, "text/html"),
                _ => (null, null)
            };
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        content,
                        Encoding.UTF8,
                        mediaType!)
                });
        }

        // The catalog projection trims chapters to the newest two and carries
        // workType at the top level rather than a nested bookType object.
        private const string CatalogJson =
            """
            {
              "status": true,
              "data": {
                "maxPage": 1,
                "items": [{
                  "id": 96,
                  "title": "Blue Lock",
                  "workType": "manga",
                  "imageUrl": "https://r2.capibara.example/covers/bl.jpg",
                  "status": "ongoing",
                  "manga": { "title": "Blue Lock", "slug": "blue-lock" },
                  "organization": { "name": "SenshiManga", "slug": "senshimanga" },
                  "genres": [{ "name": "Deportes" }],
                  "chapters": [
                    { "number": 2, "title": "Dos", "isUnreleased": false },
                    { "number": 1, "title": "Uno", "isUnreleased": false }
                  ]
                }]
              }
            }
            """;

        // The per-work route returns the complete list, including one chapter
        // that has not been released yet.
        private const string WorkJson =
            """
            {
              "status": true,
              "data": {
                "id": 96,
                "title": "Blue Lock",
                "workType": "manga",
                "manga": { "title": "Blue Lock", "slug": "blue-lock" },
                "organization": { "name": "SenshiManga", "slug": "senshimanga" },
                "chapters": [
                  { "number": 3, "title": "Tres", "isUnreleased": true },
                  { "number": 2, "title": "Dos", "isUnreleased": false },
                  { "number": 1, "title": "Uno", "isUnreleased": false }
                ]
              }
            }
            """;

        // Only the newest block is server-rendered, which is why this is not a
        // usable chapter source.
        private const string DetailHtml =
            """
            <!doctype html>
            <a href="/senshimanga/manga/blue-lock/chapters/2">
              <h4 class="text-white">Dos</h4>
            </a>
            """;

        private const string PagesJson =
            """
            {
              "status": true,
              "data": [
                { "number": 1, "imageUrl": "https://r2.capibara.example/p/bl-1-1.webp" }
              ]
            }
            """;

        private const string NotReleasedJson =
            """
            {
              "status": false,
              "message": "Este capítulo aún no ha sido publicado.",
              "errorType": "not_released"
            }
            """;
    }

    private sealed class ZonaTmoMangaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            var content = uri.AbsolutePath switch
            {
                "/biblioteca" => LibraryHtml,
                "/library/manga/30593/tensei-shitara-slime-datta-ken" =>
                    DetailHtml,
                _ => null
            };
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        content,
                        Encoding.UTF8,
                        "text/html")
                });
        }

        private const string LibraryHtml =
            """
            <!doctype html>
            <div class="element col-6" data-identifier="30593">
              <a href="/library/manga/30593/tensei-shitara-slime-datta-ken">
                <img class="cover-bg-img" src="https://zonatmo.example/storage/covers/30593.webp">
                <h4 class="text-truncate" title="Tensei Shitara Slime Datta Ken">
                  Tensei Shitara Slime Datta Ken
                </h4>
                <span class="book-type badge badge-manga">MANGA</span>
                <span class="book-meta-status meta-success">Publicándose</span>
              </a>
            </div>
            """;

        // Copied from the capture: the badge is upper cased and the demography
        // is a div here, where the library card uses a span.
        private const string DetailHtml =
            """
            <!doctype html>
            <h1 class="element-title my-2">Tensei Shitara Slime Datta Ken</h1>
            <h1 class="book-type bg-manga mt-4">
                MANGA
            </h1>
            <div class="demography shounen" title="Shounen: dirigido a chicos adolescentes"> Shounen </div>
            <img class="book-thumbnail" src="https://storage.zonatmo.example/proxy/30593.webp">
            <p class="element-description" id="manga-synopsis">A slime is reborn.</p>
            <span class="book-status publishing">Publicándose</span>
            <a href="/biblioteca?genders[]=1">Acción</a>
            <ul>
              <li class="list-group-item upload-link" data-chapter-number="144">
                <span class="chapter-number" data-number="144">Capítulo 144</span>
                <a class="btn" href="/view_uploads/1008663">Leer online</a>
              </li>
            </ul>
            """;
    }

    private sealed class JkAnimeHomeHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            return Task.FromResult(uri.AbsolutePath == "/"
                ? new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        HomeHtml,
                        Encoding.UTF8,
                        "text/html")
                }
                : new HttpResponseMessage(HttpStatusCode.NotFound));
        }

        // Copied from the capture, which carries 72 absolute episode hrefs and
        // no relative ones.
        private const string HomeHtml =
            """
            <!doctype html>
            <a href="https://jkanime.example/kami-no-shizuku/17/">
              <div class="d-thumb">
                <img class="card-img-top"
                     src="https://cdn.jkdesa.com/assets/images/animes/video/image/jkvideo_d499.jpg"
                     data-animepic="https://cdn.jkdesa.com/assets/images/animes/image/kami-no-shizuku.jpg">
                <div class="badges badges-top"><span class="badge badge-primary">Ep 17</span></div>
              </div>
              <div class="card-body d-flex flex-column">
                <h5 class="strlimit card-title">Kami no Shizuku</h5>
              </div>
            </a>
            """;
    }

    private sealed class UchuujinManhwaHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            var content = uri.AbsolutePath switch
            {
                "/" => SearchHtml,
                "/manga/mi-prometida-es-muy-mala/" => DetailHtml,
                _ => null
            };
            return Task.FromResult(content is null
                ? new HttpResponseMessage(HttpStatusCode.NotFound)
                : new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(
                        content,
                        Encoding.UTF8,
                        "text/html")
                });
        }

        private const string SearchHtml =
            """
            <div class="listupd">
              <div class="bs"><div class="bsx">
                <a href="https://uchuujin.example/manga/mi-prometida-es-muy-mala/"
                   title="¡Mi Prometida es Muy Mala!">
                  <img class="ts-post-image" src="https://uchuujin.example/covers/p.webp">
                  <div class="tt">¡Mi Prometida es Muy Mala!</div>
                </a>
              </div></div>
            </div>
            """;

        // The capture's home page carries Manhua and Manhwa cards alongside
        // Manga, so this type really does occur.
        private const string DetailHtml =
            """
            <meta property="og:image" content="https://uchuujin.example/covers/p.webp">
            <div class="info-desc bixbox">
              <h1 class="entry-title">¡Mi Prometida es Muy Mala!</h1>
              <span class="mgen"><a href="https://uchuujin.example/genres/romance/">Romance</a></span>
              <div class="entry-content">A frustrating love story.</div>
            </div>
            <div class="imptdt">Estado <i>Ongoing</i></div>
            <div class="imptdt">Tipo <a>Manhwa</a></div>
            <div class="imptdt">Lanzado <i>2024</i></div>
            <div class="eph-num"><a href="https://uchuujin.example/2026/03/19/mi-prometida-capitulo-16/"><span class="chapternum">Capítulo 16</span></a></div>
            """;
    }
}

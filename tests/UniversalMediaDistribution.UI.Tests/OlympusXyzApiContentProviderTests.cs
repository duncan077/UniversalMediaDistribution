using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class OlympusXyzApiContentProviderTests(ITestOutputHelper output)
{
    private static readonly Uri BaseUri =
        new("https://olympus.example/");

    private const string CurrentHomepage =
        """
        <section>
          <h2>Popular del día</h2>
          <figure><a href="/series/comic-popular" title="Popular">Popular</a></figure>
        </section>
        <section>
          <h2>Nuevos capítulos</h2>
          <figure>
            <a href="/series/comic-murim-king" title="The Murim King">
              <span class="cover-glyph">M</span>
              <img src="https://images.olympus.example/covers/daily.webp" loading="lazy">
            </a>
            <figcaption>
              <a href="/series/comic-murim-king" class="chcard-title">The Murim King</a>
              <a href="/series/comic-murim-king/capitulo/3" class="chchip fresh">
                <b>Cap. 3</b><time datetime="2026-07-23T10:00:00Z">1 min</time>
              </a>
              <a href="/series/comic-murim-king/capitulo/2.5" class="chchip">
                <time datetime="2026-07-22T10:00:00Z">2 d</time><b>Cap. 2.5</b>
              </a>
            </figcaption>
          </figure>
        </section>
        <section>
          <h2>Novelas</h2>
          <figure><a href="/series/novel-unrelated" title="A novel">A novel</a></figure>
          <figure><a href="/series/comic-unrelated" title="Unrelated">Unrelated</a></figure>
        </section>
        """;

    [Fact]
    public async Task MapsSeriesChaptersAndReaderPagesAndCachesSeriesList()
    {
        var handler = new OlympusHandler();
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                23,
                12,
                0,
                0,
                TimeSpan.Zero));
        var provider = new OlympusXyzApiContentProvider(
            new HttpClient(handler),
            timeProvider);

        Assert.Empty(await provider.SearchAsync(
            BaseUri,
            "king",
            MediaType.Movie));
        var results = await provider.SearchAsync(
            BaseUri,
            "king",
            MediaType.Book);
        var result = Assert.Single(results);
        Assert.Equal("murim-king", result.ExternalId);
        Assert.Equal("The Murim King", result.PreviewItem.Name);

        _ = await provider.SearchAsync(
            BaseUri,
            "murim",
            MediaType.Book);
        Assert.Equal(1, handler.SeriesListRequestCount);

        timeProvider.Advance(
            TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        _ = await provider.SearchAsync(
            BaseUri,
            "king",
            MediaType.Book);
        Assert.Equal(2, handler.SeriesListRequestCount);

        var item = await provider.LoadItemAsync(
            BaseUri,
            result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("A martial-arts story.", item.CatalogItem.Description);
        Assert.Equal("ongoing", item.CatalogItem.PublicationStatus);
        Assert.Equal(["Action", "Fantasy"], item.CatalogItem.Genres);
        Assert.Collection(
            item.CatalogItem.Units,
            chapter =>
            {
                Assert.Equal("1", chapter.DisplayNumber);
                Assert.Equal("2026-07-20", chapter.ReleaseDate);
            },
            chapter => Assert.Equal("2.5", chapter.DisplayNumber));

        var pages = await provider.LoadUnitImagesAsync(
            BaseUri,
            result,
            new ApiContentUnitRequest(
                ContentUnitKind.Chapter,
                "2.5",
                seasonNumber: null));
        Assert.NotNull(pages);
        Assert.Collection(
            pages.Pages,
            page => Assert.Equal(
                "https://images.olympus.example/c2/001.webp",
                page.Uri.AbsoluteUri),
            page => Assert.Equal(
                "https://images.olympus.example/c2/002.webp",
                page.Uri.AbsoluteUri));
        Assert.Contains(
            provider.GetTrustedArtworkOrigins(BaseUri),
            uri => uri.AbsoluteUri ==
                "https://images.olympus.example/");
        Assert.All(
            handler.Requests,
            request => Assert.Equal(
                "https://olympus.example",
                request.Origin));
    }

    [Fact]
    public async Task DailyUpdatesMapNewestHomepageCards()
    {
        var provider = new OlympusXyzApiContentProvider(
            new HttpClient(new OlympusHandler()));

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);

        var update = Assert.Single(updates);
        Assert.Equal("murim-king", update.ExternalId);
        Assert.Equal("The Murim King", update.PreviewItem.Name);
        Assert.Equal(
            "https://images.olympus.example/covers/daily.webp",
            update.PreviewItem.ThumbnailRef);
        Assert.Collection(
            update.PreviewItem.Units,
            chapter =>
            {
                Assert.Equal("2.5", chapter.DisplayNumber);
                Assert.Equal("2026-07-22", chapter.ReleaseDate);
            },
            chapter =>
            {
                Assert.Equal("3", chapter.DisplayNumber);
                Assert.Equal("2026-07-23", chapter.ReleaseDate);
            });
        Assert.Equal(
            "205",
            Assert.Single(provider.GetKnownUnitRoutes(
                BaseUri,
                update,
                new ApiContentUnitRequest(ContentUnitKind.Chapter, "2.5", null))).Route);
    }

    [Theory]
    [InlineData("Nuevos capítulos")]
    [InlineData("Nuevos cap&iacute;tulos")]
    [InlineData("Nuevos <span>capítulos</span>")]
    [InlineData("NUEVOS CAPITULOS")]
    public async Task DailyUpdatesSupportRedesignedHomepage(string heading)
    {
        using var client = new HttpClient(new OlympusHandler(
            CurrentHomepage.Replace("Nuevos capítulos", heading, StringComparison.Ordinal)));
        var provider = new OlympusXyzApiContentProvider(client);

        var update = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));

        Assert.Equal("murim-king", update.ExternalId);
        Assert.Equal("The Murim King", update.PreviewItem.Name);
        Assert.Equal(
            "https://images.olympus.example/covers/daily.webp",
            update.PreviewItem.ThumbnailRef);
        Assert.Collection(
            update.PreviewItem.Units,
            chapter =>
            {
                Assert.Equal("2.5", chapter.DisplayNumber);
                Assert.Equal("2026-07-22", chapter.ReleaseDate);
                Assert.Equal(0, chapter.SortOrder);
            },
            chapter =>
            {
                Assert.Equal("3", chapter.DisplayNumber);
                Assert.Equal("2026-07-23", chapter.ReleaseDate);
                Assert.Equal(1, chapter.SortOrder);
            });
        Assert.Empty(provider.GetKnownUnitRoutes(
            BaseUri,
            update,
            new ApiContentUnitRequest(ContentUnitKind.Chapter, "3", null)));
    }

    [Fact]
    public async Task RedesignedChapterRoutesKeepApiIdsSeparateFromReaderNumbers()
    {
        var handler = new OlympusHandler(CurrentHomepage);
        using var client = new HttpClient(handler);
        var provider = new OlympusXyzApiContentProvider(client);
        var update = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));
        var unit = new ApiContentUnitRequest(ContentUnitKind.Chapter, "2.5", null);
        Assert.Empty(provider.GetKnownUnitRoutes(BaseUri, update, unit));

        var images = await provider.LoadUnitImagesAsync(BaseUri, update, unit);

        Assert.NotNull(images);
        Assert.Equal(2, images.Pages.Count);
        Assert.Contains(handler.Requests, request => request.Uri.PathAndQuery ==
            "/api/series/murim-king/chapters?page=1&direction=asc&type=comic");
        Assert.Contains(handler.Requests, request => request.Uri.PathAndQuery ==
            "/api/capitulo/murim-king/2.5?type=comic");
        Assert.DoesNotContain(handler.Requests, request =>
            request.Uri.AbsolutePath == "/api/capitulo/murim-king/205");
        Assert.Equal("205", Assert.Single(provider.GetKnownUnitRoutes(BaseUri, update, unit)).Route);

        // Refreshing the homepage must not overwrite a resolved ID with its display number.
        _ = await provider.LoadDailyUpdatesAsync(BaseUri);
        Assert.Equal("205", Assert.Single(provider.GetKnownUnitRoutes(BaseUri, update, unit)).Route);
    }

    [Fact]
    public async Task ReaderSupportsLegacyIdEndpointAfterNumberEndpointReturnsNotFound()
    {
        var handler = new OlympusHandler(useLegacyReader: true);
        using var client = new HttpClient(handler);
        var provider = new OlympusXyzApiContentProvider(client);
        var update = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));

        var images = await provider.LoadUnitImagesAsync(
            BaseUri,
            update,
            new ApiContentUnitRequest(ContentUnitKind.Chapter, "2.5", null));

        Assert.NotNull(images);
        Assert.Equal(2, images.Pages.Count);
        Assert.Equal(
            ["/", "/api/capitulo/murim-king/2.5", "/api/capitulo/murim-king/205"],
            handler.Requests.Select(request => request.Uri.AbsolutePath));
    }

    [Fact]
    public async Task ReaderRejectsPagesForDifferentChapter()
    {
        using var client = new HttpClient(new OlympusHandler(readerName: "3"));
        var provider = new OlympusXyzApiContentProvider(client);
        var update = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));

        await Assert.ThrowsAsync<InvalidDataException>(() => provider.LoadUnitImagesAsync(
            BaseUri,
            update,
            new ApiContentUnitRequest(ContentUnitKind.Chapter, "2.5", null)).AsTask());
    }

    [Theory]
    [InlineData("https://olympusxyz.com/")]
    [InlineData("https://www.olympusxyz.com/")]
    [InlineData("https://olympus.example/")]
    public async Task ChapterListsUseConfiguredWebsiteInsteadOfPanel(string baseUrl)
    {
        var root = new Uri(baseUrl);
        var handler = new OlympusHandler();
        using var client = new HttpClient(handler);
        var provider = new OlympusXyzApiContentProvider(client);
        var result = Assert.Single(await provider.SearchAsync(root, "king", MediaType.Book));

        var item = await provider.LoadItemAsync(root, result);

        Assert.Equal(2, item.CatalogItem.Units.Count);
        var chaptersRequest = Assert.Single(handler.Requests, request =>
            request.Uri.AbsolutePath == "/api/series/murim-king/chapters");
        Assert.Equal(root.Host, chaptersRequest.Uri.Host);
        Assert.Equal(root.GetLeftPart(UriPartial.Authority), chaptersRequest.Origin);
        Assert.DoesNotContain(handler.Requests, request => request.Uri.Host == "panel.olympusxyz.com");
    }

    [Theory]
    [InlineData("/series/comic-unrelated/capitulo/2.5")]
    [InlineData("/series/novel-murim-king/capitulo/2.5")]
    [InlineData("/series/comic-murim-king/capitulo/not-a-number")]
    [InlineData("/series/comic-murim-king/capitulo/-2.5")]
    [InlineData("/series/comic-murim-king/capitulo/2.5/extra")]
    [InlineData("/series/comic-murim-king/capitulo/2%2f5")]
    [InlineData("https://external.example/series/comic-murim-king/capitulo/2.5")]
    [InlineData("http://127.0.0.1/series/comic-murim-king/capitulo/2.5")]
    [InlineData("javascript:alert(1)")]
    public async Task DailyUpdatesIgnoreUnrelatedOrInvalidChapterLinks(string chapterUrl)
    {
        var html = $$"""
            <section><h2>Nuevos capítulos</h2><figure>
              <a href="/series/comic-murim-king" title="The Murim King">The Murim King</a>
              <a href="{{chapterUrl}}"><b>Cap. 2.5</b></a>
            </figure></section>
            """;
        using var client = new HttpClient(new OlympusHandler(html));
        var provider = new OlympusXyzApiContentProvider(client);

        var update = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));

        Assert.Empty(update.PreviewItem.Units);
        Assert.Empty(provider.GetKnownUnitRoutes(
            BaseUri,
            update,
            new ApiContentUnitRequest(ContentUnitKind.Chapter, "2.5", null)));
    }

    [Theory]
    [InlineData("<h1>Just a moment...</h1>")]
    [InlineData("<section><h2>Popular del día</h2><p>Nuevos capítulos</p></section>")]
    [InlineData("<h2>Nuevos capítulos</h2>")]
    public async Task DailyUpdatesRejectMissingOrMalformedSection(string html)
    {
        using var client = new HttpClient(new OlympusHandler(html));
        var provider = new OlympusXyzApiContentProvider(client);

        await Assert.ThrowsAsync<InvalidDataException>(() =>
            provider.LoadDailyUpdatesAsync(BaseUri).AsTask());
    }

    [Fact]
    public async Task ProbeLiveDailyUpdatesAndReaderWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_OLYMPUS") != "1")
        {
            return;
        }

        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var client = CatalogCrawlerHttpClient.Create();
        var provider = new OlympusXyzApiContentProvider(client);
        var root = new Uri("https://olympusxyz.com/");
        var updates = await provider.LoadDailyUpdatesAsync(root, cancellation.Token);
        Assert.NotEmpty(updates);
        var update = updates.First(item => item.PreviewItem.Units.Count > 0);
        Assert.False(string.IsNullOrWhiteSpace(update.PreviewItem.ThumbnailRef));
        var detail = await provider.LoadItemAsync(root, update, cancellation.Token);
        Assert.NotEmpty(detail.CatalogItem.Units);
        var chapter = detail.CatalogItem.Units[0];
        var pages = await provider.LoadUnitImagesAsync(
            root,
            update,
            new ApiContentUnitRequest(ContentUnitKind.Chapter, chapter.DisplayNumber, null),
            cancellation.Token);
        Assert.NotNull(pages);
        Assert.NotEmpty(pages.Pages);
        output.WriteLine(
            $"Olympus: {updates.Count} daily titles; {detail.CatalogItem.Name}: " +
            $"{detail.CatalogItem.Units.Count} chapters; chapter {chapter.DisplayNumber}: " +
            $"{pages.Pages.Count} reader-page URLs. No image bytes downloaded.");
    }

    private sealed class OlympusHandler(
        string? homepage = null,
        bool useLegacyReader = false,
        string readerName = "2.5") : HttpMessageHandler
    {
        public int SeriesListRequestCount { get; private set; }

        public List<RequestRecord> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ??
                throw new InvalidOperationException();
            Requests.Add(new RequestRecord(
                uri,
                request.Headers.GetValues("Origin").Single()));

            string content;
            if (uri.AbsolutePath == "/")
            {
                content = homepage ??
                    """
                    <section>
                      <h2>Nuevos Lanzamientos</h2>
                      <div class="grid">
                        <figure>
                          <a href="/series/comic-murim-king"
                             title="The Murim King">
                            <img src="https://images.olympus.example/covers/daily.webp">
                          </a>
                          <a href="/capitulo/205/comic-murim-king">
                            <div>Capítulo 2.5</div>
                            <time datetime="2026-07-22T10:00:00Z">ayer</time>
                          </a>
                          <a href="/capitulo/301/comic-murim-king">
                            <div>Capítulo 3</div>
                            <time datetime="2026-07-23T10:00:00Z">hoy</time>
                          </a>
                        </figure>
                      </div>
                    </section>
                    """;
            }
            else if (uri.AbsolutePath == "/api/series/list")
            {
                SeriesListRequestCount++;
                content =
                    """
                    {
                      "data": [
                        {
                          "id": 7,
                          "name": "The Murim King",
                          "slug": "murim-king",
                          "cover": "https://images.olympus.example/covers/king.webp",
                          "type": "comic"
                        },
                        {
                          "id": 8,
                          "name": "Unrelated",
                          "slug": "unrelated",
                          "type": "comic"
                        }
                      ]
                    }
                    """;
            }
            else if (uri.AbsolutePath == "/api/series/murim-king")
            {
                content =
                    """
                    {
                      "data": {
                        "id": 7,
                        "name": "The Murim King",
                        "slug": "murim-king",
                        "summary": " A martial-arts   story. ",
                        "cover": "https://images.olympus.example/covers/detail.webp",
                        "type": "comic",
                        "status": { "id": 1, "name": "Activo" },
                        "genres": [
                          { "id": 1, "name": "Action" },
                          { "id": 2, "name": "Fantasy" }
                        ]
                      }
                    }
                    """;
            }
            else if (uri.AbsolutePath ==
                     "/api/series/murim-king/chapters")
            {
                content =
                    """
                    {
                      "data": [
                        {
                          "id": 101,
                          "name": "1",
                          "published_at": "2026-07-20T10:00:00Z"
                        },
                        {
                          "id": 205,
                          "name": "Capítulo 2.5",
                          "published_at": "2026-07-22T10:00:00Z"
                        }
                      ],
                      "meta": {
                        "current_page": 1,
                        "last_page": 1
                      }
                    }
                    """;
            }
            else if (uri.AbsolutePath == (useLegacyReader
                         ? "/api/capitulo/murim-king/205"
                         : "/api/capitulo/murim-king/2.5"))
            {
                content =
                    $$"""
                    {
                      "chapter": {
                        "id": 205,
                        "name": "{{readerName}}",
                        "pages": [
                          "https://images.olympus.example/c2/001.webp",
                          "https://images.olympus.example/c2/002.webp"
                        ]
                      },
                      "prev_chapter": { "id": 101, "name": "1" },
                      "next_chapter": null
                    }
                    """;
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed record RequestRecord(
        Uri Uri,
        string Origin);

    private sealed class AdjustableTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan duration) =>
            _utcNow += duration;
    }
}

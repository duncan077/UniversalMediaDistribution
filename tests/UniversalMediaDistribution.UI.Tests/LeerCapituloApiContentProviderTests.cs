using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class LeerCapituloApiContentProviderTests
{
    private static readonly Uri BaseUri =
        new("https://reader.example/");
    private const string PlainAlphabet =
        "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789";

    [Theory]
    [InlineData(" HREF = \"/leer/test/?a=1&amp;b=2\"", "href", "/leer/test/?a=1&b=2")]
    [InlineData("title='Capitulo &#49;'", "title", "Capitulo 1")]
    [InlineData("disabled href=/leer/test/1/ class=chapter", "href", "/leer/test/1/")]
    [InlineData("data-href='/wrong' href='/right'", "href", "/right")]
    [InlineData("title=\"href='/wrong'\" href='/right'", "href", "/right")]
    [InlineData("href=''", "href", "")]
    [InlineData("href='/first' href='/second'", "href", "/first")]
    [InlineData("hidden title='chapter'", "href", null)]
    [InlineData("title=\"unterminated href='/wrong'", "href", null)]
    [InlineData("href='unterminated", "href", null)]
    [InlineData("/> href='/outside'", "href", null)]
    public void AttributesRespectQuotingBoundariesAndHtmlEntities(
        string attributes,
        string name,
        string? expected)
    {
        Assert.Equal(
            expected,
            LeerCapituloApiContentProvider.GetAttribute(attributes, name));
    }

    [Fact]
    public void LongValuelessAttributesDoNotRequireBacktracking()
    {
        var attributes = new string('a', 1_000_000);
        Assert.Null(LeerCapituloApiContentProvider.GetAttribute(attributes, "href"));
        Assert.Equal("/leer/test/1/", LeerCapituloApiContentProvider.GetAttribute(
            attributes + " href='/leer/test/1/'", "href"));
        Assert.Null(LeerCapituloApiContentProvider.GetAttribute(
            "data-value='" + attributes, "href"));
    }

    [Fact]
    public async Task DailyUpdatesParseChaptersAfterLongValuelessAttributes()
    {
        var handler = new LeerCapituloHandler
        {
            AdditionalChapterAttributes = new string('a', 100_000) + " "
        };
        var provider = new LeerCapituloApiContentProvider(new HttpClient(handler));

        var update = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));

        Assert.Equal(
            ["72", "73"],
            update.PreviewItem.Units.Select(static unit => unit.DisplayNumber));
    }

    [Fact]
    public async Task SearchAndLoadItemMapMangaMetadataAndChapters()
    {
        var handler = new LeerCapituloHandler();
        var provider = new LeerCapituloApiContentProvider(
            new HttpClient(handler));

        var nonBookResults = await provider.SearchAsync(
            BaseUri,
            "Hidden Logistics",
            MediaType.Movie);
        Assert.Empty(nonBookResults);
        Assert.Empty(handler.Requests);

        var results = await provider.SearchAsync(
            BaseUri,
            "Hidden Logistics",
            MediaType.Book);
        var result = Assert.Single(results);
        Assert.Equal(
            "qg6ll5mn3j/hidden-logistics",
            result.ExternalId);
        Assert.Equal("Hidden Logistics", result.PreviewItem.Name);
        Assert.Equal(
            "https://reader.example/covers/hidden.jpg?v=1",
            result.PreviewItem.ThumbnailRef);

        var item = await provider.LoadItemAsync(
            BaseUri,
            result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });

        Assert.Equal("Hidden Logistics", item.CatalogItem.Name);
        Assert.Equal(
            "A ruined world.\nA secret warehouse.",
            item.CatalogItem.Description);
        Assert.Equal("ongoing", item.CatalogItem.PublicationStatus);
        Assert.Equal(
            ["Drama", "Action"],
            item.CatalogItem.Genres);
        Assert.Equal(
            ["Manga", "Manhwa"],
            item.CatalogItem.Categories);
        Assert.Equal(
            ["Centro oculto", "숨겨진 물류센터"],
            item.CatalogItem.AlternativeNames
                .Select(static alias => alias.Name));
        Assert.Collection(
            item.CatalogItem.Units,
            chapter =>
            {
                Assert.Equal("1", chapter.DisplayNumber);
                Assert.Equal(0, chapter.SortOrder);
                Assert.Equal("Capitulo 1: Arrival", chapter.Name);
            },
            chapter =>
            {
                Assert.Equal("2", chapter.DisplayNumber);
                Assert.Equal(1, chapter.SortOrder);
                Assert.Equal("Capitulo 2", chapter.Name);
            });
        Assert.Empty(item.ImageSets);
    }

    [Fact]
    public async Task DailyUpdatesMapHomepageCardsAndNewestChapters()
    {
        var provider = new LeerCapituloApiContentProvider(
            new HttpClient(new LeerCapituloHandler()));

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);

        var update = Assert.Single(updates);
        Assert.Equal(
            "qg6ll5mn3j/hidden-logistics",
            update.ExternalId);
        Assert.Equal("Hidden Logistics", update.PreviewItem.Name);
        Assert.Equal(
            "https://reader.example/covers/daily.jpg?v=3",
            update.PreviewItem.ThumbnailRef);
        Assert.Equal(
            ["72", "73"],
            update.PreviewItem.Units.Select(
                static unit => unit.DisplayNumber));
    }

    [Theory]
    [InlineData("src='/covers/current.jpg?v=4'", "https://reader.example/covers/current.jpg?v=4")]
    [InlineData("data-src='/covers/lazy.jpg' src='/placeholder.png'", "https://reader.example/covers/lazy.jpg")]
    [InlineData("data-src='' src='/covers/current.jpg?v=4'", "https://reader.example/covers/current.jpg?v=4")]
    [InlineData("src='https://untrusted.example/cover.jpg'", null)]
    public async Task RedesignedDailyCardsKeepCoversAndExcludeSidebar(
        string imageAttributes, string? expectedThumbnail)
    {
        var handler = new LeerCapituloHandler
        {
            HomepageHtml = $$"""
                <aside><section><a href="/manga/sidebar/popular/">Popular</a></section></aside>
                <div class="col-12 col-lg-9">
                  <div class="lc-section-title"><h1>Ultimos capitulos agregados</h1></div>
                  <div class="row g-3"><div class="col-12 col-xl-6">
                    <article class="featured lc-release">
                      <a class="lc-release-cover" href="/manga/qg6ll5mn3j/hidden-logistics/" tabindex="-1" aria-hidden="true">
                        <img {{imageAttributes}} alt="" loading="lazy" width="148" height="210">
                      </a>
                      <div class="lc-release-body">
                        <a class="lc-release-title" href="/manga/qg6ll5mn3j/hidden-logistics/">Hidden &amp; Logistics</a>
                        <ul class="lc-chapter-list">
                          <li><a class="lc-chapter-link" href="/leer/qg6ll5mn3j/hidden-logistics/73.5/">Capitulo 73.5</a><span>40 mins</span></li>
                          <li><a class="lc-chapter-link" href="/leer/qg6ll5mn3j/hidden-logistics/73/">Capitulo 73</a></li>
                        </ul>
                      </div>
                    </article>
                    <article class="lc-release">
                      <a href="/manga/another/other/"><img src="/covers/other.jpg" alt=""></a>
                      <a href="/manga/another/other/">Hidden &amp; Logistics</a>
                      <a href="/leer/another/other/1/">Capitulo 1</a>
                    </article>
                    <article class="lc-release-recommendation"><a href="/manga/ignored/recommendation/">Ignore</a></article>
                  </div></div>
                </div>
                """
        };
        using var client = new HttpClient(handler);
        var provider = new LeerCapituloApiContentProvider(client);

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);

        Assert.Equal(2, updates.Count);
        Assert.Equal("qg6ll5mn3j/hidden-logistics", updates[0].ExternalId);
        Assert.Equal("Hidden & Logistics", updates[0].PreviewItem.Name);
        Assert.Equal(expectedThumbnail, updates[0].PreviewItem.ThumbnailRef);
        Assert.Equal(["73", "73.5"], updates[0].PreviewItem.Units.Select(unit => unit.DisplayNumber));
        Assert.Equal("https://reader.example/covers/other.jpg", updates[1].PreviewItem.ThumbnailRef);
        Assert.Equal("1", Assert.Single(updates[1].PreviewItem.Units).DisplayNumber);
    }

    [Fact]
    public async Task DailyUpdatesStillRejectUnrecognizedPages()
    {
        using var client = new HttpClient(new LeerCapituloHandler
        {
            HomepageHtml = "<html><h1>Maintenance</h1></html>"
        });
        var provider = new LeerCapituloApiContentProvider(client);
        await Assert.ThrowsAsync<InvalidDataException>(
            async () => await provider.LoadDailyUpdatesAsync(BaseUri));
    }

    [Fact]
    public async Task GenreShelvesAreNativeAndPaginated()
    {
        var handler = new LeerCapituloHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new LeerCapituloApiContentProvider(httpClient);

        var first = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Action));

        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.Genre);
        Assert.Equal(
            ApiContentFilterSupport.Unsupported,
            provider.SearchCapabilities.ReleaseYear);
        Assert.True(provider.SearchCapabilities.SupportsFilterOnlySearch);
        var item = Assert.Single(first.Items);
        Assert.Equal(
            "qg6ll5mn3j/hidden-logistics",
            item.ExternalId);
        Assert.Equal("Hidden Logistics", item.PreviewItem.Name);
        Assert.Equal(
            "https://reader.example/covers/genre-hidden.jpg",
            item.PreviewItem.ThumbnailRef);
        Assert.Equal(["Drama", "Acción"], item.PreviewItem.Genres);
        Assert.Equal("2", first.ContinuationToken);

        var second = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                continuationToken: first.ContinuationToken));

        Assert.Equal(
            "manga-2/second-title",
            Assert.Single(second.Items).ExternalId);
        Assert.Null(second.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/genre/accion/" &&
                uri.Query == "?page=2");
    }

    [Fact]
    public async Task ReaderDecoderRefreshesForChangedScriptAndAfterOneDay()
    {
        var handler = new LeerCapituloHandler();
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                23,
                12,
                0,
                0,
                TimeSpan.Zero));
        var provider = new LeerCapituloApiContentProvider(
            new HttpClient(handler),
            timeProvider);
        var searchItem = new ApiContentSearchItem(
            "qg6ll5mn3j/hidden-logistics",
            new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = "Hidden Logistics",
                Categories = ["Manga"],
                Units =
                [
                    new CatalogTransferUnit
                    {
                        Kind = "chapter",
                        DisplayNumber = "73",
                        SortOrder = 0
                    }
                ]
            });
        var unit = new ApiContentUnitRequest(
            ContentUnitKind.Chapter,
            "73",
            seasonNumber: null);

        var first = await provider.LoadUnitImagesAsync(
            BaseUri,
            searchItem,
            unit);
        AssertPageOrder(first);
        Assert.Equal(1, handler.ReaderScriptRequestCount);

        var cached = await provider.LoadUnitImagesAsync(
            BaseUri,
            searchItem,
            unit);
        AssertPageOrder(cached);
        Assert.Equal(1, handler.ReaderScriptRequestCount);

        handler.ScriptVersion = 2;
        var changedScript = await provider.LoadUnitImagesAsync(
            BaseUri,
            searchItem,
            unit);
        AssertPageOrder(changedScript);
        Assert.Equal(2, handler.ReaderScriptRequestCount);

        timeProvider.Advance(TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        var expired = await provider.LoadUnitImagesAsync(
            BaseUri,
            searchItem,
            unit);
        AssertPageOrder(expired);
        Assert.Equal(3, handler.ReaderScriptRequestCount);

        Assert.Contains(
            provider.GetTrustedArtworkOrigins(BaseUri),
            uri => uri.AbsoluteUri == "https://images.example/");
    }

    private static void AssertPageOrder(ApiContentImageSet? imageSet)
    {
        Assert.NotNull(imageSet);
        Assert.Equal(ContentUnitKind.Chapter, imageSet.UnitKind);
        Assert.Equal("73", imageSet.DisplayNumber);
        Assert.Collection(
            imageSet.Pages,
            page =>
            {
                Assert.Equal(0, page.Position);
                Assert.Equal(
                    "https://images.example/chapter/page-1.jpg",
                    page.Uri.AbsoluteUri);
            },
            page =>
            {
                Assert.Equal(1, page.Position);
                Assert.Equal(
                    "https://images.example/chapter/page-2.jpg",
                    page.Uri.AbsoluteUri);
            });
    }

    private static string BuildReaderScript(string encodedAlphabet)
    {
        var decodedChunks = SplitAlphabet(PlainAlphabet);
        var encodedChunks = SplitAlphabet(encodedAlphabet);
        return $$"""
            function D(a,b){var c=words();return D=function(d,e){d=d-(0x20*0x8);var f=c[d];return f;},D(a,b);}
            var reader=payload.replace(/[A-Z0-9]/gi,x=>(D('0x100')+D('0x101')+D('0x102'))[(D('0x103')+D('0x104')+D('0x105'))['indexOf'](x)]);
            function words(){var r=['noise-one','noise-two','{{decodedChunks[0]}}','{{decodedChunks[1]}}','{{decodedChunks[2]}}','{{encodedChunks[0]}}','{{encodedChunks[1]}}','{{encodedChunks[2]}}','noise-three'];return words=function(){return r;},words();}
            """;
    }

    private static string BuildEncodedPayload(
        string encodedAlphabet)
    {
        var shuffled =
            "https://images.example/chapter/page-2.jpg," +
            "https://images.example/chapter/page-1.jpg";
        var base64 = Convert.ToBase64String(
            Encoding.UTF8.GetBytes(shuffled));
        var encoded = new StringBuilder(base64.Length);
        foreach (var character in base64)
        {
            var index = PlainAlphabet.IndexOf(character);
            encoded.Append(
                index >= 0
                    ? encodedAlphabet[index]
                    : character);
        }

        return encoded.ToString();
    }

    private static string[] SplitAlphabet(string alphabet) =>
    [
        alphabet[..20],
        alphabet[20..40],
        alphabet[40..]
    ];

    private static string RotateAlphabet(int amount) =>
        PlainAlphabet[amount..] + PlainAlphabet[..amount];

    private sealed class LeerCapituloHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];

        public int ScriptVersion { get; set; } = 1;

        public string AdditionalChapterAttributes { get; init; } = string.Empty;

        public string? HomepageHtml { get; init; }

        public int ReaderScriptRequestCount { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ??
                throw new InvalidOperationException(
                    "The test request requires a URI.");
            Requests.Add(uri);

            string content;
            string mediaType;
            if (uri.AbsolutePath == "/")
            {
                content =
                    HomepageHtml ??
                    """
                    <nav class="genres">
                      <a href="/genre/accion/">Acción</a>
                      <a href="/genre/drama/">Drama</a>
                      <a href="/genre/romance/">Romance</a>
                    </nav>
                    <section class="bodycontainer">
                      <h2>Últimos Capitulos Agregados</h2>
                      <div class="media mainpage-manga">
                        <a href="/manga/qg6ll5mn3j/hidden-logistics/">
                          <img data-src="/covers/daily.jpg?v=3"
                               alt="Hidden Logistics">
                        </a>
                        <a href="/manga/qg6ll5mn3j/hidden-logistics/"
                           title="Hidden Logistics">
                          <h4>Hidden Logistics</h4>
                        </a>
                        <a href="/leer/qg6ll5mn3j/hidden-logistics/73/">
                          Capitulo 73
                        </a>
                        <a href="/leer/qg6ll5mn3j/hidden-logistics/72/">
                          Capitulo 72
                        </a>
                      </div>
                    </section>
                    """;
                mediaType = "text/html";
            }
            else if (uri.AbsolutePath == "/genre/accion/" &&
                     uri.Query.Length == 0)
            {
                content =
                    """
                    <main>
                      <div class="media mainpage-manga">
                        <a href="/manga/qg6ll5mn3j/hidden-logistics/"
                           title="Hidden Logistics">
                          <img src="/covers/genre-hidden.jpg"
                               alt="Hidden Logistics">
                        </a>
                        <div class="media-body">
                          <h4 class="manga-newest">
                            <a href="/manga/qg6ll5mn3j/hidden-logistics/">
                              Hidden Logistics
                            </a>
                          </h4>
                          <p>
                            Géneros:
                            <a href="/genre/drama/">Drama</a>,
                            <a href="/genre/accion/">Acción</a>
                          </p>
                        </div>
                      </div>
                      <a href="/genre/accion/?page=2">2</a>
                    </main>
                    """;
                mediaType = "text/html";
            }
            else if (uri.AbsolutePath == "/genre/accion/" &&
                     uri.Query == "?page=2")
            {
                content =
                    """
                    <main>
                      <div class="media mainpage-manga">
                        <a href="/manga/manga-2/second-title/"
                           title="Second Title">
                          <img data-src="/covers/second.jpg"
                               alt="Second Title">
                        </a>
                        <div class="media-body">
                          <p>
                            Géneros:
                            <a href="/genre/accion/">Acción</a>
                          </p>
                        </div>
                      </div>
                      <a href="/genre/accion/">1</a>
                    </main>
                    """;
                mediaType = "text/html";
            }
            else if (uri.AbsolutePath == "/search-autocomplete")
            {
                content =
                    """
                    [
                      {
                        "value": "Hidden Logistics",
                        "label": "Hidden Logistics",
                        "link": "/manga/qg6ll5mn3j/hidden-logistics/",
                        "thumbnail": "/covers/hidden.jpg?v=1"
                      }
                    ]
                    """;
                mediaType = "application/json";
            }
            else if (uri.AbsolutePath ==
                     "/manga/qg6ll5mn3j/hidden-logistics/")
            {
                content =
                    """
                    <!doctype html>
                    <html>
                      <head>
                        <meta content="/covers/detail.jpg?v=2" property="og:image">
                      </head>
                      <body>
                        <h1 class="hero title-manga">Hidden Logistics</h1>
                        <p class="description-update">
                          <span>Títulos Alternativos: </span>Centro oculto, 숨겨진 물류센터<br>
                          <span>Géneros: </span>
                          <a href="/genre/drama/">Drama</a>,
                          <a href="/genre/action/">Action</a><br>
                          <span>Escribe: Manhwa</span><br>
                          <span>Estado: </span>Ongoing<br>
                        </p>
                        <p class="manga-collapse">
                          A ruined world.<br>A secret warehouse.
                        </p>
                        <a href="/leer/qg6ll5mn3j/hidden-logistics/2/" title="Capitulo 2">Capitulo 2</a>
                        <a title="Capitulo 1: Arrival" href="/leer/qg6ll5mn3j/hidden-logistics/1/">Capitulo 1: Arrival</a>
                      </body>
                    </html>
                    """;
                mediaType = "text/html";
            }
            else if (uri.AbsolutePath ==
                     "/leer/qg6ll5mn3j/hidden-logistics/73/")
            {
                var alphabet = ScriptVersion == 1
                    ? RotateAlphabet(17)
                    : new string(PlainAlphabet.Reverse().ToArray());
                content = $$"""
                    <!doctype html>
                    <html>
                      <head>
                        <meta content="0a1" property="ad:check">
                      </head>
                      <body>
                        <p id="array_data">{{BuildEncodedPayload(alphabet)}}</p>
                        <script src="/assets/build/pVaTVp.js?v={{ScriptVersion}}"></script>
                      </body>
                    </html>
                    """;
                mediaType = "text/html";
            }
            else if (uri.AbsolutePath ==
                     "/assets/build/pVaTVp.js")
            {
                ReaderScriptRequestCount++;
                var version = int.Parse(
                    uri.Query
                        .TrimStart('?')
                        .Split('=', 2)
                        .Last(),
                    System.Globalization.CultureInfo.InvariantCulture);
                var alphabet = version == 1
                    ? RotateAlphabet(17)
                    : new string(PlainAlphabet.Reverse().ToArray());
                content = BuildReaderScript(alphabet);
                mediaType = "application/javascript";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            if (AdditionalChapterAttributes.Length > 0 && mediaType == "text/html")
            {
                content = content.Replace(
                    "<a href=\"/leer/",
                    "<a " + AdditionalChapterAttributes + "href=\"/leer/",
                    StringComparison.Ordinal);
            }

            var response = new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    mediaType)
            };
            return Task.FromResult(response);
        }
    }

    private sealed class AdjustableTimeProvider(
        DateTimeOffset utcNow) : TimeProvider
    {
        private DateTimeOffset _utcNow = utcNow;

        public override DateTimeOffset GetUtcNow() => _utcNow;

        public void Advance(TimeSpan value) => _utcNow += value;
    }
}

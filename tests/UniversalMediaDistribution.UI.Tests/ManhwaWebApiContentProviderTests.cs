using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ManhwaWebApiContentProviderTests
{
    private static readonly Uri BaseUri =
        new("https://manhwa.example/");

    [Fact]
    public async Task PlaceholderNamesCannotOverrideARealTitleOrBecomeIdentityAliases()
    {
        var provider = new ManhwaWebApiContentProvider(new HttpClient(new ManhwaHandler { PlaceholderNames = true }));
        var result = new ApiContentSearchItem("reality-quest_1717626334843",
            new CatalogTransferItem { MediaType = "book", Subtype = "manga", Name = "Stale bound title" }, RequireProviderTitle: true);
        var item = (await provider.LoadItemAsync(BaseUri, result)).CatalogItem;
        Assert.Equal("리얼리티 퀘스트", item.Name);
        Assert.Null(item.OriginalName);
        Assert.DoesNotContain(item.AlternativeNames, alias => !CatalogTransferIdentity.IsUsableName(alias.Name));
    }

    [Theory]
    [InlineData("novela", "novel")]
    [InlineData("NOVEL", "novel")]
    [InlineData("novela ligera", "light_novel")]
    [InlineData("web-novel", "web_novel")]
    [InlineData("cómic", "comic")]
    [InlineData("manhwa", "manga")]
    [InlineData("manhua", "manga")]
    public async Task UsesProviderTypeInSearchDetailsAndDailyUpdates(string providerType, string expectedSubtype)
    {
        var handler = new ManhwaHandler { WorkType = providerType };
        var provider = new ManhwaWebApiContentProvider(new HttpClient(handler));
        var result = Assert.Single(await provider.SearchAsync(BaseUri, "quest", MediaType.Book));
        var detail = await provider.LoadItemAsync(BaseUri, result);
        var daily = Assert.Single(await provider.LoadDailyUpdatesAsync(BaseUri));
        foreach (var item in new[] { result.PreviewItem, detail.CatalogItem, daily.PreviewItem })
        {
            Assert.Equal(expectedSubtype, item.Subtype);
            if (expectedSubtype != "manga") Assert.DoesNotContain("Manga", item.Categories);
        }
    }

    [Fact]
    public async Task DetailsWithoutTypeKeepTheSearchClassification()
    {
        var handler = new ManhwaHandler { WorkType = "novela", OmitDetailType = true };
        var provider = new ManhwaWebApiContentProvider(new HttpClient(handler));
        var result = Assert.Single(await provider.SearchAsync(BaseUri, "quest", MediaType.Book));
        var detail = await provider.LoadItemAsync(BaseUri, result);
        Assert.Equal("novel", detail.CatalogItem.Subtype);
        Assert.DoesNotContain("Manga", detail.CatalogItem.Categories);
    }

    [Fact]
    public async Task DiscoversDailyApiAndMapsCatalogAndReaderPayloads()
    {
        var handler = new ManhwaHandler();
        var timeProvider = new AdjustableTimeProvider(
            new DateTimeOffset(
                2026,
                7,
                23,
                12,
                0,
                0,
                TimeSpan.Zero));
        var provider = new ManhwaWebApiContentProvider(
            new HttpClient(handler),
            timeProvider);

        Assert.Empty(await provider.SearchAsync(
            BaseUri,
            "quest",
            MediaType.Audio));
        var results = await provider.SearchAsync(
            BaseUri,
            "quest",
            MediaType.Book);
        var result = Assert.Single(results);
        Assert.Equal(
            "reality-quest_1717626334843",
            result.ExternalId);
        Assert.Equal("Reality Quest", result.PreviewItem.Name);
        Assert.Equal(1, handler.HomepageRequestCount);
        Assert.Equal(1, handler.ScriptRequestCount);
        Assert.Contains(
            handler.Requests,
            uri => uri.IdnHost == "api-one.example" &&
                   uri.AbsolutePath == "/manhwa/library");

        _ = await provider.SearchAsync(
            BaseUri,
            "reality",
            MediaType.Book);
        Assert.Equal(1, handler.HomepageRequestCount);
        Assert.Equal(1, handler.ScriptRequestCount);

        handler.ApiVersion = 2;
        timeProvider.Advance(
            TimeSpan.FromDays(1) + TimeSpan.FromSeconds(1));
        _ = await provider.SearchAsync(
            BaseUri,
            "quest",
            MediaType.Book);
        Assert.Equal(2, handler.HomepageRequestCount);
        Assert.Equal(2, handler.ScriptRequestCount);
        Assert.Contains(
            handler.Requests,
            uri => uri.IdnHost == "api-two.example" &&
                   uri.AbsolutePath == "/manhwa/library");

        var item = await provider.LoadItemAsync(
            BaseUri,
            result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("Reality Quest", item.CatalogItem.Name);
        Assert.Equal("Reality Quest Original", item.CatalogItem.OriginalName);
        Assert.Equal("A game-like reality.", item.CatalogItem.Description);
        Assert.Equal("ongoing", item.CatalogItem.PublicationStatus);
        Assert.Equal(["Drama", "Accion"], item.CatalogItem.Genres);
        Assert.Contains("Manhwa", item.CatalogItem.Categories);
        Assert.Collection(
            item.CatalogItem.Units,
            chapter => Assert.Equal("1", chapter.DisplayNumber),
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
            page =>
            {
                Assert.Equal(
                    "https://img2mw.example/reality/001.webp",
                    page.Uri.AbsoluteUri);
                Assert.Equal(BaseUri, page.Referrer);
            },
            page =>
            {
                Assert.Equal(
                    "https://img2mw.example/reality/002.webp",
                    page.Uri.AbsoluteUri);
                Assert.Equal(BaseUri, page.Referrer);
            });
        Assert.Contains(
            provider.GetTrustedArtworkOrigins(BaseUri),
            uri => uri.AbsoluteUri == "https://img2mw.example/");
        Assert.Equal(
            BaseUri,
            provider.GetArtworkReferrer(
                BaseUri,
                pages.Pages[0].Uri));
        Assert.Null(provider.GetArtworkReferrer(
            BaseUri,
            new Uri("https://untrusted.example/page.webp")));
    }

    [Fact]
    public async Task DailyUpdatesUseLatestSpanishChapterFeed()
    {
        var provider = new ManhwaWebApiContentProvider(
            new HttpClient(new ManhwaHandler()));

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);

        var update = Assert.Single(updates);
        Assert.Equal(
            "reality-quest_1717626334843",
            update.ExternalId);
        Assert.Equal("Reality Quest", update.PreviewItem.Name);
        var chapter = Assert.Single(update.PreviewItem.Units);
        Assert.Equal("3", chapter.DisplayNumber);
        Assert.Equal("2026-07-23", chapter.ReleaseDate);
    }

    [Fact]
    public async Task GenreIdsComeFromTheExistingApplicationBundle()
    {
        var handler = new ManhwaHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new ManhwaWebApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Action));

        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.Genre);
        Assert.Equal(
            ApiContentFilterSupport.Unsupported,
            provider.SearchCapabilities.ReleaseYear);
        Assert.True(provider.SearchCapabilities.SupportsFilterOnlySearch);
        var item = Assert.Single(page.Items);
        Assert.Equal("Reality Quest", item.PreviewItem.Name);
        Assert.Equal(["Drama", "Acción"], item.PreviewItem.Genres);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/manhwa/library" &&
                uri.Query.Contains("buscar=", StringComparison.Ordinal) &&
                uri.Query.Contains("generes=3", StringComparison.Ordinal));

        // Endpoint discovery and taxonomy discovery share one script download.
        Assert.Equal(1, handler.HomepageRequestCount);
        Assert.Equal(1, handler.ScriptRequestCount);
    }

    private sealed class ManhwaHandler : HttpMessageHandler
    {
        public int ApiVersion { get; set; } = 1;

        public string WorkType { get; init; } = "manhwa";

        public bool OmitDetailType { get; init; }

        public bool PlaceholderNames { get; init; }

        public int HomepageRequestCount { get; private set; }

        public int ScriptRequestCount { get; private set; }

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ??
                throw new InvalidOperationException();
            Requests.Add(uri);

            string content;
            string mediaType;
            if (uri.IdnHost == "manhwa.example" &&
                uri.AbsolutePath == "/")
            {
                HomepageRequestCount++;
                content = $"""
                    <!doctype html>
                    <script type="module" src="/assets/index-{ApiVersion}.js"></script>
                    """;
                mediaType = "text/html";
            }
            else if (uri.IdnHost == "manhwa.example" &&
                     uri.AbsolutePath.StartsWith(
                         "/assets/index-",
                         StringComparison.Ordinal))
            {
                ScriptRequestCount++;
                var apiHost = ApiVersion == 1
                    ? "api-one.example"
                    : "api-two.example";
                content = $$"""
                    const apiRoot="https://{{apiHost}}";
                    const generos=[
                      {id:1,nombre:"Drama"},
                      {id:3,nombre:"Acción"},
                      {id:29,nombre:"Aventura"}
                    ];
                    fetch(`${apiRoot}/manhwa/library`);
                    fetch(`${apiRoot}/manhwa/see/title`);
                    fetch(`${apiRoot}/chapters/see/chapter`);
                    """;
                mediaType = "application/javascript";
            }
            else if (uri.AbsolutePath == "/manhwa/library" &&
                     uri.IdnHost.StartsWith(
                         "api-",
                         StringComparison.Ordinal))
            {
                content =
                    """
                    {
                      "data": [
                        {
                          "_id": "reality-quest_1717626334843",
                          "_imagen": "https://covers.mw.example/reality.webp",
                          "_status": "publicandose",
                          "_categoris": [1, 3],
                          "_numero_cap": 2,
                          "_tipo": "manhwa",
                          "_demografi": "shonen",
                          "the_real_name": "Reality Quest Original",
                          "name_esp": "Reality Quest",
                          "name_raw": "리얼리티 퀘스트"
                        }
                      ],
                      "next": false
                    }
                    """;
                mediaType = "application/json";
            }
            else if (uri.AbsolutePath == "/latest/new-manhwa" &&
                     uri.IdnHost.StartsWith(
                         "api-",
                         StringComparison.Ordinal))
            {
                content =
                    """
                    {
                      "manhwas": {
                        "_manhwas": [],
                        "manhwas_esp": [
                          {
                            "name_manhwa": "Reality Quest",
                            "chapter": 3,
                            "create": 1784764800000,
                            "img": "https://covers.mw.example/reality.webp",
                            "id_rel": "reality-quest_1717626334843",
                            "id_manhwa": "reality-quest_1717626334843",
                            "_tipo": "manhwa",
                            "_demografi": "shonen"
                          }
                        ],
                        "manhwas_raw": []
                      }
                    }
                    """;
                mediaType = "application/json";
            }
            else if (uri.AbsolutePath ==
                     "/manhwa/see/reality-quest_1717626334843")
            {
                content =
                    """
                    {
                      "_id": "reality-quest_1717626334843",
                      "_imagen": "https://covers.mw.example/reality.webp",
                      "_status": "publicandose",
                      "_categoris": [
                        { "1": "Drama" },
                        { "3": "Accion" }
                      ],
                      "_sinopsis": " A game-like   reality. ",
                      "_tipo": "manhwa",
                      "_demografi": "shonen",
                      "the_real_name": "Reality Quest Original",
                      "name_esp": "Reality Quest",
                      "name_raw": "리얼리티 퀘스트",
                      "chapters": [
                        {
                          "chapter": 2.5,
                          "img": ["https://covers.mw.example/chapter-2.webp"],
                          "link": "https://manhwa.example/leer/reality-quest_1717626334843-2_5_01",
                          "create": 1717626334
                        },
                        {
                          "chapter": 1,
                          "img": ["https://covers.mw.example/chapter-1.webp"],
                          "link": "https://manhwa.example/leer/reality-quest_1717626334843-1_01",
                          "create": 1717000000
                        }
                      ]
                    }
                    """;
                mediaType = "application/json";
            }
            else if (uri.AbsolutePath ==
                     "/chapters/see/reality-quest_1717626334843-2_5_01")
            {
                content =
                    """
                    {
                      "name": "Reality Quest",
                      "chapter": {
                        "chapter": "2_5_01",
                        "img": [
                          "https://img2mw.example/reality/001.webp",
                          "https://img2mw.example/reality/002.webp"
                        ]
                      }
                    }
                    """;
                mediaType = "application/json";
            }
            else
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound)
                {
                    RequestMessage = request
                });
            }

            content = content.Replace("\"_tipo\": \"manhwa\"", "\"_tipo\": " + System.Text.Json.JsonSerializer.Serialize(WorkType), StringComparison.Ordinal);
            if (PlaceholderNames)
                content = content.Replace("\"the_real_name\": \"Reality Quest Original\"", "\"the_real_name\": \"Desconocido\"", StringComparison.Ordinal)
                    .Replace("\"name_esp\": \"Reality Quest\"", "\"name_esp\": \"Unknown\"", StringComparison.Ordinal);
            if (OmitDetailType && uri.AbsolutePath.StartsWith("/manhwa/see/", StringComparison.Ordinal))
                content = content.Replace("\"_tipo\": " + System.Text.Json.JsonSerializer.Serialize(WorkType) + ",", string.Empty, StringComparison.Ordinal);
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(
                    content,
                    Encoding.UTF8,
                    mediaType)
            });
        }
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

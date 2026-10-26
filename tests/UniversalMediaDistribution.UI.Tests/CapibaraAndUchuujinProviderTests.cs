using System.Net;
using System.Text;
using System.Text.Json.Nodes;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CapibaraTraductorApiContentProviderTests
{
    private static readonly Uri BaseUri = new("https://capibara.example/");

    [Fact]
    public async Task PublicCatalogDetailAndPageApiProduceNativeManga()
    {
        var handler = new CapibaraHandler();
        using var client = new HttpClient(handler);
        var provider = new CapibaraTraductorApiContentProvider(client);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(query: "Ao to Haru", pageSize: 10));

        var result = Assert.Single(page.Items);
        Assert.Equal("1663/kirishimafansub/ao-to-haru", result.ExternalId);
        Assert.Equal("Ao to Haru", result.PreviewItem.Name);
        Assert.Equal("2", page.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static request => request.Query.Contains(
                "search=Ao%20to%20Haru",
                StringComparison.Ordinal));

        var item = await provider.LoadItemAsync(BaseUri, result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("ongoing", item.CatalogItem.PublicationStatus);
        Assert.Contains("Romance", item.CatalogItem.Genres);
        Assert.Equal(
            ["1", "2"],
            item.CatalogItem.Units.Select(static unit => unit.DisplayNumber));

        var unit = new ApiContentUnitRequest(
            ContentUnitKind.Chapter,
            "2",
            seasonNumber: null);
        var images = await provider.LoadUnitImagesAsync(
            BaseUri,
            result,
            unit);
        Assert.NotNull(images);
        Assert.Equal(2, images.Pages.Count);
        Assert.Equal(
            "https://r2.capibara.example/pages/ao-2-1.webp",
            images.Pages[0].Uri.AbsoluteUri);
        Assert.Equal(
            "https://capibara.example/kirishimafansub/manga/ao-to-haru/chapters/2",
            images.Pages[0].Referrer?.AbsoluteUri);
        Assert.Contains(
            provider.GetTrustedArtworkOrigins(BaseUri),
            static origin => origin.Host == "r2.capibara.example");
    }

    [Theory]
    [InlineData("manga", "novel")]
    [InlineData("manga", "light-novel")]
    [InlineData("manga", "BOOK")]
    [InlineData("manga", " short-story ")]
    [InlineData("novel", "manga")]
    [InlineData(null, "novel")]
    [InlineData("light-novel", null)]
    public async Task ProseIsExcludedEvenWhenTopLevelWorkTypeSaysManga(string? workType, string? bookType)
    {
        var handler = new CapibaraHandler { WorkType = workType, BookType = bookType };
        using var client = new HttpClient(handler);
        var provider = new CapibaraTraductorApiContentProvider(client);
        Assert.Empty(await provider.SearchAsync(BaseUri, "Ao"));
        Assert.Empty(await provider.LoadDailyUpdatesAsync(BaseUri));

        var previouslyQueued = new ApiContentSearchItem("1663/kirishimafansub/ao-to-haru",
            new CatalogTransferItem { MediaType = "book", Subtype = "manga", Name = "Old preview" });
        await Assert.ThrowsAsync<ApiCatalogItemUnsupportedException>(() =>
            provider.LoadItemAsync(BaseUri, previouslyQueued).AsTask());
        Assert.All(handler.Requests, uri => Assert.Equal("/api/manga-custom", uri.AbsolutePath));
    }

    [Fact]
    public async Task CatalogDiscoveryKeepsComicsAndExcludesMislabeledNovels()
    {
        using var client = new HttpClient(new CapibaraHandler
        { WorkType = "manga", BookType = "novel", IncludeComic = true });
        var provider = new CapibaraTraductorApiContentProvider(client);
        var page = await provider.ReadCatalogPageAsync(BaseUri, null);
        Assert.Equal("Comic fixture", Assert.Single(page.Items).PreviewItem.Name);
        Assert.Equal("2", page.ContinuationToken);
    }

    [Theory]
    [InlineData("manga", "manga")]
    [InlineData(null, "manga")]
    [InlineData("manga", null)]
    public async Task ComicTypesAndOlderCatalogShapesRemainSupported(string? workType, string? bookType)
    {
        using var client = new HttpClient(new CapibaraHandler { WorkType = workType, BookType = bookType });
        var provider = new CapibaraTraductorApiContentProvider(client);
        var result = Assert.Single(await provider.SearchAsync(BaseUri, "Ao"));
        Assert.Equal(2, (await provider.LoadItemAsync(BaseUri, result)).CatalogItem.Units.Count);
    }

    private sealed class CapibaraHandler : HttpMessageHandler
    {
        public List<Uri> Requests { get; } = [];
        public string? WorkType { get; init; }
        public string? BookType { get; init; }
        public bool IncludeComic { get; init; }

        private string CatalogPayload()
        {
            var payload = JsonNode.Parse(CatalogJson)!;
            var item = payload["data"]!["items"]![0]!;
            if (WorkType is not null) item["workType"] = WorkType;
            if (BookType is not null) item["manga"]!["bookType"] = new JsonObject { ["code"] = BookType };
            if (IncludeComic)
            {
                var comic = item.DeepClone();
                comic["id"] = 1664;
                comic["title"] = "Comic fixture";
                comic["workType"] = "manga";
                comic["manga"]!["slug"] = "comic-fixture";
                comic["manga"]!["bookType"] = new JsonObject { ["code"] = "manga" };
                ((JsonArray)payload["data"]!["items"]!).Add(comic);
            }
            return payload.ToJsonString();
        }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            Requests.Add(uri);
            var content = uri.AbsolutePath switch
            {
                "/api/manga-custom" => CatalogPayload(),
                "/kirishimafansub/manga/ao-to-haru" => DetailHtml,
                "/api/manga-custom/ao-to-haru/chapter/2/pages" => PagesJson,
                _ => null
            };
            if (content is null)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.NotFound));
            }

            var mediaType = uri.AbsolutePath.StartsWith(
                "/api/",
                StringComparison.Ordinal)
                ? "application/json"
                : "text/html";
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(content, Encoding.UTF8, mediaType)
            });
        }

        private const string CatalogJson =
            """
            {
              "status": true,
              "data": {
                "maxPage": 2,
                "items": [{
                  "id": 1663,
                  "title": "Ao to Haru",
                  "description": "A spring romance.",
                  "imageUrl": "https://r2.capibara.example/covers/ao.webp",
                  "bannerUrl": "https://r2.capibara.example/banners/ao.webp",
                  "status": "ongoing",
                  "manga": {
                    "title": "Ao to Haru",
                    "slug": "ao-to-haru",
                    "demography": { "name": "Shoujo" }
                  },
                  "organization": {
                    "name": "Kirishima Fansub",
                    "slug": "kirishimafansub"
                  },
                  "genres": [{ "name": "Romance" }],
                  "chapters": [
                    { "number": 2, "title": "Nuestra cita" },
                    { "number": 1, "title": "Capítulo 1" }
                  ]
                }]
              }
            }
            """;

        private const string DetailHtml =
            """
            <!doctype html>
            <a href="/kirishimafansub/manga/ao-to-haru/chapters/2">
              <h4 class="text-white">Nuestra cita</h4>
            </a>
            <a href="/kirishimafansub/manga/ao-to-haru/chapters/1">
              <h4 class="text-white">Capítulo 1</h4>
            </a>
            """;

        private const string PagesJson =
            """
            {
              "status": true,
              "data": [
                { "number": 2, "imageUrl": "https://r2.capibara.example/pages/ao-2-2.webp" },
                { "number": 1, "imageUrl": "https://r2.capibara.example/pages/ao-2-1.webp" }
              ]
            }
            """;
    }
}

public sealed class UchuujinMangasApiContentProviderTests
{
    private static readonly Uri BaseUri = new("https://uchuujin.example/");

    [Fact]
    public async Task ArchiveDetailAndEmbeddedReaderProduceNativeManga()
    {
        var handler = new UchuujinHandler();
        using var client = new HttpClient(handler);
        var provider = new UchuujinMangasApiContentProvider(client);

        var results = await provider.SearchAsync(BaseUri, "Prometida");
        var result = Assert.Single(results);
        Assert.Equal("mi-prometida-es-muy-mala", result.ExternalId);

        var item = await provider.LoadItemAsync(BaseUri, result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("¡Mi Prometida es Muy Mala!", item.CatalogItem.Name);
        Assert.Equal(2024, item.CatalogItem.ReleaseYear);
        Assert.Equal("ongoing", item.CatalogItem.PublicationStatus);
        Assert.Contains("Romance", item.CatalogItem.Genres);
        Assert.Contains(
            item.CatalogItem.AlternativeNames,
            static alias => alias.Name == "My Fiancée Is So Mean!");
        Assert.Equal(
            ["1", "13.2", "16"],
            item.CatalogItem.Units.Select(static unit => unit.DisplayNumber));

        var unit = new ApiContentUnitRequest(
            ContentUnitKind.Chapter,
            "16",
            seasonNumber: null);
        var images = await provider.LoadUnitImagesAsync(
            BaseUri,
            result,
            unit);
        Assert.NotNull(images);
        Assert.Equal(2, images.Pages.Count);
        Assert.Equal(
            "https://img.uchuujin.example/2026/03/chapter-16-1.webp",
            images.Pages[0].Uri.AbsoluteUri);
        Assert.Equal(
            "https://uchuujin.example/2026/03/19/mi-prometida-capitulo-16/",
            images.Pages[0].Referrer?.AbsoluteUri);
    }

    [Fact]
    public void ReaderScannerIgnoresBracesInsideJsonStrings()
    {
        var pages = UchuujinMangasApiContentProvider.ParseReaderImages(
            BaseUri,
            """
            <script>
              ts_reader.run({"note":"a } brace", "sources":[
                {"source":"Servidor 1","images":[
                  "https:\/\/img.uchuujin.example\/one.webp"
                ]}
              ]});
            </script>
            """);

        Assert.Equal(
            "https://img.uchuujin.example/one.webp",
            Assert.Single(pages).AbsoluteUri);
    }

    private sealed class UchuujinHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            var content = uri.AbsolutePath switch
            {
                "/" => SearchHtml,
                "/manga/mi-prometida-es-muy-mala/" => DetailHtml,
                "/2026/03/19/mi-prometida-capitulo-16/" => ReaderHtml,
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
                  <img class="ts-post-image" src="https://img.uchuujin.example/covers/prometida.webp">
                  <div class="tt">¡Mi Prometida es Muy Mala!</div>
                </a>
              </div></div>
            </div>
            """;

        private const string DetailHtml =
            """
            <meta property="og:image" content="https://img.uchuujin.example/covers/prometida.webp">
            <div class="info-desc bixbox">
              <h1 class="entry-title">¡Mi Prometida es Muy Mala!</h1>
              <span class="alternative">My Fiancée Is So Mean!</span>
              <span class="mgen">
                <a href="https://uchuujin.example/genres/romance/">Romance</a>
              </span>
              <div class="entry-content">A frustrating love story.</div>
            </div>
            <div class="imptdt">Estado <i>Ongoing</i></div>
            <div class="imptdt">Tipo <a>Manga</a></div>
            <div class="imptdt">Lanzado <i>2024</i></div>
            <div class="eph-num"><a href="https://uchuujin.example/2026/03/19/mi-prometida-capitulo-16/"><span class="chapternum">Capítulo 16</span></a></div>
            <div class="eph-num"><a href="https://uchuujin.example/2025/09/30/mi-prometida-capitulo-13-2/"><span class="chapternum">Capítulo 13.2</span></a></div>
            <div class="eph-num"><a href="https://uchuujin.example/2024/12/29/mi-prometida-capitulo-1/"><span class="chapternum">Capítulo 1</span></a></div>
            """;

        private const string ReaderHtml =
            """
            <script>
              ts_reader.run({
                "post_id": 165735,
                "sources": [{
                  "source": "Servidor 1",
                  "images": [
                    "https://img.uchuujin.example/2026/03/chapter-16-1.webp",
                    "https://img.uchuujin.example/2026/03/chapter-16-2.webp"
                  ]
                }]
              });
            </script>
            """;
    }
}

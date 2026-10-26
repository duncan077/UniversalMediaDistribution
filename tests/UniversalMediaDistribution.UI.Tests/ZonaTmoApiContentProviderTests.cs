using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ZonaTmoApiContentProviderTests
{
    private static readonly Uri BaseUri =
        new("https://zonatmo.example/");

    [Fact]
    public async Task LibraryDetailAndReaderFollowCapturedRoutes()
    {
        var handler = new ZonaTmoHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new ZonaTmoApiContentProvider(httpClient);

        var firstPage = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                query: "caballero",
                genre: ApiContentGenre.Action,
                pageSize: 10));

        var result = Assert.Single(firstPage.Items);
        Assert.Equal(
            "manhwa/31352/la-ventura-del-caballero-blanco",
            result.ExternalId);
        Assert.Equal("La Ventura del Caballero Blanco", result.PreviewItem.Name);
        Assert.Equal("manga", result.PreviewItem.Subtype);
        Assert.Equal("En emisión", result.PreviewItem.PublicationStatus);
        Assert.Contains("accion", result.PreviewItem.Genres);
        Assert.Equal("2", firstPage.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.AbsolutePath == "/biblioteca" &&
                request.Uri.Query.Contains("filter_by=title", StringComparison.Ordinal) &&
                request.Uri.Query.Contains("title=caballero", StringComparison.Ordinal) &&
                request.Uri.Query.Contains("genders%5B%5D=1", StringComparison.Ordinal) &&
                request.Uri.Query.Contains("page=1", StringComparison.Ordinal));

        var item = await provider.LoadItemAsync(BaseUri, result);
        CatalogTransferValidator.Validate(new CatalogTransferDocument
        {
            Items = [item.CatalogItem]
        });
        Assert.Equal("La Ventura del Caballero Blanco", item.CatalogItem.Name);
        Assert.Equal("manga", item.CatalogItem.Subtype);
        Assert.Equal("A knight begins a new journey.", item.CatalogItem.Description);
        Assert.Equal("En emisión", item.CatalogItem.PublicationStatus);
        Assert.Contains("Acción", item.CatalogItem.Genres);
        Assert.Contains(
            item.CatalogItem.AlternativeNames,
            static alias => alias.Name == "The White Knight's Venture");
        Assert.Equal(
            ["1", "2.5"],
            item.CatalogItem.Units.Select(static unit => unit.DisplayNumber));

        var unit = new ApiContentUnitRequest(
            ContentUnitKind.Chapter,
            "2.5",
            seasonNumber: null);
        var route = Assert.Single(provider.GetKnownUnitRoutes(
            BaseUri,
            result,
            unit));
        Assert.Equal("/view_uploads/1008663", route.Route);

        var images = await provider.LoadUnitImagesAsync(
            BaseUri,
            result,
            unit);
        Assert.NotNull(images);
        Assert.Collection(
            images.Pages,
            first =>
            {
                Assert.Equal(0, first.Position);
                Assert.Equal(
                    "https://storage2.zonatmo.example/chapters/1008663/1.webp",
                    first.Uri.AbsoluteUri);
                Assert.Equal(
                    "https://zonatmo.example/view_uploads/1008663",
                    first.Referrer?.AbsoluteUri);
            },
            second =>
            {
                Assert.Equal(1, second.Position);
                Assert.Equal(
                    "https://storage2.zonatmo.example/chapters/1008663/2.webp",
                    second.Uri.AbsoluteUri);
            });
        Assert.Contains(
            provider.GetTrustedArtworkOrigins(BaseUri),
            static origin => origin.AbsoluteUri ==
                "https://storage2.zonatmo.example/");
        Assert.Equal(
            BaseUri,
            provider.GetArtworkReferrer(BaseUri, images.Pages[0].Uri));

        var readerRequest = Assert.Single(
            handler.Requests,
            static request => request.Uri.AbsolutePath ==
                "/view_uploads/1008663");
        Assert.Equal(
            "https://zonatmo.example/library/manhwa/31352/la-ventura-del-caballero-blanco",
            readerRequest.Referrer?.AbsoluteUri);
    }

    [Fact]
    public async Task DailyUpdatesUseCreationOrderingAndUnsupportedYearIsSkipped()
    {
        var handler = new ZonaTmoHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new ZonaTmoApiContentProvider(httpClient);

        var unsupported = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(releaseYear: 2025));
        Assert.Empty(unsupported.Items);
        Assert.Empty(handler.Requests);

        var updates = await provider.LoadDailyUpdatesAsync(BaseUri);
        Assert.Equal(
            "manhwa/31352/la-ventura-del-caballero-blanco",
            Assert.Single(updates).ExternalId);
        Assert.Contains(
            handler.Requests,
            static request =>
                request.Uri.Query.Contains(
                    "order_item=creation",
                    StringComparison.Ordinal) &&
                request.Uri.Query.Contains(
                    "order_dir=desc",
                    StringComparison.Ordinal));
    }

    private sealed class ZonaTmoHandler : HttpMessageHandler
    {
        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri ?? throw new InvalidOperationException();
            Requests.Add(new CapturedRequest(uri, request.Headers.Referrer));

            var content = uri.AbsolutePath switch
            {
                "/biblioteca" => LibraryHtml,
                "/library/manhwa/31352/la-ventura-del-caballero-blanco" =>
                    DetailHtml,
                "/view_uploads/1008663" => ReaderHtml,
                _ => null
            };
            if (content is null)
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
                Content = new StringContent(content, Encoding.UTF8, "text/html")
            });
        }

        private const string LibraryHtml =
            """
            <!doctype html>
            <form id="form-filters">
              <input type="checkbox" name="genders[]" id="gender_1" value="1">
              <label for="gender_1">Acción</label>
              <input type="checkbox" name="genders[]" id="gender_2" value="2">
              <label for="gender_2">Aventura</label>
            </form>
            <div class="element col-6" data-identifier="31352">
              <a href="/library/manhwa/31352/la-ventura-del-caballero-blanco">
                <div class="thumbnail book" data-bg="https://storage.zonatmo.example/covers/31352.webp">
                  <img class="cover-bg-img" src="https://storage.zonatmo.example/covers/31352.webp">
                </div>
                <h4 class="text-truncate" title="La Ventura del Caballero Blanco">
                  La Ventura del Caballero Blanco
                </h4>
                <span class="book-type">MANHWA</span>
                <span class="book-meta-status">En emisión</span>
                <span class="demography josei">Josei</span>
              </a>
            </div>
            <nav class="pagination">
              <a href="/biblioteca?page=2" rel="next">Siguiente</a>
            </nav>
            """;

        private const string DetailHtml =
            """
            <!doctype html>
            <h1 class="element-title">La Ventura del Caballero Blanco</h1>
            <h1 class="book-type">MANHWA</h1>
            <img class="book-thumbnail"
                 src="https://storage2.zonatmo.example/proxy/covers/31352.webp">
            <p class="element-description" id="manga-synopsis">
              A knight begins a <strong>new journey</strong>.
            </p>
            <span class="book-status">
              <span class="status-dot"></span>
              En emisión
            </span>
            <a href="/biblioteca?genders[]=1">Acción</a>
            <a href="/biblioteca?genders[]=2">Aventura</a>
            <div class="synonyms-wrap">
              <span class="syn-badge">
                La Ventura del Caballero Blanco; The White Knight's Venture
              </span>
            </div>
            <ul>
              <li class="list-group-item upload-link" data-chapter-number="2.5">
                <span class="chapter-number" data-number="2.5">Capítulo 2.5</span>
                <a class="btn" href="/view_uploads/1008663">Leer online</a>
              </li>
              <li class="list-group-item upload-link" data-chapter-number="1">
                <span class="chapter-number" data-number="1">Capítulo 1</span>
                <a class="btn" href="/view_uploads/1008600">Leer online</a>
              </li>
            </ul>
            """;

        private const string ReaderHtml =
            """
            <!doctype html>
            <div class="reader-img-wrap" data-page="2">
              <img class="reader-image"
                   data-src="https://storage2.zonatmo.example/chapters/1008663/2.webp">
            </div>
            <div class="reader-img-wrap" data-page="1">
              <img class="reader-image"
                   src="https://storage2.zonatmo.example/chapters/1008663/1.webp">
            </div>
            """;
    }

    private sealed record CapturedRequest(Uri Uri, Uri? Referrer);
}

using System.Net;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// PelisPlus browses the live genre shelves and verifies years against metadata
/// already carried by their cards. Search cards may instead append the year to
/// the title, which is also metadata already in hand.
/// </summary>
public sealed class PelisPlusFilteredSearchTests
{
    private static readonly Uri BaseUri = new("https://pelisplus.example/");

    [Fact]
    public async Task GenreUsesTheNativeShelfAndYearUsesItsCardMetadata()
    {
        var handler = new CardHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new PelisPlusApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                "a",
                genre: ApiContentGenre.Action,
                releaseYear: 1988));

        var item = Assert.Single(page.Items);
        Assert.Equal("Akira", item.PreviewItem.Name);
        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.Genre);
        Assert.Contains(
            handler.Requests,
            static uri => uri.AbsolutePath == "/genero/accion");
    }

    [Fact]
    public async Task AYearThatDoesNotMatchIsDropped()
    {
        using var httpClient = new HttpClient(new CardHandler());
        IApiContentProvider provider =
            new PelisPlusApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest("a", releaseYear: 1999));

        Assert.Empty(page.Items);
    }

    [Fact]
    public async Task ACardWithNoReadableYearIsDroppedNotAssumedToMatch()
    {
        // Real search pages render an empty year span for some titles. An
        // unknown year is not evidence of a matching one, so the title is left
        // out rather than smuggled into a filtered result.
        using var httpClient = new HttpClient(new CardHandler());
        IApiContentProvider provider =
            new PelisPlusApiContentProvider(httpClient);

        var unfiltered = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest("undated"));
        var filtered = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest("undated", releaseYear: 2020));

        Assert.Single(unfiltered.Items);
        Assert.Empty(filtered.Items);
    }

    [Fact]
    public async Task YearOnlySearchUsesTheCapturedRouteAndDecoratedYear()
    {
        var handler = new CardHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new PelisPlusApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(releaseYear: 2025));

        var item = Assert.Single(page.Items);
        Assert.Equal("Death Valley", item.PreviewItem.Name);
        Assert.Equal("2025", item.PreviewItem.ReleaseDate);
        Assert.Contains(
            handler.Requests,
            static uri => uri.AbsolutePath == "/busqueda/2025");
    }

    [Fact]
    public async Task PelisPlusGenreShelfSupportsFilterOnlyPagination()
    {
        var handler = new CardHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new PelisPlusApiContentProvider(httpClient);

        var first = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988));

        Assert.True(provider.SearchCapabilities.SupportsFilterOnlySearch);
        Assert.Equal("Akira", Assert.Single(first.Items).PreviewItem.Name);
        Assert.Equal("2", first.ContinuationToken);

        var second = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988,
                continuationToken: first.ContinuationToken));

        Assert.Equal(
            "Action Sequel",
            Assert.Single(second.Items).PreviewItem.Name);
        Assert.Null(second.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static uri =>
                uri.AbsolutePath == "/genero/accion/page/2");
    }

    [Fact]
    public async Task StreamflixGenreShelvesAreNativeAndKeepLocalYearChecks()
    {
        var handler = new CardHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new PelisflixHdApiContentProvider(httpClient);

        var first = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988));

        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.Genre);
        Assert.True(provider.SearchCapabilities.SupportsFilterOnlySearch);
        var item = Assert.Single(first.Items);
        Assert.Equal("Akira", item.PreviewItem.Name);
        Assert.Contains("accion", item.PreviewItem.Genres);
        Assert.Equal("2", first.ContinuationToken);
        Assert.Contains(
            handler.Requests,
            static uri => uri.AbsolutePath == "/genero/accion/");

        var second = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                genre: ApiContentGenre.Action,
                releaseYear: 1988,
                continuationToken: first.ContinuationToken));

        Assert.Equal(
            "Action Sequel",
            Assert.Single(second.Items).PreviewItem.Name);
        Assert.Null(second.ContinuationToken);
    }

    private sealed class CardHandler : HttpMessageHandler
    {
        private const string Html = """
            <main>
              <nav>
                <a href="/genero/accion/">Acción</a>
                <a href="/genero/romance/">Romance</a>
              </nav>
              <div class="movie-item">
                <a href="/pelicula/akira/">
                  <div class="item-picture">
                    <figure>
                      <img class="poster" src="/cdn/akira.webp" alt="Akira">
                      <span class="year text-center">1988</span>
                    </figure>
                  </div>
                  <div class="item-detail"><p>Akira</p></div>
                  <div class="card-hover-info">
                    <ul class="card-hover-info-genre">
                      <li>Acción</li>
                      <li>C. Ficción</li>
                    </ul>
                  </div>
                </a>
              </div>
              <div class="movie-item">
                <a href="/pelicula/amelie/">
                  <div class="item-picture">
                    <figure>
                      <img class="poster" src="/cdn/amelie.webp" alt="Amelie">
                      <span class="year text-center">2001</span>
                    </figure>
                  </div>
                  <div class="item-detail"><p>Amelie</p></div>
                  <div class="card-hover-info">
                    <ul class="card-hover-info-genre">
                      <li>Romance</li>
                    </ul>
                  </div>
                </a>
              </div>
              <div class="movie-item">
                <a href="/pelicula/undated/">
                  <div class="item-picture">
                    <figure>
                      <img class="poster" src="/cdn/undated.webp" alt="Undated">
                      <span class="year text-center"></span>
                    </figure>
                  </div>
                  <div class="item-detail"><p>Undated</p></div>
                </a>
              </div>
              <div class="movie-item">
                <a href="/serie/death-valley-2025/">
                  <div class="item-picture">
                    <figure>
                      <img class="poster"
                           src="/cdn/death-valley.webp"
                           alt="Death Valley (2025)">
                      <span class="year text-center"></span>
                    </figure>
                  </div>
                  <div class="item-detail">
                    <p>Death Valley (2025)</p>
                  </div>
                </a>
              </div>
              <a href="/genero/accion/page/2/">2</a>
            </main>
            """;

        private const string SecondHtml = """
            <main>
              <div class="movie-item">
                <a href="/pelicula/action-sequel/">
                  <img src="/cdn/action-sequel.webp" alt="Action Sequel">
                  <span class="year">1988</span>
                  <div class="item-detail"><p>Action Sequel</p></div>
                </a>
              </div>
              <a href="/genero/accion/">Acción</a>
            </main>
            """;

        public List<Uri> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var uri = request.RequestUri ??
                throw new InvalidOperationException("Missing request URI.");
            Requests.Add(uri);
            var html = uri.AbsolutePath.Contains(
                "/page/2",
                StringComparison.Ordinal)
                ? SecondHtml
                : Html;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(html))
            });
        }
    }
}

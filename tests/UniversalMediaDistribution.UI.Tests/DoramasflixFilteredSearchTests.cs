using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Pins the request this provider builds and how it behaves around unmapped
/// genres. The filter names and scalar types match the live schema.
/// </summary>
public sealed class DoramasflixFilteredSearchTests
{
    private static readonly Uri BaseUri = new("https://doramasflix.example/");

    [Fact]
    public void CapabilitiesDeclareNativeGenreAndYear()
    {
        var provider = new DoramasflixApiContentProvider(new HttpClient());

        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.Genre);
        Assert.Equal(
            ApiContentFilterSupport.Native,
            provider.SearchCapabilities.ReleaseYear);
        Assert.True(provider.SearchCapabilities.SupportsFilterOnlySearch);
    }

    [Fact]
    public async Task GenreIdsAreResolvedFromTheCatalogueNotHardcoded()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new DoramasflixApiContentProvider(httpClient);

        await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Action));

        Assert.Contains(
            handler.Operations,
            operation => operation == "listGenres");
        var filtered = handler.Payloads.First(payload =>
            payload.Contains("paginationDorama", StringComparison.Ordinal));
        Assert.Contains(
            "6011f2c1a2b3c4d5e6f70001",
            filtered,
            StringComparison.Ordinal);
        Assert.Contains(
            "genreId: $genre",
            filtered,
            StringComparison.Ordinal);
        Assert.Contains(
            "$genre: MongoID",
            filtered,
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task TheYearIsSentAsANativeFilter()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new DoramasflixApiContentProvider(httpClient);

        await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(releaseYear: 2019));

        var filtered = handler.Payloads.First(payload =>
            payload.Contains("paginationDorama", StringComparison.Ordinal));
        using var parsed = JsonDocument.Parse(filtered);
        Assert.Equal(
            "2019",
            parsed.RootElement
                .GetProperty("variables")
                .GetProperty("year")
                .GetString());
        Assert.Contains("year: $year", filtered, StringComparison.Ordinal);
        Assert.Contains("$year: String", filtered, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AGenreTheCatalogueDoesNotPublishSkipsTheProvider()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new DoramasflixApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Isekai));

        Assert.Empty(page.Items);
        Assert.DoesNotContain(
            handler.Payloads,
            payload =>
                payload.Contains("paginationDorama", StringComparison.Ordinal));
    }

    [Fact]
    public async Task AnUnfilteredRequestUsesTheTextSearchEndpoint()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new DoramasflixApiContentProvider(httpClient);

        await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest("Crash Landing"));

        // Paging the whole catalogue to find a title by name would be far more
        // expensive than the search query the site already exposes.
        Assert.Contains(handler.Operations, operation => operation == "searchAll");
        Assert.DoesNotContain(
            handler.Operations,
            operation => operation == "filterDoramas");
    }

    [Fact]
    public async Task QueryTextIsVerifiedLocallyAgainstFilteredResults()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new DoramasflixApiContentProvider(httpClient);

        var page = await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(
                "Crash",
                genre: ApiContentGenre.Action));

        // Search and filter are separate GraphQL entry points, so a combined
        // request has to filter upstream and check the text here.
        var item = Assert.Single(page.Items);
        Assert.Equal("Crash Landing on You", item.PreviewItem.Name);
    }

    [Fact]
    public async Task TheGenreCatalogueIsFetchedOnceAndReused()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        IApiContentProvider provider =
            new DoramasflixApiContentProvider(httpClient);

        await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Action));
        await provider.SearchAsync(
            BaseUri,
            new ApiContentSearchRequest(genre: ApiContentGenre.Romance));

        Assert.Equal(
            1,
            handler.Operations.Count(operation => operation == "listGenres"));
    }

    [Fact]
    public async Task SeriesDetailsCarryThePublishedStatus()
    {
        var handler = new DoramasflixHandler();
        using var httpClient = new HttpClient(handler);
        var provider = new DoramasflixApiContentProvider(httpClient);
        var searchItem = new ApiContentSearchItem(
            "doramas-online/dream-to-you",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "series",
                Name = "Dream to You"
            });

        var item = await provider.LoadItemAsync(BaseUri, searchItem);

        Assert.Equal("Finalizado", item.CatalogItem.PublicationStatus);
    }

    private sealed class DoramasflixHandler : HttpMessageHandler
    {
        public ConcurrentBag<string> Payloads { get; } = [];

        public ConcurrentBag<string> Operations { get; } = [];

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                return new HttpResponseMessage(HttpStatusCode.OK)
                {
                    RequestMessage = request,
                    Content = new StringContent(DetailHtml)
                };
            }

            var payload = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            Payloads.Add(payload);

            using var parsed = JsonDocument.Parse(payload);
            var operation = parsed.RootElement
                .GetProperty("operationName")
                .GetString() ?? string.Empty;
            Operations.Add(operation);

            var body = operation switch
            {
                "listGenres" => """
                    {"data":{"listGenres":[
                      {"_id":"6011f2c1a2b3c4d5e6f70001","name":"Action",
                       "name_es":"Acción","slug":"accion"},
                      {"_id":"6011f2c1a2b3c4d5e6f70002","name":"Romance",
                       "name_es":"Romance","slug":"romance"}
                    ]}}
                    """,
                "searchAll" => """
                    {"data":{"searchDorama":[],"searchMovie":[]}}
                    """,
                _ => Pagination(operation)
            };

            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new ByteArrayContent(Encoding.UTF8.GetBytes(body))
            };
        }

        private static string Pagination(string operation) =>
            operation.Contains("Movies", StringComparison.Ordinal)
                ? """
                  {"data":{"paginationMovie":{"items":[],
                    "pageInfo":{"hasNextPage":false}}}}
                  """
                : """
                  {"data":{"paginationDorama":{"items":[
                    {"_id":"1","slug":"crash-landing-on-you",
                     "name":"Crash Landing on You","name_es":null,
                     "poster_path":"/poster.jpg","poster":null},
                    {"_id":"2","slug":"goblin","name":"Goblin",
                     "name_es":null,"poster_path":"/goblin.jpg","poster":null}
                  ],"pageInfo":{"hasNextPage":false}}}}
                  """;

        private const string DetailHtml = """
            <!doctype html>
            <html>
              <body>
                <h1>Dream to You</h1>
                <dl>
                  <dt>Estado</dt>
                  <dd>Finalizado</dd>
                </dl>
              </body>
            </html>
            """;
    }
}

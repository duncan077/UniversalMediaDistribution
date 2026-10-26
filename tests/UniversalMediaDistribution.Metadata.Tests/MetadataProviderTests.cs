using System.Net;
using System.Text;
using UniversalMediaDistribution.Application.Metadata;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata.Tests;

public sealed class MetadataProviderTests
{
    [Fact]
    public void Providers_ClaimOnlyTheirSupportedCatalogTypes()
    {
        var tmdb = new TmdbMetadataProvider();
        var myAnimeList = new MyAnimeListMetadataProvider(
            new HttpClient(new QueueHttpMessageHandler()));

        Assert.True(tmdb.CanRefresh(MetadataTestData.Details(
            MediaType.Movie,
            "movie.series",
            "Series")));
        Assert.False(tmdb.CanRefresh(MetadataTestData.Details(
            MediaType.Movie,
            "movie.anime_series",
            "Anime series")));
        Assert.False(tmdb.CanRefresh(MetadataTestData.Details(
            MediaType.Movie,
            "movie.anime_movie",
            "Anime movie")));
        Assert.False(tmdb.CanRefresh(MetadataTestData.Details(
            MediaType.Book,
            "book.book",
            "Book")));
        Assert.True(myAnimeList.CanRefresh(MetadataTestData.Details(
            MediaType.Book,
            "book.manga",
            "Manga")));
        Assert.True(myAnimeList.CanRefresh(MetadataTestData.Details(
            MediaType.Book,
            "book.web_novel",
            "Web novel")));
        Assert.True(myAnimeList.CanRefresh(MetadataTestData.Details(
            MediaType.Movie,
            "movie.anime_series",
            "Anime series")));
        Assert.True(myAnimeList.CanRefresh(MetadataTestData.Details(
            MediaType.Movie,
            "movie.anime_movie",
            "Anime movie")));
        Assert.False(myAnimeList.CanRefresh(MetadataTestData.Details(
            MediaType.Book,
            "book.book",
            "Book")));
        Assert.False(myAnimeList.CanRefresh(MetadataTestData.Details(
            MediaType.Book,
            "book.light_novel",
            "Light novel")));
        Assert.Collection(
            MetadataProviderFactory.CreateBrowserCompatible(),
            static provider => Assert.IsType<TmdbMetadataProvider>(provider));
    }

    [Fact]
    public async Task MyAnimeList_MapsEnglishTitleAliasesSynopsisAndArtwork()
    {
        const string searchJson = """
            {
              "data": [
                {
                  "node": {
                    "id": 42,
                    "title": "Sousou no Frieren",
                    "main_picture": {
                      "medium": "https://cdn.myanimelist.net/images/manga/3/222295.jpg",
                      "large": "https://cdn.myanimelist.net/images/manga/3/222295l.jpg"
                    },
                    "alternative_titles": {
                      "synonyms": ["Frieren at the Funeral"],
                      "en": "Frieren: Beyond Journey's End",
                      "ja": "葬送のフリーレン"
                    },
                    "start_date": "2020-04-28",
                    "media_type": "manga"
                  }
                }
              ]
            }
            """;
        const string detailsJson = """
            {
              "id": 42,
              "title": "Sousou no Frieren",
              "main_picture": {
                "medium": "https://cdn.myanimelist.net/images/manga/3/222295.jpg",
                "large": "https://cdn.myanimelist.net/images/manga/3/222295l.jpg"
              },
              "alternative_titles": {
                "synonyms": ["Frieren at the Funeral"],
                "en": "Frieren: Beyond Journey's End",
                "ja": "葬送のフリーレン"
              },
              "synopsis": "An elven mage revisits the meaning of her journey.",
              "start_date": "2020-04-28",
              "media_type": "manga"
            }
            """;
        var handler = new QueueHttpMessageHandler(searchJson, detailsJson);
        var provider = new MyAnimeListMetadataProvider(new HttpClient(handler));
        var details = MetadataTestData.Details(
            MediaType.Book,
            "book.manga",
            "Frieren: Beyond Journey's End",
            releaseDate: "2020-04-28");

        var result = await provider.FetchAsync(
            details,
            new MetadataProviderSettings
            {
                MyAnimeListClientId = "test-client-id",
                PreferredLanguage = "en-US"
            });

        Assert.Equal("Frieren: Beyond Journey's End", result.Title);
        Assert.Equal(
            "An elven mage revisits the meaning of her journey.",
            result.Description);
        Assert.Equal(
            "https://cdn.myanimelist.net/images/manga/3/222295l.jpg",
            result.ThumbnailRef);
        Assert.Contains(
            result.AlternativeNames,
            static name => name.Name == "葬送のフリーレン" &&
                           name.Language == "ja" &&
                           name.Kind == "original");
        Assert.Equal(2, handler.Requests.Count);
        Assert.All(
            handler.Requests,
            request => Assert.Equal("test-client-id", request.ClientId));
        Assert.StartsWith("/v2/manga?", handler.Requests[0].Path);
        Assert.Equal("/v2/manga/42?fields=id,title,main_picture,alternative_titles,synopsis,start_date,media_type",
            handler.Requests[1].Path);
    }

    [Fact]
    public async Task MyAnimeList_UsesAnimeApiForAnimeMetadata()
    {
        const string searchJson = """
            {
              "data": [
                {
                  "node": {
                    "id": 18444,
                    "title": "Re:Zero kara Hajimeru Isekai Seikatsu 3rd Season",
                    "main_picture": {
                      "medium": "https://cdn.myanimelist.net/images/anime/11/146225.jpg",
                      "large": "https://cdn.myanimelist.net/images/anime/11/146225l.jpg"
                    },
                    "alternative_titles": {
                      "synonyms": ["Re:ZERO Season 3"],
                      "en": "Re:ZERO -Starting Life in Another World- Season 3",
                      "ja": "Re:ゼロから始める異世界生活 3rd season"
                    },
                    "start_date": "2024-10-02",
                    "media_type": "tv"
                  }
                }
              ]
            }
            """;
        const string detailsJson = """
            {
              "id": 18444,
              "title": "Re:Zero kara Hajimeru Isekai Seikatsu 3rd Season",
              "main_picture": {
                "medium": "https://cdn.myanimelist.net/images/anime/11/146225.jpg",
                "large": "https://cdn.myanimelist.net/images/anime/11/146225l.jpg"
              },
              "alternative_titles": {
                "synonyms": ["Re:ZERO Season 3"],
                "en": "Re:ZERO -Starting Life in Another World- Season 3",
                "ja": "Re:ゼロから始める異世界生活 3rd season"
              },
              "synopsis": "Subaru's journey continues.",
              "start_date": "2024-10-02",
              "media_type": "tv"
            }
            """;
        var handler = new QueueHttpMessageHandler(searchJson, detailsJson);
        var provider = new MyAnimeListMetadataProvider(new HttpClient(handler));
        var details = MetadataTestData.Details(
            MediaType.Movie,
            "movie.anime_series",
            "Re:Zero kara Hajimeru Isekai Seikatsu 3rd Season",
            releaseDate: "2024-10-02");

        var result = await provider.FetchAsync(
            details,
            new MetadataProviderSettings
            {
                MyAnimeListClientId = "test-client-id",
                PreferredLanguage = "en-US"
            });

        Assert.Equal(
            "Re:ZERO -Starting Life in Another World- Season 3",
            result.Title);
        Assert.Equal(
            "https://cdn.myanimelist.net/images/anime/11/146225l.jpg",
            result.ThumbnailRef);
        Assert.Equal(2, handler.Requests.Count);
        Assert.StartsWith("/v2/anime?", handler.Requests[0].Path);
        Assert.Equal(
            "/v2/anime/18444?fields=id,title,main_picture,alternative_titles,synopsis,start_date,media_type",
            handler.Requests[1].Path);
    }

    [Fact]
    public async Task Providers_RequireLocalCredentialsBeforeNetworkAccess()
    {
        var tmdb = new TmdbMetadataProvider();
        var handler = new QueueHttpMessageHandler();
        var myAnimeList = new MyAnimeListMetadataProvider(new HttpClient(handler));

        var tmdbException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => tmdb.FetchAsync(
                MetadataTestData.Details(
                    MediaType.Movie,
                    "movie.movie",
                    "Movie"),
                new MetadataProviderSettings()).AsTask());
        var malException = await Assert.ThrowsAsync<InvalidOperationException>(
            () => myAnimeList.FetchAsync(
                MetadataTestData.Details(
                    MediaType.Book,
                    "book.manga",
                    "Manga"),
                new MetadataProviderSettings()).AsTask());

        Assert.Contains("TMDB v3 API key", tmdbException.Message);
        Assert.Contains("MyAnimeList client ID", malException.Message);
        Assert.Empty(handler.Requests);
    }

    private sealed class QueueHttpMessageHandler : HttpMessageHandler
    {
        private readonly Queue<string> _responses;

        public QueueHttpMessageHandler(params string[] responses)
        {
            _responses = new Queue<string>(responses);
        }

        public List<CapturedRequest> Requests { get; } = [];

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var clientId = request.Headers.TryGetValues(
                "X-MAL-CLIENT-ID",
                out var values)
                ? Assert.Single(values)
                : null;
            Requests.Add(new CapturedRequest(
                request.RequestUri?.PathAndQuery ?? string.Empty,
                clientId));
            if (_responses.Count == 0)
            {
                return Task.FromResult(new HttpResponseMessage(
                    HttpStatusCode.InternalServerError));
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(
                    _responses.Dequeue(),
                    Encoding.UTF8,
                    "application/json")
            });
        }
    }

    private sealed record CapturedRequest(string Path, string? ClientId);
}

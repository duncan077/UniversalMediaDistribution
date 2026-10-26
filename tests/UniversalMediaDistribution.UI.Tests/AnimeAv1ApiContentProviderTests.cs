using System.Net;
using System.Text.Json.Nodes;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class AnimeAv1ApiContentProviderTests
{
    private static readonly Uri Root = new("https://animeav1.example/");
    private const string Media = """
        {"id":197,"slug":"test-anime","title":"Test Anime","category":{"id":1,"name":"TV Anime"},
         "synopsis":"A description.","startDate":"2026-01-16","aka":{"ja-jp":"テスト","en-us":"Test"},
         "genres":[{"name":"Acción"}],"episodes":[{"number":2},{"number":1},{"number":1.5},{"number":1}]}
        """;
    private static ApiContentSearchItem Candidate => new("test-anime", new CatalogTransferItem
    { MediaType = "movie", Subtype = "anime_series", Name = "Test Anime" });
    private static ApiContentUnitRequest Episode => new(ContentUnitKind.Episode, "1", null);

    [Fact]
    public async Task DetailsDecodeReferencesAndMapMetadataAndSortedEpisodes()
    {
        using var client = Client("{\"media\":" + Media + "}");
        var provider = new AnimeAv1ApiContentProvider(client);
        var item = (await provider.LoadItemAsync(Root, Candidate)).CatalogItem;
        CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [item] });
        Assert.Equal("Test Anime", item.Name);
        Assert.Equal("anime_series", item.Subtype);
        Assert.Equal("A description.", item.Description);
        Assert.Equal("2026-01-16", item.ReleaseDate);
        Assert.Equal("テスト", item.OriginalName);
        Assert.Equal(2, item.AlternativeNames.Count);
        Assert.Equal(["Acción"], item.Genres);
        Assert.Equal("https://cdn.animeav1.com/covers/197.jpg", item.ThumbnailRef);
        Assert.Equal("https://cdn.animeav1.com/backdrops/197.jpg", item.BackdropRef);
        Assert.Equal(["1", "1.5", "2"], item.Units.Select(x => x.DisplayNumber));
        Assert.Equal([0, 1, 2], item.Units.Select(x => x.SortOrder));
        Assert.Equal("https://cdn.animeav1.com/screenshots/197/1.jpg", item.Units[0].ThumbnailRef);
        Assert.Contains(new Uri("https://cdn.animeav1.com/"), provider.GetTrustedArtworkOrigins(Root));
        Assert.Equal(Root, provider.GetArtworkReferrer(Root, new Uri(item.ThumbnailRef!)));
    }

    [Fact]
    public async Task MoviesRemainOneEpisode()
    {
        var media = JsonNode.Parse(Media)!;
        media["category"]!["id"] = 2;
        media["episodes"] = JsonNode.Parse("[{\"number\":1}]");
        using var client = Client(new JsonObject { ["media"] = media }.ToJsonString());
        var item = await new AnimeAv1ApiContentProvider(client).LoadItemAsync(Root, Candidate);
        Assert.Equal("anime_movie", item.CatalogItem.Subtype);
        Assert.Equal("1", Assert.Single(item.CatalogItem.Units).DisplayNumber);
    }

    [Fact]
    public async Task DailyUpdatesUseLatestEpisodesNotRecommendationsAndDeduplicateTitles()
    {
        using var client = Client("""
            {"latestEpisodes":[
              {"number":20,"media":{"id":10,"slug":"anime-a","title":"A"}},
              {"number":19,"media":{"id":10,"slug":"anime-a","title":"A"}},
              {"number":1,"media":{"id":11,"slug":"anime-b","title":"B"}}],
             "featured":[{"id":12,"slug":"recommendation","title":"Ignore"}]}
            """);
        var results = await new AnimeAv1ApiContentProvider(client).LoadDailyUpdatesAsync(Root);
        Assert.Equal(["anime-a", "anime-b"], results.Select(x => x.ExternalId));
        Assert.Equal("20", Assert.Single(results[0].PreviewItem.Units).DisplayNumber);
        Assert.All(results, x => Assert.NotNull(x.PreviewItem.ThumbnailRef));
    }

    [Fact]
    public async Task SearchPreservesRemainderAndEscapesQuery()
    {
        var requests = new List<Uri>();
        var data = new JsonObject
        {
            ["results"] = new JsonArray(Enumerable.Range(0, 20).Select(index => (JsonNode)new JsonObject
            { ["id"] = index + 1, ["slug"] = "anime-" + index, ["title"] = "Anime " + index }).ToArray()),
            ["pagination"] = new JsonObject { ["totalRecords"] = 23 }
        };
        using var client = new HttpClient(new Handler((request, _) =>
        {
            requests.Add(request.RequestUri!);
            return Task.FromResult(Response(Encode(data.ToJsonString())));
        }));
        var provider = new AnimeAv1ApiContentProvider(client);
        var first = await provider.SearchAsync(Root, new ApiContentSearchRequest("A & B", pageSize: 7));
        var second = await provider.SearchAsync(Root, new ApiContentSearchRequest("A & B", pageSize: 7, continuationToken: first.ContinuationToken));
        var third = await provider.SearchAsync(Root, new ApiContentSearchRequest("A & B", pageSize: 7, continuationToken: "14"));
        Assert.Equal("7", first.ContinuationToken);
        Assert.Equal("anime-7", second.Items[0].ExternalId);
        Assert.Equal(6, third.Items.Count);
        Assert.Equal("20", third.ContinuationToken);
        await provider.SearchAsync(Root, new ApiContentSearchRequest("A & B", continuationToken: "20"));
        Assert.Contains("search=A%20%26%20B", requests[0].Query);
        Assert.Contains("page=2", requests[^1].Query);
    }

    [Fact]
    public async Task VideoSourcesKeepLanguagesFragmentsAndReferrerAndRejectUnsafeUrls()
    {
        using var client = Client("""
            {"media":{"slug":"test-anime"},"episode":{"number":1},"embeds":{
            "SUB":[{"server":"UPNShare","url":"https://animeav1.uns.bio/#abc"},
                   {"server":"UPNShare","url":"https://animeav1.uns.bio/#abc"},
                   {"server":"Byse","url":"https://byselapuix.com/e/id"},
                   {"server":"Local","url":"http://192.168.1.1/private"},
                   {"server":"Bad","url":"javascript:alert(1)"},
                   {"server":"MP4Upload","url":"https://www.mp4upload.com/embed-undef.html"}],
            "DUB":[{"server":"UPNShare","url":"https://animeav1.uns.bio/#dub"},
                   {"server":"Direct","url":"https://media.example/video.m3u8"}]},
            "downloads":{"SUB":[{"server":"Mega","url":"https://mega.nz/file/id"}]}}
            """);
        var set = await new AnimeAv1ApiContentProvider(client).LoadUnitVideosAsync(Root, Candidate, Episode);
        Assert.NotNull(set);
        Assert.Equal(4, set.Sources.Count);
        Assert.Equal("#abc", set.Sources[0].PlaybackUri.Fragment);
        Assert.Equal("UPNShare · SUB", set.Sources[0].Label);
        Assert.Equal("UPNShare · DUB", set.Sources[2].Label);
        Assert.True(set.Sources[0].IsEmbeddedWeb);
        Assert.True(VideoChallengeCapture.SupportsInteractiveCapture(set.Sources[0].PlaybackUri));
        Assert.False(set.Sources[3].IsEmbeddedWeb);
        Assert.All(set.Sources, source => Assert.Equal(new Uri(Root, "media/test-anime/1"), source.Referrer));
    }

    [Fact]
    public async Task UsesSharedHosterResolver()
    {
        using var client = Client("""
            {"media":{"slug":"test-anime"},"episode":{"number":1},
             "embeds":{"SUB":[{"server":"Test","url":"https://player.example/embed"}]}}
            """);
        var resolver = new VideoHosterResolver(client, [new Extractor()]);
        var provider = new AnimeAv1ApiContentProvider(client, resolver);
        var result = await provider.LoadUnitVideosAsync(Root, Candidate, Episode);
        Assert.False(Assert.Single(result!.Sources).IsEmbeddedWeb);
    }

    [Theory]
    [InlineData("../private")]
    [InlineData("https://other.example/path")]
    [InlineData("test?secret=1")]
    public async Task RejectsInvalidSlugBeforeHttp(string slug)
    {
        using var client = new HttpClient(new Handler((_, _) => throw new Xunit.Sdk.XunitException("Unexpected request")));
        var provider = new AnimeAv1ApiContentProvider(client);
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await provider.LoadItemAsync(Root, new ApiContentSearchItem(slug, Candidate.PreviewItem)));
    }

    [Theory]
    [InlineData("{\"type\":\"redirect\",\"location\":\"/login\"}")]
    [InlineData("{\"nodes\":[{\"data\":[{\"media\":999}]}]}")]
    [InlineData("{\"nodes\":[{\"data\":[{\"media\":\"not-an-index\"}]}]}")]
    [InlineData("{\"nodes\":[{\"data\":[{\"media\":0}]}]}")]
    public async Task InvalidOrCyclicDataFailsWithoutRecursion(string response)
    {
        using var client = new HttpClient(new Handler((_, _) => Task.FromResult(Response(response))));
        await Assert.ThrowsAsync<InvalidDataException>(async () =>
            await new AnimeAv1ApiContentProvider(client).LoadItemAsync(Root, Candidate));
    }

    [Fact]
    public async Task CancellationReachesHttpRequest()
    {
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var client = new HttpClient(new Handler(async (_, token) =>
        {
            started.SetResult();
            await Task.Delay(Timeout.Infinite, token);
            return Response("");
        }));
        using var cancellation = new CancellationTokenSource();
        var task = new AnimeAv1ApiContentProvider(client).LoadDailyUpdatesAsync(Root, cancellation.Token).AsTask();
        await started.Task.WaitAsync(TimeSpan.FromSeconds(3));
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
    }

    private static HttpClient Client(string json) => new(new Handler((_, _) => Task.FromResult(Response(Encode(json)))));
    private static HttpResponseMessage Response(string json) => new(HttpStatusCode.OK)
    { Content = new StringContent(json, System.Text.Encoding.UTF8, "application/json") };

    // Produce devalue's indexed wire shape, including numeric leaves (which
    // are values only after resolving their containing reference).
    private static string Encode(string json)
    {
        var table = new JsonArray();
        int Add(JsonNode? node)
        {
            var index = table.Count;
            table.Add((JsonNode?)null);
            table[index] = node switch
            {
                JsonObject obj => new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(Add(pair.Value))))),
                JsonArray array => new JsonArray(array.Select(value => (JsonNode?)JsonValue.Create(Add(value))).ToArray()),
                _ => node?.DeepClone()
            };
            return index;
        }
        Add(JsonNode.Parse(json));
        return new JsonObject { ["type"] = "data", ["nodes"] = new JsonArray(null, new JsonObject { ["type"] = "data", ["data"] = table }) }.ToJsonString();
    }

    private sealed class Handler(Func<HttpRequestMessage, CancellationToken, Task<HttpResponseMessage>> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken token) => send(request, token);
    }

    private sealed class Extractor : IApiVideoHosterExtractor
    {
        public string Name => "Test";
        public bool Matches(Uri uri) => uri.IdnHost == "player.example";
        public ValueTask<ApiContentVideoSource?> TryResolveAsync(ApiContentVideoSource source, HttpClient client, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<ApiContentVideoSource?>(new ApiContentVideoSource(source.Label, new Uri("https://media.example/video.mp4"), "video/mp4"));
    }
}

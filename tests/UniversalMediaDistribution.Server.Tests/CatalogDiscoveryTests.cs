using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class CatalogDiscoveryTests(ITestOutputHelper output)
{
    [Fact]
    public void EveryBuiltInProviderHasHeadlessCatalogDiscoveryWithoutEagerStartup()
    {
        using var client = new HttpClient(new Handler(_ => throw new InvalidOperationException("No network expected.")));
        var providers = BuiltInApiContentProviders.Create(client);
        Assert.Equal(17, providers.Count);
        Assert.Equal(17, providers.Select(provider => provider.Descriptor.Id).Distinct().Count());
        Assert.All(providers, provider => Assert.False(provider.IsValueCreated));
        Assert.All(providers, provider => Assert.IsAssignableFrom<IApiCatalogProvider>(provider.GetProvider()));
    }

    [Theory]
    [InlineData("http://localhost/")]
    [InlineData("http://10.0.0.1/")]
    [InlineData("http://169.254.169.254/")]
    [InlineData("http://192.168.0.1/")]
    [InlineData("http://[fd00::1]/")]
    [InlineData("http://[::ffff:127.0.0.1]/")]
    [InlineData("http://public.example:9000/")]
    public void CrawlerRejectsPrivateAndNonWebEndpoints(string url) => Assert.False(CatalogCrawlerHttpClient.IsPublicUri(new Uri(url)));

    [Fact]
    public async Task FullIndexesAreNotClippedBySearchLimits()
    {
        var animeRows = new JsonArray(Enumerable.Range(1, 610).Select(number => (JsonNode)new JsonObject
        { ["id"] = number, ["slug"] = $"title-{number}", ["title"] = $"Title {number}", ["type_slug"] = "tv" }).ToArray());
        using var client = new HttpClient(new Handler(_ => animeRows.ToJsonString()));
        IApiCatalogProvider anime = new AnimeTmoApiContentProvider(client);
        var first = await anime.ReadCatalogPageAsync(new Uri("https://anime.example/"), null);
        var second = await anime.ReadCatalogPageAsync(new Uri("https://anime.example/"), first.ContinuationToken);
        Assert.Equal(500, first.Items.Count);
        Assert.Equal(110, second.Items.Count);
        Assert.Null(second.ContinuationToken);
        Assert.Equal(610, first.Items.Concat(second.Items).Select(item => item.ExternalId).Distinct().Count());

        var olympus = new JsonObject
        {
            ["data"] = new JsonArray(Enumerable.Range(1, 610).Select(number => (JsonNode)new JsonObject
            { ["slug"] = $"title-{number}", ["name"] = $"Title {number}", ["type"] = "comic" }).Append(new JsonObject
            { ["slug"] = "novel", ["name"] = "Unsupported novel", ["type"] = "novel" }).ToArray())
        };
        using var olympusClient = new HttpClient(new Handler(_ => olympus.ToJsonString()));
        IApiCatalogProvider comics = new OlympusXyzApiContentProvider(olympusClient);
        first = await comics.ReadCatalogPageAsync(new Uri("https://comics.example/"), null);
        second = await comics.ReadCatalogPageAsync(new Uri("https://comics.example/"), first.ContinuationToken);
        Assert.Equal(610, first.Items.Count + second.Items.Count);
        Assert.DoesNotContain(first.Items.Concat(second.Items), item => item.ExternalId == "novel");
    }

    [Fact]
    public async Task AnimeAv1DirectoryDoesNotRequireSearchTextAndKeepsPagination()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return Encode(new JsonObject
            {
                ["results"] = new JsonArray(new JsonObject { ["id"] = 1, ["slug"] = "anime", ["title"] = "Anime" }),
                ["pagination"] = new JsonObject { ["totalRecords"] = 21, ["totalPages"] = 2 }
            });
        }));
        IApiCatalogProvider provider = new AnimeAv1ApiContentProvider(client);
        var first = await provider.ReadCatalogPageAsync(new Uri("https://anime.example/"), null);
        Assert.Equal("2", first.ContinuationToken);
        Assert.Null((await provider.ReadCatalogPageAsync(new Uri("https://anime.example/"), "2")).ContinuationToken);
        Assert.Equal(["/catalogo/__data.json?page=1", "/catalogo/__data.json?page=2"], requests);
    }

    [Fact]
    public async Task LeerCapituloDirectoryFollowsCurrentPaginationAndRejectsChallengeHtml()
    {
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal("/manga/", request.RequestUri!.AbsolutePath);
            var next = request.RequestUri.Query.Contains("page=1", StringComparison.Ordinal) ? "<a href='/manga/?page=2'>2</a>" : "";
            return "<article class='lc-card'><a class='lc-card-name' href='/manga/id1/title/'>A title</a></article>" + next;
        }));
        IApiCatalogProvider provider = new LeerCapituloApiContentProvider(client);
        var first = await provider.ReadCatalogPageAsync(new Uri("https://manga.example/"), null);
        Assert.Equal("2", first.ContinuationToken);
        Assert.Null((await provider.ReadCatalogPageAsync(new Uri("https://manga.example/"), "2")).ContinuationToken);
        using var blocked = new HttpClient(new Handler(_ => "<h1>Just a moment...</h1>"));
        await Assert.ThrowsAsync<InvalidDataException>(() => ((IApiCatalogProvider)new LeerCapituloApiContentProvider(blocked))
            .ReadCatalogPageAsync(new Uri("https://manga.example/"), null).AsTask());
    }

    [Fact]
    public async Task ExternalUmdCatalogReloadsCompleteUnitsFromStrippedQueuePreviews()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(_ =>
        {
            calls++;
            return """{"format":"universal-media-distribution.catalog","version":1,"items":[{"mediaType":"book","subtype":"manga","name":"External title","units":[{"kind":"chapter","displayNumber":"1","sortOrder":1}]}]}""";
        }));
        IApiCatalogProvider provider = new UmdJsonApiContentProvider(client);
        var root = new Uri("https://catalog.example/api/v1/catalog/");
        var page = await provider.ReadCatalogPageAsync(root, null);
        var title = Assert.Single(page.Items);
        var restored = await provider.LoadCatalogItemAsync(root, new ApiContentSearchItem(title.ExternalId, title.PreviewItem with { Units = [] }));
        Assert.Single(restored.Units);
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task SeriesFlixCrawlsItsOwnSeriesDirectoryNotTheExternalMovieNavigation()
    {
        var requests = new List<string>();
        using var client = new HttpClient(new Handler(request =>
        {
            requests.Add(request.RequestUri!.PathAndQuery);
            return "<a href='/serie/test'><h3>A series</h3></a><a href='/peliculas-online/series-online/page/2'>2</a>";
        }));
        IApiCatalogProvider provider = new SeriesFlixApiContentProvider(client);
        var first = await provider.ReadCatalogPageAsync(new Uri("https://series.example/"), null);
        Assert.Equal("series:2", first.ContinuationToken);
        await provider.ReadCatalogPageAsync(new Uri("https://series.example/"), first.ContinuationToken);
        Assert.Equal(["/series-online/", "/peliculas-online/series-online/page/2"], requests);
    }

    [Fact]
    public async Task MetadataOnlyMovieLoadingNeverRequestsThePlayer()
    {
        var calls = 0;
        using var client = new HttpClient(new Handler(request =>
        {
            Assert.Equal(1, ++calls);
            Assert.Equal("/pelicula/test", request.RequestUri!.AbsolutePath);
            return "<h1>Test Movie</h1><iframe src='https://player.example/embed'></iframe>";
        }));
        IApiCatalogProvider provider = new PelisPlusToApiContentProvider(client);
        var item = await provider.LoadCatalogItemAsync(new Uri("https://film.example/"), new ApiContentSearchItem("pelicula/test", new CatalogTransferItem
        { MediaType = "movie", Subtype = "movie", Name = "Preview" }));
        Assert.Equal("Test Movie", item.Name);
        Assert.Equal("1", Assert.Single(item.Units).DisplayNumber);
        Assert.Equal(1, calls);
    }

    // Explicit opt-in only. Read two public listing pages per provider, with
    // pacing and timeouts; never start the bulk crawler or download media.
    [Fact]
    public async Task ProbeLiveCatalogDiscoveryWhenExplicitlyEnabled()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_CATALOG") != "1") return;
        using var client = CatalogCrawlerHttpClient.Create();
        foreach (var registration in BuiltInApiContentProviders.Create(client))
        {
            var descriptor = registration.Descriptor;
            var onlyProvider = Environment.GetEnvironmentVariable("UMD_TEST_LIVE_PROVIDER");
            if (!string.IsNullOrEmpty(onlyProvider) && onlyProvider != descriptor.Id) continue;
            if (descriptor.DefaultBaseUrl.Length == 0) { output.WriteLine($"{descriptor.Id}: configuration_required"); continue; }
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(50));
            try
            {
                var page = await ((IApiCatalogProvider)registration.GetProvider()).ReadCatalogPageAsync(new Uri(descriptor.DefaultBaseUrl), null, timeout.Token);
                Assert.NotEmpty(page.Items);
                output.WriteLine($"{descriptor.Id}: items={page.Items.Count}; next={page.ContinuationToken is not null}");
                if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_DETAILS") == "1")
                {
                    using var detailTimeout = new CancellationTokenSource(TimeSpan.FromMinutes(3));
                    var detail = await ((IApiCatalogProvider)registration.GetProvider()).LoadCatalogItemAsync(new Uri(descriptor.DefaultBaseUrl), page.Items[0], detailTimeout.Token);
                    CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [CatalogMetadataOnly.Sanitize(detail)] });
                    var count = detail.Units.Count + detail.Seasons.Sum(season => season.Units.Count);
                    output.WriteLine($"{descriptor.Id}: detail_units={count}; publication_status={detail.PublicationStatus}");
                    if (count == 0 && page.Items.Count > 1)
                    {
                        var another = await ((IApiCatalogProvider)registration.GetProvider()).LoadCatalogItemAsync(new Uri(descriptor.DefaultBaseUrl), page.Items[1], detailTimeout.Token);
                        output.WriteLine($"{descriptor.Id}: second_title_units={another.Units.Count + another.Seasons.Sum(season => season.Units.Count)}");
                    }
                }
                if (page.ContinuationToken is { } next)
                {
                    var second = await ((IApiCatalogProvider)registration.GetProvider()).ReadCatalogPageAsync(new Uri(descriptor.DefaultBaseUrl), next, timeout.Token);
                    output.WriteLine($"{descriptor.Id}: page2={second.Items.Count}; new_ids={second.Items.Select(item => item.ExternalId).Except(page.Items.Select(item => item.ExternalId)).Count()}");
                }
            }
            catch (Exception exception)
            {
                output.WriteLine($"{descriptor.Id}: {exception.GetType().Name}; status={(exception as HttpRequestException)?.StatusCode}");
            }
        }
    }

    private static string Encode(JsonNode node)
    {
        var table = new JsonArray();
        int Add(JsonNode? value)
        {
            var index = table.Count;
            table.Add((JsonNode?)null);
            table[index] = value switch
            {
                JsonObject obj => new JsonObject(obj.Select(pair => new KeyValuePair<string, JsonNode?>(pair.Key, JsonValue.Create(Add(pair.Value))))),
                JsonArray array => new JsonArray(array.Select(item => (JsonNode?)JsonValue.Create(Add(item))).ToArray()),
                _ => value?.DeepClone()
            };
            return index;
        }
        Add(node);
        return new JsonObject { ["type"] = "data", ["nodes"] = new JsonArray(null, new JsonObject { ["type"] = "data", ["data"] = table }) }.ToJsonString();
    }

    [Fact]
    public async Task ProbeLiveCapibaraSkipsMislabeledProseAndLoadsComicWithoutRedirects()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_CAPIBARA") != "1") return;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        using var client = CatalogCrawlerHttpClient.Create();
        var provider = new CapibaraTraductorApiContentProvider(client);
        var root = new Uri("https://capibaratraductor.com/");
        var page = await provider.ReadCatalogPageAsync(root, null, timeout.Token);
        Assert.NotEmpty(page.Items);
        Assert.DoesNotContain(page.Items, item => item.ExternalId == "1651/cazadorademangas/lovezero");
        var selected = page.Items.FirstOrDefault(item => item.ExternalId.StartsWith("2796/", StringComparison.Ordinal)) ?? page.Items[0];
        var detail = await provider.LoadItemAsync(root, selected, timeout.Token);
        Assert.NotEmpty(detail.CatalogItem.Units);
        await Assert.ThrowsAsync<ApiCatalogItemUnsupportedException>(() => provider.LoadItemAsync(root,
            new ApiContentSearchItem("1651/cazadorademangas/lovezero", new CatalogTransferItem
            { Name = "LOVE:ZERO", MediaType = "book", Subtype = "manga" }), timeout.Token).AsTask());
        output.WriteLine($"Capibara: {page.Items.Count} comic titles; {detail.CatalogItem.Name}: {detail.CatalogItem.Units.Count} chapters; mislabeled novel skipped before its 301 HTML route.");
    }

    private sealed class Handler(Func<HttpRequestMessage, string> send) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(send(request)), RequestMessage = request });
    }
}

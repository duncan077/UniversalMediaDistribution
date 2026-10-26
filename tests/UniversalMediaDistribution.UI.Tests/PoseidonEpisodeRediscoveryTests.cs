using System.Net;
using System.Text;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class PoseidonEpisodeRediscoveryTests
{
    private static readonly Uri Root = new("https://poseidon.example/");
    private const string Route = "serie/91425/el-tren-infinito";
    private const string Title = "El Tren Infinito";

    [Fact]
    public async Task ExistingUnboundSeriesIsRediscoveredWithOneNarrowerQueryWithoutOpeningPlayers()
    {
        var handler = new PoseidonHandler();
        using var http = new HttpClient(handler);
        var provider = new PoseidonHd2ApiContentProvider(http);
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [LocalSeries()] });
        var service = CreateService(repository, http, provider, new InMemoryApiTitleBindingStore());
        var details = await Details(repository);
        var episode = details.Seasons.Single().Units.Single(unit => unit.DisplayNumber == "10");

        var candidates = await service.FindApiVideoCandidatesForUnitAsync(details, episode);

        var candidate = Assert.Single(candidates);
        Assert.Equal(Route, candidate.ExternalId);
        Assert.Equal(1, candidate.UnitRequest.SeasonNumber);
        Assert.Equal("10", candidate.UnitRequest.DisplayNumber);
        Assert.Equal("Infinity Train", candidate.ApiItem.CatalogItem.OriginalName);
        Assert.Equal([Title, "Tren Infinito"], handler.Queries);
        Assert.DoesNotContain(handler.Requests, uri => uri.AbsolutePath.Contains("episodio", StringComparison.Ordinal));
        Assert.DoesNotContain(handler.Requests, uri => uri.Query.Contains("page", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ImportRemembersSelectedProviderRouteAndActualTitleSoPlaybackDoesNotNeedSearchAgain(bool englishLocalName)
    {
        var handler = new PoseidonHandler();
        using var http = new HttpClient(handler);
        var provider = new PoseidonHd2ApiContentProvider(http);
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument
        {
            Items = [LocalSeries() with { Name = englishLocalName ? "Infinity Train" : Title }]
        });
        var bindings = new InMemoryApiTitleBindingStore();
        var service = CreateService(repository, http, provider, bindings);
        var originalId = (await Details(repository)).Item.Id;
        var search = await service.SearchAsync(Title, MediaType.Movie);
        var selected = Assert.Single(search.Results, result => result.IsOnline);
        var loaded = await service.LoadOnlineItemAsync(selected);

        Assert.Equal(originalId, loaded.Details.Item.Id);
        var binding = Assert.Single(await bindings.LoadAsync(originalId)).Value;
        Assert.Equal(Route, binding.ExternalId);
        Assert.Equal(Title, binding.Title);
        var searchCount = handler.Queries.Count;
        handler.RejectSearch = true;
        var episode = loaded.Details.Seasons.Single().Units.Single(unit => unit.DisplayNumber == "10");
        var candidates = await service.FindApiVideoCandidatesForUnitAsync(loaded.Details, episode);
        Assert.True(Assert.Single(candidates).UsedKnownBinding);
        Assert.Equal(searchCount, handler.Queries.Count);
    }

    [Fact]
    public async Task ExactSearchResultDoesNotTriggerExtraRequest()
    {
        var handler = new PoseidonHandler { ExactSearch = true };
        using var http = new HttpClient(handler);
        var provider = new PoseidonHd2ApiContentProvider(http);
        Assert.Equal(Route, Assert.Single(await provider.SearchAsync(Root, Title, MediaType.Movie)).ExternalId);
        Assert.Equal([Title], handler.Queries);
    }

    [Fact]
    public async Task NarrowerSearchStillRequiresTheOriginalFullTitle()
    {
        var handler = new PoseidonHandler { NarrowerResultIsUnrelated = true };
        using var http = new HttpClient(handler);
        var provider = new PoseidonHd2ApiContentProvider(http);
        Assert.Empty(await provider.SearchAsync(Root, Title, MediaType.Movie));
        Assert.Equal([Title, "Tren Infinito"], handler.Queries);
    }

    [Fact]
    public async Task OriginalTitleCannotComeFromRecommendedOrDifferentSeries()
    {
        var handler = new PoseidonHandler
        {
            Detail = DetailHtml.Replace("\"name\":\"El Tren Infinito\",\"original\"", "\"name\":\"Another Series\",\"original\"", StringComparison.Ordinal)
        };
        using var http = new HttpClient(handler);
        var provider = new PoseidonHd2ApiContentProvider(http);
        var item = await provider.LoadItemAsync(Root, new(Route, LocalSeries()));
        Assert.Null(item.CatalogItem.OriginalName);
        Assert.Empty(item.CatalogItem.AlternativeNames);
    }

    [Fact]
    public async Task NarrowerSearchObservesCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var handler = new PoseidonHandler { CancelNarrowerSearch = cancellation };
        using var http = new HttpClient(handler);
        var provider = new PoseidonHd2ApiContentProvider(http);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => provider.SearchAsync(Root, Title, MediaType.Movie, cancellation.Token).AsTask());
        Assert.Equal(2, handler.Queries.Count);
    }

    // Opt-in metadata-only probe. Never fetch player pages or video feeds.
    [Fact]
    public async Task LivePoseidonFindsTheExistingSeriesSeasonOneEpisodeTen()
    {
        if (Environment.GetEnvironmentVariable("UMD_TEST_LIVE_POSEIDON") != "1") return;
        using var http = CatalogCrawlerHttpClient.Create();
        var provider = new PoseidonHd2ApiContentProvider(http);
        var root = new Uri(provider.Descriptor.DefaultBaseUrl);
        var repository = new InMemoryCatalogRepository(new CatalogTransferDocument { Items = [LocalSeries()] });
        var service = CreateService(repository, http, provider, new InMemoryApiTitleBindingStore(), root);
        var details = await Details(repository);
        var episode = Assert.Single(Assert.Single(details.Seasons).Units);
        var candidate = Assert.Single(await service.FindApiVideoCandidatesForUnitAsync(details, episode));
        Assert.Equal(Route, candidate.ExternalId.TrimEnd('/'));
        Assert.Equal(1, candidate.UnitRequest.SeasonNumber);
        Assert.Equal("10", candidate.UnitRequest.DisplayNumber);
        Assert.Equal("Infinity Train", candidate.ApiItem.CatalogItem.OriginalName);
        Assert.Empty(candidate.ApiItem.VideoSets);
    }

    private static CatalogTransferItem LocalSeries() => new()
    {
        Name = Title,
        MediaType = "movie",
        Subtype = "series",
        Seasons = [new() { Number = 1, Units = [new() { Kind = "episode", DisplayNumber = "10", Name = Title + " 1x10" }] }]
    };

    private static async Task<CatalogItemDetails> Details(InMemoryCatalogRepository repository)
    {
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single((await browse.GetOverviewAsync(MediaType.Movie)).Items);
        return Assert.IsType<CatalogItemDetails>(await browse.GetDetailsAsync(card.Id));
    }

    private static ContentSourceCatalogService CreateService(InMemoryCatalogRepository repository, HttpClient http,
        IApiContentProvider provider, IApiTitleBindingStore bindings, Uri? root = null) => new(new CatalogBrowseService(repository),
        new CatalogJsonTransferService(repository), new InMemoryContentSourceProviderSettingsStore(new()
        {
            Providers = [new() { Id = provider.Descriptor.Id, DisplayName = "Poseidon", Kind = ContentSourceProviderKind.Api, IsEnabled = true, BaseUrl = (root ?? Root).AbsoluteUri }]
        }), http, new[] { provider }, titleBindingStore: bindings);

    private const string DetailHtml = """
        <h1>El Tren Infinito</h1>
        <script id="__NEXT_DATA__" type="application/json">
        {"props":{"pageProps":{"thisSerie":{"TMDbId":"91425",
          "titles":{"name":"El Tren Infinito","original":{"name":"Infinity Train"}},
          "seasons":[{"number":1,"episodes":[{"number":10,"title":"El Tren Infinito 1x10",
            "url":{"slug":"series/91425/el-tren-infinito/seasons/1/episodes/10"}}]}]},
          "topSeriesDay":[{"titles":{"name":"Unrelated Recommendation","original":{"name":"Wrong Alias"}}}]}}}
        </script>
        """;

    private sealed class PoseidonHandler : HttpMessageHandler
    {
        internal List<Uri> Requests { get; } = [];
        internal List<string> Queries { get; } = [];
        internal bool ExactSearch { get; init; }
        internal bool NarrowerResultIsUnrelated { get; init; }
        internal bool RejectSearch { get; set; }
        internal CancellationTokenSource? CancelNarrowerSearch { get; init; }
        internal string Detail { get; init; } = DetailHtml;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var uri = request.RequestUri!;
            Requests.Add(uri);
            string html;
            if (uri.AbsolutePath == "/search")
            {
                if (RejectSearch) throw new InvalidOperationException("Name search should not be needed after an explicit import.");
                var query = Uri.UnescapeDataString(uri.Query[3..]);
                Queries.Add(query);
                if (query == "Tren Infinito")
                {
                    CancelNarrowerSearch?.Cancel();
                    cancellationToken.ThrowIfCancellationRequested();
                }
                var matches = ExactSearch || query == "Tren Infinito" && !NarrowerResultIsUnrelated;
                html = matches
                    ? "<a href='/serie/91425/el-tren-infinito'><h2>El Tren Infinito</h2></a>"
                    : "<a href='/pelicula/other'><h2>El hombre que conocía el infinito</h2></a>";
            }
            else if (uri.AbsolutePath.Trim('/') == Route) html = Detail;
            else throw new InvalidOperationException($"Unexpected request: {uri.AbsolutePath}");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                RequestMessage = request,
                Content = new StringContent(html, Encoding.UTF8, "text/html")
            });
        }
    }
}

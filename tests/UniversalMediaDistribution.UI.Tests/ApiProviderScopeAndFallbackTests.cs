using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Covers the two ways episode discovery used to give up while a provider that
/// could answer was sitting right there: the scope split that never asked half
/// the providers, and taking only the leading title from the one it did ask.
/// </summary>
public sealed class ApiProviderScopeAndFallbackTests
{
    private const string SeriesName = "All American";

    /// <summary>
    /// MediaSubtypeGroups.Resolve has no unknown case, so anything not tagged
    /// as anime is live action and only live-action providers were searched.
    /// Some anime providers carry live-action titles under /latino/ but declare the
    /// anime scope, so it was never asked. Now the other half runs as a second
    /// pass once the first finds nothing.
    /// </summary>
    [Fact]
    public async Task AnAnimeScopedProviderIsSearchedWhenNoLiveActionProviderAnswers()
    {
        var liveAction = new StubProvider(
            "liveaction-stub",
            ApiContentProviderScope.LiveAction,
            SeriesName,
            episodes: [],
            answersSearch: false);
        var anime = new StubProvider(
            "anime-stub",
            ApiContentProviderScope.Anime,
            SeriesName,
            episodes: ["1", "2", "3"]);

        var candidates = await DiscoverAsync(
            episodeNumber: "3",
            liveAction,
            anime);

        var candidate = Assert.Single(candidates);
        Assert.Equal("anime-stub", candidate.ProviderId);
        Assert.True(
            anime.WasSearched,
            "the anime-scoped provider must be searched once the live-action half comes back empty.");
    }

    /// <summary>
    /// The widened pass is a fallback, not a merge: a title the in-scope
    /// provider can answer must not start costing requests to the other half.
    /// </summary>
    [Fact]
    public async Task TheOtherScopeIsLeftAloneWhenTheInScopeProviderAnswers()
    {
        var liveAction = new StubProvider(
            "liveaction-stub",
            ApiContentProviderScope.LiveAction,
            SeriesName,
            episodes: ["1", "2", "3"]);
        var anime = new StubProvider(
            "anime-stub",
            ApiContentProviderScope.Anime,
            SeriesName,
            episodes: ["1", "2", "3"]);

        var candidates = await DiscoverAsync(
            episodeNumber: "3",
            liveAction,
            anime);

        Assert.Equal("liveaction-stub", Assert.Single(candidates).ProviderId);
        Assert.False(
            anime.WasSearched,
            "the anime-scoped provider must stay untouched while the in-scope one answers.");
    }

    /// <summary>
    /// Containment ties several titles at similarity 1, so the leader is a
    /// guess. Only the leader was ever loaded, so one wrong guess reported the
    /// episode as unavailable — a same-named film, a duplicate listing, a
    /// spin-off. Here two listings share a name and only the second carries the
    /// episode; the first sorts ahead of it by provider id.
    /// </summary>
    [Fact]
    public async Task ARunnerUpTitleIsTriedWhenTheLeadingOneLacksTheEpisode()
    {
        var provider = new StubProvider(
            "liveaction-stub",
            ApiContentProviderScope.LiveAction,
            SeriesName,
            episodes: ["1", "2"],
            secondListingId: "b-second-listing",
            secondListingEpisodes: ["1", "2", "3"]);

        var candidates = await DiscoverAsync(
            episodeNumber: "3",
            provider);

        var candidate = Assert.Single(candidates);
        Assert.Equal(
            "b-second-listing",
            candidate.SearchItem.ExternalId);
    }

    /// <summary>
    /// The fallback must not turn a genuine miss into a wrong answer: when no
    /// listing has the episode, discovery still fails.
    /// </summary>
    [Fact]
    public async Task NothingIsReturnedWhenNoListingCarriesTheEpisode()
    {
        var provider = new StubProvider(
            "liveaction-stub",
            ApiContentProviderScope.LiveAction,
            SeriesName,
            episodes: ["1", "2"],
            secondListingId: "b-second-listing",
            secondListingEpisodes: ["1", "2"]);

        await Assert.ThrowsAnyAsync<InvalidOperationException>(
            () => DiscoverAsync("3", provider).AsTask());
    }

    private static async ValueTask<IReadOnlyList<ApiContentUnitVideoCandidate>>
        DiscoverAsync(
            string episodeNumber,
            params IApiContentProvider[] providers)
    {
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                Items = [CreateLiveActionSeries()]
            });
        using var httpClient = new HttpClient(new UnusedHandler());
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers = providers
                    .Select(provider => new ContentSourceProviderConfiguration
                    {
                        Id = provider.Descriptor.Id,
                        DisplayName = provider.Descriptor.DisplayName,
                        Kind = ContentSourceProviderKind.Api,
                        IsEnabled = true,
                        BaseUrl = $"https://{provider.Descriptor.Id}.example/"
                    })
                    .ToArray()
            });
        var service = new ContentSourceCatalogService(
            new CatalogBrowseService(repository),
            new CatalogJsonTransferService(repository),
            settings,
            httpClient,
            providers);

        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var episode = Assert.Single(
            details.Units,
            unit => unit.DisplayNumber == episodeNumber);

        return await service.FindApiVideoCandidatesForUnitAsync(
            details,
            episode);
    }

    /// <summary>
    /// Deliberately not tagged as anime, so MediaSubtypeGroups.Resolve puts it
    /// in the live-action half.
    /// </summary>
    private static CatalogTransferItem CreateLiveActionSeries() =>
        new()
        {
            MediaType = "movie",
            Subtype = "series",
            Name = SeriesName,
            Description = "Local live-action series",
            Categories = ["Series"],
            Units = Enumerable.Range(1, 3)
                .Select(number => new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = number.ToString(),
                    SortOrder = number - 1
                })
                .ToArray()
        };

    private sealed class StubProvider : IApiContentProvider
    {
        private readonly string _title;
        private readonly IReadOnlyList<string> _episodes;
        private readonly bool _answersSearch;
        private readonly string? _secondListingId;
        private readonly IReadOnlyList<string> _secondListingEpisodes;

        public StubProvider(
            string id,
            ApiContentProviderScope scope,
            string title,
            IReadOnlyList<string> episodes,
            bool answersSearch = true,
            string? secondListingId = null,
            IReadOnlyList<string>? secondListingEpisodes = null)
        {
            Descriptor = new ApiContentProviderDescriptor(
                id,
                id,
                scope: scope);
            _title = title;
            _episodes = episodes;
            _answersSearch = answersSearch;
            _secondListingId = secondListingId;
            _secondListingEpisodes = secondListingEpisodes ?? [];
        }

        public ApiContentProviderDescriptor Descriptor { get; }

        public bool WasSearched { get; private set; }

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            WasSearched = true;
            if (!_answersSearch)
            {
                return ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>(
                    []);
            }

            // "a-..." deliberately sorts ahead of "b-..." so the leading
            // listing is the one without the episode.
            List<ApiContentSearchItem> results =
            [
                new("a-first-listing", BuildItem(_episodes))
            ];
            if (_secondListingId is not null)
            {
                results.Add(
                    new(_secondListingId, BuildItem(_secondListingEpisodes)));
            }

            return ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>(
                results);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(
                new ApiContentItem(searchItem.PreviewItem));
        }

        public ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var available = searchItem.PreviewItem.Units
                .Select(static candidate => candidate.DisplayNumber);
            if (!available.Contains(unit.DisplayNumber, StringComparer.Ordinal))
            {
                return ValueTask.FromResult<ApiContentVideoSet?>(null);
            }

            return ValueTask.FromResult<ApiContentVideoSet?>(
                new ApiContentVideoSet(
                    ContentUnitKind.Episode,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    [
                        new ApiContentVideoSource(
                            "Stub server",
                            new Uri("https://media.example/stub.m3u8"),
                            "application/vnd.apple.mpegurl")
                    ]));
        }

        private CatalogTransferItem BuildItem(IReadOnlyList<string> episodes) =>
            new()
            {
                MediaType = "movie",
                Subtype = "series",
                Name = _title,
                Categories = ["Series"],
                Units = episodes
                    .Select((number, index) => new CatalogTransferUnit
                    {
                        Kind = "episode",
                        DisplayNumber = number,
                        SortOrder = index
                    })
                    .ToArray()
            };
    }

    private sealed class UnusedHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "This test does not expect an HTTP request.");
    }
}

using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ContentSourceCatalogVideoTests
{
    [Fact]
    public async Task DynamicEpisodeMatchMergesAnimeAndReturnsAllServers()
    {
        var provider = new DynamicAnimeProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateAnimeRepository();
        var service = CreateService(repository, httpClient, provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var episode = Assert.Single(details.Units);
        var originalEpisodeKey = episode.Identity.StableKey;

        var resolution = await service.ResolveApiVideosForUnitAsync(
            details,
            episode);

        Assert.Equal("Example test", resolution.ProviderName);
        Assert.Equal(
            ["PremiunVIP", "SaveFiles"],
            resolution.VideoSet.Sources.Select(static source => source.Label));
        Assert.Equal(details.Item.Id, resolution.Details.Item.Id);
        Assert.Equal(3, resolution.Details.Units.Count);
        Assert.Equal(originalEpisodeKey, resolution.Unit.Identity.StableKey);
        Assert.Equal(1, provider.UnitVideoLoadCount);
        Assert.All(
            provider.MediaTypes,
            static mediaType => Assert.Equal(MediaType.Movie, mediaType));
    }

    [Fact]
    public async Task DynamicEpisodeLookupLetsTheCallerChooseAmongMatchingProviders()
    {
        var first = new DynamicAnimeProvider(
            "anime-first",
            "First anime source");
        var second = new DynamicAnimeProvider(
            "anime-second",
            "Second anime source");
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateAnimeRepository();
        var bindings = new InMemoryApiTitleBindingStore();
        var service = CreateService(
            repository,
            httpClient,
            bindings,
            first,
            second);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var episode = Assert.Single(details.Units);

        var candidates =
            await service.FindApiVideoCandidatesForUnitAsync(
                details,
                episode);

        Assert.Equal(
            ["First anime source", "Second anime source"],
            candidates.Select(static candidate =>
                candidate.ProviderName));
        Assert.Equal(0, first.UnitVideoLoadCount);
        Assert.Equal(0, second.UnitVideoLoadCount);
        var selected = Assert.Single(
            candidates,
            static candidate =>
                candidate.ProviderName == "Second anime source");
        var resolution = await service.ResolveApiVideosForUnitAsync(
            details,
            episode,
            selected);
        Assert.Equal(
            "Second anime source",
            resolution.ProviderName);
        Assert.Equal(0, first.UnitVideoLoadCount);
        Assert.Equal(1, second.UnitVideoLoadCount);

        var firstSearches = first.MediaTypes.Count;
        var secondSearches = second.MediaTypes.Count;
        var remembered = await service.FindApiVideoCandidatesForUnitAsync(
            details,
            episode);

        Assert.Equal(
            ["First anime source", "Second anime source"],
            remembered.Select(static candidate => candidate.ProviderName));
        Assert.True(first.MediaTypes.Count > firstSearches);
        Assert.Equal(secondSearches, second.MediaTypes.Count);
    }

    [Fact]
    public async Task RememberedEpisodeRouteIsReportedBeforeOtherProvidersFinish()
    {
        var slowRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var remembered = new DynamicAnimeProvider(
            "anime-remembered",
            "Remembered anime source");
        var slow = new DynamicAnimeProvider(
            "anime-slow",
            "Slow anime source",
            slowRelease.Task);
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateAnimeRepository();
        var bindings = new InMemoryApiTitleBindingStore();
        var service = CreateService(
            repository,
            httpClient,
            bindings,
            remembered,
            slow);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var episode = Assert.Single(details.Units);
        await bindings.SaveAsync(
            new ApiTitleBinding(
                card.Id,
                remembered.Descriptor.Id,
                "yani-futari",
                "Yani Futari",
                [
                    new ApiProviderUnitRoute(
                        ContentUnitKind.Episode,
                        episode.DisplayNumber,
                        episode.Identity.SeasonNumber,
                        "/episode/yani-futari-1")
                ],
                DateTimeOffset.UtcNow));
        var earlyCandidate = new TaskCompletionSource<
            ApiContentUnitVideoCandidate>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var lookup = service.FindApiVideoCandidatesForUnitAsync(
                details,
                episode,
                (candidate, _) =>
                {
                    earlyCandidate.TrySetResult(candidate);
                    return ValueTask.CompletedTask;
                })
            .AsTask();
        var reported = await earlyCandidate.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(remembered.Descriptor.Id, reported.ProviderId);
        Assert.False(lookup.IsCompleted);

        slowRelease.SetResult();
        var candidates = await lookup;

        Assert.Equal(
            ["Remembered anime source", "Slow anime source"],
            candidates.Select(static candidate => candidate.ProviderName));
    }

    [Fact]
    public async Task EpisodeMatchesAreReportedAsEachProviderFinishes()
    {
        var slowRelease = new TaskCompletionSource(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var fast = new DynamicAnimeProvider(
            "anime-fast",
            "Fast anime source");
        var slow = new DynamicAnimeProvider(
            "anime-slow",
            "Slow anime source",
            slowRelease.Task);
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateAnimeRepository();
        var service = CreateService(
            repository,
            httpClient,
            fast,
            slow);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var episode = Assert.Single(details.Units);
        var reportedProviderIds = new List<string>(2);
        var firstReported = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var secondReported = new TaskCompletionSource<string>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        var lookup = service.FindApiVideoCandidatesForUnitAsync(
                details,
                episode,
                (candidate, _) =>
                {
                    reportedProviderIds.Add(candidate.ProviderId);
                    if (reportedProviderIds.Count == 1)
                    {
                        firstReported.TrySetResult(candidate.ProviderId);
                    }
                    else
                    {
                        secondReported.TrySetResult(candidate.ProviderId);
                    }

                    return ValueTask.CompletedTask;
                })
            .AsTask();
        var firstProviderId = await firstReported.Task.WaitAsync(
            TimeSpan.FromSeconds(2));

        Assert.Equal(fast.Descriptor.Id, firstProviderId);
        Assert.False(lookup.IsCompleted);

        slowRelease.SetResult();
        var secondProviderId = await secondReported.Task.WaitAsync(
            TimeSpan.FromSeconds(2));
        var candidates = await lookup;

        Assert.Equal(slow.Descriptor.Id, secondProviderId);
        Assert.Equal(
            [fast.Descriptor.Id, slow.Descriptor.Id],
            reportedProviderIds);
        Assert.Equal(2, candidates.Count);
    }

    [Fact]
    public async Task EnteringSavedAnimeCanRefreshNewProviderEpisodes()
    {
        var provider = new DynamicAnimeProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = CreateAnimeRepository();
        var service = CreateService(repository, httpClient, provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));

        var refreshed = await service.RefreshApiItemAsync(details);

        Assert.NotNull(refreshed);
        Assert.Equal(details.Item.Id, refreshed.Details.Item.Id);
        Assert.Equal(3, refreshed.Details.Units.Count);
        Assert.Contains(
            refreshed.Details.Units,
            static unit => unit.DisplayNumber == "3");
        Assert.Equal(0, provider.UnitVideoLoadCount);
    }

    [Fact]
    public async Task StandaloneMovieMatchesByTitleAndUsesProviderUnitNumber()
    {
        var provider = new StandaloneMovieProvider();
        using var httpClient = new HttpClient(new EmptyHandler());
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                Items =
                [
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = "movie",
                        Name = "The Wandering Earth",
                        Units =
                        [
                            new CatalogTransferUnit
                            {
                                Kind = "episode",
                                DisplayNumber = "feature",
                                SortOrder = 0,
                                Name = "Full movie"
                            }
                        ]
                    }
                ]
            });
        var service = CreateService(repository, httpClient, provider);
        var browse = new CatalogBrowseService(repository);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));
        var localUnit = Assert.Single(details.Units);
        var originalUnitKey = localUnit.Identity.StableKey;

        var resolution = await service.ResolveApiVideosForUnitAsync(
            details,
            localUnit);

        Assert.Equal("1", provider.RequestedDisplayNumber);
        Assert.Equal("Movie CDN", Assert.Single(
            resolution.VideoSet.Sources).Label);
        Assert.Equal(details.Item.Id, resolution.Details.Item.Id);
        var refreshedUnit = Assert.Single(resolution.Details.Units);
        Assert.Equal("feature", refreshedUnit.DisplayNumber);
        Assert.Equal(originalUnitKey, refreshedUnit.Identity.StableKey);
        Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
    }
    private static InMemoryCatalogRepository CreateAnimeRepository() =>
        new(new CatalogTransferDocument
        {
            Items =
            [
                CreateAnime(
                    "Yani Futari",
                    "Saved anime",
                    episodeCount: 1)
            ]
        });

    private static ContentSourceCatalogService CreateService(
        InMemoryCatalogRepository repository,
        HttpClient httpClient,
        params IApiContentProvider[] providers) =>
        CreateService(
            repository,
            httpClient,
            titleBindingStore: null,
            providers);

    private static ContentSourceCatalogService CreateService(
        InMemoryCatalogRepository repository,
        HttpClient httpClient,
        IApiTitleBindingStore? titleBindingStore,
        params IApiContentProvider[] providers)
    {
        var settings = new InMemoryContentSourceProviderSettingsStore(
            new ContentSourceProviderSettings
            {
                Providers = providers
                    .Select(provider =>
                        new ContentSourceProviderConfiguration
                        {
                            Id = provider.Descriptor.Id,
                            DisplayName = provider.Descriptor.DisplayName,
                            Kind = ContentSourceProviderKind.Api,
                            IsEnabled = true,
                            BaseUrl =
                            $"https://{provider.Descriptor.Id}.example/"
                        })
                    .ToArray()
            });
        return new ContentSourceCatalogService(
            new CatalogBrowseService(repository),
            new CatalogJsonTransferService(repository),
            settings,
            httpClient,
            providers,
            dailyUpdateStore: null,
            timeProvider: null,
            titleBindingStore);
    }

    private static CatalogTransferItem CreateAnime(
        string name,
        string description,
        int episodeCount) =>
        new()
        {
            MediaType = "movie",
            Subtype = "anime_series",
            Name = name,
            Description = description,
            PublicationStatus = "ongoing",
            Genres = ["Comedy"],
            Categories = ["Anime"],
            Units = Enumerable.Range(1, episodeCount)
                .Select(number => new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = number.ToString(),
                    SortOrder = number - 1
                })
                .ToArray()
        };

    private sealed class DynamicAnimeProvider : IApiContentProvider
    {
        private readonly Task? _searchGate;
        private readonly CatalogTransferItem _item =
            CreateAnime(
                "Yani Futari",
                "Current provider anime",
                episodeCount: 3) with
            {
                Units =
                [
                    new CatalogTransferUnit
                    {
                        Kind = "episode",
                        DisplayNumber = "01",
                        SortOrder = 0
                    },
                    new CatalogTransferUnit
                    {
                        Kind = "episode",
                        DisplayNumber = "2",
                        SortOrder = 1
                    },
                    new CatalogTransferUnit
                    {
                        Kind = "episode",
                        DisplayNumber = "3",
                        SortOrder = 2
                    }
                ]
            };

        public DynamicAnimeProvider(
            string id = "example-test",
            string displayName = "Example test",
            Task? searchGate = null)
        {
            Descriptor = new ApiContentProviderDescriptor(
                id,
                displayName);
            _searchGate = searchGate;
        }

        public ApiContentProviderDescriptor Descriptor { get; }

        public List<MediaType?> MediaTypes { get; } = [];

        public int UnitVideoLoadCount { get; private set; }

        public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_searchGate is not null)
            {
                await _searchGate.WaitAsync(cancellationToken);
            }

            MediaTypes.Add(mediaType);
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new("yani-futari", _item)
            ];
            return results;
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ApiContentItem(_item));
        }

        public ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            UnitVideoLoadCount++;
            ApiContentVideoSet videoSet = new(
                ContentUnitKind.Episode,
                "01",
                seasonNumber: null,
                [
                    new ApiContentVideoSource(
                        "PremiunVIP",
                        new Uri("https://media.example/yani-1.mp4"),
                        "video/mp4"),
                    new ApiContentVideoSource(
                        "SaveFiles",
                        new Uri("https://media.example/yani-1.m3u8"),
                        "application/vnd.apple.mpegurl")
                ]);
            return ValueTask.FromResult<ApiContentVideoSet?>(
                unit.Matches(videoSet) ? videoSet : null);
        }
    }

    private sealed class StandaloneMovieProvider : IApiContentProvider
    {
        private readonly CatalogTransferItem _item = new()
        {
            MediaType = "movie",
            Subtype = "movie",
            Name = "The Wandering Earth",
            ThumbnailRef = "https://images.example/wandering-earth.jpg",
            Units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = "1",
                    SortOrder = 0,
                    Name = "Provider playback entry"
                }
            ]
        };

        public ApiContentProviderDescriptor Descriptor { get; } = new(
            "standalone-movie-test",
            "Standalone movie test");

        public string? RequestedDisplayNumber { get; private set; }

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new("movie:wandering-earth", _item)
            ];
            return ValueTask.FromResult(results);
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return ValueTask.FromResult(new ApiContentItem(_item));
        }

        public ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            ApiContentUnitRequest unit,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            RequestedDisplayNumber = unit.DisplayNumber;
            if (!string.Equals(
                    unit.DisplayNumber,
                    "1",
                    StringComparison.Ordinal))
            {
                return ValueTask.FromResult<ApiContentVideoSet?>(null);
            }

            return ValueTask.FromResult<ApiContentVideoSet?>(
                new ApiContentVideoSet(
                    ContentUnitKind.Episode,
                    "1",
                    seasonNumber: null,
                    [
                        new ApiContentVideoSource(
                            "Movie CDN",
                            new Uri("https://media.example/movie.m3u8"),
                            "application/vnd.apple.mpegurl")
                    ]));
        }
    }
    private sealed class EmptyHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                "This test does not expect an HTTP request.");
    }
}

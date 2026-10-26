using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A film's one playable unit is the thing a refresh goes to fetch, so
/// refusing to refresh until it already has one leaves it unplayable forever:
/// nothing arrives, and the page offers nothing to select.
/// </summary>
/// <remarks>
/// The rule earns its keep on a series, where the episodes already recorded
/// are what a provider's answer is checked against, so both halves are held
/// here. Reported against "Camp Rock 3" — saved as movie.movie with no units,
/// while the provider served it as a single episode.
/// </remarks>
public sealed class StandaloneMovieRefreshTests
{
    [Fact]
    public async Task AFilmWithNothingRecordedYetStillRefreshes()
    {
        var provider = new SingleUnitMovieProvider("movie");
        var (service, details) = await CreateAsync(provider, "movie");

        var refreshed = await service.RefreshApiItemAsync(details);

        Assert.NotNull(refreshed);
        var unit = Assert.Single(refreshed.Details.Units);
        Assert.Equal(ContentUnitKind.Episode, unit.Kind);
    }

    [Fact]
    public async Task ASeriesWithNothingRecordedYetAlsoRefreshes()
    {
        // The same circle, and the larger half of it: the daily fetch
        // discovers a series as a name and a poster, so requiring an episode
        // before it may look for episodes leaves it empty for good.
        var provider = new SeriesProvider();
        var (service, details) = await CreateAsync(provider, "series");

        var refreshed = await service.RefreshApiItemAsync(details);

        Assert.NotNull(refreshed);
        var season = Assert.Single(refreshed.Details.Seasons);
        Assert.Equal(2, season.Units.Count);
    }

    private static async Task<(ContentSourceCatalogService, CatalogItemDetails)>
        CreateAsync(IApiContentProvider provider, string subtype)
    {
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                Items =
                [
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = subtype,
                        Name = "Camp Rock 3"
                    }
                ]
            });
        var httpClient = new HttpClient(new NoNetworkHandler());
        var browse = new CatalogBrowseService(repository);
        var service = new ContentSourceCatalogService(
            browse,
            new CatalogJsonTransferService(repository),
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers =
                    [
                        new ContentSourceProviderConfiguration
                        {
                            Id = provider.Descriptor.Id,
                            DisplayName = provider.Descriptor.DisplayName,
                            Kind = ContentSourceProviderKind.Api,
                            IsEnabled = true,
                            BaseUrl = "https://films.example/api"
                        }
                    ]
                }),
            httpClient,
            [provider],
            dailyUpdateStore: null,
            timeProvider: null,
            titleBindingStore: null);
        var card = Assert.Single(
            (await browse.GetOverviewAsync(MediaType.Movie)).Items);
        var details = Assert.IsType<CatalogItemDetails>(
            await browse.GetDetailsAsync(card.Id));

        Assert.Empty(details.Units);
        return (service, details);
    }

    private sealed class SeriesProvider : IApiContentProvider
    {
        private readonly CatalogTransferItem _item = new()
        {
            MediaType = "movie",
            Subtype = "series",
            Name = "Camp Rock 3",
            Description = "A series the provider knows all about.",
            ThumbnailRef = "https://films.example/series.jpg",
            ReleaseDate = "2026-08-01",
            Genres = ["Drama"],
            Categories = ["Series"],
            Seasons =
            [
                new CatalogTransferSeason
                {
                    Number = 1,
                    Units =
                    [
                        new CatalogTransferUnit
                        {
                            Kind = "episode",
                            DisplayNumber = "1",
                            SortOrder = 0
                        },
                        new CatalogTransferUnit
                        {
                            Kind = "episode",
                            DisplayNumber = "2",
                            SortOrder = 1
                        }
                    ]
                }
            ]
        };

        public ApiContentProviderDescriptor Descriptor { get; } = new(
            "series-refresh-test",
            "Series refresh test");

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new("series:camp-rock-3", _item)
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
    }

    /// <summary>
    /// Nothing here should reach the network: the provider is a stand-in.
    /// </summary>
    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Unexpected request to {request.RequestUri}.");
    }

    private sealed class SingleUnitMovieProvider(string subtype) :
        IApiContentProvider
    {
        private readonly CatalogTransferItem _item = new()
        {
            MediaType = "movie",
            Subtype = subtype,
            Name = "Camp Rock 3",
            Description = "The one the provider actually serves.",
            ThumbnailRef = "https://films.example/camp-rock-3.jpg",
            ReleaseDate = "2026-08-01",
            Genres = ["Music"],
            Categories = ["Family"],
            Units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = "1",
                    SortOrder = 0,
                    Name = "Camp Rock 3"
                }
            ]
        };

        public List<string> Queries { get; } = [];

        public ApiContentProviderDescriptor Descriptor { get; } = new(
            "standalone-movie-test",
            "Standalone movie test");

        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
            Uri baseUri,
            string query,
            MediaType? mediaType = null,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Queries.Add(query);
            IReadOnlyList<ApiContentSearchItem> results =
            [
                new("movie:camp-rock-3", _item)
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
    }
}

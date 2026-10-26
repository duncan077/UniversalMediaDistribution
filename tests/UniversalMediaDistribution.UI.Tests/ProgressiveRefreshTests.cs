using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Catching a title up asks every provider at once and answers as each
/// replies. The quickest fills the page immediately; a slower one carrying
/// more chapters still gets to replace it, because the fullest source is
/// often not the fastest.
/// </summary>
public sealed class ProgressiveRefreshTests
{
    private const string Title = "Copper Skies";

    [Fact]
    public async Task FullerLateAnswerKeepsEarlierMetadataAndArtwork()
    {
        var quick = new ChapterProvider("quick", "Quick", 2, transform: item => item with
        {
            ThumbnailRef = "https://quick-art.example/cover.jpg",
            BackdropRef = "https://chapters.example/backdrop.jpg",
            OriginalName = "Original title",
            Units = item.Units.Select(unit => unit with
            {
                Name = "Chapter title",
                ThumbnailRef = "https://chapters.example/chapter.jpg",
                PageCount = 12
            }).ToArray()
        });
        var slow = new ChapterProvider("slow", "Slow", 10, gated: true, transform: item => item with
        {
            Description = null,
            ThumbnailRef = " ",
            ReleaseDate = null,
            Genres = [],
            Categories = []
        });
        var (service, details) = CreateService(quick, slow);
        var first = new TaskCompletionSource<ContentSourceCatalogRefreshResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = service.RefreshApiItemAsync(details, new CallbackProgress(report => first.TrySetResult(report))).AsTask();
        var early = await first.Task.WaitAsync(TimeSpan.FromSeconds(10));
        slow.Release();
        var result = (await task.WaitAsync(TimeSpan.FromSeconds(10)))!;
        Assert.Equal(10, result.Details.Units.Count);
        Assert.Equal("Filled in by Quick", result.Details.Item.Description);
        Assert.Equal("https://quick-art.example/cover.jpg", result.Details.Item.ThumbnailRef);
        Assert.Equal("https://chapters.example/backdrop.jpg", result.Details.Item.BackdropRef);
        Assert.Equal(early.Details.Item.Description, result.Details.Item.Description);
        Assert.Equal(early.Details.Item.ThumbnailRef, result.Details.Item.ThumbnailRef);
        Assert.Equal(early.Details.Item.BackdropRef, result.Details.Item.BackdropRef);
        Assert.Equal(early.Details.OriginalName, result.Details.OriginalName);
        Assert.Equal(early.Details.ReleaseDate, result.Details.ReleaseDate);
        Assert.Equal(early.Details.Item.Genres, result.Details.Item.Genres);
        Assert.Equal(early.Details.Item.Categories, result.Details.Item.Categories);
        Assert.Equal("Chapter title", result.Details.Units[0].Name);
        Assert.Equal("https://chapters.example/chapter.jpg", result.Details.Units[0].ThumbnailRef);
        Assert.Equal(12, result.Details.Units[0].PageCount);
        Assert.Contains(new Uri("https://chapters.example/"), result.TrustedArtworkOrigins);
        Assert.Contains(new Uri("https://quick-art.example/"), result.TrustedArtworkOrigins);
        Assert.Equal(new Uri("https://chapters.example/manga"), result.ArtworkReferrer);
    }

    [Fact]
    public async Task TheQuickAnswerArrivesBeforeTheSlowOneFinishes()
    {
        var quick = new ChapterProvider("quick", "Quick", chapters: 2);
        var slow = new ChapterProvider(
            "slow",
            "Slow",
            chapters: 10,
            gated: true);
        var (service, details) = CreateService(quick, slow);

        var reports = new List<ContentSourceCatalogRefreshResult>();
        var firstArrived = new TaskCompletionSource();
        var progress = new CallbackProgress(report =>
        {
            lock (reports)
            {
                reports.Add(report);
                if (reports.Count == 1)
                {
                    firstArrived.TrySetResult();
                }
            }
        });

        var refresh = service.RefreshApiItemAsync(details, progress).AsTask();
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));

        // The slow provider has not answered yet, and the page already shows
        // what the quick one found.
        lock (reports)
        {
            Assert.Single(reports);
            Assert.Equal(2, reports[0].Details.Units.Count);
        }

        slow.Release();
        var final = await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(final);
        Assert.Equal(10, final.Details.Units.Count);
        Assert.Equal("Slow", final.ProviderName);
        lock (reports)
        {
            Assert.Equal(2, reports.Count);
        }
    }

    [Fact]
    public async Task AThinnerLateAnswerDoesNotReplaceAFullerOne()
    {
        // The reverse race: the fuller source answers first, so the thin one
        // arriving later must not undo it — and must not cost a second write.
        var quick = new ChapterProvider("quick", "Quick", chapters: 10);
        var slow = new ChapterProvider(
            "slow",
            "Slow",
            chapters: 2,
            gated: true);
        var (service, details) = CreateService(quick, slow);

        var reports = new List<ContentSourceCatalogRefreshResult>();
        var firstArrived = new TaskCompletionSource();
        var progress = new CallbackProgress(report =>
        {
            lock (reports)
            {
                reports.Add(report);
                firstArrived.TrySetResult();
            }
        });

        var refresh = service.RefreshApiItemAsync(details, progress).AsTask();
        await firstArrived.Task.WaitAsync(TimeSpan.FromSeconds(10));
        slow.Release();
        var final = await refresh.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.NotNull(final);
        Assert.Equal(10, final.Details.Units.Count);
        lock (reports)
        {
            Assert.Single(reports);
        }
    }

    private static (ContentSourceCatalogService Service,
        CatalogItemDetails Details) CreateService(
            params ChapterProvider[] providers)
    {
        var repository = new InMemoryCatalogRepository(
            new CatalogTransferDocument
            {
                Items =
                [
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = Title
                    }
                ]
            });
        var browse = new CatalogBrowseService(repository);
        var service = new ContentSourceCatalogService(
            browse,
            new CatalogJsonTransferService(repository),
            new InMemoryContentSourceProviderSettingsStore(
                new ContentSourceProviderSettings
                {
                    Providers = providers
                        .Select(static provider =>
                            new ContentSourceProviderConfiguration
                            {
                                Id = provider.Descriptor.Id,
                                DisplayName = provider.Descriptor.DisplayName,
                                Kind = ContentSourceProviderKind.Api,
                                IsEnabled = true,
                                BaseUrl = "https://chapters.example/api"
                            })
                        .ToArray()
                }),
            new HttpClient(new NoNetworkHandler()),
            providers,
            dailyUpdateStore: null,
            timeProvider: null,
            titleBindingStore: null);
        var card = browse.GetOverviewAsync(MediaType.Book)
            .AsTask()
            .GetAwaiter()
            .GetResult()
            .Items[0];
        var details = browse.GetDetailsAsync(card.Id)
            .AsTask()
            .GetAwaiter()
            .GetResult()!;
        return (service, details);
    }

    private sealed class CallbackProgress(
        Action<ContentSourceCatalogRefreshResult> report) :
        IProgress<ContentSourceCatalogRefreshResult>
    {
        // Reported straight through rather than posted: a test needs to know
        // an answer landed before it releases the next one.
        public void Report(ContentSourceCatalogRefreshResult value) =>
            report(value);
    }

    private sealed class NoNetworkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken) =>
            throw new InvalidOperationException(
                $"Unexpected request to {request.RequestUri}.");
    }

    private sealed class ChapterProvider : IApiContentProvider
    {
        private readonly TaskCompletionSource? _gate;
        private readonly CatalogTransferItem _item;

        public ChapterProvider(
            string id,
            string displayName,
            int chapters,
            bool gated = false,
            Func<CatalogTransferItem, CatalogTransferItem>? transform = null)
        {
            _gate = gated ? new TaskCompletionSource() : null;
            Descriptor = new ApiContentProviderDescriptor(id, displayName);
            _item = new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = Title,
                Description = "Filled in by " + displayName,
                ThumbnailRef = "https://chapters.example/cover.jpg",
                ReleaseDate = "2026-01-01",
                Genres = ["Drama"],
                Categories = ["Manga"],
                Units = Enumerable.Range(1, chapters)
                    .Select(static number => new CatalogTransferUnit
                    {
                        Kind = "chapter",
                        DisplayNumber = number.ToString(),
                        SortOrder = number
                    })
                    .ToArray()
            };
            if (transform is not null) _item = transform(_item);
        }

        public ApiContentProviderDescriptor Descriptor { get; }

        public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) =>
            [baseUri, new Uri($"https://{Descriptor.Id}-art.example/")];

        public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri) =>
            artworkUri.Host == $"{Descriptor.Id}-art.example" ? new Uri(baseUri, "manga") : null;

        public void Release() => _gate?.TrySetResult();

        public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
            SearchAsync(
                Uri baseUri,
                string query,
                MediaType? mediaType = null,
                CancellationToken cancellationToken = default)
        {
            if (_gate is not null)
            {
                await _gate.Task.WaitAsync(cancellationToken);
            }

            return [new ApiContentSearchItem(Descriptor.Id + ":copper", _item)];
        }

        public ValueTask<ApiContentItem> LoadItemAsync(
            Uri baseUri,
            ApiContentSearchItem searchItem,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(new ApiContentItem(_item));
    }
}

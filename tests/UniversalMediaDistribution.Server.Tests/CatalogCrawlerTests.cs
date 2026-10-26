using System.Net;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class CatalogCrawlerTests
{
    [Fact]
    public async Task ResumesPagesMergesAliasesAndStoresOnlyMetadata()
    {
        using var scope = TestDirectory.Create();
        var provider = new FakeProvider();
        var database = new ServerDatabase(scope.Path);
        var options = new CatalogCrawlerOptions { PagesPerProvider = 1 };
        var first = await new CatalogCrawler(database, [new(provider)], options).RunAsync(CancellationToken.None);
        Assert.Equal(1, first.TitlesUpdated);
        Assert.Equal("2", new CatalogCrawlerStore(database).State("fake").Cursor);
        var firstItem = Assert.Single(new SqliteOnlineCatalogStore(scope.Path).Page(null, 10, CancellationToken.None).Document.Items);
        Assert.Null(firstItem.ThumbnailRef);
        Assert.Null(firstItem.BackdropRef);
        Assert.Null(firstItem.Contributors[0].ThumbnailRef);
        Assert.Null(firstItem.Contributors[0].Url);
        Assert.All(firstItem.Units, unit => Assert.Null(unit.ThumbnailRef));
        var second = await new CatalogCrawler(new ServerDatabase(scope.Path), [new(provider)], options).RunAsync(CancellationToken.None);
        Assert.Equal(1, second.TitlesUpdated);
        var item = Assert.Single(new SqliteOnlineCatalogStore(scope.Path).Page(null, 10, CancellationToken.None).Document.Items);
        Assert.Equal(firstItem.Id, item.Id);
        Assert.Equal("Known title", item.Name);
        Assert.Equal("Keep this description", item.Description);
        Assert.Equal(2, item.Units.Count);
        Assert.True(new CatalogCrawlerStore(database).State("fake").Complete);
        Assert.Equal([null, "2"], provider.Cursors);
        Assert.All(provider.Details, result => { Assert.Empty(result.UnitRoutes); Assert.True(result.RequireProviderTitle); });
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT preview FROM crawler_titles;";
        using var reader = command.ExecuteReader();
        while (reader.Read()) Assert.DoesNotContain("https://", reader.GetString(0), StringComparison.Ordinal);
    }

    [Fact]
    public async Task LeastRecentlyAttemptedProvidersRunFirstAcrossRestarts()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var store = new CatalogCrawlerStore(database);
        store.State("fake");
        store.ProviderAttempted("fake", DateTimeOffset.UtcNow);
        var order = new List<string>();
        var older = new FakeProvider { OnDiscovery = () => order.Add("fake") };
        var newer = new FakeProvider { Id = "new", OnDiscovery = () => order.Add("new") };
        await new CatalogCrawler(database, [new(older), new(newer)], new CatalogCrawlerOptions { PagesPerProvider = 1 }).RunAsync(CancellationToken.None);
        Assert.Equal(["new", "fake"], order);
        Assert.NotNull(new CatalogCrawlerStore(new ServerDatabase(scope.Path)).State("new").LastAttempt);
    }

    [Fact]
    public void RepeatedDiscoveryPageDoesNotAdvanceCheckpointEvenAfterRestart()
    {
        using var scope = TestDirectory.Create();
        var store = new CatalogCrawlerStore(new ServerDatabase(scope.Path));
        store.State("fake");
        var page = new[] { new ApiContentSearchItem("one", Title("One")) };
        store.Enqueue("fake", page, DateTimeOffset.UtcNow, updates: false, nextCursor: "2", discovery: true);
        store = new CatalogCrawlerStore(new ServerDatabase(scope.Path));
        Assert.Throws<InvalidDataException>(() => store.Enqueue("fake", page, DateTimeOffset.UtcNow,
            updates: false, nextCursor: "3", discovery: true, requestedCursor: "2"));
        Assert.Equal("2", store.State("fake").Cursor);
        Assert.False(store.State("fake").Complete);
        // A fresh periodic rescan is allowed to encounter the same first page.
        store.Enqueue("fake", page, DateTimeOffset.UtcNow, updates: false, nextCursor: "2", discovery: true);
    }

    [Fact]
    public async Task LeasePreventsOverlappingWorkersAndRecoversAfterExpiration()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var store = new CatalogCrawlerStore(database);
        var now = DateTimeOffset.UtcNow;
        Assert.True(store.TryAcquireLease("first", now, TimeSpan.FromMinutes(1)));
        var provider = new FakeProvider();
        var blocked = await new CatalogCrawler(database, [new(provider)], new()).RunAsync(CancellationToken.None);
        Assert.False(blocked.Acquired);
        Assert.Empty(provider.Cursors);
        Assert.True(new CatalogCrawlerStore(new ServerDatabase(scope.Path)).TryAcquireLease("second", now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        store.ReleaseLease("first");
        Assert.False(store.TryAcquireLease("third", now.AddMinutes(2), TimeSpan.FromMinutes(1)));
        store.ReleaseLease("second");
        Assert.True(store.TryAcquireLease("third", now.AddMinutes(2), TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public async Task ThrottlingDoesNotAdvanceCursorAndHonorsRetryAfter()
    {
        using var scope = TestDirectory.Create();
        var provider = new FakeProvider { DiscoveryError = new CatalogProviderThrottledException(TimeSpan.FromHours(3)) };
        var database = new ServerDatabase(scope.Path);
        var first = await new CatalogCrawler(database, [new(provider)], new()).RunAsync(CancellationToken.None);
        Assert.True(first.Failures > 0);
        var state = new CatalogCrawlerStore(database).State("fake");
        Assert.Null(state.Cursor);
        Assert.False(state.Complete);
        Assert.True(state.RetryAt > DateTimeOffset.UtcNow.AddHours(2));
        await new CatalogCrawler(database, [new(provider)], new()).RunAsync(CancellationToken.None);
        Assert.Single(provider.Cursors);
    }

    [Fact]
    public async Task BlockedDirectoryStillProcessesAccessibleDailyTitles()
    {
        using var scope = TestDirectory.Create();
        var provider = new FakeProvider { DiscoveryError = new HttpRequestException("Blocked", null, HttpStatusCode.Forbidden), Daily = true };
        var result = await new CatalogCrawler(new ServerDatabase(scope.Path), [new(provider)], new()).RunAsync(CancellationToken.None);
        Assert.Equal(1, result.TitlesUpdated);
        Assert.True(result.Failures > 0);
        Assert.Single(new SqliteOnlineCatalogStore(scope.Path).Page(null, 10, CancellationToken.None).Document.Items);
    }

    [Fact]
    public async Task CancellationReleasesLeaseWithoutMarkingPendingTitleSuccessful()
    {
        using var scope = TestDirectory.Create();
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var database = new ServerDatabase(scope.Path);
        var result = await new CatalogCrawler(database, [new(new FakeProvider())], new()).RunAsync(cancellation.Token);
        Assert.Equal("cancelled", result.Status);
        Assert.True(new CatalogCrawlerStore(database).TryAcquireLease("next", DateTimeOffset.UtcNow, TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void MissingYearCannotJoinDifferentKnownRemakes()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        var movie = new CatalogTransferItem { MediaType = "movie", Subtype = "movie", Name = "Same movie" };
        var first = catalog.MergeScraped("a", "1", movie with { ReleaseYear = 1990 }, CancellationToken.None);
        var second = catalog.MergeScraped("b", "1", movie with { ReleaseYear = 2020 }, CancellationToken.None);
        var unknown = catalog.MergeScraped("c", "1", movie, CancellationToken.None);
        Assert.Equal(3, catalog.Page(null, 100, CancellationToken.None).Document.Items.Count);
        Assert.NotEqual(first.ItemId, unknown.ItemId);
        Assert.NotEqual(second.ItemId, unknown.ItemId);
        var store = new CatalogCrawlerStore(new ServerDatabase(scope.Path));
        store.State("a");
        store.Enqueue("a", [new("1", movie)], DateTimeOffset.UtcNow, updates: true);
        store.TitleSucceeded("a", "1", first.ItemId, DateTimeOffset.UtcNow);
        Assert.Equal(first.ItemId, catalog.MergeScraped("a", "1", movie, CancellationToken.None).ItemId);
        Assert.Contains(catalog.Page(null, 100, CancellationToken.None).Document.Items, item => item.Id == second.ItemId && item.ReleaseYear == 2020);
    }

    [Fact]
    public void MergeKeepsFormatsAndKnownRemakeYearsSeparateAndDoesNotRestampUnchangedItems()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        var first = catalog.MergeScraped("a", "1", Title("Same", "manga"), CancellationToken.None);
        Assert.False(catalog.MergeScraped("a", "1", Title("Same", "manga"), CancellationToken.None).Changed);
        var second = catalog.MergeScraped("b", "1", Title("Same", "novel"), CancellationToken.None);
        Assert.NotEqual(first.ItemId, second.ItemId);
        var movie = new CatalogTransferItem { Name = "A remake", MediaType = "movie", Subtype = "movie", ReleaseYear = 1990 };
        var old = catalog.MergeScraped("c", "1", movie, CancellationToken.None);
        var remake = catalog.MergeScraped("d", "1", movie with { ReleaseYear = 2020 }, CancellationToken.None);
        Assert.NotEqual(old.ItemId, remake.ItemId);
        Assert.Equal(4, catalog.Page(null, 10, CancellationToken.None).Document.Items.Count);
        var page = catalog.Page(null, 2, CancellationToken.None);
        Assert.Equal(2, page.Document.Items.Count);
        var next = catalog.Page(page.ContinuationToken, 2, CancellationToken.None);
        Assert.Equal(2, next.Document.Items.Count);
        Assert.Null(next.ContinuationToken);
        Assert.Empty(page.Document.Items.Select(item => item.Id).Intersect(next.Document.Items.Select(item => item.Id)));
        Assert.Equal(2, catalog.SearchDatabase("same", "book", 20, CancellationToken.None).Items.Count);
    }

    [Fact]
    public async Task PreviouslyQueuedUnsupportedWorksDoNotBlockComicTitlesOrCountAsProviderFailures()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var store = new CatalogCrawlerStore(database);
        store.State("fake");
        var old = DateTimeOffset.UtcNow.AddHours(-2);
        var entries = new[] { "a-prose", "b-prose", "c-prose", "z-comic" }
            .Select(id => new ApiContentSearchItem(id, Title("Known title"))).ToArray();
        store.Enqueue("fake", entries, old, updates: true);
        foreach (var item in entries.Take(3)) store.TitleFailed("fake", new(item.ExternalId, item, 0), "network", old);
        var provider = new FakeProvider
        {
            DetailError = item => item.ExternalId.EndsWith("prose", StringComparison.Ordinal)
                ? new ApiCatalogItemUnsupportedException("This work is prose.") : null
        };

        var result = await new CatalogCrawler(database, [new(provider)], new()).RunAsync(CancellationToken.None);

        Assert.Equal("completed", result.Status);
        Assert.Equal(0, result.Failures);
        Assert.Contains(provider.Details, item => item.ExternalId == "z-comic");
        Assert.Empty(new CatalogCrawlerStore(new ServerDatabase(scope.Path)).Due("fake", DateTimeOffset.UtcNow, 20));
        Assert.Contains(store.Due("fake", DateTimeOffset.UtcNow.AddDays(8), 20), item => item.ExternalId == "a-prose");
        var diagnostics = new ServerDiagnosticsStore(database);
        Assert.Empty(diagnostics.ReadLogs("fake", null, null, 20).Items);
        var state = Assert.Single(diagnostics.Snapshot([provider.Descriptor], new HashSet<string>(),
            new(DateTimeOffset.UtcNow, 0, 0, 0, 0)).Providers);
        Assert.Equal("ok", state.Status);
        Assert.Equal(0, state.FailedTitles);
    }

    private static CatalogTransferItem Title(string name, string subtype = "manga") => new()
    {
        MediaType = "book",
        Subtype = subtype,
        Name = name,
        Description = name == "Known title" ? "Keep this description" : null,
        AlternativeNames = name == "Alternative" ? [new CatalogTransferAlias { Name = "Known title" }] : [],
        ThumbnailRef = "https://art.example/cover.jpg",
        BackdropRef = "https://art.example/background.jpg",
        Contributors = [new CatalogTransferContributor { Name = "Author", Url = "https://author.example/", ThumbnailRef = "https://art.example/author.jpg" }],
        Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = name == "Alternative" ? "2" : "1", ThumbnailRef = "https://art.example/chapter.jpg" }]
    };

    private sealed class FakeProvider : IApiCatalogProvider
    {
        public string Id { get; init; } = "fake";
        public Action? OnDiscovery { get; init; }
        public ApiContentProviderDescriptor Descriptor => new(Id, "Fake", "https://provider.example/");
        public string CatalogDiscoveryDescription => "Fixture";
        public bool Daily { get; init; }
        public bool SupportsDailyUpdates => Daily;
        public Exception? DiscoveryError { get; init; }
        public Func<ApiContentSearchItem, Exception?>? DetailError { get; init; }
        public List<string?> Cursors { get; } = [];
        public List<ApiContentSearchItem> Details { get; } = [];
        public ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
        {
            Cursors.Add(cursor);
            OnDiscovery?.Invoke();
            if (DiscoveryError is { } error) throw error;
            return ValueTask.FromResult(new ApiContentSearchPage([new(cursor ?? "1", Title(cursor is null ? "Known title" : "Alternative"))], cursor is null ? "2" : null));
        }
        public ValueTask<IReadOnlyList<ApiContentSearchItem>> LoadDailyUpdatesAsync(Uri baseUri, CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>([new("1", Title("Known title"))]);
        public ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(Uri baseUri, string query, MediaType? mediaType = null, CancellationToken cancellationToken = default) => throw new NotSupportedException();
        public ValueTask<ApiContentItem> LoadItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default)
        {
            Details.Add(item);
            if (DetailError?.Invoke(item) is { } error) throw error;
            return ValueTask.FromResult(new ApiContentItem(Title(item.ExternalId == "1" ? "Known title" : "Alternative")));
        }
    }
}

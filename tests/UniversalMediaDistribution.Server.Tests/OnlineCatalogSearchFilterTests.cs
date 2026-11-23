using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class OnlineCatalogSearchFilterTests
{
    [Fact]
    public async Task RecentlyAddedKeepsArrivalOrderAcrossFullSnapshots()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var first = Item(1);
        var second = Item(2);
        await store.SaveAsync(new CatalogTransferDocument { Items = [first, second] }, default);
        await store.SaveAsync(new CatalogTransferDocument { Items = [Item(3), second, first] }, default);
        Assert.Equal(new[] { "Title 3", "Title 2", "Title 1" }, store.RecentlyAdded(default).Items.Select(i => i.Name));
    }

    [Fact]
    public async Task SummarySearchOmitsChapterGraphsButSingleItemLookupKeepsThem()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var item = Item(1) with { Units = [new() { Kind = "chapter", DisplayNumber = "900" }] };
        await store.SaveAsync(new CatalogTransferDocument { Items = [item] }, default);
        var page = store.SearchDatabasePage(new ApiContentSearchRequest(category: "Manhwa"), 10, default, summary: true);
        Assert.Empty(Assert.Single(page.Document.Items).Units);
        Assert.Single(Assert.Single(store.ReadPublicItem(item.Id!.Value, default)!.Items).Units);
    }
    [Fact]
    public async Task FilterOnlySearchAppliesEveryCriterionBeforeLimitingAndPagesWithoutDuplicates()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var items = Enumerable.Range(1, 120).Select(i => Item(i) with { Genres = ["Drama"] }).ToList();
        items.Add(Item(121));
        items.Add(Item(122));
        items.Add(Item(123) with { PublicationStatus = "ongoing" });
        items.Add(Item(124) with { ReleaseYear = 2025 });
        items.Add(Item(125) with { Categories = ["Other"] });
        items.Add(Item(126) with { Contributors = [] });
        await store.SaveAsync(new CatalogTransferDocument { Items = items }, default);
        var request = new ApiContentSearchRequest(genre: ApiContentGenre.Action, releaseYear: 2026,
            category: "Manhwa", status: PublicationStatus.Completed, providerName: "ManhwaWeb");
        var first = store.SearchDatabasePage(request, 1, default);
        Assert.Equal("Title 121", Assert.Single(first.Document.Items).Name);
        Assert.NotNull(first.ContinuationToken);
        var next = store.SearchDatabasePage(new ApiContentSearchRequest(genre: ApiContentGenre.Action, releaseYear: 2026,
            category: "Manhwa", status: PublicationStatus.Completed, providerName: "ManhwaWeb", continuationToken: first.ContinuationToken), 1, default);
        Assert.Equal("Title 122", Assert.Single(next.Document.Items).Name);
        Assert.Null(next.ContinuationToken);
    }

    [Theory]
    [InlineData("category")]
    [InlineData("status")]
    [InlineData("provider")]
    [InlineData("year")]
    [InlineData("genre")]
    [InlineData("browse")]
    public async Task EachFilterCanSearchWithoutAName(string filter)
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        await store.SaveAsync(new CatalogTransferDocument { Items = [Item(1)] }, default);
        var request = new ApiContentSearchRequest(category: filter == "category" ? "Manhwa" : null,
            status: filter == "status" ? PublicationStatus.Completed : null,
            providerName: filter == "provider" ? "ManhwaWeb" : null,
            genre: filter == "genre" ? ApiContentGenre.Action : null,
            releaseYear: filter == "year" ? 2026 : null, browse: filter == "browse");
        Assert.Single(store.SearchDatabasePage(request, 10, default).Document.Items);
    }

    [Fact]
    public async Task ProviderMeansDistributorNotAuthorAndStatusDoesNotApplyToStandaloneFilms()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        await store.SaveAsync(new CatalogTransferDocument
        {
            Items =
        [
            Item(1) with { Contributors = [new() { Name = "ManhwaWeb", Credits = [new() { Role = "author" }] }] },
            Item(2) with { MediaType = "movie", Subtype = "movie" }
        ]
        }, default);
        Assert.Single(store.SearchDatabasePage(new ApiContentSearchRequest(providerName: "ManhwaWeb"), 10, default).Document.Items);
        Assert.Single(store.SearchDatabasePage(new ApiContentSearchRequest(status: PublicationStatus.Completed), 10, default).Document.Items);
        Assert.Throws<ArgumentException>(() => store.SearchDatabasePage(new ApiContentSearchRequest(browse: true, continuationToken: "bad-cursor"), 10, default));
    }

    private static CatalogTransferItem Item(int number) => new()
    {
        Id = Guid.ParseExact(number.ToString("x32"), "N"),
        MediaType = "book",
        Subtype = "manga",
        Name = $"Title {number}",
        Genres = ["Acción"],
        Categories = ["Manhwa"],
        PublicationStatus = "completed",
        ReleaseYear = 2026,
        Contributors = [new() { Name = "ManhwaWeb", Kind = "organization", Credits = [new() { Role = "distributor" }] }]
    };
}

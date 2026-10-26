using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;
using UniversalMediaDistribution.Application.UserState;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogUnitDeletionTests
{
    [Fact]
    public async Task DeletingLatestChapterRemovesOnlyItsProgressAndUpdatesLatestUnit()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document());
        var title = (await scope.Repository.ListAsync()).Single(item => item.Name == "Book");
        var details = (await scope.Repository.GetDetailsAsync(title.Id))!;
        var state = new SqliteUserMediaStateStore(scope.Database);
        foreach (var unit in details.Units)
            await state.SaveProgressAsync(unit.Identity,
                new MediaProgressState(1, MediaPositionKind.Page, true, DateTimeOffset.UtcNow));

        Assert.True(await scope.Repository.DeleteUnitAsync(details.Units[1].Identity));
        Assert.Null(await state.LoadProgressAsync(details.Units[1].Identity));
        Assert.NotNull(await state.LoadProgressAsync(details.Units[0].Identity));
        Assert.Equal(details.Units[0].Identity, Assert.Single(await state.LoadCatalogProgressAsync()).Content);
        Assert.Equal("1", (await scope.Repository.GetLatestUnitsAsync())[title.Id].DisplayNumber);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletesOnlyTheSelectedSeasonEpisodeAndKeepsTheParent(bool inMemory)
    {
        await using var scope = TestDatabaseScope.Create();
        ICatalogTransferStore repository = inMemory ? new InMemoryCatalogRepository() : scope.Repository;
        await repository.MergeCatalogAsync(Document());
        var title = (await repository.ListAsync()).Single(item => item.Name == "Series A");
        var details = (await repository.GetDetailsAsync(title.Id))!;
        var episode = details.Seasons.Single(season => season.Number == 1).Units[0];
        var service = new CatalogBrowseService(repository);
        Assert.True(service.CanDeleteUnits);
        Assert.True(await service.DeleteUnitAsync(episode.Identity));
        Assert.False(await service.DeleteUnitAsync(episode.Identity));

        // Reopen through a fresh repository to check persistence, not just cached UI state.
        if (!inMemory) repository = new SqliteCatalogRepository(new SqliteCatalogDatabase(scope.DatabasePath));
        var remaining = (await repository.GetDetailsAsync(title.Id))!;
        Assert.Equal("2", Assert.Single(remaining.Seasons.Single(season => season.Number == 1).Units).DisplayNumber);
        Assert.Equal(2, remaining.Seasons.Single(season => season.Number == 2).Units.Count);
        Assert.Equal(3, (await repository.ListAsync()).Count);
        var other = (await repository.ListAsync()).Single(item => item.Name == "Series B");
        Assert.Equal(2, (await repository.GetDetailsAsync(other.Id))!.Seasons[0].Units.Count);
        var exported = await repository.ExportCatalogAsync();
        Assert.Single(exported.Items.Single(item => item.Name == "Series A").Seasons[0].Units);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DeletesDirectChaptersAndRejectsWholeTitleIdentities(bool inMemory)
    {
        await using var scope = TestDatabaseScope.Create();
        ICatalogTransferStore repository = inMemory ? new InMemoryCatalogRepository() : scope.Repository;
        await repository.MergeCatalogAsync(Document());
        var title = (await repository.ListAsync()).Single(item => item.Name == "Book");
        var details = (await repository.GetDetailsAsync(title.Id))!;
        var service = new CatalogBrowseService(repository);
        await Assert.ThrowsAsync<ArgumentException>(() => service.DeleteUnitAsync(details.Identity).AsTask());
        Assert.True(await service.DeleteUnitAsync(details.Units[1].Identity));
        Assert.Equal("1", Assert.Single((await repository.GetDetailsAsync(title.Id))!.Units).DisplayNumber);
        Assert.False(await service.DeleteUnitAsync(new CatalogContentIdentity(Guid.NewGuid(), "missing",
            ContentUnitKind.Chapter, "1")));
    }

    private static CatalogTransferDocument Document() => new()
    {
        Items =
        [
            Series("Series A"), Series("Series B"),
            new CatalogTransferItem { Name = "Book", MediaType = "book", Subtype = "manga", Units = Units("chapter") }
        ]
    };
    private static CatalogTransferItem Series(string name) => new()
    {
        Name = name,
        MediaType = "movie",
        Subtype = "series",
        Seasons =
        [
            new CatalogTransferSeason { Number = 1, SortOrder = 0, Units = Units("episode") },
            new CatalogTransferSeason { Number = 2, SortOrder = 1, Units = Units("episode") }
        ]
    };
    private static CatalogTransferUnit[] Units(string kind) =>
    [
        new() { Kind = kind, DisplayNumber = "1", SortOrder = 0 },
        new() { Kind = kind, DisplayNumber = "2", SortOrder = 1 }
    ];
}

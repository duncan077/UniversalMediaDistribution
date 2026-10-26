using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// A card says a chapter is waiting when the title gained one past where the
/// reader stopped, so being on 57 of 58 is visible from the shelf.
/// </summary>
public sealed class LaterUnitProgressTests
{
    [Fact]
    public async Task ProgressOnTheLastChapterHasNothingWaiting()
    {
        await using var scope = TestDatabaseScope.Create();
        var (itemId, units) = await SeedAsync(scope, chapterCount: 2);
        var store = new SqliteUserMediaStateStore(scope.Database);
        await MarkAsync(store, units[^1]);

        var summary = await LoadAsync(store, itemId);

        Assert.Equal(0, summary.LaterUnitCount);
        Assert.False(summary.HasLaterUnits);
    }

    [Fact]
    public async Task ProgressBehindTheLastChapterCountsWhatIsWaiting()
    {
        await using var scope = TestDatabaseScope.Create();
        var (itemId, units) = await SeedAsync(scope, chapterCount: 3);
        var store = new SqliteUserMediaStateStore(scope.Database);
        await MarkAsync(store, units[0]);

        var summary = await LoadAsync(store, itemId);

        Assert.Equal(2, summary.LaterUnitCount);
        Assert.Equal("3", summary.LatestUnitNumber);
    }

    [Fact]
    public async Task AChapterAddedAfterwardsBecomesWaiting()
    {
        // The case the badge exists for: caught up, then the title grows.
        await using var scope = TestDatabaseScope.Create();
        var (itemId, units) = await SeedAsync(scope, chapterCount: 2);
        var store = new SqliteUserMediaStateStore(scope.Database);
        await MarkAsync(store, units[^1]);
        Assert.Equal(0, (await LoadAsync(store, itemId)).LaterUnitCount);

        await AddChapterAsync(scope, "3", sortOrder: 2);

        var summary = await LoadAsync(store, itemId);
        Assert.Equal(1, summary.LaterUnitCount);
        Assert.Equal("3", summary.LatestUnitNumber);
    }

    [Fact]
    public async Task ProgressOnTheTitleItselfHasNothingToBePast()
    {
        // Item-level progress names no unit, so there is no "after" to count
        // and the badge must stay off rather than counting every chapter.
        await using var scope = TestDatabaseScope.Create();
        var (itemId, _) = await SeedAsync(scope, chapterCount: 3);
        var store = new SqliteUserMediaStateStore(scope.Database);
        await store.SaveProgressAsync(
            new CatalogContentIdentity(itemId, $"item:{itemId:N}"),
            new MediaProgressState(
                0,
                MediaPositionKind.Page,
                isCompleted: false,
                DateTimeOffset.UtcNow));

        var summary = await LoadAsync(store, itemId);

        Assert.Equal(0, summary.LaterUnitCount);
    }

    private static async Task<(Guid ItemId, CatalogContentUnitDetails[] Units)>
        SeedAsync(TestDatabaseScope scope, int chapterCount)
    {
        var document = TestCatalogDocuments.RichCatalog();
        var manga = document.Items[0];
        manga.Units =
        [
            .. Enumerable.Range(1, chapterCount).Select(number =>
                new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = number.ToString(),
                    SortOrder = number - 1,
                    Name = $"Chapter {number}"
                })
        ];
        await scope.Repository.MergeCatalogAsync(document);
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        var details = Assert.IsType<CatalogItemDetails>(
            await scope.Repository.GetDetailsAsync(item.Id));
        return (item.Id, details.Units
            .OrderBy(static unit => unit.SortOrder)
            .ToArray());
    }

    private static async Task AddChapterAsync(
        TestDatabaseScope scope,
        string displayNumber,
        int sortOrder)
    {
        var document = TestCatalogDocuments.RichCatalog();
        var manga = document.Items[0];
        manga.Units =
        [
            new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = displayNumber,
                SortOrder = sortOrder,
                Name = $"Chapter {displayNumber}"
            }
        ];
        await scope.Repository.MergeCatalogAsync(document);
    }

    private static ValueTask MarkAsync(
        SqliteUserMediaStateStore store,
        CatalogContentUnitDetails unit) =>
        store.SaveProgressAsync(
            unit.Identity,
            new MediaProgressState(
                0,
                MediaPositionKind.Page,
                isCompleted: true,
                DateTimeOffset.UtcNow));

    private static async Task<CatalogProgressSummary> LoadAsync(
        SqliteUserMediaStateStore store,
        Guid itemId) =>
        Assert.Single(
            await store.LoadCatalogProgressAsync(),
            summary => summary.CatalogItemId == itemId);
}

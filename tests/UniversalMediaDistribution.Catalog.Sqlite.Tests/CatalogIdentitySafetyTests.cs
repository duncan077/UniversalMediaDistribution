using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogIdentitySafetyTests
{
    [Fact]
    public async Task GenericAliasCannotTransferChaptersFavoritesOrProgressToAnotherManga()
    {
        await using var scope = TestDatabaseScope.Create();
        var first = new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "manga",
            Name = "Manual de Reeducación de Damas Nobles",
            AlternativeNames = [new CatalogTransferAlias { Name = "Desconocido" }],
            Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "1" }]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument { Items = [first] });
        var original = Assert.Single(await scope.Repository.ListAsync());
        var details = (await scope.Repository.GetDetailsAsync(original.Id))!;
        var state = new SqliteUserMediaStateStore(scope.Database);
        await state.SetFavoriteCatalogItemAsync(original.Id, true);
        await state.SaveProgressAsync(details.Units[0].Identity,
            new MediaProgressState(12, MediaPositionKind.Page, false, DateTimeOffset.UtcNow));
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = [first with
            {
                Name = "Magic Emperor",
                Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "924" }]
            }]
        });
        Assert.Equal(2, (await scope.Repository.ListAsync()).Count);
        var unchanged = (await scope.Repository.GetDetailsAsync(original.Id))!;
        Assert.Equal(details.Units[0].Identity, Assert.Single(unchanged.Units).Identity);
        var exported = await new SqliteUserSyncStore(scope.Database, scope.Repository).ExportAsync("test", "Test");
        Assert.Equal(12, Assert.Single(exported.Progress).Position);
        Assert.Single(exported.FavoriteItemStates);
    }
}

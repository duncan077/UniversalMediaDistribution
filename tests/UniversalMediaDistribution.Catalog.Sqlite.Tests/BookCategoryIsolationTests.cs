using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class BookCategoryIsolationTests
{
    [Theory]
    [InlineData("novel", "manga")]
    [InlineData("web_novel", "manga")]
    [InlineData("light_novel", "comic")]
    [InlineData("manga", "comic")]
    public async Task SameNameWorksKeepTheirChaptersProgressAndFavoritesAcrossDevices(string firstSubtype, string secondSubtype)
    {
        await using var source = TestDatabaseScope.Create();
        var document = new CatalogTransferDocument { Items = [Book(firstSubtype, "1"), Book(secondSubtype, "2")] };
        await source.Repository.MergeCatalogAsync(document);
        await source.Repository.MergeCatalogAsync(document);
        var cards = await source.Repository.ListAsync();
        Assert.Equal(2, cards.Count);
        var state = new SqliteUserMediaStateStore(source.Database);
        var list = await state.CreateUserListAsync("Both formats");
        foreach (var card in cards)
        {
            var details = (await source.Repository.GetDetailsAsync(card.Id))!;
            var chapter = Assert.Single(details.Units);
            var first = card.SubtypeCode == "book." + firstSubtype;
            Assert.Equal(first ? "1" : "2", chapter.Identity.DisplayNumber);
            await state.SaveProgressAsync(chapter.Identity, new MediaProgressState(
                first ? 10 : 20, MediaPositionKind.Page, false, DateTimeOffset.UtcNow));
            await state.SaveReaderDisplayModeAsync(details.Identity, first ? ReaderDisplayMode.Paged : ReaderDisplayMode.Continuous);
            await state.SetFavoriteCatalogItemAsync(card.Id, true);
            await state.SetUserListMembershipAsync(list.Id, card.Id, true);
        }
        var sourceSync = new SqliteUserSyncStore(source.Database, source.Repository);
        var exported = await sourceSync.ExportAsync("source", "Source");
        Assert.Equal(2, exported.Catalog.Items.Select(item => item.Id).Distinct().Count());
        Assert.Equal(2, exported.Progress.Count);
        Assert.Equal(2, exported.FavoriteItemStates.Count);
        Assert.Equal(2, Assert.Single(exported.Lists).Items.Count);
        await using var target = TestDatabaseScope.Create();
        var targetSync = new SqliteUserSyncStore(target.Database, target.Repository);
        await targetSync.RestoreAsync(exported);
        await sourceSync.RestoreAsync(await targetSync.ExportAsync("target", "Target"));
        Assert.Equal(2, (await target.Repository.ListAsync()).Count);
        var roundtrip = await sourceSync.ExportAsync("source", "Source");
        Assert.Equal(new[] { 10d, 20d }, roundtrip.Progress.Select(entry => entry.Position).Order());
        Assert.Equal(2, roundtrip.ReaderPreferences.Count);

        var firstCard = cards.Single(card => card.SubtypeCode == "book." + firstSubtype);
        var patch = await sourceSync.ExportProgressPatchAsync("source", "Source", [firstCard.Id]);
        Assert.Equal(firstSubtype, Assert.Single(patch.Catalog.Items).Subtype);
        Assert.Equal(10, Assert.Single(patch.Progress).Position);
        await state.SetFavoriteCatalogItemAsync(firstCard.Id, false);
        await state.SetUserListMembershipAsync(list.Id, firstCard.Id, false);
        await targetSync.RestoreAsync(await sourceSync.ExportAsync("source", "Source"));
        var updated = await targetSync.ExportAsync("target", "Target");
        Assert.Equal(secondSubtype, Assert.Single(updated.FavoriteItemStates, item => item.IsFavorite).Item.Subtype);
        Assert.Single(Assert.Single(updated.Lists).Items);

        await source.Repository.DeleteAsync(firstCard.Id);
        await targetSync.RestoreAsync(await sourceSync.ExportAsync("source", "Source"));
        Assert.Equal("book." + secondSubtype, Assert.Single(await target.Repository.ListAsync()).SubtypeCode);
        await source.Repository.ImportSelectedItemAsync(Book(firstSubtype, "1"));
        Assert.Equal(2, (await source.Repository.ListAsync()).Count);
    }

    [Fact]
    public void LegacyDevicesWithTheSameIdForNovelAndMangaDoNotFoldThemTogether()
    {
        var oldId = CatalogStableId.Create(MediaType.Book, "Same title");
        var novel = Book("novel", "1") with { Id = oldId };
        var manga = Book("manga", "2") with { Id = oldId };
        foreach (var reverse in new[] { false, true })
        {
            var merged = CatalogTransferDocumentMerger.Merge(
                new CatalogTransferDocument { Items = [reverse ? manga : novel] },
                new CatalogTransferDocument { Items = [reverse ? novel : manga] });
            Assert.Equal(2, merged.Items.Count);
            Assert.Equal(2, merged.Items.Select(item => item.Id).Distinct().Count());
            Assert.All(merged.Items, item => Assert.Single(item.Units));
        }
    }

    private static CatalogTransferItem Book(string subtype, string chapter) => new()
    {
        MediaType = "book",
        Subtype = subtype,
        Name = "Same title",
        Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = chapter, SortOrder = 0 }]
    };
}

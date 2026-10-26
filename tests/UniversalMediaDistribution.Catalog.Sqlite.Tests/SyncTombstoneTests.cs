using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Stage 2: deletion is a synced fact and the newest edit wins. A tombstone
/// beats older edits, loses to newer ones, suppresses the daily fetch from
/// re-creating the title, and travels to other devices.
/// </summary>
public sealed class SyncTombstoneTests
{
    private const string Title = "Serie que ya no quiero";
    private static readonly string DeviceB = new('b', 32);

    [Fact]
    public async Task DeletingFromTheCatalogCreatesATombstoneAndDoesNotResurrect()
    {
        await using var scope = TestDatabaseScope.Create();
        var providerDocument = CreateCatalog();
        await scope.Repository.MergeCatalogAsync(providerDocument);
        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        var service = new CatalogBrowseService(scope.Repository);

        Assert.True(await service.DeleteAsync(item.Id));
        Assert.Empty(await scope.Repository.ListAsync(MediaType.Book));
        Assert.Null(await scope.Repository.GetDetailsAsync(item.Id));
        Assert.False(await service.DeleteAsync(item.Id));

        var exported = await scope.Repository.ExportCatalogAsync();
        var tombstone = Assert.Single(
            exported.Items,
            static candidate => candidate.Deleted);
        Assert.Equal(Title, tombstone.Name);
        Assert.NotNull(tombstone.SyncVersion);

        await scope.Repository.MergeCatalogAsync(providerDocument);

        Assert.Empty(await scope.Repository.ListAsync(MediaType.Book));
    }

    [Fact]
    public async Task ADeletionMadeElsewhereRemovesTheTitleHere()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(CreateCatalog());
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        await store.RestoreAsync(CreateDocument(
            CreateTombstoneCatalog(daysAgo: 0)));

        Assert.Empty(await scope.Repository.ListAsync(MediaType.Book));
    }

    [Fact]
    public async Task TheDeletionTravelsOnwards()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(CreateCatalog());
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        await store.RestoreAsync(CreateDocument(
            CreateTombstoneCatalog(daysAgo: 0)));

        var exported = await store.ExportAsync(
            new string('e', 32),
            "Exporting device");

        var tombstone = Assert.Single(
            exported.Catalog.Items,
            static item => item.Deleted);
        Assert.Equal(Title, tombstone.Name);
        Assert.NotNull(tombstone.SyncVersion);
    }

    [Fact]
    public async Task TheDailyFetchDoesNotResurrectADeletedTitle()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(CreateCatalog());
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        await store.RestoreAsync(CreateDocument(
            CreateTombstoneCatalog(daysAgo: 0)));

        // What the provider fetch imports: the same title, rediscovered,
        // carrying no version because no person edited anything.
        await scope.Repository.MergeCatalogAsync(CreateCatalog());

        Assert.Empty(await scope.Repository.ListAsync(MediaType.Book));
    }

    [Fact]
    public async Task ANewerEditResurrectsTheTitle()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(CreateCatalog());
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        await store.RestoreAsync(CreateDocument(
            CreateTombstoneCatalog(daysAgo: 1)));

        var edited = CreateCatalog();
        edited.Items = [Assert.Single(edited.Items) with
        {
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                DeviceB,
                DateTimeOffset.UtcNow,
                "item-edit:resurrect")
        }];
        await scope.Repository.MergeCatalogAsync(edited);

        Assert.Single(await scope.Repository.ListAsync(MediaType.Book));

        // And the tombstone is gone, so the next export spreads the revival
        // rather than the deletion.
        var exported = await (new SqliteUserSyncStore(
                scope.Database,
                scope.Repository))
            .ExportAsync(new string('e', 32), "Exporting device");
        Assert.DoesNotContain(
            exported.Catalog.Items,
            static item => item.Deleted);
    }

    [Fact]
    public void TheNewestEditWinsTheNameAndTheUnitsStillUnion()
    {
        var identity = Guid.NewGuid();
        var older = CreateItem(
            "Old name",
            identity,
            episodes: ["1", "2"],
            version: UserSyncDocumentMerger.CreateLegacyVersion(
                DeviceB,
                DateTimeOffset.UtcNow.AddDays(-1),
                "edit:old"));
        var newer = CreateItem(
            "New name",
            identity,
            episodes: ["3"],
            version: UserSyncDocumentMerger.CreateLegacyVersion(
                DeviceB,
                DateTimeOffset.UtcNow,
                "edit:new"));

        var merged = CatalogTransferDocumentMerger.Merge(
            new CatalogTransferDocument { Items = [older] },
            new CatalogTransferDocument { Items = [newer] });

        var item = Assert.Single(merged.Items);
        Assert.Equal("New name", item.Name);
        Assert.Equal(
            ["1", "2", "3"],
            item.Units
                .Select(static unit => unit.DisplayNumber)
                .Order(StringComparer.Ordinal));
    }

    [Fact]
    public void ANewerTombstoneBeatsAnOlderEditAndViceVersa()
    {
        var identity = Guid.NewGuid();
        var edit = CreateItem(
            Title,
            identity,
            episodes: ["1"],
            version: UserSyncDocumentMerger.CreateLegacyVersion(
                DeviceB,
                DateTimeOffset.UtcNow.AddDays(-1),
                "edit"));
        var tombstone = CreateTombstone(identity, daysAgo: 0);

        var deletedWins = CatalogTransferDocumentMerger.Merge(
            new CatalogTransferDocument { Items = [edit] },
            new CatalogTransferDocument { Items = [tombstone] });
        Assert.True(Assert.Single(deletedWins.Items).Deleted);

        var newerEdit = edit with
        {
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                DeviceB,
                DateTimeOffset.UtcNow.AddDays(1),
                "edit:newer")
        };
        var editWins = CatalogTransferDocumentMerger.Merge(
            new CatalogTransferDocument { Items = [newerEdit] },
            new CatalogTransferDocument { Items = [tombstone] });
        Assert.False(Assert.Single(editWins.Items).Deleted);
    }

    private static CatalogTransferDocument CreateCatalog() =>
        new()
        {
            Items =
            [
                CreateItem(Title, id: null, episodes: ["1", "2"], version: null)
            ]
        };

    private static CatalogTransferDocument CreateTombstoneCatalog(
        int daysAgo) =>
        new()
        {
            Items =
            [
                CreateTombstone(
                    CatalogStableId.Create(MediaType.Book, Title),
                    daysAgo)
            ]
        };

    private static CatalogTransferItem CreateTombstone(
        Guid identity,
        int daysAgo) =>
        new()
        {
            Id = identity,
            Deleted = true,
            MediaType = "book",
            Subtype = "manga",
            Name = Title,
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                DeviceB,
                DateTimeOffset.UtcNow.AddDays(-daysAgo),
                "delete:" + identity.ToString("N"))
        };

    private static CatalogTransferItem CreateItem(
        string name,
        Guid? id,
        string[] episodes,
        UserSyncVersion? version) =>
        new()
        {
            Id = id,
            SyncVersion = version,
            MediaType = "book",
            Subtype = "manga",
            Name = name,
            Units = episodes
                .Select(static (number, index) => new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = number,
                    SortOrder = index
                })
                .ToArray()
        };

    private static UserSyncDocument CreateDocument(
        CatalogTransferDocument catalog) =>
        new()
        {
            DeviceId = DeviceB,
            DeviceName = "Device B",
            ExportedAt = DateTimeOffset.UtcNow,
            Catalog = catalog
        };
}

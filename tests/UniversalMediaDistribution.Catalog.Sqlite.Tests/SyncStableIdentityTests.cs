using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Stage 1 of the sync rework: items carry an identity that survives what
/// display names cannot. These hold the three properties everything else
/// leans on — minting is deterministic, references follow the identity
/// through renames, and a folded identity leaves a redirect behind.
/// </summary>
public sealed class SyncStableIdentityTests
{
    private const string Title = "La voluntad de supervivencia del villano";

    [Fact]
    public void TheSameTitleMintsTheSameIdentityEverywhere()
    {
        var first = CatalogStableId.Create(MediaType.Book, Title);
        var second = CatalogStableId.Create(
            MediaType.Book,
            "  la  VOLUNTAD de supervivencia del villano ");

        // Normalization folds spacing and case, so two devices spelling the
        // title slightly differently still converge on one identity.
        Assert.Equal(first, second);
        Assert.NotEqual(
            first,
            CatalogStableId.Create(MediaType.Movie, Title));
    }

    [Fact]
    public async Task TwoDevicesCreatingTheSameTitleConvergeWithoutTalking()
    {
        await using var one = TestDatabaseScope.Create();
        await using var two = TestDatabaseScope.Create();
        await one.Repository.MergeCatalogAsync(CreateCatalog(Title));
        await two.Repository.MergeCatalogAsync(CreateCatalog(Title));

        var exportedOne = await one.Repository.ExportCatalogAsync();
        var exportedTwo = await two.Repository.ExportCatalogAsync();

        var idOne = Assert.Single(exportedOne.Items).Id;
        Assert.NotNull(idOne);
        Assert.Equal(idOne, Assert.Single(exportedTwo.Items).Id);
    }

    [Fact]
    public async Task AProgressRecordFollowsTheIdentityThroughARename()
    {
        // The failure class this stage exists to end: another device renamed
        // the title, and every record naming the old spelling used to dangle.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(CreateCatalog(Title));
        var identity = Assert.Single(
            (await scope.Repository.ExportCatalogAsync()).Items).Id!.Value;
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        var renamed = CreateDocument(
            CreateCatalog("El villano renombrado", identity),
            progress: new UserSyncProgressEntry
            {
                Content = new UserSyncContentIdentity
                {
                    Item = new UserSyncCatalogItemIdentity
                    {
                        MediaType = "book",
                        ItemId = identity,
                        Name = "El villano renombrado"
                    },
                    UnitKind = "chapter",
                    DisplayNumber = "1"
                },
                Position = 3,
                PositionKind = "page",
                UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1),
                SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                    new string('d', 32),
                    DateTimeOffset.UnixEpoch.AddDays(1),
                    "progress")
            });

        var result = await store.RestoreAsync(renamed);

        Assert.Equal(1, result.ProgressCount);
        var items = await scope.Repository.ListAsync(MediaType.Book);
        Assert.Single(items);
    }

    [Fact]
    public async Task TwoIdentitiesForOneTitleFoldAndLeaveARedirect()
    {
        // This device minted its own identity; the snapshot arrives carrying
        // a different one for the same title, with a position recorded under
        // it. The pair must fold to one identity and the position must land.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(CreateCatalog(Title));
        var localId = Assert.Single(
            (await scope.Repository.ExportCatalogAsync()).Items).Id!.Value;
        var foreignId = Guid.NewGuid();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        var document = CreateDocument(
            CreateCatalog(Title, foreignId),
            progress: new UserSyncProgressEntry
            {
                Content = new UserSyncContentIdentity
                {
                    Item = new UserSyncCatalogItemIdentity
                    {
                        MediaType = "book",
                        ItemId = foreignId,
                        Name = Title
                    },
                    UnitKind = "chapter",
                    DisplayNumber = "1"
                },
                Position = 7,
                PositionKind = "page",
                UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1),
                SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                    new string('d', 32),
                    DateTimeOffset.UnixEpoch.AddDays(1),
                    "progress")
            });

        var result = await store.RestoreAsync(document);

        Assert.Equal(1, result.ProgressCount);
        Assert.Single(await scope.Repository.ListAsync(MediaType.Book));

        // The fold is remembered: the losing identity forwards to the winner
        // in the next export, so a device still holding records under it can
        // repoint them.
        var exported = await store.ExportAsync(
            new string('e', 32),
            "Exporting device");
        var winner = localId.CompareTo(foreignId) <= 0 ? localId : foreignId;
        var loser = winner == localId ? foreignId : localId;
        var redirect = Assert.Single(exported.CatalogRedirects);
        Assert.Equal(loser, redirect.FromId);
        Assert.Equal(winner, redirect.ToId);
    }

    [Fact]
    public async Task AVersionThreeDocumentStillRestores()
    {
        // The fleet upgrades together, but the server holds a version-three
        // snapshot the moment the first upgraded device syncs. It has no
        // identities anywhere and must keep working by name, as before.
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var legacyJson = JsonSerializer.Serialize(
            CreateDocument(CreateCatalog(Title)) with { Version = 3 });
        var legacy = JsonSerializer.Deserialize<UserSyncDocument>(legacyJson)!;

        var result = await store.RestoreAsync(legacy);

        Assert.Equal(1, result.CatalogCreatedCount);
        var exported = await store.ExportAsync(
            new string('e', 32),
            "Exporting device");

        // And what leaves this device afterwards carries identities.
        Assert.Equal(4, exported.Version);
        Assert.NotNull(Assert.Single(exported.Catalog.Items).Id);
    }

    private static CatalogTransferDocument CreateCatalog(
        string name,
        Guid? id = null) =>
        new()
        {
            Items =
            [
                new CatalogTransferItem
                {
                    Id = id,
                    MediaType = "book",
                    Subtype = "manga",
                    Name = name,
                    Units =
                    [
                        new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = "1",
                            SortOrder = 0
                        }
                    ]
                }
            ]
        };

    private static UserSyncDocument CreateDocument(
        CatalogTransferDocument catalog,
        UserSyncProgressEntry? progress = null) =>
        new()
        {
            DeviceId = new string('d', 32),
            DeviceName = "Test device",
            ExportedAt = DateTimeOffset.UtcNow,
            Catalog = catalog,
            Progress = progress is null ? [] : [progress]
        };
}

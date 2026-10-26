using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// The whole restore, against a real store: normalize, merge with what is
/// already here, then import. Each of those validates, so a rule relaxed at
/// one of them only moves the refusal to the next.
/// </summary>
public sealed class SyncRestoreDuplicateNameTests
{
    private const string DuplicatedName =
        "Guia de supervivencia del extra de la academia";

    private const string SameNameOtherSpelling =
        "Guia  de  supervivencia  del  extra  de  la  academia";

    [Fact]
    public async Task ASnapshotWithTwoSpellingsOfOneTitleRestores()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        await store.RestoreAsync(
            CreateDocument(DuplicatedName, SameNameOtherSpelling));

        var books = await scope.Repository.ListAsync(MediaType.Book);
        Assert.Single(books);
    }

    [Fact]
    public async Task TheSecondSpellingDoesNotArriveAsASeparateTitle()
    {
        // Restoring twice is the ordinary case — every sync does it — so the
        // collapse has to be stable rather than only correct the first time.
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var document = CreateDocument(DuplicatedName, SameNameOtherSpelling);

        await store.RestoreAsync(document);
        await store.RestoreAsync(document);

        Assert.Single(await scope.Repository.ListAsync(MediaType.Book));
    }

    [Fact]
    public async Task ASnapshotColldingWithATitleAlreadyHereRestores()
    {
        // The reported case: this device already has the title under one
        // spelling and the server sends another.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            CreateDocument(DuplicatedName).Catalog);
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        await store.RestoreAsync(CreateDocument(SameNameOtherSpelling));

        Assert.Single(await scope.Repository.ListAsync(MediaType.Book));
    }

    private static UserSyncDocument CreateDocument(params string[] names) =>
        new()
        {
            DeviceId = new string('d', 32),
            DeviceName = "Test device",
            ExportedAt = DateTimeOffset.UtcNow,
            Catalog = new CatalogTransferDocument
            {
                Items = names
                    .Select(static name => new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = name
                    })
                    .ToArray()
            }
        };
}

using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Importing converges two titles that share a name onto one item, which
/// renames whichever of them it collapsed. Everything the account recorded
/// against the collapsed spelling has to follow the title: a snapshot is
/// refused whole if a position names something the catalog no longer lists,
/// so one stale spelling would otherwise strand the entire account.
/// </summary>
public sealed class SyncConvergedRenameTests
{
    private const string PrimaryName = "+99 palo de madera";
    private const string CollapsedName = "Palo de madera";

    [Fact]
    public async Task ASnapshotWhoseTitlesConvergeRestores()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        await store.RestoreAsync(CreateDocument());

        var books = await scope.Repository.ListAsync(MediaType.Book);
        Assert.Equal(PrimaryName, Assert.Single(books).Name);
    }

    [Fact]
    public async Task ThePositionUnderTheCollapsedTitleIsKept()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);

        var result = await store.RestoreAsync(CreateDocument());

        Assert.Equal(1, result.ProgressCount);
    }

    [Fact]
    public async Task TheKeptPositionNamesTheTitleThatSurvived()
    {
        // Counting it is not enough: a position left pointing at the name the
        // merge dropped would be exported again and refused by the next
        // device to read it.
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        await store.RestoreAsync(CreateDocument());

        var exported = await store.ExportAsync(
            new string('e', 32),
            "Exporting device");

        Assert.Equal(
            PrimaryName,
            Assert.Single(exported.Progress).Content.Item.Name);
    }

    [Fact]
    public async Task RestoringTheSameSnapshotTwiceStillRestores()
    {
        // Every sync restores, so collapsing has to be stable rather than
        // only correct against an empty catalog.
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var document = CreateDocument();

        await store.RestoreAsync(document);
        await store.RestoreAsync(document);

        Assert.Single(await scope.Repository.ListAsync(MediaType.Book));
    }

    private static UserSyncDocument CreateDocument() =>
        new()
        {
            DeviceId = new string('d', 32),
            DeviceName = "Test device",
            ExportedAt = DateTimeOffset.UtcNow,
            Catalog = new CatalogTransferDocument
            {
                Items =
                [
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = PrimaryName,
                        // What makes the two converge: the second title is
                        // already recorded here as another name for the first.
                        AlternativeNames =
                        [
                            new CatalogTransferAlias
                            {
                                Name = CollapsedName,
                                AliasType = "provider"
                            }
                        ]
                    },
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = CollapsedName
                    }
                ]
            },
            Progress =
            [
                new UserSyncProgressEntry
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = new UserSyncCatalogItemIdentity
                        {
                            MediaType = "book",
                            Name = CollapsedName
                        }
                    },
                    Position = 12,
                    PositionKind = "page",
                    UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(1),
                    SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                        new string('d', 32),
                        DateTimeOffset.UnixEpoch.AddDays(1),
                        "progress")
                }
            ]
        };
}

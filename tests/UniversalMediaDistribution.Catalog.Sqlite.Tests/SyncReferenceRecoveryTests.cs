using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class SyncReferenceRecoveryTests
{
    private static readonly DateTimeOffset Timestamp = DateTimeOffset.UnixEpoch.AddDays(10);

    [Fact]
    public async Task DuplicateReferencesKeepTheNewestStateAndRestoreIdempotently()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var document = Document("Title", " title ");
        var newest = Progress("Title", 30);
        document = document with
        {
            Progress = [newest, Progress(" title ", 10)],
            ReaderPreferences = [Preference("Title", 30), Preference(" title ", 10)],
            FavoriteItemStates = [Favorite("Title", 30), Favorite(" title ", 10)],
            Lists = [List("Title", " title ")]
        };

        await store.RestoreAsync(document);
        await store.RestoreAsync(document);

        var exported = await store.ExportAsync("local", "Local");
        Assert.Single(exported.Catalog.Items);
        Assert.Equal(30, Assert.Single(exported.Progress).Position);
        Assert.Equal(newest.SyncVersion, Assert.Single(exported.Progress).SyncVersion);
        Assert.Equal("continuous", Assert.Single(exported.ReaderPreferences).DisplayMode);
        Assert.Single(exported.FavoriteItemStates);
        Assert.Single(Assert.Single(exported.Lists).Items);
        UserSyncDocumentValidator.Validate(exported);
    }

    [Fact]
    public async Task MissingTargetsDoNotBlockValidStateOrCreatePhantomTitles()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var document = Document("Title") with
        {
            Progress =
            [
                Progress("Title", 10), Progress("Missing", 20),
                Progress("Title", 30) with
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = Identity("Title"), UnitKind = "chapter", DisplayNumber = "99"
                    }
                }
            ],
            ReaderPreferences = [Preference("Title", 10), Preference("Missing", 20)],
            FavoriteItemStates = [Favorite("Title", 10), Favorite("Missing", 20)],
            Lists = [List("Title", "Missing")]
        };

        await store.RestoreAsync(document);
        await store.RestoreAsync(document);

        var exported = await store.ExportAsync("local", "Local");
        Assert.Equal("Title", Assert.Single(exported.Catalog.Items).Name);
        Assert.Equal(10, Assert.Single(exported.Progress).Position);
        Assert.Single(exported.ReaderPreferences);
        Assert.Single(exported.FavoriteItemStates);
        Assert.Equal("Title", Assert.Single(Assert.Single(exported.Lists).Items).Name);
        UserSyncDocumentValidator.Validate(exported);
    }

    [Fact]
    public async Task ReferencesMissingFromIncomingCatalogResolveAgainstLocalCatalog()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document("Local title").Catalog);
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        await store.RestoreAsync(Document() with
        {
            Progress = [Progress("Local title", 20)],
            ReaderPreferences = [Preference("Local title", 20)],
            FavoriteItemStates = [Favorite("Local title", 20)],
            Lists = [List("Local title")]
        });

        var exported = await store.ExportAsync("local", "Local");
        Assert.Equal(20, Assert.Single(exported.Progress).Position);
        Assert.Single(exported.ReaderPreferences);
        Assert.Single(exported.FavoriteItemStates);
        Assert.Single(Assert.Single(exported.Lists).Items);
    }

    [Fact]
    public async Task DeletedTitleWithStaleStateDoesNotAbortRestoreOrResurrect()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var document = Document("Deleted");
        document.Catalog.Items = [document.Catalog.Items[0] with
        {
            Id = Guid.NewGuid(), Deleted = true,
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                "remote", DateTimeOffset.UtcNow, "delete")
        }];
        await store.RestoreAsync(document with
        {
            Progress = [Progress("Deleted", 10)],
            ReaderPreferences = [Preference("Deleted", 10)],
            FavoriteItemStates = [Favorite("Deleted", 10)],
            Lists = [List("Deleted")]
        });

        Assert.Empty(await scope.Repository.ListAsync(MediaType.Book));
        var exported = await store.ExportAsync("local", "Local");
        Assert.True(Assert.Single(exported.Catalog.Items).Deleted);
        Assert.Empty(exported.Progress);
        Assert.Empty(exported.ReaderPreferences);
        Assert.Empty(exported.FavoriteItemStates);
        Assert.Empty(Assert.Single(exported.Lists).Items);
    }

    [Fact]
    public void RecoveryStillRejectsInvalidPayloads()
    {
        Assert.Throws<InvalidDataException>(() => UserSyncDocumentMerger.Normalize(
            Document() with { Progress = [Progress("Missing", 10) with { Position = -1 }] }));
        Assert.Throws<InvalidDataException>(() => UserSyncDocumentMerger.Normalize(
            Document() with { Progress = [Progress("Missing", 10) with { SyncVersion = null }] }));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RenameIntoAnExistingTitleFoldsBothIdentities(bool reverse)
    {
        var old = Document("Old title").Catalog.Items[0] with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000001"),
            SyncVersion = Version(10)
        };
        var other = old with
        {
            Id = Guid.Parse("00000000-0000-0000-0000-000000000002"),
            Name = "New title"
        };
        var local = new CatalogTransferDocument { Items = [old, other] };
        var remote = new CatalogTransferDocument
        {
            Items = [old with { Name = "New title", SyncVersion = Version(30) }]
        };

        var merged = CatalogTransferDocumentMerger.MergeWithRedirects(
            reverse ? remote : local, reverse ? local : remote);

        var item = Assert.Single(merged.Document.Items);
        Assert.Equal("New title", item.Name);
        Assert.Equal(old.Id, item.Id);
        Assert.Equal(other.Id, Assert.Single(merged.Redirects).FromId);
        Assert.Equal(old.Id, Assert.Single(merged.Redirects).ToId);

        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var localDocument = Document() with
        {
            Catalog = local,
            Progress = [Progress("Old title", 30) with { Position = 1 }, Progress("New title", 10)]
        };
        var remoteDocument = Document() with { Catalog = remote };
        await store.RestoreAsync(reverse ? remoteDocument : localDocument);
        await store.RestoreAsync(reverse ? localDocument : remoteDocument);
        Assert.Single(await scope.Repository.ListAsync(MediaType.Book));
        var exported = await store.ExportAsync("local", "Local");
        Assert.Equal(1, Assert.Single(exported.Progress).Position);
        Assert.Equal(Version(30), exported.Progress[0].SyncVersion);
    }

    private static UserSyncDocument Document(params string[] names) => new()
    {
        DeviceId = "remote",
        DeviceName = "Remote",
        ExportedAt = Timestamp,
        Catalog = new CatalogTransferDocument
        {
            Items = names.Select(name => new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = name
            }).ToArray()
        }
    };

    private static UserSyncCatalogItemIdentity Identity(string name) => new()
    {
        MediaType = "book",
        Name = name
    };

    private static UserSyncVersion Version(int clock) =>
        UserSyncDocumentMerger.CreateLegacyVersion("remote", Timestamp.AddSeconds(clock), $"state:{clock}");

    private static UserSyncProgressEntry Progress(string name, int clock) => new()
    {
        Content = new UserSyncContentIdentity { Item = Identity(name) },
        Position = clock,
        PositionKind = "page",
        UpdatedAt = Timestamp.AddSeconds(clock),
        SyncVersion = Version(clock)
    };

    private static UserSyncReaderPreference Preference(string name, int clock) => new()
    {
        Item = Identity(name),
        DisplayMode = clock > 10 ? "continuous" : "paged",
        UpdatedAt = Timestamp.AddSeconds(clock),
        SyncVersion = Version(clock)
    };

    private static UserSyncFavoriteItemState Favorite(string name, int clock) => new()
    {
        Item = Identity(name),
        IsFavorite = true,
        SyncVersion = Version(clock)
    };

    private static UserSyncMediaList List(params string[] names) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Reading",
        CreatedAt = Timestamp,
        UpdatedAt = Timestamp,
        SyncVersion = Version(1),
        ItemStates = names.Select((name, index) => new UserSyncListItemState
        {
            Item = Identity(name),
            IsMember = true,
            SyncVersion = Version(index + 1)
        }).ToArray()
    };
}

using System.Diagnostics;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class SyncStateWriteTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StateUpdatesAreIsolatedByProfileAndBookType(bool catalogChanged)
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = new[] { "manga", "novel" }.Select(subtype => new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = subtype,
                Name = "Same title",
                Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "1" }]
            }).ToArray()
        });
        var firstProfile = scope.Database.ActiveProfileId;
        var secondProfile = await new SqliteUserProfileStore(scope.Database).CreateAsync("Second");
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var snapshot = await store.ExportAsync("state-device", "State device");
        var timestamp = DateTimeOffset.UnixEpoch.AddDays(1);
        var version = UserSyncDocumentMerger.CreateLegacyVersion("state-device", timestamp, "profile-state");
        var identities = snapshot.Catalog.Items.Select(item => new UserSyncCatalogItemIdentity
        {
            ItemId = item.Id,
            MediaType = item.MediaType,
            Subtype = item.Subtype,
            Name = item.Name
        }).ToArray();
        var seed = snapshot with
        {
            Progress = identities.Select(identity => new UserSyncProgressEntry
            {
                Content = new UserSyncContentIdentity { Item = identity, UnitKind = "chapter", DisplayNumber = "1" },
                Position = 1,
                PositionKind = "page",
                UpdatedAt = timestamp,
                SyncVersion = version
            }).ToArray(),
            ReaderPreferences = identities.Select(identity => new UserSyncReaderPreference
            {
                Item = identity,
                DisplayMode = "paged",
                UpdatedAt = timestamp,
                SyncVersion = version
            }).ToArray(),
            FavoriteItemStates = identities.Select(identity => new UserSyncFavoriteItemState
            {
                Item = identity,
                IsFavorite = true,
                SyncVersion = version
            }).ToArray(),
            FavoriteStates = [new UserSyncFavoriteState { ContributorKey = "organization:provider", IsFavorite = true, SyncVersion = version }],
            Lists = [new UserSyncMediaList
            {
                Id = Guid.CreateVersion7(), Name = "Keep", CreatedAt = timestamp, UpdatedAt = timestamp, SyncVersion = version,
                ItemStates = [new UserSyncListItemState { Item = identities[0], IsMember = true, SyncVersion = version }]
            }]
        };
        await store.RestoreAsync(seed);
        var first = await store.ExportAsync("state-device", "State device");
        scope.Database.ActiveProfileId = secondProfile.Id;
        var empty = await store.ExportAsync("state-device", "State device");
        Assert.Empty(empty.Progress);
        Assert.Empty(empty.ReaderPreferences);
        Assert.Empty(empty.FavoriteStates);
        Assert.Empty(empty.FavoriteItemStates);
        Assert.Empty(empty.Lists);
        var ids = (await scope.Repository.ListAsync()).Select(item => item.Id).ToArray();
        Assert.Empty((await store.ExportProgressPatchAsync("state-device", "State device", ids)).Progress);
        await store.RestoreAsync(seed with { Lists = [seed.Lists[0] with { Id = Guid.CreateVersion7() }] });
        var second = await store.ExportAsync("state-device", "State device");
        var next = version with { Clock = version.Clock + 1, ModifiedAt = timestamp.AddMinutes(1), OperationId = Guid.CreateVersion7() };
        var manga = identities.Single(identity => identity.Subtype == "manga");
        var patch = second with
        {
            Progress = [second.Progress.Single(entry => entry.Content.Item.Subtype == "manga") with { Position = 9, SyncVersion = next }],
            ReaderPreferences = [new UserSyncReaderPreference { Item = manga, DisplayMode = "continuous", UpdatedAt = next.ModifiedAt, SyncVersion = next }],
            FavoriteItemStates = [new UserSyncFavoriteItemState { Item = manga, IsFavorite = false, SyncVersion = next }],
            FavoriteStates = [new UserSyncFavoriteState { ContributorKey = "organization:provider", IsFavorite = false, SyncVersion = next }],
            FavoriteContributorKeys = [],
            Lists = []
        };
        if (catalogChanged)
        {
            patch = patch with
            {
                Catalog = patch.Catalog with
                {
                    Items = patch.Catalog.Items.Select(item => item.Subtype == "manga" ? item with
                    {
                        Units = [.. item.Units, new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "2", SortOrder = 1 }]
                    } : item).ToArray()
                }
            };
        }
        await store.RestoreAsync(patch);
        var after = await store.ExportAsync("state-device", "State device");
        Assert.Equal(9, after.Progress.Single(entry => entry.Content.Item.Subtype == "manga").Position);
        Assert.Equal(1, after.Progress.Single(entry => entry.Content.Item.Subtype == "novel").Position);
        Assert.Equal("continuous", after.ReaderPreferences.Single(entry => entry.Item.Subtype == "manga").DisplayMode);
        Assert.Equal("paged", after.ReaderPreferences.Single(entry => entry.Item.Subtype == "novel").DisplayMode);
        Assert.False(after.FavoriteItemStates.Single(entry => entry.Item.Subtype == "manga").IsFavorite);
        Assert.True(after.FavoriteItemStates.Single(entry => entry.Item.Subtype == "novel").IsFavorite);
        Assert.False(Assert.Single(after.FavoriteStates).IsFavorite);
        Assert.Single(after.Lists);
        scope.Database.ActiveProfileId = firstProfile;
        var unchanged = await store.ExportAsync("state-device", "State device");
        Assert.Empty(UserSyncDocumentDelta.ComputeChangedKeys(first with { Catalog = unchanged.Catalog }, unchanged));
        Assert.Empty(UserSyncDocumentDelta.ComputeChangedKeys(unchanged, first with { Catalog = unchanged.Catalog }));
    }

    [Theory]
    [InlineData(1, false)]
    [InlineData(1000, false)]
    [InlineData(1, true)]
    public async Task OneProgressChangeDoesNotRebuildUnrelatedState(int chapterCount, bool rejectWrite)
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = [new CatalogTransferItem
            {
                MediaType = "book", Subtype = "manga", Name = "History",
                Units = Enumerable.Range(1, chapterCount).Select(number => new CatalogTransferUnit
                {
                    Kind = "chapter", DisplayNumber = number.ToString(), SortOrder = number
                }).ToArray()
            }]
        });
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var snapshot = await store.ExportAsync("state-device", "State device");
        var item = Assert.Single(snapshot.Catalog.Items);
        var identity = new UserSyncCatalogItemIdentity { ItemId = item.Id, MediaType = "book", Subtype = "manga", Name = item.Name };
        var timestamp = DateTimeOffset.UnixEpoch.AddDays(1);
        var version = UserSyncDocumentMerger.CreateLegacyVersion("state-device", timestamp, "initial-state");
        await store.RestoreAsync(snapshot with
        {
            Progress = item.Units.Select(unit => new UserSyncProgressEntry
            {
                Content = new UserSyncContentIdentity { Item = identity, UnitKind = "chapter", DisplayNumber = unit.DisplayNumber },
                Position = 1,
                PositionKind = "page",
                UpdatedAt = timestamp,
                SyncVersion = version
            }).ToArray(),
            ReaderPreferences = [new UserSyncReaderPreference { Item = identity, DisplayMode = "paged", UpdatedAt = timestamp, SyncVersion = version }],
            FavoriteStates = [new UserSyncFavoriteState { ContributorKey = "organization:provider", IsFavorite = true, SyncVersion = version }],
            FavoriteItemStates = [new UserSyncFavoriteItemState { Item = identity, IsFavorite = true, SyncVersion = version }],
            Lists = [new UserSyncMediaList
            {
                Id = Guid.CreateVersion7(), Name = "Keep", CreatedAt = timestamp, UpdatedAt = timestamp, SyncVersion = version,
                ItemStates = [new UserSyncListItemState { Item = identity, IsMember = true, SyncVersion = version }]
            }]
        });
        snapshot = await store.ExportAsync("state-device", "State device");
        await using var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = "CREATE TABLE state_write_probe (table_name TEXT, operation TEXT);";
        await command.ExecuteNonQueryAsync();
        foreach (var table in new[] { "playback_progress", "reader_preferences", "favorite_contributors", "library_entries", "user_media_lists", "user_media_list_items", "user_sync_records" })
        {
            foreach (var operation in new[] { "INSERT", "UPDATE", "DELETE" })
            {
                command.CommandText = $"""
                    CREATE TRIGGER probe_{table}_{operation} AFTER {operation} ON {table}
                    BEGIN INSERT INTO state_write_probe VALUES ('{table}', '{operation}'); END;
                    """;
                await command.ExecuteNonQueryAsync();
            }
        }
        var changed = snapshot.Progress[0] with
        {
            Position = 9,
            UpdatedAt = timestamp.AddMinutes(1),
            SyncVersion = version with { Clock = version.Clock + 1, ModifiedAt = timestamp.AddMinutes(1), OperationId = Guid.CreateVersion7() }
        };
        var patch = snapshot with
        {
            Progress = [changed],
            ReaderPreferences = [],
            FavoriteStates = [],
            FavoriteContributorKeys = [],
            FavoriteItemStates = [],
            Lists = []
        };
        if (rejectWrite)
        {
            command.CommandText = """
                CREATE TRIGGER reject_sync_version BEFORE UPDATE ON user_sync_records
                BEGIN SELECT RAISE(ABORT, 'Simulated write failure'); END;
                """;
            await command.ExecuteNonQueryAsync();
            await Assert.ThrowsAsync<SqliteException>(() => store.RestoreAsync(patch).AsTask());
            var unchanged = await store.ExportAsync("state-device", "State device");
            Assert.Empty(UserSyncDocumentDelta.ComputeChangedKeys(snapshot, unchanged));
            Assert.Empty(UserSyncDocumentDelta.ComputeChangedKeys(unchanged, snapshot));
            command.CommandText = "SELECT COUNT(*) FROM state_write_probe;";
            Assert.Equal(0L, (long)(await command.ExecuteScalarAsync())!);
            return;
        }
        var timer = Stopwatch.StartNew();
        var result = await store.RestoreAsync(patch);
        timer.Stop();
        command.CommandText = "SELECT COUNT(*) FROM state_write_probe;";
        var writes = (long)(await command.ExecuteScalarAsync())!;
        output.WriteLine($"history={chapterCount}; restore={timer.Elapsed.TotalMilliseconds:F1}ms; row-writes={writes}");
        Assert.Equal(chapterCount, result.ProgressCount);
        var after = await store.ExportAsync("state-device", "State device");
        Assert.Equal(changed, Assert.Single(after.Progress, entry => entry.Content == changed.Content));
        Assert.Equal("progress:" + UserSyncDocumentValidator.ValidateContentIdentity(changed.Content),
            Assert.Single(UserSyncDocumentDelta.ComputeChangedKeys(snapshot, after)));
        Assert.Equal(3, writes); // One position replaced, and its version updated.
    }
}

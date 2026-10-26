using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class SyncRenameCollisionStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FoldingRowsPreservesOtherProfilesAndOverlappingUnits(bool seasonal)
    {
        await using var scope = TestDatabaseScope.Create();
        var firstId = Guid.Parse("00000000-0000-0000-0000-000000000001");
        var secondId = Guid.Parse("00000000-0000-0000-0000-000000000002");
        var template = TestCatalogDocuments.RichCatalog().Items[seasonal ? 1 : 0];
        var first = template with
        {
            Id = firstId,
            Name = "Old title",
            OriginalName = null,
            AlternativeNames = [],
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion("remote", DateTimeOffset.UtcNow, "old")
        };
        var unit = seasonal ? first.Seasons[0].Units[0] : first.Units[0];
        var units = new[] { unit, unit with { DisplayNumber = "2", SortOrder = 1 } };
        var second = first with
        {
            Id = secondId,
            Name = "New title",
            Units = seasonal ? [] : units,
            Seasons = seasonal ? [first.Seasons[0] with { Units = units }] : []
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument { Items = [first, second] });

        await using var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        var profileId = Guid.NewGuid().ToString();
        await using (var seed = connection.CreateCommand())
        {
            seed.CommandText = """
                INSERT INTO profiles (id, name, name_key, sort_order, created_at)
                VALUES ($profile, 'Other', 'OTHER', 1, datetime('now'));
                INSERT INTO library_entries (profile_id, catalog_item_id, is_favorite, added_at)
                SELECT $profile, id, CASE WHEN name = 'New title' THEN 1 ELSE 0 END, datetime('now')
                FROM catalog_items;
                INSERT INTO reader_preferences (profile_id, catalog_item_id, display_mode, updated_at)
                SELECT $profile, id, 'continuous', datetime('now') FROM catalog_items WHERE name = 'New title';
                INSERT INTO user_media_lists (id, profile_id, name, name_key, created_at, updated_at)
                VALUES ($profile, $profile, 'Later', 'LATER', datetime('now'), datetime('now'));
                INSERT INTO user_media_list_items (list_id, catalog_item_id, added_at)
                SELECT $profile, id, datetime('now') FROM catalog_items;
                INSERT INTO playback_progress
                    (id, profile_id, content_unit_id, position, position_kind, is_completed, updated_at)
                SELECT cu.id, $profile, cu.id, 42, 'page', 0, datetime('now')
                FROM content_units cu JOIN catalog_items ci ON ci.id = cu.catalog_item_id
                WHERE ci.name = 'New title';
                """;
            seed.Parameters.AddWithValue("$profile", profileId);
            await seed.ExecuteNonQueryAsync();
        }

        var merged = CatalogTransferDocumentMerger.Merge(
            new CatalogTransferDocument { Items = [first, second] },
            new CatalogTransferDocument
            {
                Items = [first with
            {
                Name = "New title",
                SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                    "remote", DateTimeOffset.UtcNow.AddMinutes(1), "rename")
            }]
            });
        await scope.Repository.MergeCatalogAsync(merged);

        Assert.Single(await scope.Repository.ListAsync());
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM content_units"));
        Assert.Equal(2L, await CountAsync("SELECT COUNT(*) FROM playback_progress WHERE profile_id = $profile AND position = 42"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM library_entries WHERE profile_id = $profile AND is_favorite = 1"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM reader_preferences WHERE profile_id = $profile AND display_mode = 'continuous'"));
        Assert.Equal(1L, await CountAsync("SELECT COUNT(*) FROM user_media_list_items WHERE list_id = $profile"));
        Assert.Equal(seasonal ? 1L : 0L, await CountAsync("SELECT COUNT(*) FROM seasons"));
        await using var integrity = connection.CreateCommand();
        integrity.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await integrity.ExecuteScalarAsync());

        async Task<long> CountAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.Parameters.AddWithValue("$profile", profileId);
            return (long)(await command.ExecuteScalarAsync())!;
        }
    }
}

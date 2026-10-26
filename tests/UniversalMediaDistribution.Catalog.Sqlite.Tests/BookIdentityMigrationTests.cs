using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class BookIdentityMigrationTests
{
    [Fact]
    public async Task VersionFourteenDatabaseKeepsRowsAndRecordVersionsWhileAddingTheOtherFormat()
    {
        await using var scope = TestDatabaseScope.Create();
        var novel = new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "novel",
            Name = "Existing novel",
            Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "1", SortOrder = 0 }]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument { Items = [novel] });
        var original = Assert.Single(await scope.Repository.ListAsync());
        var details = (await scope.Repository.GetDetailsAsync(original.Id))!;
        var state = new SqliteUserMediaStateStore(scope.Database);
        await state.SaveProgressAsync(details.Units[0].Identity, new MediaProgressState(42, MediaPositionKind.Page, false, DateTimeOffset.UtcNow));
        await state.SetFavoriteCatalogItemAsync(original.Id, true);
        var list = await state.CreateUserListAsync("Reading");
        await state.SetUserListMembershipAsync(list.Id, original.Id, true);
        var before = await new SqliteUserSyncStore(scope.Database, scope.Repository).ExportAsync("device", "Device");

        // Rebuild the historical constraint and namespaced keys as schema 14
        // actually stored them, with dependent rows still pointing at their IDs.
        await using (var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False"))
        {
            await connection.OpenAsync();
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE name = 'catalog_items';";
            var schema = (string)(await command.ExecuteScalarAsync())!;
            var indexes = new List<string>();
            command.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'catalog_items' AND sql IS NOT NULL;";
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) indexes.Add(reader.GetString(0));
            command.CommandText = "PRAGMA foreign_keys = OFF;";
            await command.ExecuteNonQueryAsync();
            command.CommandText = schema.Replace("CREATE TABLE \"catalog_items\"", "CREATE TABLE old_catalog_items", StringComparison.Ordinal)
                .Replace("UNIQUE (media_type, subtype_code, name_key)", "UNIQUE (media_type, name_key)", StringComparison.Ordinal) + ";\n" + """
                INSERT INTO old_catalog_items SELECT * FROM catalog_items;
                DROP TABLE catalog_items;
                ALTER TABLE old_catalog_items RENAME TO catalog_items;
                UPDATE catalog_items SET sync_id = $legacyId;
                UPDATE user_sync_records SET record_key = replace(record_key, 'book.novel:', 'book:');
                UPDATE schema_info SET value = '14' WHERE key = 'schema_version';
                PRAGMA user_version = 14;
                """;
            command.Parameters.AddWithValue("$legacyId", CatalogStableId.Create(MediaType.Book, novel.Name).ToString());
            await command.ExecuteNonQueryAsync();
            foreach (var index in indexes)
            {
                command.CommandText = index;
                await command.ExecuteNonQueryAsync();
            }
        }

        var migrated = new SqliteCatalogDatabase(scope.DatabasePath);
        var repository = new SqliteCatalogRepository(migrated);
        var after = await new SqliteUserSyncStore(migrated, repository).ExportAsync("device", "Device");
        Assert.Equal(original.Id, Assert.Single(await repository.ListAsync()).Id);
        Assert.Equal(before.Progress[0].SyncVersion, Assert.Single(after.Progress).SyncVersion);
        Assert.Equal(42, after.Progress[0].Position);
        Assert.Equal(before.FavoriteItemStates[0].SyncVersion, Assert.Single(after.FavoriteItemStates).SyncVersion);
        Assert.Equal(before.Lists[0].ItemStates[0].SyncVersion, Assert.Single(after.Lists[0].ItemStates).SyncVersion);
        await repository.MergeCatalogAsync(new CatalogTransferDocument { Items = [novel with { Subtype = "manga" }] });
        Assert.Equal(2, (await repository.ListAsync()).Count);
        await using var verify = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False");
        await verify.OpenAsync();
        await using var check = verify.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(await check.ExecuteScalarAsync());
    }
}

using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// Splitting per-person state into profiles rewrites five tables of a database
/// somebody is already using. What matters is that nothing they accumulated is
/// lost on the way: everything starred, watched and listed has to come out the
/// other side owned by the profile they inherit.
/// </summary>
public sealed class ProfileMigrationTests
{
    /// <summary>
    /// The identifier the migration gives the person whose library this already
    /// was. Repeated here rather than read from the migrator, which is internal
    /// — a test that took the value from the code under test could not notice
    /// it changing.
    /// </summary>
    private const string DefaultProfileId =
        "00000000-0000-0000-0000-000000000001";

    [Fact]
    public async Task AnExistingLibraryInheritsOneProfile()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.ListAsync();

        Assert.Equal(
            1,
            await ScalarAsync<long>(
                scope.DatabasePath,
                "SELECT count(*) FROM profiles;"));
        Assert.Equal(
            DefaultProfileId,
            await ScalarAsync<string>(
                scope.DatabasePath,
                "SELECT id FROM profiles;"));
    }

    /// <summary>
    /// The trap this migration has to avoid. user_media_list_items declares
    /// ON DELETE CASCADE against user_media_lists, so rebuilding the lists
    /// table with foreign keys still enforced empties every list on the way
    /// past — silently, and noticed only by the person whose lists they were.
    /// </summary>
    [Fact]
    public async Task AnUpgradedLibraryKeepsEverythingItHad()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Kept"));
        var item = Assert.Single(await scope.Repository.ListAsync());

        var state = new SqliteUserMediaStateStore(scope.Database);
        var list = await state.CreateUserListAsync("Later");
        await state.SetUserListMembershipAsync(list.Id, item.Id, true);
        await state.SetFavoriteCatalogItemAsync(item.Id, true);
        await state.SetFavoriteContributorAsync("contributor:one", true);

        // Put the file back the way a release without profiles left it, so
        // reopening genuinely runs the upgrade. Simply reopening does not: the
        // database is already current and the migration correctly skips, which
        // would leave this test proving nothing.
        await ReverseProfilesAsync(scope.DatabasePath);

        var upgraded = new SqliteUserMediaStateStore(
            new SqliteCatalogDatabase(scope.DatabasePath));

        Assert.Equal(
            [item.Id],
            Assert.Single(await upgraded.LoadUserListsAsync()).CatalogItemIds);
        Assert.Contains(
            item.Id,
            await upgraded.LoadFavoriteCatalogItemIdsAsync());
        Assert.Contains(
            "contributor:one",
            await upgraded.LoadFavoriteContributorKeysAsync());
        Assert.Equal(
            15L,
            await ScalarAsync<long>(
                scope.DatabasePath,
                "PRAGMA user_version;"));
    }

    /// <summary>
    /// Rewrites the database into the shape the last release without profiles
    /// used: the five per-person tables without their profile column, no
    /// profiles, and the version stamped back.
    /// </summary>
    private static async Task ReverseProfilesAsync(string databasePath)
    {
        await ExecuteAsync(
            databasePath,
            """
            PRAGMA foreign_keys = OFF;

            CREATE TABLE library_entries_v10 (
                catalog_item_id TEXT NOT NULL PRIMARY KEY REFERENCES catalog_items(id) ON DELETE CASCADE,
                is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
                added_at TEXT NOT NULL,
                last_opened_at TEXT NULL,
                user_rating REAL NULL
            );
            INSERT INTO library_entries_v10
            SELECT catalog_item_id, is_favorite, added_at, last_opened_at, user_rating
            FROM library_entries;
            DROP TABLE library_entries;
            ALTER TABLE library_entries_v10 RENAME TO library_entries;

            CREATE TABLE reader_preferences_v10 (
                catalog_item_id TEXT NOT NULL PRIMARY KEY REFERENCES catalog_items(id) ON DELETE CASCADE,
                display_mode TEXT NOT NULL CHECK (display_mode IN ('paged', 'continuous')),
                updated_at TEXT NOT NULL
            );
            INSERT INTO reader_preferences_v10
            SELECT catalog_item_id, display_mode, updated_at FROM reader_preferences;
            DROP TABLE reader_preferences;
            ALTER TABLE reader_preferences_v10 RENAME TO reader_preferences;

            CREATE TABLE favorite_contributors_v10 (
                contributor_key TEXT NOT NULL PRIMARY KEY,
                added_at TEXT NOT NULL
            );
            INSERT INTO favorite_contributors_v10
            SELECT contributor_key, added_at FROM favorite_contributors;
            DROP TABLE favorite_contributors;
            ALTER TABLE favorite_contributors_v10 RENAME TO favorite_contributors;

            CREATE TABLE user_media_lists_v10 (
                id TEXT NOT NULL PRIMARY KEY,
                name TEXT NOT NULL,
                name_key TEXT NOT NULL UNIQUE,
                created_at TEXT NOT NULL,
                updated_at TEXT NOT NULL
            );
            INSERT INTO user_media_lists_v10
            SELECT id, name, name_key, created_at, updated_at FROM user_media_lists;
            DROP TABLE user_media_lists;
            ALTER TABLE user_media_lists_v10 RENAME TO user_media_lists;

            CREATE TABLE user_sync_records_v10 (
                record_kind TEXT NOT NULL,
                record_key TEXT NOT NULL,
                sync_clock INTEGER NOT NULL CHECK (sync_clock > 0),
                sync_device_id TEXT NOT NULL,
                sync_operation_id TEXT NOT NULL,
                modified_at TEXT NOT NULL,
                is_deleted INTEGER NOT NULL CHECK (is_deleted IN (0, 1)),
                item_media_type TEXT NULL,
                item_name TEXT NULL,
                PRIMARY KEY (record_kind, record_key)
            );
            INSERT INTO user_sync_records_v10
            SELECT record_kind, record_key, sync_clock, sync_device_id,
                   sync_operation_id, modified_at, is_deleted, item_media_type, item_name
            FROM user_sync_records;
            DROP TABLE user_sync_records;
            ALTER TABLE user_sync_records_v10 RENAME TO user_sync_records;

            CREATE TABLE playback_progress_v10 (
                id TEXT NOT NULL PRIMARY KEY,
                catalog_item_id TEXT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
                content_unit_id TEXT NULL REFERENCES content_units(id) ON DELETE CASCADE,
                position REAL NOT NULL DEFAULT 0 CHECK (position >= 0),
                position_kind TEXT NOT NULL,
                is_completed INTEGER NOT NULL DEFAULT 0 CHECK (is_completed IN (0, 1)),
                updated_at TEXT NOT NULL
            );
            INSERT INTO playback_progress_v10
            SELECT id, catalog_item_id, content_unit_id, position,
                   position_kind, is_completed, updated_at
            FROM playback_progress;
            DROP TABLE playback_progress;
            ALTER TABLE playback_progress_v10 RENAME TO playback_progress;

            DROP TABLE profile_settings;
            DROP TABLE profiles;

            UPDATE schema_info SET value = '10' WHERE key = 'schema_version';
            PRAGMA user_version = 10;
            PRAGMA foreign_keys = ON;
            """);
    }

    private static async Task ExecuteAsync(string databasePath, string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
        await connection.CloseAsync();
        SqliteConnection.ClearPool(connection);
    }

    /// <summary>
    /// Every row carrying per-person state has to name the profile it belongs
    /// to, or the store cannot tell whose it is.
    /// </summary>
    [Fact]
    public async Task EveryCarriedRowNamesItsProfile()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Named"));
        var item = Assert.Single(await scope.Repository.ListAsync());

        var state = new SqliteUserMediaStateStore(scope.Database);
        await state.SetFavoriteCatalogItemAsync(item.Id, true);
        await state.SetFavoriteContributorAsync("contributor:one", true);
        await state.CreateUserListAsync("Later");

        foreach (var table in new[]
                 {
                     "library_entries",
                     "favorite_contributors",
                     "user_media_lists",
                     "playback_progress",
                     "reader_preferences",
                     "user_sync_records"
                 })
        {
            Assert.Equal(
                0,
                await ScalarAsync<long>(
                    scope.DatabasePath,
                    $"SELECT count(*) FROM {table} WHERE profile_id IS NULL;"));
        }
    }

    /// <summary>
    /// The catalog is shared, so nothing about it may have moved.
    /// </summary>
    [Fact]
    public async Task TheCatalogItselfIsUntouched()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            TestCatalogDocuments.SingleMovie("Shared"));

        var reopened = new SqliteCatalogRepository(
            new SqliteCatalogDatabase(scope.DatabasePath));
        var item = Assert.Single(await reopened.ListAsync(MediaType.Movie));

        Assert.Equal("Shared", item.Name);
    }

    /// <summary>
    /// Opens, reads and closes in one go. A connection left open holds the file
    /// and the scope cannot delete its directory, which fails the test for a
    /// reason that has nothing to do with what it was checking.
    /// </summary>
    private static async Task<T> ScalarAsync<T>(
        string databasePath,
        string sql)
    {
        await using var connection = new SqliteConnection(
            $"Data Source={databasePath}");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        var value = await command.ExecuteScalarAsync();
        await connection.CloseAsync();
        SqliteConnection.ClearPool(connection);
        return (T)Convert.ChangeType(value!, typeof(T));
    }
}

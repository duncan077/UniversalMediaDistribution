using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal static partial class SqliteCatalogMigrator
{
    public const int CurrentVersion = 15;

    /// <summary>
    /// The profile every row written before profiles existed belongs to. Fixed
    /// rather than generated so the backfill, the store's default and any test
    /// fixture all name the same one.
    /// </summary>
    public static readonly Guid DefaultProfileId =
        new("00000000-0000-0000-0000-000000000001");
    public const string ApplicationId = "universal-media-distribution";

    public static async ValueTask MigrateAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var currentVersion = await GetUserVersionAsync(connection, cancellationToken);
        if (currentVersion > CurrentVersion)
        {
            throw new InvalidOperationException(
                $"Database schema version {currentVersion} is newer than supported version {CurrentVersion}.");
        }

        // Identity before mutation. A database that already carries a schema
        // must prove it is ours before any migration writes tables into it;
        // only a genuinely empty file is safe to build from scratch.
        if (currentVersion > 0)
        {
            await ValidateApplicationIdAsync(connection, cancellationToken);
        }
        else if (await HasUserTablesAsync(connection, cancellationToken))
        {
            throw new InvalidDataException(
                "The database does not belong to Universal Media Distribution.");
        }

        if (currentVersion == 0)
        {
            await ApplyVersionOneAsync(connection, cancellationToken);
            currentVersion = 1;
        }

        if (currentVersion == 1)
        {
            await ApplyVersionTwoAsync(connection, cancellationToken);
            currentVersion = 2;
        }

        if (currentVersion == 2)
        {
            await ApplyVersionThreeAsync(connection, cancellationToken);
            currentVersion = 3;
        }

        if (currentVersion == 3)
        {
            await ApplyVersionFourAsync(connection, cancellationToken);
            currentVersion = 4;
        }

        if (currentVersion == 4)
        {
            await ApplyVersionFiveAsync(connection, cancellationToken);
            currentVersion = 5;
        }

        if (currentVersion == 5)
        {
            await ApplyVersionSixAsync(connection, cancellationToken);
            currentVersion = 6;
        }

        if (currentVersion == 6)
        {
            await ApplyVersionSevenAsync(connection, cancellationToken);
            currentVersion = 7;
        }

        if (currentVersion == 7)
        {
            await ApplyVersionEightAsync(connection, cancellationToken);
            currentVersion = 8;
        }

        if (currentVersion == 8)
        {
            await ApplyVersionNineAsync(connection, cancellationToken);
            currentVersion = 9;
        }

        if (currentVersion == 9)
        {
            await ApplyVersionTenAsync(connection, cancellationToken);
            currentVersion = 10;
        }

        if (currentVersion == 10)
        {
            await ApplyVersionElevenAsync(connection, cancellationToken);
            currentVersion = 11;
        }

        if (currentVersion == 11)
        {
            await ApplyVersionTwelveAsync(connection, cancellationToken);
            currentVersion = 12;
        }

        if (currentVersion == 12)
        {
            await ApplyVersionThirteenAsync(connection, cancellationToken);
            currentVersion = 13;
        }

        if (currentVersion == 13)
        {
            await ApplyVersionFourteenAsync(connection, cancellationToken);
            currentVersion = 14;
        }

        if (currentVersion == 14)
        {
            await ApplyVersionFifteenAsync(connection, cancellationToken);
            currentVersion = 15;
        }

        if (currentVersion != CurrentVersion)
        {
            throw new InvalidOperationException($"No migration path exists from schema version {currentVersion}.");
        }

        await ValidateApplicationIdAsync(connection, cancellationToken);
    }

    private static async ValueTask<int> GetUserVersionAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA user_version;";
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static async ValueTask ApplyVersionOneAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionOneSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ValidateApplicationIdAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT value FROM schema_info WHERE key = 'application_id';";
        var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));

        if (!string.Equals(value, ApplicationId, StringComparison.Ordinal))
        {
            throw new InvalidDataException("The database does not belong to Universal Media Distribution.");
        }
    }

    private static async ValueTask ApplyVersionTwoAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionTwoSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionThreeAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionThreeSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionFourAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasCredentialKey = await ColumnExistsAsync(
            connection,
            "tracker_endpoints",
            "credential_key",
            cancellationToken);
        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = hasCredentialKey
            ? VersionFourStampSql
            : VersionFourSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionFiveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasContributorUrl = await ColumnExistsAsync(
            connection,
            "contributors",
            "url",
            cancellationToken);
        var hasTorrentLanguage = await ColumnExistsAsync(
            connection,
            "torrent_packages",
            "language",
            cancellationToken);

        var statements = new List<string>();
        if (!hasContributorUrl)
        {
            statements.Add("ALTER TABLE contributors ADD COLUMN url TEXT NULL;");
        }

        if (!hasTorrentLanguage)
        {
            statements.Add(
                "ALTER TABLE torrent_packages " +
                "ADD COLUMN language TEXT NOT NULL DEFAULT 'und';");
        }

        statements.Add(VersionFiveCoreSql);

        await using var transaction = await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = string.Join(Environment.NewLine, statements);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionSixAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionSixSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionSevenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionSevenSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionEightAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionEightSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Covering indexes for the foreign keys the reader actually filters on.
    /// Nothing about the data changes; only the plans SQLite picks for queries
    /// that were scanning whole tables.
    /// </summary>
    private static async ValueTask ApplyVersionTwelveAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = VersionTwelveSql;
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionNineAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasReleaseYear = await ColumnExistsAsync(
            connection,
            "catalog_items",
            "release_year",
            cancellationToken);

        var statements = new List<string>();
        if (!hasReleaseYear)
        {
            statements.Add(
                "ALTER TABLE catalog_items ADD COLUMN release_year INTEGER NULL " +
                "CHECK (release_year IS NULL " +
                "OR (release_year >= 1800 AND release_year <= 2200));");
        }

        statements.Add(VersionNineCoreSql);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = string.Join(Environment.NewLine, statements);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Remembers what an import last said about each item, so a re-import of
    /// the same payload can be told apart from one that actually carries
    /// something new. Left NULL on existing rows: the first merge after the
    /// upgrade fills it in, and until then an item is treated as changed,
    /// which is the safe direction.
    /// </summary>
    private static async ValueTask ApplyVersionTenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var statements = new List<string>();
        if (!await ColumnExistsAsync(
                connection,
                "catalog_items",
                "content_hash",
                cancellationToken))
        {
            statements.Add(
                "ALTER TABLE catalog_items ADD COLUMN content_hash TEXT NULL;");
        }

        statements.Add(VersionTenCoreSql);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        await using var command = connection.CreateCommand();
        command.Transaction = (SqliteTransaction)transaction;
        command.CommandText = string.Join(Environment.NewLine, statements);
        await command.ExecuteNonQueryAsync(cancellationToken);
        await transaction.CommitAsync(cancellationToken);
    }

    /// <summary>
    /// Splits the per-person state into profiles, leaving the catalog shared.
    /// <para>
    /// Everything a viewer accumulates — what they starred, how far they got,
    /// their lists, their reader settings and the account they sync with —
    /// becomes owned by a profile. The library itself does not: two people
    /// sharing a device share the titles and disagree only about what they have
    /// done with them.
    /// </para>
    /// <para>
    /// Four tables are rebuilt rather than altered because their key has to
    /// widen: SQLite can add a column but cannot change a primary key or a
    /// unique constraint, and two profiles must be able to star the same title
    /// and name a list the same thing.
    /// </para>
    /// </summary>
    private static async ValueTask ApplyVersionElevenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // The procedure SQLite documents for rebuilding a table, and it is not
        // optional here. With enforcement left on, dropping user_media_lists
        // fires the ON DELETE CASCADE that user_media_list_items declares
        // against it and every list loses its contents. The pragma is also
        // ignored inside a transaction, so it has to be set first.
        // A database recovered from a partly-applied run, or rebuilt from a
        // fixture that already carries the new shape, arrives here with the
        // work done. Redoing it would fail on the first CREATE, so the version
        // is simply stamped and the rest skipped — the same guarding the
        // earlier column migrations use.
        if (await ColumnExistsAsync(
                connection,
                "library_entries",
                "profile_id",
                cancellationToken))
        {
            await ExecuteAsync(
                connection,
                """
                UPDATE schema_info SET value = '11' WHERE key = 'schema_version';
                PRAGMA user_version = 11;
                """,
                cancellationToken);
            return;
        }

        await ExecuteAsync(
            connection,
            "PRAGMA foreign_keys = OFF;",
            cancellationToken);
        try
        {
            await using (var transaction =
                await connection.BeginTransactionAsync(cancellationToken))
            {
                await using var command = connection.CreateCommand();
                command.Transaction = (SqliteTransaction)transaction;
                command.CommandText = VersionElevenSql.Replace(
                    "$default_profile_id",
                    $"'{DefaultProfileId:D}'",
                    StringComparison.Ordinal);
                await command.ExecuteNonQueryAsync(cancellationToken);

                // Proves the rebuild left no row pointing at something that is
                // gone, while enforcement is still off and the damage would
                // otherwise go unnoticed until much later.
                await using var check = connection.CreateCommand();
                check.Transaction = (SqliteTransaction)transaction;
                check.CommandText = "PRAGMA foreign_key_check;";
                await using var reader =
                    await check.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken))
                {
                    throw new InvalidOperationException(
                        "Splitting the library into profiles left a broken " +
                        $"reference in '{reader.GetValue(0)}'.");
                }

                await reader.CloseAsync();
                await transaction.CommitAsync(cancellationToken);
            }
        }
        finally
        {
            await ExecuteAsync(
                connection,
                "PRAGMA foreign_keys = ON;",
                cancellationToken);
        }
    }

    private static async ValueTask ExecuteAsync(
        SqliteConnection connection,
        string sql,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<bool> HasUserTablesAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT count(*) FROM sqlite_master
            WHERE type = 'table' AND name NOT LIKE 'sqlite_%';
            """;
        return Convert.ToInt32(
            await command.ExecuteScalarAsync(cancellationToken)) > 0;
    }

    private static async ValueTask<bool> ColumnExistsAsync(
        SqliteConnection connection,
        string tableName,
        string columnName,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = $"PRAGMA table_info('{tableName}');";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            if (string.Equals(reader.GetString(1), columnName, StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private const string VersionFourSql = """
        ALTER TABLE tracker_endpoints ADD COLUMN credential_key TEXT NULL;

        UPDATE schema_info SET value = '4' WHERE key = 'schema_version';
        PRAGMA user_version = 4;
        """;

    private const string VersionFourStampSql = """
        UPDATE schema_info SET value = '4' WHERE key = 'schema_version';
        PRAGMA user_version = 4;
        """;

    private const string VersionFiveCoreSql = """
        CREATE TABLE IF NOT EXISTS favorite_contributors (
            contributor_key TEXT NOT NULL PRIMARY KEY,
            added_at TEXT NOT NULL
        );

        UPDATE schema_info SET value = '5' WHERE key = 'schema_version';
        PRAGMA user_version = 5;
        """;

    private const string VersionSixSql = """
        CREATE TABLE user_media_lists (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE user_media_list_items (
            list_id TEXT NOT NULL REFERENCES user_media_lists(id) ON DELETE CASCADE,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            added_at TEXT NOT NULL,
            PRIMARY KEY (list_id, catalog_item_id)
        );

        CREATE INDEX ix_user_media_list_items_catalog_item
        ON user_media_list_items(catalog_item_id, list_id);

        UPDATE schema_info SET value = '6' WHERE key = 'schema_version';
        PRAGMA user_version = 6;
        """;

    private const string VersionSevenSql = """
        CREATE TABLE IF NOT EXISTS managed_downloads (
            id TEXT NOT NULL PRIMARY KEY,
            activity_id TEXT NULL,
            session_id TEXT NULL,
            title TEXT NOT NULL,
            detail TEXT NULL,
            state TEXT NOT NULL,
            downloaded_bytes INTEGER NOT NULL DEFAULT 0
                CHECK (downloaded_bytes >= 0),
            total_bytes INTEGER NULL
                CHECK (total_bytes IS NULL OR total_bytes >= 0),
            wanted_downloaded_bytes INTEGER NULL
                CHECK (wanted_downloaded_bytes IS NULL OR wanted_downloaded_bytes >= 0),
            wanted_bytes INTEGER NULL
                CHECK (wanted_bytes IS NULL OR wanted_bytes >= 0),
            progress REAL NULL
                CHECK (progress IS NULL OR (progress >= 0 AND progress <= 1)),
            error_code TEXT NULL,
            storage_key TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            completed_at TEXT NULL
        );

        CREATE INDEX IF NOT EXISTS ix_managed_downloads_updated
        ON managed_downloads(updated_at DESC);

        CREATE INDEX IF NOT EXISTS ix_managed_downloads_storage_key
        ON managed_downloads(storage_key)
        WHERE storage_key IS NOT NULL;

        CREATE TABLE IF NOT EXISTS managed_download_files (
            download_id TEXT NOT NULL
                REFERENCES managed_downloads(id) ON DELETE CASCADE,
            ordinal INTEGER NOT NULL CHECK (ordinal >= 0),
            complete_path TEXT NOT NULL,
            incomplete_path TEXT NOT NULL,
            length INTEGER NOT NULL CHECK (length >= 0),
            PRIMARY KEY (download_id, ordinal)
        );

        UPDATE schema_info SET value = '7' WHERE key = 'schema_version';
        PRAGMA user_version = 7;
        """;

    private const string VersionEightSql = """
        CREATE TABLE IF NOT EXISTS user_sync_records (
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

        CREATE INDEX IF NOT EXISTS ix_user_sync_records_kind_deleted
        ON user_sync_records(record_kind, is_deleted);

        UPDATE schema_info SET value = '8' WHERE key = 'schema_version';
        PRAGMA user_version = 8;
        """;

    // Backfills from the existing release_date so the year filter sees the same
    // value the UI already derived from it, and only where that prefix really
    // is a plausible year. The ADD COLUMN itself is issued separately, guarded
    // by a column check, so a database that already carries the column still
    // migrates.
    private const string VersionNineCoreSql = """
        UPDATE catalog_items
        SET release_year = CAST(substr(release_date, 1, 4) AS INTEGER)
        WHERE release_year IS NULL
          AND release_date IS NOT NULL
          AND length(release_date) >= 4
          AND substr(release_date, 1, 4) GLOB '[0-9][0-9][0-9][0-9]'
          AND CAST(substr(release_date, 1, 4) AS INTEGER) BETWEEN 1800 AND 2200;

        CREATE INDEX IF NOT EXISTS ix_catalog_items_release_year
        ON catalog_items (release_year);

        UPDATE schema_info SET value = '9' WHERE key = 'schema_version';
        PRAGMA user_version = 9;
        """;

    // The ADD COLUMN is issued separately, guarded by a column check, so a
    // database that already carries the column still migrates.
    private const string VersionTenCoreSql = """
        UPDATE schema_info SET value = '10' WHERE key = 'schema_version';
        PRAGMA user_version = 10;
        """;

    // Indexes only. Every statement is IF NOT EXISTS so a database rebuilt from
    // a fixture that already carries them still migrates.
    //
    // content_units already had two indexes leading with catalog_item_id, but
    // both are PARTIAL - ux_content_units_direct is WHERE season_id IS NULL and
    // ux_content_units_season is WHERE season_id IS NOT NULL. SQLite may only
    // use a partial index when the query provably implies its WHERE clause, and
    // the per-title reads do not constrain season_id at all, so every one of
    // them fell back to a full table scan. sort_order rides along because the
    // last-unit lookups in SqliteUserMediaStateStore order by it.
    //
    // media_sources had no index on any of its three foreign keys. Besides the
    // reader's own joins, each unindexed key made the ON DELETE CASCADE from
    // its parent scan the whole table, which is what made re-importing a title
    // cost more the larger the library got.
    //
    // playback_progress was indexed on profile_id alone. On a single-profile
    // install - which is nearly all of them - that column is one constant
    // value, so the index matched every row and SQLite then sorted them; the
    // resume lookup runs on a two-second timer during playback.
    private static async ValueTask ApplyVersionThirteenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        // Guarded the way version nine guards release_year: a database
        // restored from a backup can carry the column while its stamped
        // version says otherwise, and redoing an ALTER is the one statement
        // here that cannot be repeated.
        var hasSyncId = await ColumnExistsAsync(
            connection,
            "catalog_items",
            "sync_id",
            cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        if (!hasSyncId)
        {
            await using var alter = connection.CreateCommand();
            alter.Transaction = (SqliteTransaction)transaction;
            alter.CommandText =
                "ALTER TABLE catalog_items ADD COLUMN sync_id TEXT NULL;";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = VersionThirteenSql;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Backfilled in code: the identity is a hash of the media type and
        // the normalized title, which SQL cannot compute. Deterministic on
        // purpose — every device derives the same IDs for the same titles,
        // so the fleet converges without ever coordinating.
        var assignments = new List<(string RowId, string SyncId)>();
        await using (var select = connection.CreateCommand())
        {
            select.Transaction = (SqliteTransaction)transaction;
            select.CommandText =
                "SELECT id, media_type, name FROM catalog_items " +
                "WHERE sync_id IS NULL;";
            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var mediaType = CatalogSubtypeResolver.ParseMediaType(
                    reader.GetString(1));
                assignments.Add((
                    reader.GetString(0),
                    CatalogStableId.Create(
                        mediaType,
                        reader.GetString(2)).ToString()));
            }
        }

        foreach (var (rowId, syncId) in assignments)
        {
            await using var update = connection.CreateCommand();
            update.Transaction = (SqliteTransaction)transaction;
            update.CommandText =
                "UPDATE catalog_items SET sync_id = $sync_id WHERE id = $id;";
            update.Parameters.AddWithValue("$sync_id", syncId);
            update.Parameters.AddWithValue("$id", rowId);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var stamp = connection.CreateCommand())
        {
            stamp.Transaction = (SqliteTransaction)transaction;
            stamp.CommandText = """
                UPDATE schema_info SET value = '13' WHERE key = 'schema_version';
                PRAGMA user_version = 13;
                """;
            await stamp.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private static async ValueTask ApplyVersionFourteenAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        var hasVersionColumns = await ColumnExistsAsync(
            connection,
            "catalog_items",
            "sync_clock",
            cancellationToken);

        await using var transaction =
            await connection.BeginTransactionAsync(cancellationToken);
        if (!hasVersionColumns)
        {
            await using var alter = connection.CreateCommand();
            alter.Transaction = (SqliteTransaction)transaction;
            alter.CommandText = """
                ALTER TABLE catalog_items ADD COLUMN sync_clock INTEGER NULL;
                ALTER TABLE catalog_items ADD COLUMN sync_device TEXT NULL;
                ALTER TABLE catalog_items ADD COLUMN sync_operation TEXT NULL;
                ALTER TABLE catalog_items ADD COLUMN sync_modified TEXT NULL;
                """;
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }

        await using (var command = connection.CreateCommand())
        {
            command.Transaction = (SqliteTransaction)transaction;
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS catalog_tombstones (
                    sync_id TEXT NOT NULL PRIMARY KEY,
                    media_type TEXT NOT NULL,
                    subtype_code TEXT NOT NULL,
                    name TEXT NOT NULL,
                    clock INTEGER NOT NULL,
                    device_id TEXT NOT NULL,
                    operation_id TEXT NOT NULL,
                    modified_at TEXT NOT NULL
                );

                UPDATE schema_info SET value = '14' WHERE key = 'schema_version';
                PRAGMA user_version = 14;
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
    }

    private const string VersionThirteenSql = """
        CREATE UNIQUE INDEX IF NOT EXISTS ix_catalog_items_sync_id
        ON catalog_items (sync_id) WHERE sync_id IS NOT NULL;

        CREATE TABLE IF NOT EXISTS catalog_redirects (
            from_id TEXT NOT NULL PRIMARY KEY,
            to_id TEXT NOT NULL,
            clock INTEGER NOT NULL,
            device_id TEXT NOT NULL,
            operation_id TEXT NOT NULL,
            modified_at TEXT NOT NULL,
            CHECK (from_id <> to_id)
        );
        """;

    private const string VersionTwelveSql = """
        CREATE INDEX IF NOT EXISTS ix_content_units_catalog_item
        ON content_units (catalog_item_id, sort_order);

        CREATE INDEX IF NOT EXISTS ix_media_sources_torrent_package
        ON media_sources (torrent_package_id);

        CREATE INDEX IF NOT EXISTS ix_media_sources_catalog_item
        ON media_sources (catalog_item_id);

        CREATE INDEX IF NOT EXISTS ix_media_sources_content_unit
        ON media_sources (content_unit_id);

        CREATE INDEX IF NOT EXISTS ix_playback_progress_profile_unit
        ON playback_progress (profile_id, content_unit_id);

        CREATE INDEX IF NOT EXISTS ix_playback_progress_profile_updated
        ON playback_progress (profile_id, updated_at DESC);

        UPDATE schema_info SET value = '12' WHERE key = 'schema_version';
        PRAGMA user_version = 12;
        """;

    private const string VersionThreeSql = """
        DELETE FROM catalog_aliases
        WHERE trim(language) = ''
          AND EXISTS (
              SELECT 1
              FROM catalog_aliases existing
              WHERE existing.catalog_item_id = catalog_aliases.catalog_item_id
                AND existing.name_key = catalog_aliases.name_key
                AND existing.alias_type = catalog_aliases.alias_type
                AND lower(trim(existing.language)) = 'und'
                AND existing.id <> catalog_aliases.id
          );

        UPDATE catalog_aliases
        SET language = 'und'
        WHERE trim(language) = '';

        ALTER TABLE catalog_aliases RENAME TO catalog_alternative_names;

        CREATE TABLE categories (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE
        );

        CREATE TABLE catalog_item_categories (
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            category_id TEXT NOT NULL REFERENCES categories(id) ON DELETE CASCADE,
            PRIMARY KEY (catalog_item_id, category_id)
        );

        UPDATE schema_info SET value = '3' WHERE key = 'schema_version';
        PRAGMA user_version = 3;
        """;

    private const string VersionTwoSql = """
        CREATE TABLE application_settings (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        UPDATE schema_info SET value = '2' WHERE key = 'schema_version';
        PRAGMA user_version = 2;
        """;

    private const string VersionElevenSql = """
        CREATE TABLE profiles (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE,
            accent_key TEXT NULL,
            sort_order INTEGER NOT NULL DEFAULT 0,
            created_at TEXT NOT NULL,
            last_used_at TEXT NULL
        );

        -- Per-profile key/value, alongside the global application_settings.
        -- The server account lives here: with a profile per account, the
        -- session and the server address stop being properties of the install.
        CREATE TABLE profile_settings (
            profile_id TEXT NOT NULL DEFAULT $default_profile_id
                REFERENCES profiles(id) ON DELETE CASCADE,
            key TEXT NOT NULL,
            value TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            PRIMARY KEY (profile_id, key)
        );

        INSERT INTO profiles (
            id, name, name_key, accent_key, sort_order, created_at, last_used_at)
        VALUES (
            $default_profile_id,
            'Me',
            'ME',
            NULL,
            0,
            datetime('now'),
            datetime('now'));

        -- Progress keeps its own identifier, so the column is simply added.
        ALTER TABLE playback_progress
        ADD COLUMN profile_id TEXT NOT NULL DEFAULT $default_profile_id;

        UPDATE playback_progress SET profile_id = $default_profile_id;

        CREATE INDEX ix_playback_progress_profile
        ON playback_progress(profile_id);

        -- The four below are rebuilt: each is keyed on something that has to
        -- become per profile.
        CREATE TABLE library_entries_v11 (
            profile_id TEXT NOT NULL DEFAULT $default_profile_id
                REFERENCES profiles(id) ON DELETE CASCADE,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
            added_at TEXT NOT NULL,
            last_opened_at TEXT NULL,
            user_rating REAL NULL,
            PRIMARY KEY (profile_id, catalog_item_id)
        );

        INSERT INTO library_entries_v11 (
            profile_id, catalog_item_id, is_favorite, added_at, last_opened_at, user_rating)
        SELECT $default_profile_id, catalog_item_id, is_favorite, added_at, last_opened_at, user_rating
        FROM library_entries;

        DROP TABLE library_entries;
        ALTER TABLE library_entries_v11 RENAME TO library_entries;

        CREATE TABLE reader_preferences_v11 (
            profile_id TEXT NOT NULL DEFAULT $default_profile_id
                REFERENCES profiles(id) ON DELETE CASCADE,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            display_mode TEXT NOT NULL CHECK (display_mode IN ('paged', 'continuous')),
            updated_at TEXT NOT NULL,
            PRIMARY KEY (profile_id, catalog_item_id)
        );

        INSERT INTO reader_preferences_v11 (
            profile_id, catalog_item_id, display_mode, updated_at)
        SELECT $default_profile_id, catalog_item_id, display_mode, updated_at
        FROM reader_preferences;

        DROP TABLE reader_preferences;
        ALTER TABLE reader_preferences_v11 RENAME TO reader_preferences;

        CREATE TABLE favorite_contributors_v11 (
            profile_id TEXT NOT NULL DEFAULT $default_profile_id
                REFERENCES profiles(id) ON DELETE CASCADE,
            contributor_key TEXT NOT NULL,
            added_at TEXT NOT NULL,
            PRIMARY KEY (profile_id, contributor_key)
        );

        INSERT INTO favorite_contributors_v11 (profile_id, contributor_key, added_at)
        SELECT $default_profile_id, contributor_key, added_at
        FROM favorite_contributors;

        DROP TABLE favorite_contributors;
        ALTER TABLE favorite_contributors_v11 RENAME TO favorite_contributors;

        -- The list name is unique per profile, not per device: two people may
        -- each keep a list called "Later".
        CREATE TABLE user_media_lists_v11 (
            id TEXT NOT NULL PRIMARY KEY,
            profile_id TEXT NOT NULL DEFAULT $default_profile_id
                REFERENCES profiles(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (profile_id, name_key)
        );

        INSERT INTO user_media_lists_v11 (
            id, profile_id, name, name_key, created_at, updated_at)
        SELECT id, $default_profile_id, name, name_key, created_at, updated_at
        FROM user_media_lists;

        DROP TABLE user_media_lists;
        ALTER TABLE user_media_lists_v11 RENAME TO user_media_lists;

        -- Sync versions describe one profile's records, because each profile
        -- now syncs with its own account.
        CREATE TABLE user_sync_records_v11 (
            profile_id TEXT NOT NULL DEFAULT $default_profile_id
                REFERENCES profiles(id) ON DELETE CASCADE,
            record_kind TEXT NOT NULL,
            record_key TEXT NOT NULL,
            sync_clock INTEGER NOT NULL CHECK (sync_clock > 0),
            sync_device_id TEXT NOT NULL,
            sync_operation_id TEXT NOT NULL,
            modified_at TEXT NOT NULL,
            is_deleted INTEGER NOT NULL CHECK (is_deleted IN (0, 1)),
            item_media_type TEXT NULL,
            item_name TEXT NULL,
            PRIMARY KEY (profile_id, record_kind, record_key)
        );

        INSERT INTO user_sync_records_v11 (
            profile_id, record_kind, record_key, sync_clock, sync_device_id,
            sync_operation_id, modified_at, is_deleted, item_media_type, item_name)
        SELECT $default_profile_id, record_kind, record_key, sync_clock, sync_device_id,
               sync_operation_id, modified_at, is_deleted, item_media_type, item_name
        FROM user_sync_records;

        DROP TABLE user_sync_records;
        ALTER TABLE user_sync_records_v11 RENAME TO user_sync_records;

        CREATE INDEX ix_user_sync_records_kind_deleted
        ON user_sync_records(profile_id, record_kind, is_deleted);

        -- The signed-in account moves to the profile that inherited everything
        -- else, so the person already using this install stays signed in.
        INSERT INTO profile_settings (profile_id, key, value, updated_at)
        SELECT $default_profile_id, key, value, updated_at
        FROM application_settings
        WHERE key LIKE 'remote_server_%' OR key LIKE 'remote_session_%';

        DELETE FROM application_settings
        WHERE key LIKE 'remote_server_%' OR key LIKE 'remote_session_%';

        UPDATE schema_info SET value = '11' WHERE key = 'schema_version';
        PRAGMA user_version = 11;
        """;

    private const string VersionOneSql = """
        CREATE TABLE schema_info (
            key TEXT NOT NULL PRIMARY KEY,
            value TEXT NOT NULL
        );

        INSERT INTO schema_info (key, value) VALUES
            ('application_id', 'universal-media-distribution'),
            ('schema_version', '1'),
            ('catalog_revision', '0');

        CREATE TABLE catalog_items (
            id TEXT NOT NULL PRIMARY KEY,
            media_type TEXT NOT NULL CHECK (media_type IN ('movie', 'book', 'audio')),
            subtype_code TEXT NOT NULL,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            original_name TEXT NULL,
            sort_name TEXT NULL,
            description TEXT NOT NULL DEFAULT '',
            release_date TEXT NULL,
            end_date TEXT NULL,
            publication_status INTEGER NOT NULL DEFAULT 0,
            original_language TEXT NULL,
            country_code TEXT NULL,
            content_rating TEXT NULL,
            runtime_seconds INTEGER NULL CHECK (runtime_seconds IS NULL OR runtime_seconds >= 0),
            page_count INTEGER NULL CHECK (page_count IS NULL OR page_count >= 0),
            thumbnail_ref TEXT NULL,
            backdrop_ref TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            UNIQUE (media_type, name_key)
        );

        CREATE INDEX ix_catalog_items_type_name ON catalog_items (media_type, name);

        CREATE TABLE catalog_aliases (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            language TEXT NOT NULL DEFAULT '',
            alias_type TEXT NOT NULL DEFAULT 'other',
            UNIQUE (catalog_item_id, name_key, language, alias_type)
        );

        CREATE TABLE genres (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL UNIQUE
        );

        CREATE TABLE catalog_item_genres (
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            genre_id TEXT NOT NULL REFERENCES genres(id) ON DELETE CASCADE,
            PRIMARY KEY (catalog_item_id, genre_id)
        );

        CREATE TABLE contributors (
            id TEXT NOT NULL PRIMARY KEY,
            name TEXT NOT NULL,
            name_key TEXT NOT NULL,
            kind TEXT NOT NULL CHECK (kind IN ('person', 'organization')),
            thumbnail_ref TEXT NULL,
            UNIQUE (kind, name_key)
        );

        CREATE TABLE catalog_item_credits (
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            contributor_id TEXT NOT NULL REFERENCES contributors(id) ON DELETE CASCADE,
            role TEXT NOT NULL,
            character_name TEXT NOT NULL DEFAULT '',
            sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
            PRIMARY KEY (catalog_item_id, contributor_id, role, character_name)
        );

        CREATE TABLE seasons (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            season_number INTEGER NOT NULL CHECK (season_number >= 0),
            display_name TEXT NULL,
            thumbnail_ref TEXT NULL,
            release_date TEXT NULL,
            sort_order INTEGER NOT NULL CHECK (sort_order >= 0),
            UNIQUE (catalog_item_id, season_number)
        );

        CREATE TABLE content_units (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            season_id TEXT NULL REFERENCES seasons(id) ON DELETE CASCADE,
            unit_kind TEXT NOT NULL CHECK (unit_kind IN ('episode', 'chapter', 'track', 'audio_chapter')),
            display_number TEXT NOT NULL,
            display_number_key TEXT NOT NULL,
            name TEXT NULL,
            thumbnail_ref TEXT NULL,
            duration_seconds INTEGER NULL CHECK (duration_seconds IS NULL OR duration_seconds >= 0),
            page_count INTEGER NULL CHECK (page_count IS NULL OR page_count >= 0),
            release_date TEXT NULL,
            is_special INTEGER NOT NULL DEFAULT 0 CHECK (is_special IN (0, 1)),
            sort_order INTEGER NOT NULL CHECK (sort_order >= 0)
        );

        CREATE UNIQUE INDEX ux_content_units_direct
            ON content_units (catalog_item_id, unit_kind, display_number_key)
            WHERE season_id IS NULL;

        CREATE UNIQUE INDEX ux_content_units_season
            ON content_units (season_id, unit_kind, display_number_key)
            WHERE season_id IS NOT NULL;

        CREATE TABLE distribution_rights (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NOT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            natural_key TEXT NOT NULL,
            source_name TEXT NOT NULL,
            source_uri TEXT NOT NULL,
            license_name TEXT NOT NULL,
            license_uri TEXT NULL,
            attribution TEXT NULL,
            rights_note TEXT NULL,
            verified_at TEXT NULL,
            UNIQUE (catalog_item_id, natural_key)
        );

        CREATE TABLE torrent_packages (
            id TEXT NOT NULL PRIMARY KEY,
            natural_key TEXT NOT NULL UNIQUE,
            display_name TEXT NULL,
            magnet_uri TEXT NULL,
            torrent_metadata BLOB NULL,
            info_hash_v1 TEXT NULL,
            info_hash_v2 TEXT NULL,
            is_private INTEGER NOT NULL DEFAULT 0 CHECK (is_private IN (0, 1)),
            total_size INTEGER NULL CHECK (total_size IS NULL OR total_size >= 0),
            source_uri TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL,
            CHECK (magnet_uri IS NOT NULL OR torrent_metadata IS NOT NULL OR info_hash_v1 IS NOT NULL OR info_hash_v2 IS NOT NULL)
        );

        CREATE TABLE tracker_endpoints (
            id TEXT NOT NULL PRIMARY KEY,
            natural_key TEXT NOT NULL UNIQUE,
            name TEXT NULL,
            endpoint_template TEXT NOT NULL,
            transport TEXT NOT NULL CHECK (transport IN ('https', 'wss', 'udp')),
            client_family TEXT NOT NULL CHECK (client_family IN ('native', 'browser', 'both')),
            requires_authentication INTEGER NOT NULL DEFAULT 0 CHECK (requires_authentication IN (0, 1)),
            is_enabled INTEGER NOT NULL DEFAULT 1 CHECK (is_enabled IN (0, 1))
        );

        CREATE TABLE torrent_package_trackers (
            torrent_package_id TEXT NOT NULL REFERENCES torrent_packages(id) ON DELETE CASCADE,
            tracker_endpoint_id TEXT NOT NULL REFERENCES tracker_endpoints(id) ON DELETE CASCADE,
            tier INTEGER NOT NULL DEFAULT 0 CHECK (tier >= 0),
            sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
            PRIMARY KEY (torrent_package_id, tracker_endpoint_id)
        );

        CREATE TABLE media_sources (
            id TEXT NOT NULL PRIMARY KEY,
            natural_key TEXT NOT NULL UNIQUE,
            torrent_package_id TEXT NOT NULL REFERENCES torrent_packages(id) ON DELETE CASCADE,
            catalog_item_id TEXT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            content_unit_id TEXT NULL REFERENCES content_units(id) ON DELETE CASCADE,
            asset_role TEXT NOT NULL,
            source_layout TEXT NOT NULL CHECK (source_layout IN ('single_file', 'image_sequence')),
            file_index INTEGER NULL CHECK (file_index IS NULL OR file_index >= 0),
            file_path TEXT NOT NULL DEFAULT '',
            mime_type TEXT NULL,
            container TEXT NULL,
            video_quality TEXT NULL,
            language TEXT NULL,
            size_bytes INTEGER NULL CHECK (size_bytes IS NULL OR size_bytes >= 0),
            sort_order INTEGER NOT NULL DEFAULT 0 CHECK (sort_order >= 0),
            CHECK ((catalog_item_id IS NOT NULL AND content_unit_id IS NULL) OR
                   (catalog_item_id IS NULL AND content_unit_id IS NOT NULL))
        );

        CREATE TABLE library_entries (
            catalog_item_id TEXT NOT NULL PRIMARY KEY REFERENCES catalog_items(id) ON DELETE CASCADE,
            is_favorite INTEGER NOT NULL DEFAULT 0 CHECK (is_favorite IN (0, 1)),
            added_at TEXT NOT NULL,
            last_opened_at TEXT NULL,
            user_rating REAL NULL
        );

        CREATE TABLE playback_progress (
            id TEXT NOT NULL PRIMARY KEY,
            catalog_item_id TEXT NULL REFERENCES catalog_items(id) ON DELETE CASCADE,
            content_unit_id TEXT NULL REFERENCES content_units(id) ON DELETE CASCADE,
            position REAL NOT NULL DEFAULT 0 CHECK (position >= 0),
            position_kind TEXT NOT NULL,
            is_completed INTEGER NOT NULL DEFAULT 0 CHECK (is_completed IN (0, 1)),
            updated_at TEXT NOT NULL,
            CHECK ((catalog_item_id IS NOT NULL AND content_unit_id IS NULL) OR
                   (catalog_item_id IS NULL AND content_unit_id IS NOT NULL))
        );

        CREATE TABLE reader_preferences (
            catalog_item_id TEXT NOT NULL PRIMARY KEY REFERENCES catalog_items(id) ON DELETE CASCADE,
            display_mode TEXT NOT NULL CHECK (display_mode IN ('paged', 'continuous')),
            updated_at TEXT NOT NULL
        );

        CREATE TABLE download_jobs (
            id TEXT NOT NULL PRIMARY KEY,
            torrent_package_id TEXT NOT NULL REFERENCES torrent_packages(id) ON DELETE CASCADE,
            state TEXT NOT NULL,
            selected_file_indexes TEXT NULL,
            download_location TEXT NULL,
            bytes_downloaded INTEGER NOT NULL DEFAULT 0 CHECK (bytes_downloaded >= 0),
            error_code TEXT NULL,
            created_at TEXT NOT NULL,
            updated_at TEXT NOT NULL
        );

        CREATE TABLE catalog_import_history (
            id TEXT NOT NULL PRIMARY KEY,
            document_hash TEXT NULL,
            source_name TEXT NULL,
            format_version INTEGER NOT NULL,
            created_count INTEGER NOT NULL CHECK (created_count >= 0),
            updated_count INTEGER NOT NULL CHECK (updated_count >= 0),
            warning_count INTEGER NOT NULL DEFAULT 0 CHECK (warning_count >= 0),
            imported_at TEXT NOT NULL
        );

        PRAGMA user_version = 1;
        """;
}

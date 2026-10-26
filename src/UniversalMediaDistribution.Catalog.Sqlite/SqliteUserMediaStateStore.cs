using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Domain.Reading;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteUserMediaStateStore(SqliteCatalogDatabase database) : IUserMediaStateStore
{

    /// <summary>
    /// A command that already knows whose state it is reading or writing.
    /// <para>
    /// Every statement in this store is scoped to one profile, so the parameter
    /// is bound once here rather than twenty times at the call sites, where a
    /// single omission would silently read another profile's rows. Statements
    /// that do not mention it simply leave it unused.
    /// </para>
    /// </summary>
    private SqliteCommand CreateProfileCommand(SqliteConnection connection)
    {
        var command = connection.CreateCommand();
        command.Parameters.AddWithValue(
            "$profile",
            database.ActiveProfileId.ToString("D"));
        return command;
    }
    public async ValueTask<MediaProgressState?> LoadProgressAsync(
        CatalogContentIdentity content,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var unitId = await ResolveUnitIdAsync(connection, content, cancellationToken);
            if (content.IsUnit && unitId is null)
            {
                return null;
            }

            await using var command = CreateProfileCommand(connection);
            command.CommandText = content.IsUnit
                ? """
                  SELECT position, position_kind, is_completed, updated_at
                  FROM playback_progress
                  WHERE profile_id = $profile AND content_unit_id = $unit_id
                  ORDER BY updated_at DESC
                  LIMIT 1;
                  """
                : """
                  SELECT position, position_kind, is_completed, updated_at
                  FROM playback_progress
                  WHERE profile_id = $profile
                    AND catalog_item_id = $item_id AND content_unit_id IS NULL
                  ORDER BY updated_at DESC
                  LIMIT 1;
                  """;
            command.Parameters.AddWithValue("$item_id", content.CatalogItemId.ToString("D"));
            command.Parameters.AddWithValue("$unit_id", (object?)unitId ?? DBNull.Value);

            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            if (!await reader.ReadAsync(cancellationToken))
            {
                return null;
            }

            return new MediaProgressState(
                reader.GetDouble(0),
                ParsePositionKind(reader.GetString(1)),
                reader.GetInt32(2) != 0,
                DateTimeOffset.Parse(reader.GetString(3), CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind));
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SaveProgressAsync(
        CatalogContentIdentity content,
        MediaProgressState progress,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(progress);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var unitId = await ResolveUnitIdAsync(connection, content, cancellationToken);
            if (content.IsUnit && unitId is null)
            {
                throw new KeyNotFoundException($"Content unit '{content.StableKey}' no longer exists.");
            }

            var syncKey = await CreateContentSyncKeyAsync(
                connection,
                content,
                cancellationToken);
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            await using (var delete = CreateProfileCommand(connection))
            {
                delete.Transaction = transaction;
                delete.CommandText = content.IsUnit
                    ? "DELETE FROM playback_progress WHERE profile_id = $profile AND content_unit_id = $unit_id;"
                    : "DELETE FROM playback_progress WHERE profile_id = $profile AND catalog_item_id = $item_id AND content_unit_id IS NULL;";
                delete.Parameters.AddWithValue("$item_id", content.CatalogItemId.ToString("D"));
                delete.Parameters.AddWithValue("$unit_id", (object?)unitId ?? DBNull.Value);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }

            await using (var insert = CreateProfileCommand(connection))
            {
                insert.Transaction = transaction;
                insert.CommandText = """
                    INSERT INTO playback_progress (
                        id, profile_id, catalog_item_id, content_unit_id, position,
                        position_kind, is_completed, updated_at)
                    VALUES (
                        $id, $profile, $item_id, $unit_id, $position,
                        $position_kind, $is_completed, $updated_at);
                    """;
                insert.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString("D"));
                insert.Parameters.AddWithValue(
                    "$item_id",
                    content.IsUnit ? DBNull.Value : content.CatalogItemId.ToString("D"));
                insert.Parameters.AddWithValue("$unit_id", (object?)unitId ?? DBNull.Value);
                insert.Parameters.AddWithValue("$position", progress.Position);
                insert.Parameters.AddWithValue("$position_kind", PositionKindToDatabase(progress.PositionKind));
                insert.Parameters.AddWithValue("$is_completed", progress.IsCompleted ? 1 : 0);
                insert.Parameters.AddWithValue("$updated_at", progress.UpdatedAt.ToString("O"));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }

            var version =
                await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                    connection,
                    transaction,
                    progress.UpdatedAt,
                    cancellationToken);
            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ProgressKind,
                syncKey,
                version,
                isDeleted: false,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<CatalogProgressSummary>>
        LoadCatalogProgressAsync(
            CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = CreateProfileCommand(connection);
            command.CommandText = """
                SELECT
                    ci.id,
                    cu.unit_kind,
                    cu.display_number,
                    s.season_number,
                    p.position,
                    p.position_kind,
                    p.is_completed,
                    p.updated_at,
                    -- What is waiting past this row. Counted here rather than
                    -- in a second pass because the join that finds the unit
                    -- this progress sits on is the same one that dates it.
                    -- Specials carry their own numbering, so they would
                    -- announce a new chapter that is not the next one.
                    (SELECT COUNT(*)
                       FROM content_units later
                      WHERE later.catalog_item_id = ci.id
                        AND later.unit_kind = cu.unit_kind
                        AND later.is_special = 0
                        AND later.sort_order > cu.sort_order),
                    (SELECT newest.display_number
                       FROM content_units newest
                      WHERE newest.catalog_item_id = ci.id
                        AND newest.unit_kind = cu.unit_kind
                        AND newest.is_special = 0
                      ORDER BY newest.sort_order DESC
                      LIMIT 1)
                FROM playback_progress p
                LEFT JOIN content_units cu ON cu.id = p.content_unit_id
                LEFT JOIN seasons s ON s.id = cu.season_id
                JOIN catalog_items ci
                  ON ci.id = COALESCE(
                      p.catalog_item_id,
                      cu.catalog_item_id)
                ORDER BY p.updated_at DESC, p.id DESC;
                """;
            var result = new List<CatalogProgressSummary>();
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                var itemId = Guid.Parse(reader.GetString(0));
                ContentUnitKind? unitKind = reader.IsDBNull(1)
                    ? null
                    : ParseUnitKind(reader.GetString(1));
                var displayNumber = reader.IsDBNull(2)
                    ? null
                    : reader.GetString(2);
                int? seasonNumber = reader.IsDBNull(3)
                    ? null
                    : reader.GetInt32(3);
                var itemKey = $"item:{itemId:N}";
                var stableKey = unitKind is null
                    ? itemKey
                    : $"{itemKey}/season:" +
                      $"{seasonNumber?.ToString(
                          CultureInfo.InvariantCulture) ?? "none"}" +
                      $"/unit:{unitKind.Value.ToString().ToLowerInvariant()}:" +
                      NameNormalizer.Normalize(displayNumber!);
                result.Add(new CatalogProgressSummary(
                    new CatalogContentIdentity(
                        itemId,
                        stableKey,
                        unitKind,
                        displayNumber,
                        seasonNumber),
                    new MediaProgressState(
                        reader.GetDouble(4),
                        ParsePositionKind(reader.GetString(5)),
                        reader.GetInt32(6) != 0,
                        DateTimeOffset.Parse(
                            reader.GetString(7),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.RoundtripKind)),
                    reader.IsDBNull(8) ? 0 : reader.GetInt32(8),
                    reader.IsDBNull(9) ? null : reader.GetString(9)));
            }

            return result;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private const string ReaderZoomKey = "reader_zoom_factor";
    private const string ChapterOrderKey = "reader_chapter_order_descending";
    private const string ReaderDisplayModeKey = "reader_display_mode";

    /// <summary>
    /// Stored in application settings rather than the synced reader
    /// preferences, so each device keeps its own zoom and sort order.
    /// </summary>
    public async ValueTask<ReaderDevicePreferences>
        LoadReaderDevicePreferencesAsync(
            CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = CreateProfileCommand(connection);
            command.CommandText = """
                SELECT key, value
                FROM application_settings
                WHERE key IN ($zoom, $order, $mode);
                """;
            command.Parameters.AddWithValue("$zoom", ReaderZoomKey);
            command.Parameters.AddWithValue("$order", ChapterOrderKey);
            command.Parameters.AddWithValue("$mode", ReaderDisplayModeKey);
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var reader =
                         await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var zoom = ReaderDevicePreferences.Default.ZoomFactor;
            if (values.TryGetValue(ReaderZoomKey, out var zoomText) &&
                double.TryParse(
                    zoomText,
                    System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out var parsedZoom))
            {
                zoom = parsedZoom;
            }

            return new ReaderDevicePreferences(
                zoom,
                string.Equals(
                    values.GetValueOrDefault(ChapterOrderKey),
                    "true",
                    StringComparison.OrdinalIgnoreCase),
                Enum.TryParse<ReaderDisplayMode>(
                    values.GetValueOrDefault(ReaderDisplayModeKey),
                    ignoreCase: true,
                    out var storedMode)
                    ? storedMode
                    : ReaderDevicePreferences.Default.DisplayMode).Normalize();
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SaveReaderDevicePreferencesAsync(
        ReaderDevicePreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        var normalized = preferences.Normalize();
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            var values = new[]
            {
                (
                    ReaderZoomKey,
                    normalized.ZoomFactor.ToString(
                        System.Globalization.CultureInfo.InvariantCulture)),
                (
                    ChapterOrderKey,
                    normalized.ChapterOrderDescending ? "true" : "false"),
                (
                    ReaderDisplayModeKey,
                    normalized.DisplayMode.ToString())
            };
            foreach (var (key, value) in values)
            {
                await using var command = CreateProfileCommand(connection);
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO application_settings (key, value, updated_at)
                    VALUES ($key, $value, $updated_at)
                    ON CONFLICT (key)
                    DO UPDATE SET
                        value = excluded.value,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue("$key", key);
                command.Parameters.AddWithValue("$value", value);
                command.Parameters.AddWithValue(
                    "$updated_at",
                    DateTimeOffset.UtcNow.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<ReaderDisplayMode> LoadReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        CancellationToken cancellationToken = default)
    {
        EnsureCatalogItem(catalogItem);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = CreateProfileCommand(connection);
            command.CommandText = "SELECT display_mode FROM reader_preferences WHERE profile_id = $profile AND catalog_item_id = $item_id;";
            command.Parameters.AddWithValue("$item_id", catalogItem.CatalogItemId.ToString("D"));
            var value = Convert.ToString(await command.ExecuteScalarAsync(cancellationToken));
            return string.Equals(value, "continuous", StringComparison.Ordinal)
                ? ReaderDisplayMode.Continuous
                : ReaderDisplayMode.Paged;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SaveReaderDisplayModeAsync(
        CatalogContentIdentity catalogItem,
        ReaderDisplayMode displayMode,
        CancellationToken cancellationToken = default)
    {
        EnsureCatalogItem(catalogItem);
        if (!Enum.IsDefined(displayMode))
        {
            throw new ArgumentOutOfRangeException(nameof(displayMode));
        }

        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var syncKey = await CreateItemSyncKeyAsync(
                connection,
                catalogItem.CatalogItemId,
                cancellationToken);
            var now = DateTimeOffset.UtcNow;
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            await using (var command = CreateProfileCommand(connection))
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO reader_preferences (
                        profile_id,
                        catalog_item_id,
                        display_mode,
                        updated_at)
                    VALUES ($profile, $item_id, $display_mode, $updated_at)
                    ON CONFLICT(profile_id, catalog_item_id) DO UPDATE SET
                        display_mode = excluded.display_mode,
                        updated_at = excluded.updated_at;
                    """;
                command.Parameters.AddWithValue(
                    "$item_id",
                    catalogItem.CatalogItemId.ToString("D"));
                command.Parameters.AddWithValue(
                    "$display_mode",
                    displayMode == ReaderDisplayMode.Continuous
                        ? "continuous"
                        : "paged");
                command.Parameters.AddWithValue(
                    "$updated_at",
                    now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            var version =
                await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                    connection,
                    transaction,
                    now,
                    cancellationToken);
            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ReaderPreferenceKind,
                syncKey,
                version,
                isDeleted: false,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<IReadOnlySet<string>> LoadFavoriteContributorKeysAsync(
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = CreateProfileCommand(connection);
            command.CommandText = """
                SELECT contributor_key
                FROM favorite_contributors
                WHERE profile_id = $profile
                ORDER BY added_at, contributor_key;
                """;

            var keys = new HashSet<string>(StringComparer.Ordinal);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                keys.Add(reader.GetString(0));
            }

            return keys;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SetFavoriteContributorAsync(
        string contributorKey,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contributorKey);
        var normalizedKey = contributorKey.Trim();

        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            var now = DateTimeOffset.UtcNow;
            int changed;
            await using (var command = CreateProfileCommand(connection))
            {
                command.Transaction = transaction;
                command.CommandText = isFavorite
                    ? """
                      INSERT INTO favorite_contributors (
                          profile_id,
                          contributor_key,
                          added_at)
                      VALUES ($profile, $contributor_key, $added_at)
                      ON CONFLICT(profile_id, contributor_key) DO NOTHING;
                      """
                    : """
                      DELETE FROM favorite_contributors
                      WHERE contributor_key = $contributor_key;
                      """;
                command.Parameters.AddWithValue(
                    "$contributor_key",
                    normalizedKey);
                if (isFavorite)
                {
                    command.Parameters.AddWithValue(
                        "$added_at",
                        now.ToString("O"));
                }

                changed =
                    await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (changed > 0)
            {
                var version =
                    await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                        connection,
                        transaction,
                        now,
                        cancellationToken);
                await SqliteUserSyncMetadata.UpsertAsync(
                    database.ActiveProfileId,
                    connection,
                    transaction,
                    SqliteUserSyncMetadata.FavoriteKind,
                    normalizedKey,
                    version,
                    isDeleted: !isFavorite,
                    itemMediaType: null,
                    itemName: null,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<IReadOnlySet<Guid>>
        LoadFavoriteCatalogItemIdsAsync(
            CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = CreateProfileCommand(connection);
            command.CommandText = """
                SELECT catalog_item_id
                FROM library_entries
                WHERE profile_id = $profile AND is_favorite = 1
                ORDER BY added_at, catalog_item_id;
                """;
            var result = new HashSet<Guid>();
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                result.Add(Guid.Parse(reader.GetString(0)));
            }

            return result;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SetFavoriteCatalogItemAsync(
        Guid catalogItemId,
        bool isFavorite,
        CancellationToken cancellationToken = default)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A favorite requires a catalog item ID.",
                nameof(catalogItemId));
        }

        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            var identity = await ReadItemSyncIdentityAsync(
                connection,
                catalogItemId,
                cancellationToken);
            var syncKey =
                UserSyncDocumentValidator.ValidateItemIdentity(identity);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            var now = DateTimeOffset.UtcNow;
            int changed;
            await using (var command = CreateProfileCommand(connection))
            {
                command.Transaction = transaction;
                command.CommandText = isFavorite
                    ? """
                      INSERT INTO library_entries (
                          profile_id,
                          catalog_item_id,
                          is_favorite,
                          added_at)
                      VALUES (
                          $profile,
                          $catalog_item_id,
                          1,
                          $added_at)
                      ON CONFLICT(profile_id, catalog_item_id) DO UPDATE SET
                          is_favorite = 1
                      WHERE library_entries.is_favorite = 0;
                      """
                    : """
                      UPDATE library_entries
                      SET is_favorite = 0
                      WHERE profile_id = $profile
                        AND catalog_item_id = $catalog_item_id
                        AND is_favorite = 1;
                      """;
                command.Parameters.AddWithValue(
                    "$catalog_item_id",
                    catalogItemId.ToString("D"));
                if (isFavorite)
                {
                    command.Parameters.AddWithValue(
                        "$added_at",
                        now.ToString("O"));
                }

                changed =
                    await command.ExecuteNonQueryAsync(cancellationToken);
            }

            if (changed > 0)
            {
                var version =
                    await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                        connection,
                        transaction,
                        now,
                        cancellationToken);
                await SqliteUserSyncMetadata.UpsertAsync(
                    database.ActiveProfileId,
                    connection,
                    transaction,
                    SqliteUserSyncMetadata.FavoriteItemKind,
                    syncKey,
                    version,
                    isDeleted: !isFavorite,
                    identity.MediaType,
                    identity.Name,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<UserMediaList>> LoadUserListsAsync(
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            return await ReadUserListsAsync(
                connection,
                listId: null,
                cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<UserMediaList> CreateUserListAsync(
        string name,
        CancellationToken cancellationToken = default)
    {
        var normalizedName = UserMediaList.NormalizeName(name);
        var nameKey = UserMediaList.GetNameKey(normalizedName);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using (var count = CreateProfileCommand(connection))
            {
                count.CommandText = "SELECT COUNT(*) FROM user_media_lists WHERE profile_id = $profile;";
                var listCount = Convert.ToInt32(
                    await count.ExecuteScalarAsync(cancellationToken),
                    CultureInfo.InvariantCulture);
                if (listCount >= UserMediaList.MaximumListCount)
                {
                    throw new InvalidOperationException(
                        $"No more than {UserMediaList.MaximumListCount} lists may be created.");
                }
            }

            var id = Guid.CreateVersion7();
            var now = DateTimeOffset.UtcNow;
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            try
            {
                await using var command = CreateProfileCommand(connection);
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO user_media_lists (
                        profile_id,
                        id, name, name_key, created_at, updated_at)
                    VALUES (
                        $profile,
                        $id, $name, $name_key, $created_at, $updated_at);
                    """;
                command.Parameters.AddWithValue("$id", id.ToString("D"));
                command.Parameters.AddWithValue("$name", normalizedName);
                command.Parameters.AddWithValue("$name_key", nameKey);
                command.Parameters.AddWithValue(
                    "$created_at",
                    now.ToString("O"));
                command.Parameters.AddWithValue(
                    "$updated_at",
                    now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException(
                    "A list with this name already exists.",
                    exception);
            }

            var version =
                await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                    connection,
                    transaction,
                    now,
                    cancellationToken);
            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ListKind,
                SqliteUserSyncMetadata.GetListKey(id),
                version,
                isDeleted: false,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UserMediaList(
                id,
                normalizedName,
                [],
                now,
                now);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask<UserMediaList> RenameUserListAsync(
        Guid listId,
        string name,
        CancellationToken cancellationToken = default)
    {
        EnsureListId(listId);
        var normalizedName = UserMediaList.NormalizeName(name);
        var nameKey = UserMediaList.GetNameKey(normalizedName);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            var current = (await ReadUserListsAsync(
                    connection,
                    listId,
                    cancellationToken))
                .SingleOrDefault() ??
                throw new KeyNotFoundException(
                    $"User media list '{listId:D}' was not found.");
            var now = DateTimeOffset.UtcNow;
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            try
            {
                await using var command = CreateProfileCommand(connection);
                command.Transaction = transaction;
                command.CommandText = """
                    UPDATE user_media_lists
                    SET name = $name,
                        name_key = $name_key,
                        updated_at = $updated_at
                    WHERE profile_id = $profile AND id = $id;
                    """;
                command.Parameters.AddWithValue(
                    "$id",
                    listId.ToString("D"));
                command.Parameters.AddWithValue("$name", normalizedName);
                command.Parameters.AddWithValue("$name_key", nameKey);
                command.Parameters.AddWithValue(
                    "$updated_at",
                    now.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
            catch (SqliteException exception)
                when (exception.SqliteErrorCode == 19)
            {
                throw new InvalidOperationException(
                    "A list with this name already exists.",
                    exception);
            }

            var version =
                await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                    connection,
                    transaction,
                    now,
                    cancellationToken);
            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ListKind,
                SqliteUserSyncMetadata.GetListKey(listId),
                version,
                isDeleted: false,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
            return new UserMediaList(
                listId,
                normalizedName,
                current.CatalogItemIds,
                current.CreatedAt,
                now);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask DeleteUserListAsync(
        Guid listId,
        CancellationToken cancellationToken = default)
    {
        EnsureListId(listId);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            await using (var command = CreateProfileCommand(connection))
            {
                command.Transaction = transaction;
                command.CommandText =
                    "DELETE FROM user_media_lists WHERE profile_id = $profile AND id = $id;";
                command.Parameters.AddWithValue(
                    "$id",
                    listId.ToString("D"));
                if (await command.ExecuteNonQueryAsync(
                        cancellationToken) == 0)
                {
                    throw new KeyNotFoundException(
                        $"User media list '{listId:D}' was not found.");
                }
            }

            var now = DateTimeOffset.UtcNow;
            var version =
                await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                    connection,
                    transaction,
                    now,
                    cancellationToken);
            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ListKind,
                SqliteUserSyncMetadata.GetListKey(listId),
                version,
                isDeleted: true,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SetUserListMembershipAsync(
        Guid listId,
        Guid catalogItemId,
        bool isMember,
        CancellationToken cancellationToken = default)
    {
        EnsureListId(listId);
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "List membership requires a catalog item ID.",
                nameof(catalogItemId));
        }

        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await EnsureListAndItemExistAsync(
                connection,
                listId,
                catalogItemId,
                cancellationToken);
            var itemIdentity = await ReadItemSyncIdentityAsync(
                connection,
                catalogItemId,
                cancellationToken);
            var itemKey =
                UserSyncDocumentValidator.ValidateItemIdentity(
                    itemIdentity);
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            var now = DateTimeOffset.UtcNow;
            int changed;
            await using (var membership = CreateProfileCommand(connection))
            {
                membership.Transaction = transaction;
                membership.CommandText = isMember
                    ? """
                      INSERT INTO user_media_list_items (
                          list_id, catalog_item_id, added_at)
                      VALUES (
                          $list_id, $catalog_item_id, $added_at)
                      ON CONFLICT(list_id, catalog_item_id) DO NOTHING;
                      """
                    : """
                      DELETE FROM user_media_list_items
                      WHERE list_id = $list_id
                        AND catalog_item_id = $catalog_item_id;
                      """;
                membership.Parameters.AddWithValue(
                    "$list_id",
                    listId.ToString("D"));
                membership.Parameters.AddWithValue(
                    "$catalog_item_id",
                    catalogItemId.ToString("D"));
                if (isMember)
                {
                    membership.Parameters.AddWithValue(
                        "$added_at",
                        now.ToString("O"));
                }

                changed = await membership.ExecuteNonQueryAsync(
                    cancellationToken);
            }

            if (changed > 0)
            {
                await using var update = CreateProfileCommand(connection);
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE user_media_lists
                    SET updated_at = $updated_at
                    WHERE profile_id = $profile AND id = $list_id;
                    """;
                update.Parameters.AddWithValue(
                    "$list_id",
                    listId.ToString("D"));
                update.Parameters.AddWithValue(
                    "$updated_at",
                    now.ToString("O"));
                await update.ExecuteNonQueryAsync(cancellationToken);

                var version =
                    await SqliteUserSyncMetadata.CreateLocalVersionAsync(
                        connection,
                        transaction,
                        now,
                        cancellationToken);
                await SqliteUserSyncMetadata.UpsertAsync(
                    database.ActiveProfileId,
                    connection,
                    transaction,
                    SqliteUserSyncMetadata.ListKind,
                    SqliteUserSyncMetadata.GetListKey(listId),
                    version,
                    isDeleted: false,
                    itemMediaType: null,
                    itemName: null,
                    cancellationToken);
                await SqliteUserSyncMetadata.UpsertAsync(
                    database.ActiveProfileId,
                    connection,
                    transaction,
                    SqliteUserSyncMetadata.ListItemKind,
                    SqliteUserSyncMetadata.GetListItemKey(
                        listId,
                        itemKey),
                    version,
                    isDeleted: !isMember,
                    itemIdentity.MediaType,
                    itemIdentity.Name,
                    cancellationToken);
            }

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private async ValueTask<IReadOnlyList<UserMediaList>>
        ReadUserListsAsync(
            SqliteConnection connection,
            Guid? listId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT
                l.id,
                l.name,
                l.created_at,
                l.updated_at,
                li.catalog_item_id
            FROM user_media_lists l
            LEFT JOIN user_media_list_items li ON li.list_id = l.id
            WHERE l.profile_id = $profile
              AND ($list_id IS NULL OR l.id = $list_id)
            ORDER BY l.created_at, l.name_key, li.added_at, li.catalog_item_id;
            """;
        command.Parameters.AddWithValue(
            "$list_id",
            listId is null
                ? DBNull.Value
                : listId.Value.ToString("D"));
        var builders = new Dictionary<Guid, UserMediaListBuilder>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = Guid.Parse(reader.GetString(0));
            if (!builders.TryGetValue(id, out var builder))
            {
                builder = new UserMediaListBuilder(
                    id,
                    reader.GetString(1),
                    DateTimeOffset.Parse(
                        reader.GetString(2),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind),
                    DateTimeOffset.Parse(
                        reader.GetString(3),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind));
                builders.Add(id, builder);
            }

            if (!reader.IsDBNull(4))
            {
                builder.CatalogItemIds.Add(
                    Guid.Parse(reader.GetString(4)));
            }
        }

        return builders.Values
            .Select(static builder => builder.Build())
            .ToArray();
    }

    private async ValueTask EnsureListAndItemExistAsync(
        SqliteConnection connection,
        Guid listId,
        Guid catalogItemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT
                EXISTS(
                    SELECT 1 FROM user_media_lists WHERE id = $list_id),
                EXISTS(
                    SELECT 1 FROM catalog_items WHERE id = $catalog_item_id);
            """;
        command.Parameters.AddWithValue("$list_id", listId.ToString("D"));
        command.Parameters.AddWithValue(
            "$catalog_item_id",
            catalogItemId.ToString("D"));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        _ = await reader.ReadAsync(cancellationToken);
        if (reader.GetInt32(0) == 0)
        {
            throw new KeyNotFoundException(
                $"User media list '{listId:D}' was not found.");
        }

        if (reader.GetInt32(1) == 0)
        {
            throw new KeyNotFoundException(
                $"Catalog item '{catalogItemId:D}' was not found.");
        }
    }

    private async ValueTask<string?> ResolveUnitIdAsync(
        SqliteConnection connection,
        CatalogContentIdentity content,
        CancellationToken cancellationToken)
    {
        if (!content.IsUnit)
        {
            return null;
        }

        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT cu.id
            FROM content_units cu
            LEFT JOIN seasons s ON s.id = cu.season_id
            WHERE cu.catalog_item_id = $item_id
              AND cu.unit_kind = $unit_kind
              AND cu.display_number_key = $display_number_key
              AND (($season_number IS NULL AND cu.season_id IS NULL) OR s.season_number = $season_number)
            LIMIT 1;
            """;
        command.Parameters.AddWithValue("$item_id", content.CatalogItemId.ToString("D"));
        command.Parameters.AddWithValue("$unit_kind", UnitKindToDatabase(content.UnitKind!.Value));
        command.Parameters.AddWithValue("$display_number_key", NameNormalizer.Normalize(content.DisplayNumber!));
        command.Parameters.AddWithValue("$season_number", (object?)content.SeasonNumber ?? DBNull.Value);
        var value = await command.ExecuteScalarAsync(cancellationToken);
        return value is null or DBNull ? null : Convert.ToString(value, CultureInfo.InvariantCulture);
    }

    private async ValueTask<string> CreateContentSyncKeyAsync(
        SqliteConnection connection,
        CatalogContentIdentity content,
        CancellationToken cancellationToken)
    {
        var item = await ReadItemSyncIdentityAsync(
            connection,
            content.CatalogItemId,
            cancellationToken);
        return UserSyncDocumentValidator.ValidateContentIdentity(
            new UserSyncContentIdentity
            {
                Item = item,
                UnitKind = content.UnitKind is null
                    ? null
                    : UserSyncDocumentValidator.UnitKindToValue(
                        content.UnitKind.Value),
                DisplayNumber = content.DisplayNumber,
                SeasonNumber = content.SeasonNumber
            });
    }

    private async ValueTask<string> CreateItemSyncKeyAsync(
        SqliteConnection connection,
        Guid catalogItemId,
        CancellationToken cancellationToken) =>
        UserSyncDocumentValidator.ValidateItemIdentity(
            await ReadItemSyncIdentityAsync(
                connection,
                catalogItemId,
                cancellationToken));

    private async ValueTask<UserSyncCatalogItemIdentity>
        ReadItemSyncIdentityAsync(
            SqliteConnection connection,
            Guid catalogItemId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT media_type, name, subtype_code
            FROM catalog_items
            WHERE id = $id;
            """;
        command.Parameters.AddWithValue(
            "$id",
            catalogItemId.ToString("D"));
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            throw new KeyNotFoundException(
                $"Catalog item '{catalogItemId:D}' was not found.");
        }

        return new UserSyncCatalogItemIdentity
        {
            MediaType = reader.GetString(0),
            Subtype = reader.GetString(2),
            Name = reader.GetString(1)
        };
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqliteCatalogDatabase.ConfigureConnectionAsync(connection, cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private static string UnitKindToDatabase(ContentUnitKind kind) => kind switch
    {
        ContentUnitKind.Episode => "episode",
        ContentUnitKind.Chapter => "chapter",
        ContentUnitKind.Track => "track",
        ContentUnitKind.AudioChapter => "audio_chapter",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static string PositionKindToDatabase(MediaPositionKind kind) => kind switch
    {
        MediaPositionKind.Seconds => "seconds",
        MediaPositionKind.Page => "page",
        _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, null)
    };

    private static MediaPositionKind ParsePositionKind(string value) => value switch
    {
        "seconds" => MediaPositionKind.Seconds,
        "page" => MediaPositionKind.Page,
        _ => throw new InvalidDataException($"Unknown playback position kind '{value}'.")
    };

    private static ContentUnitKind ParseUnitKind(string value) => value switch
    {
        "episode" => ContentUnitKind.Episode,
        "chapter" => ContentUnitKind.Chapter,
        "track" => ContentUnitKind.Track,
        "audio_chapter" => ContentUnitKind.AudioChapter,
        _ => throw new InvalidDataException(
            $"Unknown content unit kind '{value}'.")
    };

    private static void EnsureCatalogItem(CatalogContentIdentity content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.IsUnit)
        {
            throw new ArgumentException("Reader display mode belongs to a catalog item.", nameof(content));
        }
    }

    private static void EnsureListId(Guid listId)
    {
        if (listId == Guid.Empty)
        {
            throw new ArgumentException(
                "A user media list requires an ID.",
                nameof(listId));
        }
    }

    private sealed class UserMediaListBuilder(
        Guid id,
        string name,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt)
    {
        public HashSet<Guid> CatalogItemIds { get; } = [];

        public UserMediaList Build() =>
            new(
                id,
                name,
                CatalogItemIds,
                createdAt,
                updatedAt);
    }
}

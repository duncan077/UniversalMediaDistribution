using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed partial class SqliteUserSyncStore(
    SqliteCatalogDatabase database,
    SqliteCatalogRepository catalogRepository) : IUserSyncStore
{
    public async ValueTask<UserSyncDocument> ExportAsync(
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken = default)
    {
        UserSyncDocumentValidator.ValidateDeviceId(deviceId);
        UserSyncDocumentValidator.ValidateDeviceName(deviceName);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            return await BuildDocumentAsync(
                connection,
                deviceId.Trim(),
                deviceName.Trim(),
                cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    /// <summary>
    /// Exports only the positions belonging to the named titles, with those
    /// titles' catalog entries alongside.
    /// <para>
    /// Built through the same readers a full export uses, deliberately. Each
    /// record carries a version whose clock comes from when it was saved, and
    /// the server keeps whichever version is higher — so a hand-assembled patch
    /// with versions of its own would quietly lose the merge and appear to do
    /// nothing.
    /// </para>
    /// <para>
    /// Progress rows are read whole and filtered in memory. They came to 8% of
    /// a measured snapshot against the catalog's 92%, so narrowing the catalog
    /// is what saves the payload; narrowing the rows as well would mean a
    /// second query to keep in step with the first for very little.
    /// </para>
    /// </summary>
    public async ValueTask<UserSyncDocument> ExportProgressPatchAsync(
        string deviceId,
        string deviceName,
        IReadOnlyCollection<Guid> catalogItemIds,
        CancellationToken cancellationToken = default)
    {
        UserSyncDocumentValidator.ValidateDeviceId(deviceId);
        UserSyncDocumentValidator.ValidateDeviceName(deviceName);
        ArgumentNullException.ThrowIfNull(catalogItemIds);
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            var identifiers = catalogItemIds
                .Where(static id => id != Guid.Empty)
                .Select(static id => id.ToString())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var catalog = await SqliteCatalogReader.ExportItemsAsync(
                connection,
                identifiers,
                cancellationToken);
            var titles = catalog.Items.Select(item =>
                CreateTitleKey(item.MediaType, item.Name, item.Subtype)).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var metadata = await SqliteUserSyncMetadata.ReadAllAsync(
                database.ActiveProfileId,
                connection,
                cancellationToken);
            var progress = await ReadProgressAsync(
                connection,
                metadata,
                deviceId.Trim(),
                cancellationToken);
            return new UserSyncDocument
            {
                ExportedAt = DateTimeOffset.UtcNow,
                DeviceId = deviceId.Trim(),
                DeviceName = deviceName.Trim(),
                Catalog = catalog,
                Progress = progress
                    .Where(entry => titles.Contains(
                        CreateTitleKey(
                            entry.Content.Item.MediaType,
                            entry.Content.Item.Name, entry.Content.Item.Subtype)))
                    .ToArray()
            };
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private static string CreateTitleKey(string mediaType, string name, string? subtype) =>
        UserSyncDocumentValidator.CreateItemKey(CatalogSubtypeResolver.ParseMediaType(mediaType), name, subtype);

    public async ValueTask<UserSyncRestoreResult> RestoreAsync(
        UserSyncDocument document,
        CancellationToken cancellationToken = default)
    {
        var remote = UserSyncDocumentMerger.Normalize(document);
        UserSyncDocument localBeforeImport;
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            var device = await SqliteUserSyncMetadata.GetOrCreateDeviceAsync(
                connection, transaction: null, cancellationToken);
            // Capture versions under their original names before an import
            // folds rows or renames them. Their state must follow the identity.
            localBeforeImport = await BuildDocumentAsync(
                connection, device.DeviceId, device.DeviceName, cancellationToken);
            var changedKeys = UserSyncDocumentDelta.ComputeChangedKeys(localBeforeImport, remote);
            if (changedKeys.Count == 0)
            {
                return new UserSyncRestoreResult(0, 0,
                    localBeforeImport.Progress.Count, localBeforeImport.ReaderPreferences.Count,
                    localBeforeImport.FavoriteStates.Count(state => state.IsFavorite),
                    localBeforeImport.Lists.Count(list => !list.IsDeleted));
            }
            if (changedKeys.All(IsSimpleStateKey) &&
                await TryRestoreStateChangesAsync(connection, localBeforeImport, remote, cancellationToken) is { } restored)
            {
                return restored;
            }
        }
        finally
        {
            database.OperationGate.Release();
        }

        var catalogMerge = CatalogTransferDocumentMerger.MergeWithRedirects(
            localBeforeImport.Catalog,
            remote.Catalog);
        // The merger reuses untouched local items and creates a new record
        // for every folded group (including identity redirects). Only those
        // groups need importing; replaying the rest rewrites all their units
        // and metadata even when the server sent just one changed chapter.
        var originalItems = new HashSet<CatalogTransferItem>(
            localBeforeImport.Catalog.Items, ReferenceEqualityComparer.Instance);
        var changedCatalog = catalogMerge.Document with
        {
            Items = catalogMerge.Document.Items.Where(item => !originalItems.Contains(item)).ToArray()
        };
        var catalogImport = changedCatalog.Items.Count == 0
            ? new CatalogImportResult(0, 0, 0, [])
            : await catalogRepository.MergeCatalogAsync(
                changedCatalog,
                $"Device sync from {remote.DeviceName}",
                documentHash: null,
                cancellationToken);

        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            var localDevice =
                await SqliteUserSyncMetadata.GetOrCreateDeviceAsync(
                    connection,
                    transaction: null,
                    cancellationToken);
            var local = await BuildDocumentAsync(
                connection,
                localDevice.DeviceId,
                localDevice.DeviceName,
                cancellationToken);
            local = UserSyncDocumentMerger.Merge(
                localBeforeImport,
                local,
                local.Catalog,
                localDevice.DeviceId,
                localDevice.DeviceName,
                additionalRedirects: catalogMerge.Redirects);
            var merged = UserSyncDocumentMerger.Merge(
                local,
                remote,
                local.Catalog,
                localDevice.DeviceId,
                localDevice.DeviceName,
                additionalRedirects: catalogMerge.Redirects);

            var itemIds = await ReadItemIdsAsync(
                connection,
                cancellationToken);
            var unitIds = await ReadUnitIdsAsync(
                connection,
                cancellationToken);
            var progress = ResolveProgress(
                merged.Progress,
                itemIds,
                unitIds);
            var readerPreferences = ResolveReaderPreferences(
                merged.ReaderPreferences,
                itemIds);
            var favoriteItems = ResolveFavoriteItems(
                merged.FavoriteItemStates,
                itemIds);
            var lists = ResolveLists(merged.Lists, itemIds);

            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            await DeleteSyncedStateAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                cancellationToken);
            await InsertProgressAsync(
                connection,
                transaction,
                progress,
                cancellationToken);
            await InsertReaderPreferencesAsync(
                connection,
                transaction,
                readerPreferences,
                cancellationToken);
            await InsertFavoriteStatesAsync(
                connection,
                transaction,
                merged.FavoriteStates,
                cancellationToken);
            await InsertFavoriteItemStatesAsync(
                connection,
                transaction,
                favoriteItems,
                cancellationToken);
            await InsertListsAsync(
                connection,
                transaction,
                lists,
                cancellationToken);
            await SqliteUserSyncMetadata.ObserveClockAsync(
                connection,
                transaction,
                GetMaximumClock(merged),
                cancellationToken);
            await SqliteCatalogRedirects.UpsertAllAsync(
                connection,
                transaction,
                merged.CatalogRedirects,
                cancellationToken);
            await transaction.CommitAsync(cancellationToken);

            return new UserSyncRestoreResult(
                catalogImport.CreatedCount,
                catalogImport.UpdatedCount,
                progress.Count,
                readerPreferences.Count,
                merged.FavoriteStates.Count(
                    static state => state.IsFavorite),
                lists.Count(static list => !list.List.IsDeleted));
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private async ValueTask<UserSyncDocument> BuildDocumentAsync(
        SqliteConnection connection,
        string deviceId,
        string deviceName,
        CancellationToken cancellationToken)
    {
        var metadata = await SqliteUserSyncMetadata.ReadAllAsync(
            database.ActiveProfileId,
            connection,
            cancellationToken);
        var document = new UserSyncDocument
        {
            ExportedAt = DateTimeOffset.UtcNow,
            DeviceId = deviceId,
            DeviceName = deviceName,
            Catalog = await SqliteCatalogReader.ExportAsync(
                connection,
                cancellationToken),
            Progress = await ReadProgressAsync(
                connection,
                metadata,
                deviceId,
                cancellationToken),
            ReaderPreferences = await ReadReaderPreferencesAsync(
                connection,
                metadata,
                deviceId,
                cancellationToken),
            FavoriteStates = await ReadFavoriteStatesAsync(
                connection,
                metadata,
                deviceId,
                cancellationToken),
            FavoriteItemStates = await ReadFavoriteItemStatesAsync(
                connection,
                metadata,
                deviceId,
                cancellationToken),
            Lists = await ReadListsAsync(
                connection,
                metadata,
                deviceId,
                cancellationToken),
            CatalogRedirects = await SqliteCatalogRedirects.ReadAllAsync(
                connection,
                cancellationToken)
        };
        return UserSyncDocumentMerger.Normalize(document);
    }

    private async ValueTask<IReadOnlyList<UserSyncProgressEntry>>
        ReadProgressAsync(
            SqliteConnection connection,
            IReadOnlyDictionary<string, SqliteUserSyncRecord> metadata,
            string fallbackDeviceId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT
                ci.media_type,
                ci.name,
                cu.unit_kind,
                cu.display_number,
                s.season_number,
                p.position,
                p.position_kind,
                p.is_completed,
                p.updated_at,
                ci.subtype_code
            FROM playback_progress p
            LEFT JOIN content_units cu ON cu.id = p.content_unit_id
            LEFT JOIN seasons s ON s.id = cu.season_id
            JOIN catalog_items ci
              ON ci.id = COALESCE(p.catalog_item_id, cu.catalog_item_id)
            WHERE p.profile_id = $profile
            ORDER BY p.updated_at, ci.media_type, ci.name;
            """;
        var entries = new List<UserSyncProgressEntry>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = CreateContentIdentity(reader);
            var key =
                UserSyncDocumentValidator.ValidateContentIdentity(identity);
            var updatedAt = ParseTimestamp(reader.GetString(8));
            entries.Add(new UserSyncProgressEntry
            {
                Content = identity,
                Position = reader.GetDouble(5),
                PositionKind = reader.GetString(6),
                IsCompleted = reader.GetInt32(7) != 0,
                UpdatedAt = updatedAt,
                SyncVersion = GetVersion(
                    metadata,
                    SqliteUserSyncMetadata.ProgressKind,
                    key,
                    fallbackDeviceId,
                    updatedAt,
                    "progress:" + key,
                    expectedDeleted: false)
            });
        }

        return entries;
    }

    private async ValueTask<
            IReadOnlyList<UserSyncReaderPreference>>
        ReadReaderPreferencesAsync(
            SqliteConnection connection,
            IReadOnlyDictionary<string, SqliteUserSyncRecord> metadata,
            string fallbackDeviceId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT
                ci.media_type,
                ci.name,
                rp.display_mode,
                rp.updated_at,
                ci.subtype_code
            FROM reader_preferences rp
            JOIN catalog_items ci ON ci.id = rp.catalog_item_id
            WHERE rp.profile_id = $profile
            ORDER BY ci.media_type, ci.name;
            """;
        var entries = new List<UserSyncReaderPreference>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = CreateItemIdentity(
                reader.GetString(0),
                reader.GetString(1), reader.GetString(4));
            var key =
                UserSyncDocumentValidator.ValidateItemIdentity(identity);
            var updatedAt = ParseTimestamp(reader.GetString(3));
            entries.Add(new UserSyncReaderPreference
            {
                Item = identity,
                DisplayMode = reader.GetString(2),
                UpdatedAt = updatedAt,
                SyncVersion = GetVersion(
                    metadata,
                    SqliteUserSyncMetadata.ReaderPreferenceKind,
                    key,
                    fallbackDeviceId,
                    updatedAt,
                    "reader:" + key,
                    expectedDeleted: false)
            });
        }

        return entries;
    }

    private async ValueTask<IReadOnlyList<UserSyncFavoriteState>>
        ReadFavoriteStatesAsync(
            SqliteConnection connection,
            IReadOnlyDictionary<string, SqliteUserSyncRecord> metadata,
            string fallbackDeviceId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT contributor_key, added_at
            FROM favorite_contributors
            WHERE profile_id = $profile
            ORDER BY contributor_key;
            """;
        var states = new Dictionary<string, UserSyncFavoriteState>(
            StringComparer.Ordinal);
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var key = reader.GetString(0);
                var addedAt = ParseTimestamp(reader.GetString(1));
                states.Add(key, new UserSyncFavoriteState
                {
                    ContributorKey = key,
                    IsFavorite = true,
                    SyncVersion = GetVersion(
                        metadata,
                        SqliteUserSyncMetadata.FavoriteKind,
                        key,
                        fallbackDeviceId,
                        addedAt,
                        "favorite:" + key,
                        expectedDeleted: false)
                });
            }
        }

        foreach (var record in metadata.Values.Where(static record =>
                     string.Equals(
                         record.Kind,
                         SqliteUserSyncMetadata.FavoriteKind,
                         StringComparison.Ordinal) &&
                     record.IsDeleted))
        {
            states[record.Key] = new UserSyncFavoriteState
            {
                ContributorKey = record.Key,
                IsFavorite = false,
                SyncVersion = record.Version
            };
        }

        return states.Values.ToArray();
    }

    private async ValueTask<
            IReadOnlyList<UserSyncFavoriteItemState>>
        ReadFavoriteItemStatesAsync(
            SqliteConnection connection,
            IReadOnlyDictionary<string, SqliteUserSyncRecord> metadata,
            string fallbackDeviceId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT ci.media_type, ci.name, le.added_at, ci.subtype_code
            FROM library_entries le
            JOIN catalog_items ci ON ci.id = le.catalog_item_id
            WHERE le.profile_id = $profile AND le.is_favorite = 1
            ORDER BY ci.media_type, ci.name;
            """;
        var states =
            new Dictionary<string, UserSyncFavoriteItemState>(
                StringComparer.Ordinal);
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var identity = CreateItemIdentity(
                    reader.GetString(0),
                    reader.GetString(1), reader.GetString(3));
                var key =
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        identity);
                var addedAt = ParseTimestamp(reader.GetString(2));
                states.Add(key, new UserSyncFavoriteItemState
                {
                    Item = identity,
                    IsFavorite = true,
                    SyncVersion = GetVersion(
                        metadata,
                        SqliteUserSyncMetadata.FavoriteItemKind,
                        key,
                        fallbackDeviceId,
                        addedAt,
                        "favorite-item:" + key,
                        expectedDeleted: false)
                });
            }
        }

        foreach (var record in metadata.Values.Where(static record =>
                     string.Equals(
                         record.Kind,
                         SqliteUserSyncMetadata.FavoriteItemKind,
                         StringComparison.Ordinal) &&
                     record.IsDeleted))
        {
            if (string.IsNullOrWhiteSpace(record.ItemMediaType) ||
                string.IsNullOrWhiteSpace(record.ItemName))
            {
                throw new InvalidDataException(
                    "A synchronized favorite-item tombstone is missing its catalog identity.");
            }

            states[record.Key] = new UserSyncFavoriteItemState
            {
                Item = CreateItemIdentity(
                    record.ItemMediaType,
                    record.ItemName, SubtypeFromRecordKey(record.Key)),
                IsFavorite = false,
                SyncVersion = record.Version
            };
        }

        return states.Values.ToArray();
    }

    private async ValueTask<IReadOnlyList<UserSyncMediaList>>
        ReadListsAsync(
            SqliteConnection connection,
            IReadOnlyDictionary<string, SqliteUserSyncRecord> metadata,
            string fallbackDeviceId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateProfileCommand(connection);
        command.CommandText = """
            SELECT
                l.id,
                l.name,
                l.created_at,
                l.updated_at,
                ci.media_type,
                ci.name,
                li.added_at,
                ci.subtype_code
            FROM user_media_lists l
            LEFT JOIN user_media_list_items li ON li.list_id = l.id
            LEFT JOIN catalog_items ci ON ci.id = li.catalog_item_id
            WHERE l.profile_id = $profile
            ORDER BY l.created_at, l.id, li.added_at, ci.name;
            """;
        var builders = new Dictionary<Guid, SyncListBuilder>();
        await using (var reader =
                     await command.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                var id = Guid.Parse(reader.GetString(0));
                if (!builders.TryGetValue(id, out var builder))
                {
                    var updatedAt = ParseTimestamp(reader.GetString(3));
                    var key = SqliteUserSyncMetadata.GetListKey(id);
                    builder = new SyncListBuilder(
                        id,
                        reader.GetString(1),
                        ParseTimestamp(reader.GetString(2)),
                        updatedAt,
                        isDeleted: false,
                        GetVersion(
                            metadata,
                            SqliteUserSyncMetadata.ListKind,
                            key,
                            fallbackDeviceId,
                            updatedAt,
                            "list:" + key,
                            expectedDeleted: false));
                    builders.Add(id, builder);
                }

                if (!reader.IsDBNull(4))
                {
                    var item = CreateItemIdentity(
                        reader.GetString(4),
                        reader.GetString(5), reader.GetString(7));
                    var itemKey =
                        UserSyncDocumentValidator.ValidateItemIdentity(
                            item);
                    var addedAt = ParseTimestamp(reader.GetString(6));
                    var recordKey =
                        SqliteUserSyncMetadata.GetListItemKey(
                            id,
                            itemKey);
                    builder.ItemStates[itemKey] =
                        new UserSyncListItemState
                        {
                            Item = item,
                            IsMember = true,
                            SyncVersion = GetVersion(
                                metadata,
                                SqliteUserSyncMetadata.ListItemKind,
                                recordKey,
                                fallbackDeviceId,
                                addedAt,
                                "list-item:" + recordKey,
                                expectedDeleted: false)
                        };
                }
            }
        }

        foreach (var record in metadata.Values.Where(static record =>
                     string.Equals(
                         record.Kind,
                         SqliteUserSyncMetadata.ListKind,
                         StringComparison.Ordinal) &&
                     record.IsDeleted))
        {
            var id = ParseListId(record.Key);
            if (!builders.ContainsKey(id))
            {
                builders.Add(
                    id,
                    new SyncListBuilder(
                        id,
                        string.Empty,
                        record.Version.ModifiedAt,
                        record.Version.ModifiedAt,
                        isDeleted: true,
                        record.Version));
            }
        }

        foreach (var record in metadata.Values.Where(static record =>
                     string.Equals(
                         record.Kind,
                         SqliteUserSyncMetadata.ListItemKind,
                         StringComparison.Ordinal)))
        {
            var (listId, _) = ParseListItemRecordKey(record.Key);
            if (!builders.TryGetValue(listId, out var builder))
            {
                continue;
            }

            if (string.IsNullOrWhiteSpace(record.ItemMediaType) ||
                string.IsNullOrWhiteSpace(record.ItemName))
            {
                throw new InvalidDataException(
                    "A local list-item tombstone has no portable identity.");
            }

            var item = CreateItemIdentity(
                record.ItemMediaType,
                record.ItemName, SubtypeFromRecordKey(record.Key));
            var itemKey =
                UserSyncDocumentValidator.ValidateItemIdentity(item);
            builder.ItemStates[itemKey] = new UserSyncListItemState
            {
                Item = item,
                IsMember = !record.IsDeleted,
                SyncVersion = record.Version
            };
        }

        return builders.Values
            .Select(static builder => builder.Build())
            .ToArray();
    }

    private static UserSyncVersion GetVersion(
        IReadOnlyDictionary<string, SqliteUserSyncRecord> metadata,
        string kind,
        string key,
        string fallbackDeviceId,
        DateTimeOffset fallbackTimestamp,
        string fallbackDiscriminator,
        bool expectedDeleted)
    {
        if (!metadata.TryGetValue(
                SqliteUserSyncMetadata.GetCompositeKey(kind, key),
                out var record))
        {
            return UserSyncDocumentMerger.CreateLegacyVersion(
                fallbackDeviceId,
                fallbackTimestamp,
                fallbackDiscriminator);
        }

        if (record.IsDeleted != expectedDeleted)
        {
            throw new InvalidDataException(
                $"Local sync metadata for '{kind}:{key}' disagrees with its stored state.");
        }

        return record.Version;
    }

    private static async ValueTask<Dictionary<string, string>>
        ReadItemIdsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT id, media_type, name, subtype_code FROM catalog_items;";
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var mediaType = CatalogSubtypeResolver.ParseMediaType(
                reader.GetString(1));
            result.Add(
                UserSyncDocumentValidator.CreateItemKey(
                    mediaType,
                    reader.GetString(2), reader.GetString(3)),
                reader.GetString(0));
        }

        return result;
    }

    private static async ValueTask<Dictionary<string, string>>
        ReadUnitIdsAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                cu.id,
                ci.media_type,
                ci.name,
                cu.unit_kind,
                cu.display_number,
                s.season_number,
                ci.subtype_code
            FROM content_units cu
            JOIN catalog_items ci ON ci.id = cu.catalog_item_id
            LEFT JOIN seasons s ON s.id = cu.season_id;
            """;
        var result = new Dictionary<string, string>(StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var identity = new UserSyncContentIdentity
            {
                Item = CreateItemIdentity(
                    reader.GetString(1),
                    reader.GetString(2), reader.GetString(6)),
                UnitKind = reader.GetString(3),
                DisplayNumber = reader.GetString(4),
                SeasonNumber = reader.IsDBNull(5)
                    ? null
                    : reader.GetInt32(5)
            };
            result.Add(
                UserSyncDocumentValidator.ValidateContentIdentity(identity),
                reader.GetString(0));
        }

        return result;
    }

    private static IReadOnlyList<ResolvedProgress> ResolveProgress(
        IReadOnlyList<UserSyncProgressEntry> entries,
        IReadOnlyDictionary<string, string> itemIds,
        IReadOnlyDictionary<string, string> unitIds)
    {
        var resolved = new List<ResolvedProgress>(entries.Count);
        foreach (var entry in entries)
        {
            var key =
                UserSyncDocumentValidator.ValidateContentIdentity(
                    entry.Content);
            var isUnit = !string.IsNullOrWhiteSpace(
                entry.Content.UnitKind);
            var id = (isUnit ? unitIds : itemIds).GetValueOrDefault(key);
            if (id is null)
            {
                // Resolve after merging both catalogs. A deleted title or
                // missing chapter must not abort unrelated state restoration.
                continue;
            }
            resolved.Add(new ResolvedProgress(entry, key, id, isUnit));
        }

        return resolved;
    }

    private static IReadOnlyList<ResolvedReaderPreference>
        ResolveReaderPreferences(
            IReadOnlyList<UserSyncReaderPreference> entries,
            IReadOnlyDictionary<string, string> itemIds) =>
        entries.Where(entry => itemIds.ContainsKey(
            UserSyncDocumentValidator.ValidateItemIdentity(entry.Item)))
        .Select(entry =>
        {
            var key = UserSyncDocumentValidator.ValidateItemIdentity(
                entry.Item);
            return new ResolvedReaderPreference(
                entry,
                key,
                itemIds[key]);
        }).ToArray();

    private static IReadOnlyList<ResolvedFavoriteItem>
        ResolveFavoriteItems(
            IReadOnlyList<UserSyncFavoriteItemState> entries,
            IReadOnlyDictionary<string, string> itemIds) =>
        entries.Where(entry => !entry.IsFavorite || itemIds.ContainsKey(
            UserSyncDocumentValidator.ValidateItemIdentity(entry.Item)))
        .Select(entry =>
        {
            var key =
                UserSyncDocumentValidator.ValidateItemIdentity(
                    entry.Item);
            return new ResolvedFavoriteItem(
                entry,
                key,
                entry.IsFavorite
                    ? itemIds[key]
                    : null);
        }).ToArray();

    private static IReadOnlyList<ResolvedList> ResolveLists(
        IReadOnlyList<UserSyncMediaList> lists,
        IReadOnlyDictionary<string, string> itemIds) =>
        lists.Select(list => list with
        {
            ItemStates = list.ItemStates.Where(state =>
                list.IsDeleted || !state.IsMember || itemIds.ContainsKey(
                    UserSyncDocumentValidator.ValidateItemIdentity(state.Item)))
                .ToArray()
        }).Select(list => new ResolvedList(
            list,
            list.ItemStates.ToDictionary(
                static state =>
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        state.Item),
                state =>
                {
                    var key =
                        UserSyncDocumentValidator.ValidateItemIdentity(
                            state.Item);
                    return state.IsMember && !list.IsDeleted
                        ? itemIds[key]
                        : null;
                },
                StringComparer.Ordinal))).ToArray();

    private static async ValueTask DeleteSyncedStateAsync(
        Guid profileId,
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM playback_progress WHERE profile_id = $profile;
            DELETE FROM reader_preferences WHERE profile_id = $profile;
            DELETE FROM favorite_contributors WHERE profile_id = $profile;
            UPDATE library_entries SET is_favorite = 0
            WHERE profile_id = $profile;
            DELETE FROM user_media_list_items
            WHERE list_id IN (
                SELECT id FROM user_media_lists WHERE profile_id = $profile);
            DELETE FROM user_media_lists WHERE profile_id = $profile;
            DELETE FROM user_sync_records WHERE profile_id = $profile;
            """;
        command.Parameters.AddWithValue(
            "$profile",
            profileId.ToString("D"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private async ValueTask InsertProgressAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ResolvedProgress> entries,
        CancellationToken cancellationToken)
    {
        foreach (var resolved in entries)
        {
            await using (var command = CreateProfileCommand(connection))
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO playback_progress (
                        profile_id,
                        id,
                        catalog_item_id,
                        content_unit_id,
                        position,
                        position_kind,
                        is_completed,
                        updated_at)
                    VALUES (
                        $profile,
                        $id,
                        $catalog_item_id,
                        $content_unit_id,
                        $position,
                        $position_kind,
                        $is_completed,
                        $updated_at);
                    """;
                command.Parameters.AddWithValue(
                    "$id",
                    Guid.CreateVersion7().ToString("D"));
                command.Parameters.AddWithValue(
                    "$catalog_item_id",
                    resolved.IsUnit ? DBNull.Value : resolved.DatabaseId);
                command.Parameters.AddWithValue(
                    "$content_unit_id",
                    resolved.IsUnit ? resolved.DatabaseId : DBNull.Value);
                command.Parameters.AddWithValue(
                    "$position",
                    resolved.Entry.Position);
                command.Parameters.AddWithValue(
                    "$position_kind",
                    resolved.Entry.PositionKind);
                command.Parameters.AddWithValue(
                    "$is_completed",
                    resolved.Entry.IsCompleted ? 1 : 0);
                command.Parameters.AddWithValue(
                    "$updated_at",
                    resolved.Entry.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ProgressKind,
                resolved.SyncKey,
                resolved.Entry.SyncVersion!,
                isDeleted: false,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
        }
    }

    private async ValueTask InsertReaderPreferencesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ResolvedReaderPreference> entries,
        CancellationToken cancellationToken)
    {
        foreach (var resolved in entries)
        {
            await using (var command = CreateProfileCommand(connection))
            {
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO reader_preferences (
                        profile_id,
                        catalog_item_id,
                        display_mode,
                        updated_at)
                    VALUES (
                        $profile,
                        $catalog_item_id,
                        $display_mode,
                        $updated_at);
                    """;
                command.Parameters.AddWithValue(
                    "$catalog_item_id",
                    resolved.DatabaseId);
                command.Parameters.AddWithValue(
                    "$display_mode",
                    resolved.Entry.DisplayMode);
                command.Parameters.AddWithValue(
                    "$updated_at",
                    resolved.Entry.UpdatedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ReaderPreferenceKind,
                resolved.SyncKey,
                resolved.Entry.SyncVersion!,
                isDeleted: false,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
        }
    }

    private async ValueTask InsertFavoriteStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UserSyncFavoriteState> states,
        CancellationToken cancellationToken)
    {
        foreach (var state in states)
        {
            if (state.IsFavorite)
            {
                await using var command = CreateProfileCommand(connection);
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO favorite_contributors (
                        profile_id,
                        contributor_key,
                        added_at)
                    VALUES (
                        $profile,
                        $contributor_key,
                        $added_at);
                    """;
                command.Parameters.AddWithValue(
                    "$contributor_key",
                    state.ContributorKey.Trim());
                command.Parameters.AddWithValue(
                    "$added_at",
                    state.SyncVersion.ModifiedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.FavoriteKind,
                state.ContributorKey.Trim(),
                state.SyncVersion,
                isDeleted: !state.IsFavorite,
                itemMediaType: null,
                itemName: null,
                cancellationToken);
        }
    }

    private async ValueTask InsertFavoriteItemStatesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ResolvedFavoriteItem> states,
        CancellationToken cancellationToken)
    {
        foreach (var resolved in states)
        {
            var state = resolved.State;
            if (state.IsFavorite)
            {
                await using var command = CreateProfileCommand(connection);
                command.Transaction = transaction;
                command.CommandText = """
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
                        is_favorite = 1;
                    """;
                command.Parameters.AddWithValue(
                    "$catalog_item_id",
                    resolved.DatabaseId!);
                command.Parameters.AddWithValue(
                    "$added_at",
                    state.SyncVersion.ModifiedAt.ToString("O"));
                await command.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.FavoriteItemKind,
                resolved.SyncKey,
                state.SyncVersion,
                isDeleted: !state.IsFavorite,
                state.Item.MediaType,
                state.Item.Name,
                cancellationToken);
        }
    }

    private async ValueTask InsertListsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<ResolvedList> lists,
        CancellationToken cancellationToken)
    {
        foreach (var resolved in lists)
        {
            var list = resolved.List;
            if (!list.IsDeleted)
            {
                await using var listCommand = CreateProfileCommand(connection);
                listCommand.Transaction = transaction;
                listCommand.CommandText = """
                    INSERT INTO user_media_lists (
                        profile_id,
                        id,
                        name,
                        name_key,
                        created_at,
                        updated_at)
                    VALUES (
                        $profile,
                        $id,
                        $name,
                        $name_key,
                        $created_at,
                        $updated_at);
                    """;
                listCommand.Parameters.AddWithValue(
                    "$id",
                    list.Id.ToString("D"));
                listCommand.Parameters.AddWithValue(
                    "$name",
                    list.Name.Trim());
                listCommand.Parameters.AddWithValue(
                    "$name_key",
                    UserMediaList.GetNameKey(list.Name));
                listCommand.Parameters.AddWithValue(
                    "$created_at",
                    list.CreatedAt.ToString("O"));
                listCommand.Parameters.AddWithValue(
                    "$updated_at",
                    list.UpdatedAt.ToString("O"));
                await listCommand.ExecuteNonQueryAsync(cancellationToken);
            }

            await SqliteUserSyncMetadata.UpsertAsync(
                database.ActiveProfileId,
                connection,
                transaction,
                SqliteUserSyncMetadata.ListKind,
                SqliteUserSyncMetadata.GetListKey(list.Id),
                list.SyncVersion!,
                isDeleted: list.IsDeleted,
                itemMediaType: null,
                itemName: null,
                cancellationToken);

            foreach (var itemState in list.ItemStates)
            {
                var itemKey =
                    UserSyncDocumentValidator.ValidateItemIdentity(
                        itemState.Item);
                var itemDatabaseId =
                    resolved.ItemDatabaseIds.GetValueOrDefault(itemKey);
                if (!list.IsDeleted &&
                    itemState.IsMember &&
                    itemDatabaseId is not null)
                {
                    await using var membership =
                        CreateProfileCommand(connection);
                    membership.Transaction = transaction;
                    membership.CommandText = """
                        INSERT INTO user_media_list_items (
                            list_id,
                            catalog_item_id,
                            added_at)
                        VALUES (
                            $list_id,
                            $catalog_item_id,
                            $added_at);
                        """;
                    membership.Parameters.AddWithValue(
                        "$list_id",
                        list.Id.ToString("D"));
                    membership.Parameters.AddWithValue(
                        "$catalog_item_id",
                        itemDatabaseId);
                    membership.Parameters.AddWithValue(
                        "$added_at",
                        itemState.SyncVersion.ModifiedAt.ToString("O"));
                    await membership.ExecuteNonQueryAsync(
                        cancellationToken);
                }

                await SqliteUserSyncMetadata.UpsertAsync(
                    database.ActiveProfileId,
                    connection,
                    transaction,
                    SqliteUserSyncMetadata.ListItemKind,
                    SqliteUserSyncMetadata.GetListItemKey(
                        list.Id,
                        itemKey),
                    itemState.SyncVersion,
                    isDeleted: !itemState.IsMember,
                    itemState.Item.MediaType,
                    itemState.Item.Name,
                    cancellationToken);
            }
        }
    }

    private static long GetMaximumClock(UserSyncDocument document)
    {
        var clocks = document.Progress
            .Select(static entry => entry.SyncVersion!.Clock)
            .Concat(document.ReaderPreferences.Select(
                static entry => entry.SyncVersion!.Clock))
            .Concat(document.FavoriteStates.Select(
                static state => state.SyncVersion.Clock))
            .Concat(document.FavoriteItemStates.Select(
                static state => state.SyncVersion.Clock))
            .Concat(document.Lists.Select(
                static list => list.SyncVersion!.Clock))
            .Concat(document.Lists.SelectMany(static list =>
                list.ItemStates.Select(
                    static state => state.SyncVersion.Clock)));
        return clocks.DefaultIfEmpty(0).Max();
    }

    private static UserSyncContentIdentity CreateContentIdentity(
        SqliteDataReader reader) =>
        new()
        {
            Item = CreateItemIdentity(
                reader.GetString(0),
                reader.GetString(1), reader.GetString(9)),
            UnitKind = reader.IsDBNull(2)
                ? null
                : reader.GetString(2),
            DisplayNumber = reader.IsDBNull(3)
                ? null
                : reader.GetString(3),
            SeasonNumber = reader.IsDBNull(4)
                ? null
                : reader.GetInt32(4)
        };

    private static UserSyncCatalogItemIdentity CreateItemIdentity(
        string mediaType,
        string name, string? subtype = null) =>
        new()
        {
            MediaType = mediaType,
            Subtype = subtype,
            Name = name
        };

    private static string? SubtypeFromRecordKey(string key)
    {
        var itemKey = key[(key.IndexOf('|') + 1)..];
        var colon = itemKey.IndexOf(':');
        return colon > 0 && itemKey[..colon].StartsWith("book.", StringComparison.Ordinal)
            ? itemKey[..colon] : null;
    }

    private static DateTimeOffset ParseTimestamp(string value) =>
        DateTimeOffset.Parse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind);

    private static Guid ParseListId(string key) =>
        Guid.TryParseExact(key, "N", out var id)
            ? id
            : throw new InvalidDataException(
                "A local synchronized list key is invalid.");

    private static (Guid ListId, string ItemKey)
        ParseListItemRecordKey(string key)
    {
        var separator = key.IndexOf('|');
        if (separator <= 0 || separator == key.Length - 1)
        {
            throw new InvalidDataException(
                "A local synchronized list-item key is invalid.");
        }

        return (ParseListId(key[..separator]), key[(separator + 1)..]);
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = database.CreateConnection();
        try
        {
            await connection.OpenAsync(cancellationToken);
            await SqliteCatalogDatabase.ConfigureConnectionAsync(
                connection,
                cancellationToken);
            return connection;
        }
        catch
        {
            await connection.DisposeAsync();
            throw;
        }
    }

    private sealed class SyncListBuilder(
        Guid id,
        string name,
        DateTimeOffset createdAt,
        DateTimeOffset updatedAt,
        bool isDeleted,
        UserSyncVersion version)
    {
        public Dictionary<string, UserSyncListItemState> ItemStates
        {
            get;
        } = new(StringComparer.Ordinal);

        public UserSyncMediaList Build() =>
            new()
            {
                Id = id,
                Name = name,
                CreatedAt = createdAt,
                UpdatedAt = updatedAt,
                IsDeleted = isDeleted,
                SyncVersion = version,
                ItemStates = ItemStates.Values.ToArray()
            };
    }

    private sealed record ResolvedProgress(
        UserSyncProgressEntry Entry,
        string SyncKey,
        string DatabaseId,
        bool IsUnit);

    private sealed record ResolvedReaderPreference(
        UserSyncReaderPreference Entry,
        string SyncKey,
        string DatabaseId);

    private sealed record ResolvedFavoriteItem(
        UserSyncFavoriteItemState State,
        string SyncKey,
        string? DatabaseId);

    private sealed record ResolvedList(
        UserSyncMediaList List,
        IReadOnlyDictionary<string, string?> ItemDatabaseIds);
}

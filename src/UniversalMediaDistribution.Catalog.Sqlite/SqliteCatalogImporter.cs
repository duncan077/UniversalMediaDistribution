using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal static class SqliteCatalogImporter
{
    public static async ValueTask<CatalogImportResult> MergeAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogTransferDocument document,
        string? sourceName,
        string? documentHash,
        CancellationToken cancellationToken)
    {
        CatalogTransferValidator.Validate(document);

        var createdCount = 0;
        var updatedCount = 0;
        var unchangedCount = 0;
        var affectedNames = new List<string>(document.Items.Count);
        var now = DateTimeOffset.UtcNow.ToString("O");

        foreach (var item in document.Items)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var mediaType = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
            var mediaTypeValue = mediaType.ToString().ToLowerInvariant();
            var subtypeCode = CatalogSubtypeResolver.ResolveCode(mediaType, item.Subtype);
            var nameKey = NameNormalizer.Normalize(item.Name);
            var syncId = item.Id ?? CatalogStableId.Create(
                mediaType,
                item.Name, item.Subtype);
            if (item.Deleted)
            {
                if (item.SyncVersion is not null)
                {
                    await ApplyTombstoneAsync(
                        connection,
                        transaction,
                        syncId,
                        mediaTypeValue,
                        subtypeCode,
                        item,
                        cancellationToken);
                }

                continue;
            }

            var tombstoneVersion = await FindTombstoneVersionAsync(
                connection,
                transaction,
                syncId,
                cancellationToken);
            if (tombstoneVersion is not null)
            {
                // The account deleted this title. Only an edit newer than the
                // deletion brings it back; a versionless re-discovery — the
                // daily provider fetch — does not, which is what makes the
                // deletion stick instead of quietly reappearing tomorrow.
                if (item.SyncVersion is null ||
                    UserSyncDocumentMerger.CompareVersions(
                        item.SyncVersion,
                        tombstoneVersion) <= 0)
                {
                    continue;
                }

                await RemoveTombstoneAsync(
                    connection,
                    transaction,
                    syncId,
                    cancellationToken);
            }

            var contentHash = ComputeContentHash(item);
            var existing = await FindCatalogItemAsync(
                connection,
                transaction,
                mediaTypeValue,
                nameKey,
                subtypeCode,
                item.Id,
                cancellationToken);

            var itemId = existing?.Id ?? NewId();
            if (existing is null)
            {
                await InsertCatalogItemAsync(
                    connection,
                    transaction,
                    itemId,
                    mediaTypeValue,
                    subtypeCode,
                    nameKey,
                    item,
                    now,
                    contentHash,
                    cancellationToken);
                createdCount++;
            }
            else
            {
                await SqliteCatalogRowMerger.MergeNameCollisionAsync(
                    connection,
                    transaction,
                    itemId,
                    mediaTypeValue,
                    nameKey,
                    subtypeCode,
                    cancellationToken);

                // A payload identical to the one this item was last built from
                // is a re-read, not an update. The row is still rewritten —
                // another path may have edited it since — but the timestamp
                // that says "something new arrived" is left where it was.
                //
                // A row stored before hashes existed has nothing to compare
                // against, and "we do not know" must not read as "it changed":
                // that would mark the whole library updated on the first merge
                // after the upgrade, which is the very thing this prevents.
                // The hash is recorded now and the next merge can tell.
                var hasChanged = existing.ContentHash is not null &&
                    !string.Equals(
                        existing.ContentHash,
                        contentHash,
                        StringComparison.Ordinal);
                await UpdateCatalogItemAsync(
                    connection,
                    transaction,
                    itemId,
                    subtypeCode,
                    nameKey,
                    item,
                    now,
                    contentHash,
                    hasChanged,
                    cancellationToken);
                if (hasChanged)
                {
                    updatedCount++;
                }
                else
                {
                    unchangedCount++;
                }
            }

            await UpsertGenresAsync(connection, transaction, itemId, item.Genres, cancellationToken);
            await UpsertCategoriesAsync(
                connection,
                transaction,
                itemId,
                item.Categories,
                cancellationToken);
            await ReplaceAlternativeNamesAsync(
                connection,
                transaction,
                itemId,
                CatalogTransferMapper.GetAlternativeNames(item),
                cancellationToken);
            await UpsertContributorsAsync(connection, transaction, itemId, item.Contributors, cancellationToken);
            await UpsertRightsAsync(connection, transaction, itemId, item.DistributionRights, cancellationToken);

            foreach (var season in item.Seasons)
            {
                var seasonId = await UpsertSeasonAsync(
                    connection,
                    transaction,
                    itemId,
                    season,
                    cancellationToken);

                foreach (var unit in season.Units)
                {
                    var unitId = await UpsertUnitAsync(
                        connection,
                        transaction,
                        itemId,
                        seasonId,
                        unit,
                        cancellationToken);
                    await UpsertSourcesAsync(
                        connection,
                        transaction,
                        itemId: null,
                        unitId,
                        unit.Sources,
                        now,
                        cancellationToken);
                }
            }

            foreach (var unit in item.Units)
            {
                var unitId = await UpsertUnitAsync(
                    connection,
                    transaction,
                    itemId,
                    seasonId: null,
                    unit,
                    cancellationToken);
                await UpsertSourcesAsync(
                    connection,
                    transaction,
                    itemId: null,
                    unitId,
                    unit.Sources,
                    now,
                    cancellationToken);
            }

            await RenumberUnitSortOrderAsync(
                connection,
                transaction,
                itemId,
                cancellationToken);

            await UpsertSourcesAsync(
                connection,
                transaction,
                itemId,
                unitId: null,
                item.Sources,
                now,
                cancellationToken);

            affectedNames.Add(item.Name.Trim());
        }

        await IncrementCatalogRevisionAsync(connection, transaction, cancellationToken);
        await InsertImportHistoryAsync(
            connection,
            transaction,
            sourceName,
            documentHash,
            document.Version,
            createdCount,
            updatedCount,
            now,
            cancellationToken);

        return new CatalogImportResult(
            createdCount,
            updatedCount,
            unchangedCount,
            affectedNames);
    }

    private sealed record ExistingCatalogItem(string Id, string? ContentHash);

    private static async ValueTask<UserSyncVersion?>
        FindTombstoneVersionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            Guid syncId,
            CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT clock, device_id, operation_id, modified_at
            FROM catalog_tombstones
            WHERE sync_id = $sync_id;
            """);
        command.Parameters.AddWithValue("$sync_id", syncId.ToString());
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new UserSyncVersion
        {
            Clock = reader.GetInt64(0),
            DeviceId = reader.GetString(1),
            OperationId = Guid.Parse(reader.GetString(2)),
            ModifiedAt = DateTimeOffset.Parse(
                reader.GetString(3),
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.RoundtripKind)
        };
    }

    private static async ValueTask RemoveTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid syncId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            DELETE FROM catalog_tombstones WHERE sync_id = $sync_id;
            """);
        command.Parameters.AddWithValue("$sync_id", syncId.ToString());
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    /// <summary>
    /// Applies a deletion: the row goes — children with it, by cascade — when
    /// the tombstone is newer than anything the row can say for itself, and
    /// the tombstone is remembered so the title stays deleted on this device
    /// and keeps travelling to the others.
    /// </summary>
    private static async ValueTask ApplyTombstoneAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        Guid syncId,
        string mediaTypeValue,
        string subtypeCode,
        CatalogTransferItem item,
        CancellationToken cancellationToken)
    {
        await using (var find = CreateCommand(connection, transaction, """
            SELECT id, sync_clock, sync_device, sync_operation
            FROM catalog_items
            WHERE (sync_id = $sync_id
               OR (media_type = $media_type AND name_key = $name_key))
               AND (media_type <> 'book' OR subtype_code = $subtype);
            """))
        {
            find.Parameters.AddWithValue("$sync_id", syncId.ToString());
            find.Parameters.AddWithValue("$subtype", subtypeCode);
            find.Parameters.AddWithValue("$media_type", mediaTypeValue);
            find.Parameters.AddWithValue(
                "$name_key",
                NameNormalizer.Normalize(item.Name));
            string? rowId = null;
            await using (var reader =
                await find.ExecuteReaderAsync(cancellationToken))
            {
                if (await reader.ReadAsync(cancellationToken))
                {
                    rowId = reader.GetString(0);
                    if (!reader.IsDBNull(1))
                    {
                        var rowVersion = new UserSyncVersion
                        {
                            Clock = reader.GetInt64(1),
                            DeviceId = reader.GetString(2),
                            OperationId = Guid.Parse(reader.GetString(3)),
                            ModifiedAt = item.SyncVersion!.ModifiedAt
                        };
                        if (UserSyncDocumentMerger.CompareVersions(
                                item.SyncVersion!,
                                rowVersion) <= 0)
                        {
                            // A newer edit outlives the deletion.
                            return;
                        }
                    }
                }
            }

            if (rowId is not null)
            {
                await using var delete = CreateCommand(
                    connection,
                    transaction,
                    "DELETE FROM catalog_items WHERE id = $id;");
                delete.Parameters.AddWithValue("$id", rowId);
                await delete.ExecuteNonQueryAsync(cancellationToken);
            }
        }

        await using var upsert = CreateCommand(connection, transaction, """
            INSERT INTO catalog_tombstones (
                sync_id, media_type, subtype_code, name, clock, device_id,
                operation_id, modified_at)
            VALUES (
                $sync_id, $media_type, $subtype_code, $name, $clock,
                $device_id, $operation_id, $modified_at)
            ON CONFLICT (sync_id) DO UPDATE SET
                clock = excluded.clock,
                device_id = excluded.device_id,
                operation_id = excluded.operation_id,
                modified_at = excluded.modified_at
            WHERE excluded.clock > catalog_tombstones.clock
               OR (excluded.clock = catalog_tombstones.clock AND
                   excluded.device_id > catalog_tombstones.device_id);
            """);
        upsert.Parameters.AddWithValue("$sync_id", syncId.ToString());
        upsert.Parameters.AddWithValue("$media_type", mediaTypeValue);
        upsert.Parameters.AddWithValue("$subtype_code", subtypeCode);
        upsert.Parameters.AddWithValue("$name", item.Name.Trim());
        upsert.Parameters.AddWithValue("$clock", item.SyncVersion!.Clock);
        upsert.Parameters.AddWithValue(
            "$device_id",
            item.SyncVersion.DeviceId);
        upsert.Parameters.AddWithValue(
            "$operation_id",
            item.SyncVersion.OperationId.ToString());
        upsert.Parameters.AddWithValue(
            "$modified_at",
            item.SyncVersion.ModifiedAt.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture));
        await upsert.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<ExistingCatalogItem?> FindCatalogItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string mediaType,
        string nameKey,
        string subtypeCode,
        Guid? syncId,
        CancellationToken cancellationToken)
    {
        // Identity first: a renamed title matches by ID when its name no
        // longer would, which is what lets a rename made elsewhere update the
        // row here instead of creating a twin beside it.
        if (syncId is { } id)
        {
            await using var byIdCommand = CreateCommand(
                connection,
                transaction,
                """
                SELECT id, content_hash
                FROM catalog_items
                WHERE sync_id = $sync_id AND (media_type <> 'book' OR subtype_code = $subtype);
                """);
            byIdCommand.Parameters.AddWithValue("$sync_id", id.ToString());
            byIdCommand.Parameters.AddWithValue("$subtype", subtypeCode);
            await using var byIdReader =
                await byIdCommand.ExecuteReaderAsync(cancellationToken);
            if (await byIdReader.ReadAsync(cancellationToken))
            {
                return new ExistingCatalogItem(
                    byIdReader.GetString(0),
                    byIdReader.IsDBNull(1) ? null : byIdReader.GetString(1));
            }
        }

        await using var command = CreateCommand(connection, transaction, """
            SELECT id, content_hash
            FROM catalog_items
            WHERE media_type = $media_type AND name_key = $name_key
                AND (media_type <> 'book' OR subtype_code = $subtype);
            """);
        command.Parameters.AddWithValue("$media_type", mediaType);
        command.Parameters.AddWithValue("$name_key", nameKey);
        command.Parameters.AddWithValue("$subtype", subtypeCode);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        if (!await reader.ReadAsync(cancellationToken))
        {
            return null;
        }

        return new ExistingCatalogItem(
            reader.GetString(0),
            reader.IsDBNull(1) ? null : reader.GetString(1));
    }

    /// <summary>
    /// A fingerprint of everything a document says about one item — its own
    /// fields and every season, unit and source hanging off it — so a new
    /// chapter counts as a change while a re-read of the same payload does not.
    /// <para>
    /// Taken from the incoming document rather than from the stored row on
    /// purpose: the question is whether the source is telling us something
    /// different from last time, not whether the row happens to differ from a
    /// partial payload that was never meant to describe all of it.
    /// </para>
    /// </summary>
    private static string ComputeContentHash(CatalogTransferItem item)
    {
        // The identity is deliberately outside the fingerprint: stamping an
        // ID onto an unchanged item must not read as "something new arrived"
        // and mark the whole library updated after the upgrade.
        var json = JsonSerializer.SerializeToUtf8Bytes(
            item with { Id = null, SyncVersion = null, Deleted = false },
            SqliteJsonSerializerContext.Default.CatalogTransferItem);
        return Convert.ToHexStringLower(SHA256.HashData(json));
    }

    private static async ValueTask InsertCatalogItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string mediaType,
        string subtypeCode,
        string nameKey,
        CatalogTransferItem item,
        string now,
        string contentHash,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO catalog_items (
                id, sync_id, sync_clock, sync_device, sync_operation, sync_modified,
                media_type, subtype_code, name, name_key, original_name, sort_name,
                description, release_date, release_year, end_date, publication_status,
                original_language,
                country_code, content_rating, runtime_seconds, page_count, thumbnail_ref,
                backdrop_ref, content_hash, created_at, updated_at)
            VALUES (
                $id, $sync_id, $sync_clock, $sync_device, $sync_operation, $sync_modified,
                $media_type, $subtype_code, $name, $name_key, $original_name, $sort_name,
                COALESCE($description, ''), $release_date, $release_year, $end_date,
                COALESCE($publication_status, 0), $original_language,
                $country_code, $content_rating, $runtime_seconds, $page_count, $thumbnail_ref,
                $backdrop_ref, $content_hash, $created_at, $updated_at);
            """);
        AddCatalogItemParameters(
            command,
            itemId,
            mediaType,
            subtypeCode,
            nameKey,
            item,
            now,
            contentHash,
            includeCreatedAt: true);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask UpdateCatalogItemAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string subtypeCode,
        string nameKey,
        CatalogTransferItem item,
        string now,
        string contentHash,
        bool hasChanged,
        CancellationToken cancellationToken)
    {
        // COALESCE on every optional column, for the same reason the child
        // collections below are left alone when absent: a merged document that
        // says nothing about a field is not asking for the stored value to be
        // erased. A provider's daily-update payload routinely carries only the
        // title and the episode list, and without this each refresh blanked
        // the artwork, synopsis and dates a metadata pass had filled in.
        await using var command = CreateCommand(connection, transaction, """
            UPDATE catalog_items
            SET sync_id = COALESCE($sync_id, sync_id),
                sync_clock = COALESCE($sync_clock, sync_clock),
                sync_device = COALESCE($sync_device, sync_device),
                sync_operation = COALESCE($sync_operation, sync_operation),
                sync_modified = COALESCE($sync_modified, sync_modified),
                subtype_code = $subtype_code,
                name = $name,
                name_key = $name_key,
                original_name = COALESCE($original_name, original_name),
                sort_name = COALESCE($sort_name, sort_name),
                description = COALESCE($description, description),
                release_date = COALESCE($release_date, release_date),
                release_year = COALESCE($release_year, release_year),
                end_date = COALESCE($end_date, end_date),
                publication_status =
                    COALESCE($publication_status, publication_status),
                original_language =
                    COALESCE($original_language, original_language),
                country_code = COALESCE($country_code, country_code),
                content_rating = COALESCE($content_rating, content_rating),
                runtime_seconds = COALESCE($runtime_seconds, runtime_seconds),
                page_count = COALESCE($page_count, page_count),
                thumbnail_ref = COALESCE($thumbnail_ref, thumbnail_ref),
                backdrop_ref = COALESCE($backdrop_ref, backdrop_ref),
                content_hash = $content_hash,
                updated_at =
                    CASE WHEN $has_changed = 1 THEN $updated_at ELSE updated_at END
            WHERE id = $id;
            """);
        AddCatalogItemParameters(
            command,
            itemId,
            mediaType: null,
            subtypeCode,
            nameKey,
            item,
            now,
            contentHash,
            includeCreatedAt: false);
        command.Parameters.AddWithValue("$has_changed", hasChanged ? 1 : 0);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static void AddCatalogItemParameters(
        SqliteCommand command,
        string itemId,
        string? mediaType,
        string subtypeCode,
        string nameKey,
        CatalogTransferItem item,
        string now,
        string contentHash,
        bool includeCreatedAt)
    {
        command.Parameters.AddWithValue("$id", itemId);

        // Minted for a new row, adopted for an existing one; COALESCE in the
        // update keeps the stored identity when a legacy document carries
        // none. Minting from the name is what the migration backfill does,
        // so a title created here and one created there converge on the same
        // identity without coordinating.
        command.Parameters.AddWithValue(
            "$sync_id",
            (object?)(item.Id ?? (includeCreatedAt
                ? CatalogStableId.Create(
                    CatalogSubtypeResolver.ParseMediaType(item.MediaType),
                    item.Name, item.Subtype)
                : null))?.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sync_clock",
            (object?)item.SyncVersion?.Clock ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sync_device",
            (object?)item.SyncVersion?.DeviceId ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sync_operation",
            (object?)item.SyncVersion?.OperationId.ToString() ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$sync_modified",
            (object?)item.SyncVersion?.ModifiedAt.ToString(
                "O",
                System.Globalization.CultureInfo.InvariantCulture) ??
            DBNull.Value);
        if (mediaType is not null)
        {
            command.Parameters.AddWithValue("$media_type", mediaType);
        }

        command.Parameters.AddWithValue("$subtype_code", subtypeCode);
        command.Parameters.AddWithValue("$name", item.Name.Trim());
        command.Parameters.AddWithValue("$name_key", nameKey);
        command.Parameters.AddWithValue("$original_name", DbValue(item.OriginalName));
        command.Parameters.AddWithValue("$sort_name", DbValue(item.SortName));
        // Null rather than a blank or a default, so the statements above can
        // tell "the document did not mention this" apart from "the document
        // set this to empty" and keep what is already stored.
        command.Parameters.AddWithValue(
            "$description",
            DbValue(string.IsNullOrWhiteSpace(item.Description)
                ? null
                : item.Description.Trim()));
        command.Parameters.AddWithValue("$release_date", DbValue(item.ReleaseDate));
        // Falls back to the release date so a document that carries only a date
        // still populates the column the year filter reads.
        command.Parameters.AddWithValue(
            "$release_year",
            DbValue(CatalogReleaseYear.Resolve(
                item.ReleaseYear,
                item.ReleaseDate)));
        command.Parameters.AddWithValue("$end_date", DbValue(item.EndDate));
        command.Parameters.AddWithValue(
            "$publication_status",
            DbValue(string.IsNullOrWhiteSpace(item.PublicationStatus)
                ? null
                : (int?)CatalogTransferMapper.ParsePublicationStatus(
                    item.PublicationStatus)));
        command.Parameters.AddWithValue("$original_language", DbValue(item.OriginalLanguage));
        command.Parameters.AddWithValue("$country_code", DbValue(item.CountryCode));
        command.Parameters.AddWithValue("$content_rating", DbValue(item.ContentRating));
        command.Parameters.AddWithValue("$runtime_seconds", DbValue(item.RuntimeSeconds));
        command.Parameters.AddWithValue("$page_count", DbValue(item.PageCount));
        command.Parameters.AddWithValue("$thumbnail_ref", DbValue(item.ThumbnailRef));
        command.Parameters.AddWithValue("$backdrop_ref", DbValue(item.BackdropRef));
        command.Parameters.AddWithValue("$content_hash", contentHash);
        command.Parameters.AddWithValue("$updated_at", now);

        if (includeCreatedAt)
        {
            command.Parameters.AddWithValue("$created_at", now);
        }
    }

    private static async ValueTask UpsertGenresAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        IReadOnlyList<string> genres,
        CancellationToken cancellationToken)
    {
        if (genres.Count == 0)
        {
            // A merged document that carries no genres for an item is silent
            // about them, not an instruction to drop the ones already stored.
            // Provider refreshes routinely report a title without its genres.
            return;
        }

        await using (var delete = CreateCommand(
                         connection,
                         transaction,
                         "DELETE FROM catalog_item_genres WHERE catalog_item_id = $item_id;"))
        {
            delete.Parameters.AddWithValue("$item_id", itemId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var rawGenre in genres)
        {
            var name = rawGenre.Trim();
            var nameKey = NameNormalizer.Normalize(name);
            var genreId = await FindScalarAsync(
                connection,
                transaction,
                "SELECT id FROM genres WHERE name_key = $key;",
                ("$key", nameKey),
                cancellationToken);

            if (genreId is null)
            {
                genreId = NewId();
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO genres (id, name, name_key) VALUES ($id, $name, $key);
                    """);
                insert.Parameters.AddWithValue("$id", genreId);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$key", nameKey);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, "UPDATE genres SET name = $name WHERE id = $id;");
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$id", genreId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var link = CreateCommand(connection, transaction, """
                INSERT OR IGNORE INTO catalog_item_genres (catalog_item_id, genre_id)
                VALUES ($item_id, $genre_id);
                """);
            link.Parameters.AddWithValue("$item_id", itemId);
            link.Parameters.AddWithValue("$genre_id", genreId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpsertCategoriesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        IReadOnlyList<string> categories,
        CancellationToken cancellationToken)
    {
        if (categories.Count == 0)
        {
            // Same merge semantics as the genres above: absent is not empty.
            return;
        }

        await using (var delete = CreateCommand(
                         connection,
                         transaction,
                         "DELETE FROM catalog_item_categories WHERE catalog_item_id = $item_id;"))
        {
            delete.Parameters.AddWithValue("$item_id", itemId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var rawCategory in categories)
        {
            var name = rawCategory.Trim();
            var nameKey = NameNormalizer.Normalize(name);
            var categoryId = await FindScalarAsync(
                connection,
                transaction,
                "SELECT id FROM categories WHERE name_key = $key;",
                ("$key", nameKey),
                cancellationToken);

            if (categoryId is null)
            {
                categoryId = NewId();
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO categories (id, name, name_key) VALUES ($id, $name, $key);
                    """);
                insert.Parameters.AddWithValue("$id", categoryId);
                insert.Parameters.AddWithValue("$name", name);
                insert.Parameters.AddWithValue("$key", nameKey);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var update = CreateCommand(
                    connection,
                    transaction,
                    "UPDATE categories SET name = $name WHERE id = $id;");
                update.Parameters.AddWithValue("$name", name);
                update.Parameters.AddWithValue("$id", categoryId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            await using var link = CreateCommand(connection, transaction, """
                INSERT INTO catalog_item_categories (catalog_item_id, category_id)
                VALUES ($item_id, $category_id);
                """);
            link.Parameters.AddWithValue("$item_id", itemId);
            link.Parameters.AddWithValue("$category_id", categoryId);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask ReplaceAlternativeNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        IReadOnlyList<CatalogTransferAlias> alternativeNames,
        CancellationToken cancellationToken)
    {
        if (alternativeNames.Count == 0)
        {
            // Same merge semantics as the genres above: absent is not empty.
            return;
        }

        await using (var delete = CreateCommand(
                         connection,
                         transaction,
                         "DELETE FROM catalog_alternative_names WHERE catalog_item_id = $item_id;"))
        {
            delete.Parameters.AddWithValue("$item_id", itemId);
            await delete.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var alternativeName in alternativeNames)
        {
            var normalized = new CatalogAlternativeName(
                alternativeName.Name,
                alternativeName.Language,
                alternativeName.AliasType);
            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO catalog_alternative_names (
                    id, catalog_item_id, name, name_key, language, alias_type)
                VALUES ($id, $item_id, $name, $name_key, $language, $alias_type);
                """);
            command.Parameters.AddWithValue("$id", NewId());
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$name", normalized.Name);
            command.Parameters.AddWithValue("$name_key", normalized.NameKey);
            command.Parameters.AddWithValue("$language", normalized.Language);
            command.Parameters.AddWithValue("$alias_type", normalized.Kind);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpsertContributorsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        IReadOnlyList<CatalogTransferContributor> contributors,
        CancellationToken cancellationToken)
    {
        foreach (var contributor in contributors)
        {
            var kind = contributor.Kind.Trim().ToLowerInvariant();
            var nameKey = NameNormalizer.Normalize(contributor.Name);
            var contributorId = await FindScalarAsync(
                connection,
                transaction,
                "SELECT id FROM contributors WHERE kind = $kind AND name_key = $name_key;",
                ("$kind", kind),
                ("$name_key", nameKey),
                cancellationToken);

            if (contributorId is null)
            {
                contributorId = NewId();
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO contributors (
                        id, name, name_key, kind, url, thumbnail_ref)
                    VALUES (
                        $id, $name, $name_key, $kind, $url, $thumbnail_ref);
                    """);
                insert.Parameters.AddWithValue("$id", contributorId);
                insert.Parameters.AddWithValue("$name", contributor.Name.Trim());
                insert.Parameters.AddWithValue("$name_key", nameKey);
                insert.Parameters.AddWithValue("$kind", kind);
                insert.Parameters.AddWithValue(
                    "$url",
                    DbValue(contributor.Url?.Trim()));
                insert.Parameters.AddWithValue("$thumbnail_ref", DbValue(contributor.ThumbnailRef));
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, """
                    UPDATE contributors
                    SET name = $name,
                        url = $url,
                        thumbnail_ref = $thumbnail_ref
                    WHERE id = $id;
                    """);
                update.Parameters.AddWithValue("$name", contributor.Name.Trim());
                update.Parameters.AddWithValue(
                    "$url",
                    DbValue(contributor.Url?.Trim()));
                update.Parameters.AddWithValue("$thumbnail_ref", DbValue(contributor.ThumbnailRef));
                update.Parameters.AddWithValue("$id", contributorId);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }

            foreach (var credit in contributor.Credits)
            {
                await using var command = CreateCommand(connection, transaction, """
                    INSERT INTO catalog_item_credits (
                        catalog_item_id, contributor_id, role, character_name, sort_order)
                    VALUES ($item_id, $contributor_id, $role, $character_name, $sort_order)
                    ON CONFLICT (catalog_item_id, contributor_id, role, character_name)
                    DO UPDATE SET sort_order = excluded.sort_order;
                    """);
                command.Parameters.AddWithValue("$item_id", itemId);
                command.Parameters.AddWithValue("$contributor_id", contributorId);
                command.Parameters.AddWithValue("$role", credit.Role.Trim());
                command.Parameters.AddWithValue("$character_name", credit.CharacterName?.Trim() ?? string.Empty);
                command.Parameters.AddWithValue("$sort_order", credit.SortOrder);
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    /// <summary>
    /// Renumbers an item's units so what is stored agrees with the chapter or
    /// episode numbers written on them.
    ///
    /// A provider's daily update carries only what it has just published and
    /// numbers those from zero within that batch, so chapter 67 arrives
    /// claiming position 0 and lands ahead of chapter 65. Reading now orders
    /// by the number regardless, but sort_order is what an export, a device
    /// sync or any other reader sees, so it is brought back into line here —
    /// once per item, after its units are in place.
    ///
    /// Units are renumbered within their season, and anything whose number
    /// does not parse keeps its relative order at the end.
    /// </summary>
    private static async ValueTask RenumberUnitSortOrderAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        CancellationToken cancellationToken)
    {
        var units = new List<UnitOrdinal>();
        await using (var select = CreateCommand(connection, transaction, """
            SELECT id, COALESCE(season_id, ''), display_number, sort_order
            FROM content_units
            WHERE catalog_item_id = $item_id;
            """))
        {
            select.Parameters.AddWithValue("$item_id", itemId);
            await using var reader =
                await select.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                units.Add(new UnitOrdinal(
                    reader.GetString(0),
                    reader.GetString(1),
                    reader.GetString(2),
                    reader.GetInt64(3)));
            }
        }

        if (units.Count == 0)
        {
            return;
        }

        await using var update = CreateCommand(connection, transaction, """
            UPDATE content_units SET sort_order = $sort_order WHERE id = $id;
            """);
        var idParameter = update.Parameters.Add("$id", SqliteType.Text);
        var sortParameter =
            update.Parameters.Add("$sort_order", SqliteType.Integer);
        foreach (var season in units.GroupBy(
                     static unit => unit.SeasonId,
                     StringComparer.Ordinal))
        {
            var position = 0L;
            foreach (var unit in season
                         .OrderBy(static unit =>
                             CatalogDetailsMapper.UnitNumber(
                                 unit.DisplayNumber))
                         .ThenBy(static unit => unit.SortOrder))
            {
                if (unit.SortOrder != position)
                {
                    idParameter.Value = unit.Id;
                    sortParameter.Value = position;
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }

                position++;
            }
        }
    }

    private readonly record struct UnitOrdinal(
        string Id,
        string SeasonId,
        string DisplayNumber,
        long SortOrder);

    private static async ValueTask<string> UpsertSeasonAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        CatalogTransferSeason season,
        CancellationToken cancellationToken)
    {
        var seasonId = await FindScalarAsync(
            connection,
            transaction,
            "SELECT id FROM seasons WHERE catalog_item_id = $item_id AND season_number = $number;",
            ("$item_id", itemId),
            ("$number", season.Number),
            cancellationToken);

        if (seasonId is null)
        {
            seasonId = NewId();
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO seasons (
                    id, catalog_item_id, season_number, display_name,
                    thumbnail_ref, release_date, sort_order)
                VALUES (
                    $id, $item_id, $number, $display_name,
                    $thumbnail_ref, $release_date, $sort_order);
                """);
            AddSeasonParameters(insert, seasonId, itemId, season);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var update = CreateCommand(connection, transaction, """
                UPDATE seasons
                SET display_name = $display_name,
                    thumbnail_ref = $thumbnail_ref,
                    release_date = $release_date,
                    sort_order = $sort_order
                WHERE id = $id;
                """);
            AddSeasonParameters(update, seasonId, itemId, season, includeIdentity: false);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return seasonId;
    }

    private static void AddSeasonParameters(
        SqliteCommand command,
        string seasonId,
        string itemId,
        CatalogTransferSeason season,
        bool includeIdentity = true)
    {
        command.Parameters.AddWithValue("$id", seasonId);
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$number", season.Number);
        }

        command.Parameters.AddWithValue("$display_name", DbValue(season.DisplayName));
        command.Parameters.AddWithValue("$thumbnail_ref", DbValue(season.ThumbnailRef));
        command.Parameters.AddWithValue("$release_date", DbValue(season.ReleaseDate));
        command.Parameters.AddWithValue("$sort_order", season.SortOrder);
    }

    private static async ValueTask<string> UpsertUnitAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string? seasonId,
        CatalogTransferUnit unit,
        CancellationToken cancellationToken)
    {
        var kind = UnitKindToDatabase(CatalogTransferMapper.ParseUnitKind(unit.Kind));
        var displayNumberKey = NameNormalizer.Normalize(unit.DisplayNumber);
        var unitId = await FindUnitIdAsync(
            connection,
            transaction,
            itemId,
            seasonId,
            kind,
            displayNumberKey,
            cancellationToken);

        if (unitId is null)
        {
            unitId = NewId();
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO content_units (
                    id, catalog_item_id, season_id, unit_kind, display_number,
                    display_number_key, name, thumbnail_ref, duration_seconds,
                    page_count, release_date, is_special, sort_order)
                VALUES (
                    $id, $item_id, $season_id, $kind, $display_number,
                    $display_number_key, $name, $thumbnail_ref, $duration_seconds,
                    $page_count, $release_date, $is_special, $sort_order);
                """);
            AddUnitParameters(insert, unitId, itemId, seasonId, kind, displayNumberKey, unit, includeIdentity: true);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var update = CreateCommand(connection, transaction, """
                UPDATE content_units
                SET display_number = $display_number,
                    name = $name,
                    thumbnail_ref = $thumbnail_ref,
                    duration_seconds = $duration_seconds,
                    page_count = $page_count,
                    release_date = $release_date,
                    is_special = $is_special,
                    sort_order = $sort_order
                WHERE id = $id;
                """);
            AddUnitParameters(update, unitId, itemId, seasonId, kind, displayNumberKey, unit, includeIdentity: false);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return unitId;
    }

    private static async ValueTask<string?> FindUnitIdAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string? seasonId,
        string kind,
        string displayNumberKey,
        CancellationToken cancellationToken)
    {
        var sql = seasonId is null
            ? """
              SELECT id FROM content_units
              WHERE catalog_item_id = $item_id AND season_id IS NULL
                AND unit_kind = $kind AND display_number_key = $display_number_key;
              """
            : """
              SELECT id FROM content_units
              WHERE catalog_item_id = $item_id AND season_id = $season_id
                AND unit_kind = $kind AND display_number_key = $display_number_key;
              """;

        await using var command = CreateCommand(connection, transaction, sql);
        command.Parameters.AddWithValue("$item_id", itemId);
        if (seasonId is not null)
        {
            command.Parameters.AddWithValue("$season_id", seasonId);
        }

        command.Parameters.AddWithValue("$kind", kind);
        command.Parameters.AddWithValue("$display_number_key", displayNumberKey);
        return ScalarToNullableString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static void AddUnitParameters(
        SqliteCommand command,
        string unitId,
        string itemId,
        string? seasonId,
        string kind,
        string displayNumberKey,
        CatalogTransferUnit unit,
        bool includeIdentity)
    {
        command.Parameters.AddWithValue("$id", unitId);
        if (includeIdentity)
        {
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$season_id", DbValue(seasonId));
            command.Parameters.AddWithValue("$kind", kind);
            command.Parameters.AddWithValue("$display_number_key", displayNumberKey);
        }

        command.Parameters.AddWithValue("$display_number", unit.DisplayNumber.Trim());
        command.Parameters.AddWithValue("$name", DbValue(unit.Name));
        command.Parameters.AddWithValue("$thumbnail_ref", DbValue(unit.ThumbnailRef));
        command.Parameters.AddWithValue("$duration_seconds", DbValue(unit.DurationSeconds));
        command.Parameters.AddWithValue("$page_count", DbValue(unit.PageCount));
        command.Parameters.AddWithValue("$release_date", DbValue(unit.ReleaseDate));
        command.Parameters.AddWithValue("$is_special", unit.IsSpecial ? 1 : 0);
        command.Parameters.AddWithValue("$sort_order", unit.SortOrder);
    }

    private static async ValueTask UpsertRightsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        IReadOnlyList<CatalogTransferRight> rights,
        CancellationToken cancellationToken)
    {
        foreach (var right in rights)
        {
            var naturalKey = $"{right.SourceUri.Trim().ToUpperInvariant()}|{NameNormalizer.Normalize(right.LicenseName)}";
            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO distribution_rights (
                    id, catalog_item_id, natural_key, source_name, source_uri,
                    license_name, license_uri, attribution, rights_note, verified_at)
                VALUES (
                    $id, $item_id, $natural_key, $source_name, $source_uri,
                    $license_name, $license_uri, $attribution, $rights_note, $verified_at)
                ON CONFLICT (catalog_item_id, natural_key)
                DO UPDATE SET
                    source_name = excluded.source_name,
                    source_uri = excluded.source_uri,
                    license_name = excluded.license_name,
                    license_uri = excluded.license_uri,
                    attribution = excluded.attribution,
                    rights_note = excluded.rights_note,
                    verified_at = excluded.verified_at;
                """);
            command.Parameters.AddWithValue("$id", NewId());
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$natural_key", naturalKey);
            command.Parameters.AddWithValue("$source_name", right.SourceName.Trim());
            command.Parameters.AddWithValue("$source_uri", right.SourceUri.Trim());
            command.Parameters.AddWithValue("$license_name", right.LicenseName.Trim());
            command.Parameters.AddWithValue("$license_uri", DbValue(right.LicenseUri));
            command.Parameters.AddWithValue("$attribution", DbValue(right.Attribution));
            command.Parameters.AddWithValue("$rights_note", DbValue(right.RightsNote));
            command.Parameters.AddWithValue("$verified_at", DbValue(right.VerifiedAt));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }

    private static async ValueTask UpsertSourcesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? itemId,
        string? unitId,
        IReadOnlyList<CatalogTransferMediaSource> sources,
        string now,
        CancellationToken cancellationToken)
    {
        foreach (var source in sources)
        {
            var (torrentId, torrentKey) = await UpsertTorrentAsync(
                connection,
                transaction,
                source.Torrent,
                now,
                cancellationToken);

            var targetKey = itemId is not null ? $"item:{itemId}" : $"unit:{unitId}";
            var pathKey = NormalizePath(source.FilePath);
            var naturalKey = $"{targetKey}|{torrentKey}|{source.AssetRole.Trim().ToLowerInvariant()}|{pathKey}";
            var existingId = await FindScalarAsync(
                connection,
                transaction,
                "SELECT id FROM media_sources WHERE natural_key = $key;",
                ("$key", naturalKey),
                cancellationToken);

            if (existingId is null)
            {
                await using var insert = CreateCommand(connection, transaction, """
                    INSERT INTO media_sources (
                        id, natural_key, torrent_package_id, catalog_item_id, content_unit_id,
                        asset_role, source_layout, file_index, file_path, mime_type, container,
                        video_quality, language, size_bytes, sort_order)
                    VALUES (
                        $id, $natural_key, $torrent_id, $item_id, $unit_id,
                        $asset_role, $source_layout, $file_index, $file_path, $mime_type, $container,
                        $video_quality, $language, $size_bytes, $sort_order);
                    """);
                AddSourceParameters(insert, NewId(), naturalKey, torrentId, itemId, unitId, source);
                await insert.ExecuteNonQueryAsync(cancellationToken);
            }
            else
            {
                await using var update = CreateCommand(connection, transaction, """
                    UPDATE media_sources
                    SET torrent_package_id = $torrent_id,
                        catalog_item_id = $item_id,
                        content_unit_id = $unit_id,
                        asset_role = $asset_role,
                        source_layout = $source_layout,
                        file_index = $file_index,
                        file_path = $file_path,
                        mime_type = $mime_type,
                        container = $container,
                        video_quality = $video_quality,
                        language = $language,
                        size_bytes = $size_bytes,
                        sort_order = $sort_order
                    WHERE id = $id;
                    """);
                AddSourceParameters(update, existingId, naturalKey, torrentId, itemId, unitId, source, includeNaturalKey: false);
                await update.ExecuteNonQueryAsync(cancellationToken);
            }
        }
    }

    private static void AddSourceParameters(
        SqliteCommand command,
        string sourceId,
        string naturalKey,
        string torrentId,
        string? itemId,
        string? unitId,
        CatalogTransferMediaSource source,
        bool includeNaturalKey = true)
    {
        command.Parameters.AddWithValue("$id", sourceId);
        if (includeNaturalKey)
        {
            command.Parameters.AddWithValue("$natural_key", naturalKey);
        }

        command.Parameters.AddWithValue("$torrent_id", torrentId);
        command.Parameters.AddWithValue("$item_id", DbValue(itemId));
        command.Parameters.AddWithValue("$unit_id", DbValue(unitId));
        command.Parameters.AddWithValue("$asset_role", source.AssetRole.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$source_layout", source.SourceLayout.Trim().ToLowerInvariant());
        command.Parameters.AddWithValue("$file_index", DbValue(source.FileIndex));
        command.Parameters.AddWithValue("$file_path", source.FilePath?.Trim() ?? string.Empty);
        command.Parameters.AddWithValue("$mime_type", DbValue(source.MimeType));
        command.Parameters.AddWithValue("$container", DbValue(source.Container));
        command.Parameters.AddWithValue("$video_quality", DbValue(source.VideoQuality));
        command.Parameters.AddWithValue("$language", DbValue(source.Language));
        command.Parameters.AddWithValue("$size_bytes", DbValue(source.SizeBytes));
        command.Parameters.AddWithValue("$sort_order", source.SortOrder);
    }

    private static async ValueTask<(string Id, string NaturalKey)> UpsertTorrentAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogTransferTorrent torrent,
        string now,
        CancellationToken cancellationToken)
    {
        var metadata = string.IsNullOrWhiteSpace(torrent.TorrentMetadataBase64)
            ? null
            : Convert.FromBase64String(torrent.TorrentMetadataBase64);
        var naturalKey = BuildTorrentNaturalKey(torrent, metadata);
        var torrentId = await FindScalarAsync(
            connection,
            transaction,
            "SELECT id FROM torrent_packages WHERE natural_key = $key;",
            ("$key", naturalKey),
            cancellationToken);

        if (torrentId is null)
        {
            torrentId = NewId();
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO torrent_packages (
                    id, natural_key, display_name, magnet_uri, torrent_metadata,
                    language, info_hash_v1, info_hash_v2, is_private, total_size,
                    source_uri, created_at, updated_at)
                VALUES (
                    $id, $natural_key, $display_name, $magnet_uri, $torrent_metadata,
                    $language, $info_hash_v1, $info_hash_v2, $is_private, $total_size,
                    $source_uri, $created_at, $updated_at);
                """);
            AddTorrentParameters(insert, torrentId, naturalKey, torrent, metadata, now, includeCreatedAt: true);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var update = CreateCommand(connection, transaction, """
                UPDATE torrent_packages
                SET display_name = $display_name,
                    magnet_uri = $magnet_uri,
                    torrent_metadata = $torrent_metadata,
                    language = $language,
                    info_hash_v1 = $info_hash_v1,
                    info_hash_v2 = $info_hash_v2,
                    is_private = $is_private,
                    total_size = $total_size,
                    source_uri = $source_uri,
                    updated_at = $updated_at
                WHERE id = $id;
                """);
            AddTorrentParameters(update, torrentId, naturalKey, torrent, metadata, now, includeCreatedAt: false);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        foreach (var tracker in torrent.Trackers)
        {
            var trackerId = await UpsertTrackerAsync(connection, transaction, tracker, cancellationToken);
            await using var link = CreateCommand(connection, transaction, """
                INSERT INTO torrent_package_trackers (
                    torrent_package_id, tracker_endpoint_id, tier, sort_order)
                VALUES ($torrent_id, $tracker_id, $tier, $sort_order)
                ON CONFLICT (torrent_package_id, tracker_endpoint_id)
                DO UPDATE SET tier = excluded.tier, sort_order = excluded.sort_order;
                """);
            link.Parameters.AddWithValue("$torrent_id", torrentId);
            link.Parameters.AddWithValue("$tracker_id", trackerId);
            link.Parameters.AddWithValue("$tier", tracker.Tier);
            link.Parameters.AddWithValue("$sort_order", tracker.SortOrder);
            await link.ExecuteNonQueryAsync(cancellationToken);
        }

        return (torrentId, naturalKey);
    }

    private static void AddTorrentParameters(
        SqliteCommand command,
        string torrentId,
        string naturalKey,
        CatalogTransferTorrent torrent,
        byte[]? metadata,
        string now,
        bool includeCreatedAt)
    {
        command.Parameters.AddWithValue("$id", torrentId);
        if (includeCreatedAt)
        {
            command.Parameters.AddWithValue("$natural_key", naturalKey);
            command.Parameters.AddWithValue("$created_at", now);
        }

        command.Parameters.AddWithValue("$display_name", DbValue(torrent.DisplayName));
        command.Parameters.AddWithValue("$magnet_uri", DbValue(torrent.MagnetUri));
        command.Parameters.AddWithValue("$torrent_metadata", DbValue(metadata));
        command.Parameters.AddWithValue("$language", LanguageTag.Normalize(torrent.Language));
        command.Parameters.AddWithValue("$info_hash_v1", DbValue(torrent.InfoHashV1));
        command.Parameters.AddWithValue("$info_hash_v2", DbValue(torrent.InfoHashV2));
        command.Parameters.AddWithValue("$is_private", torrent.IsPrivate ? 1 : 0);
        command.Parameters.AddWithValue("$total_size", DbValue(torrent.TotalSize));
        command.Parameters.AddWithValue("$source_uri", DbValue(torrent.SourceUri));
        command.Parameters.AddWithValue("$updated_at", now);
    }

    private static async ValueTask<string> UpsertTrackerAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogTransferTracker tracker,
        CancellationToken cancellationToken)
    {
        var transport = tracker.Transport.Trim().ToLowerInvariant();
        var clientFamily = tracker.ClientFamily.Trim().ToLowerInvariant();
        var endpoint = tracker.Endpoint.Trim();
        var naturalKey = $"{transport}|{clientFamily}|{endpoint.ToUpperInvariant()}";
        var trackerId = await FindScalarAsync(
            connection,
            transaction,
            "SELECT id FROM tracker_endpoints WHERE natural_key = $key;",
            ("$key", naturalKey),
            cancellationToken);

        if (trackerId is null)
        {
            trackerId = NewId();
            await using var insert = CreateCommand(connection, transaction, """
                INSERT INTO tracker_endpoints (
                    id, natural_key, name, endpoint_template, transport,
                    client_family, requires_authentication, credential_key, is_enabled)
                VALUES (
                    $id, $natural_key, $name, $endpoint, $transport,
                    $client_family, $requires_authentication, $credential_key, 1);
                """);
            insert.Parameters.AddWithValue("$id", trackerId);
            insert.Parameters.AddWithValue("$natural_key", naturalKey);
            AddTrackerParameters(insert, tracker, endpoint, transport, clientFamily);
            await insert.ExecuteNonQueryAsync(cancellationToken);
        }
        else
        {
            await using var update = CreateCommand(connection, transaction, """
                UPDATE tracker_endpoints
                SET name = $name,
                    endpoint_template = $endpoint,
                    requires_authentication = $requires_authentication,
                    credential_key = COALESCE($credential_key, credential_key),
                    is_enabled = 1
                WHERE id = $id;
                """);
            update.Parameters.AddWithValue("$id", trackerId);
            AddTrackerParameters(update, tracker, endpoint, transport, clientFamily, includeIdentity: false);
            await update.ExecuteNonQueryAsync(cancellationToken);
        }

        return trackerId;
    }

    private static void AddTrackerParameters(
        SqliteCommand command,
        CatalogTransferTracker tracker,
        string endpoint,
        string transport,
        string clientFamily,
        bool includeIdentity = true)
    {
        command.Parameters.AddWithValue("$name", DbValue(tracker.Name));
        command.Parameters.AddWithValue("$endpoint", endpoint);
        command.Parameters.AddWithValue("$requires_authentication", tracker.RequiresAuthentication ? 1 : 0);
        command.Parameters.AddWithValue("$credential_key", DbValue(tracker.CredentialKey));

        if (includeIdentity)
        {
            command.Parameters.AddWithValue("$transport", transport);
            command.Parameters.AddWithValue("$client_family", clientFamily);
        }
    }

    private static async ValueTask IncrementCatalogRevisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            UPDATE schema_info
            SET value = CAST(CAST(value AS INTEGER) + 1 AS TEXT)
            WHERE key = 'catalog_revision';
            """);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask InsertImportHistoryAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string? sourceName,
        string? documentHash,
        int version,
        int createdCount,
        int updatedCount,
        string now,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            INSERT INTO catalog_import_history (
                id, document_hash, source_name, format_version,
                created_count, updated_count, warning_count, imported_at)
            VALUES (
                $id, $document_hash, $source_name, $format_version,
                $created_count, $updated_count, 0, $imported_at);
            """);
        command.Parameters.AddWithValue("$id", NewId());
        command.Parameters.AddWithValue("$document_hash", DbValue(documentHash));
        command.Parameters.AddWithValue("$source_name", DbValue(sourceName));
        command.Parameters.AddWithValue("$format_version", version);
        command.Parameters.AddWithValue("$created_count", createdCount);
        command.Parameters.AddWithValue("$updated_count", updatedCount);
        command.Parameters.AddWithValue("$imported_at", now);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static string BuildTorrentNaturalKey(CatalogTransferTorrent torrent, byte[]? metadata)
    {
        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV2))
        {
            return $"v2:{torrent.InfoHashV2.Trim().ToUpperInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(torrent.InfoHashV1))
        {
            return $"v1:{torrent.InfoHashV1.Trim().ToUpperInvariant()}";
        }

        if (!string.IsNullOrWhiteSpace(torrent.MagnetUri))
        {
            return $"magnet:{torrent.MagnetUri.Trim()}";
        }

        return $"metadata:{Convert.ToHexString(SHA256.HashData(metadata!))}";
    }

    private static string UnitKindToDatabase(ContentUnitKind kind) => kind switch
    {
        ContentUnitKind.AudioChapter => "audio_chapter",
        _ => kind.ToString().ToLowerInvariant()
    };

    private static string NormalizePath(string? value) =>
        (value ?? string.Empty).Trim().Replace('\\', '/').ToUpperInvariant();

    private static string NewId() => Guid.CreateVersion7().ToString("D");

    private static object DbValue(object? value) => value ?? DBNull.Value;

    private static SqliteCommand CreateCommand(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string commandText)
    {
        var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = commandText;
        return command;
    }

    private static async ValueTask<string?> FindScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) parameter,
        CancellationToken cancellationToken)
    {
        return await FindScalarAsync(connection, transaction, sql, [parameter], cancellationToken);
    }

    private static async ValueTask<string?> FindScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        (string Name, object Value) firstParameter,
        (string Name, object Value) secondParameter,
        CancellationToken cancellationToken)
    {
        return await FindScalarAsync(connection, transaction, sql, [firstParameter, secondParameter], cancellationToken);
    }

    private static async ValueTask<string?> FindScalarAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        IReadOnlyList<(string Name, object Value)> parameters,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, sql);
        foreach (var parameter in parameters)
        {
            command.Parameters.AddWithValue(parameter.Name, parameter.Value);
        }

        return ScalarToNullableString(await command.ExecuteScalarAsync(cancellationToken));
    }

    private static string? ScalarToNullableString(object? value) =>
        value is null or DBNull ? null : Convert.ToString(value);
}

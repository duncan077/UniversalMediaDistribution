using Microsoft.Data.Sqlite;

namespace UniversalMediaDistribution.Catalog.Sqlite;

/// <summary>Moves dependent rows before a rename folds two local titles.</summary>
internal static class SqliteCatalogRowMerger
{
    public static async ValueTask MergeNameCollisionAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string targetId,
        string mediaType,
        string nameKey,
        string subtypeCode,
        CancellationToken cancellationToken)
    {
        await using var lookup = connection.CreateCommand();
        lookup.Transaction = transaction;
        lookup.CommandText = """
            SELECT id FROM catalog_items
            WHERE media_type = $media AND name_key = $name AND id <> $target
                AND (media_type <> 'book' OR subtype_code = $subtype);
            """;
        lookup.Parameters.AddWithValue("$media", mediaType);
        lookup.Parameters.AddWithValue("$name", nameKey);
        lookup.Parameters.AddWithValue("$subtype", subtypeCode);
        lookup.Parameters.AddWithValue("$target", targetId);
        if (await lookup.ExecuteScalarAsync(cancellationToken) is not string sourceId)
        {
            return;
        }

        // Move non-colliding seasons whole; the others keep their units until
        // those units have been matched to the surviving season below.
        await ExecuteAsync("""
            UPDATE OR IGNORE seasons SET catalog_item_id = $target
            WHERE catalog_item_id = $source;
            """);

        var units = new List<(string Source, string? Target, string? Season)>();
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT source.id, target.id, target_season.id
                FROM content_units source
                LEFT JOIN seasons source_season ON source_season.id = source.season_id
                LEFT JOIN seasons target_season
                    ON target_season.catalog_item_id = $target
                    AND target_season.season_number = source_season.season_number
                LEFT JOIN content_units target
                    ON target.catalog_item_id = $target
                    AND target.season_id IS target_season.id
                    AND target.unit_kind = source.unit_kind
                    AND target.display_number_key = source.display_number_key
                WHERE source.catalog_item_id = $source;
                """;
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$target", targetId);
            await using var reader = await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                units.Add((reader.GetString(0),
                    reader.IsDBNull(1) ? null : reader.GetString(1),
                    reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
        }

        foreach (var unit in units)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.Parameters.AddWithValue("$unit", unit.Source);
            if (unit.Target is null)
            {
                command.CommandText = """
                    UPDATE content_units SET catalog_item_id = $target, season_id = $season
                    WHERE id = $unit;
                    """;
                command.Parameters.AddWithValue("$target", targetId);
                command.Parameters.AddWithValue("$season", (object?)unit.Season ?? DBNull.Value);
            }
            else
            {
                command.CommandText = """
                    UPDATE playback_progress SET content_unit_id = $target WHERE content_unit_id = $unit;
                    UPDATE media_sources SET content_unit_id = $target WHERE content_unit_id = $unit;
                    DELETE FROM content_units WHERE id = $unit;
                    """;
                command.Parameters.AddWithValue("$target", unit.Target);
            }

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        // Every profile shares the catalog. Move their rows as well, before
        // ON DELETE CASCADE can remove anything belonging to the folded title.
        await ExecuteAsync("""
            INSERT INTO library_entries
                (profile_id, catalog_item_id, is_favorite, added_at, last_opened_at, user_rating)
            SELECT profile_id, $target, is_favorite, added_at, last_opened_at, user_rating
            FROM library_entries WHERE catalog_item_id = $source
            ON CONFLICT(profile_id, catalog_item_id) DO UPDATE SET
                is_favorite = MAX(library_entries.is_favorite, excluded.is_favorite),
                added_at = MIN(library_entries.added_at, excluded.added_at),
                last_opened_at = COALESCE(MAX(library_entries.last_opened_at, excluded.last_opened_at),
                    library_entries.last_opened_at, excluded.last_opened_at),
                user_rating = COALESCE(library_entries.user_rating, excluded.user_rating);

            INSERT INTO reader_preferences (profile_id, catalog_item_id, display_mode, updated_at)
            SELECT profile_id, $target, display_mode, updated_at
            FROM reader_preferences WHERE catalog_item_id = $source
            ON CONFLICT(profile_id, catalog_item_id) DO UPDATE SET
                display_mode = excluded.display_mode, updated_at = excluded.updated_at
            WHERE excluded.updated_at > reader_preferences.updated_at;

            INSERT OR IGNORE INTO user_media_list_items (list_id, catalog_item_id, added_at)
            SELECT list_id, $target, added_at FROM user_media_list_items WHERE catalog_item_id = $source;

            UPDATE playback_progress SET catalog_item_id = $target WHERE catalog_item_id = $source;
            UPDATE media_sources SET catalog_item_id = $target WHERE catalog_item_id = $source;
            UPDATE OR IGNORE catalog_alternative_names SET catalog_item_id = $target WHERE catalog_item_id = $source;
            UPDATE OR IGNORE catalog_item_genres SET catalog_item_id = $target WHERE catalog_item_id = $source;
            UPDATE OR IGNORE catalog_item_categories SET catalog_item_id = $target WHERE catalog_item_id = $source;
            UPDATE OR IGNORE catalog_item_credits SET catalog_item_id = $target WHERE catalog_item_id = $source;
            UPDATE OR IGNORE distribution_rights SET catalog_item_id = $target WHERE catalog_item_id = $source;
            DELETE FROM catalog_items WHERE id = $source;
            """);

        async ValueTask ExecuteAsync(string sql)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = sql;
            command.Parameters.AddWithValue("$source", sourceId);
            command.Parameters.AddWithValue("$target", targetId);
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
    }
}

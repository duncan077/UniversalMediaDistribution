using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal static partial class SqliteCatalogMigrator
{
    private static async ValueTask ApplyVersionFifteenAsync(
        SqliteConnection connection, CancellationToken cancellationToken)
    {
        await ExecuteAsync(connection, "PRAGMA foreign_keys = OFF;", cancellationToken);
        try
        {
            await using var transaction = (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken);
            var indexes = new List<string>();
            string definition;
            await using (var schema = connection.CreateCommand())
            {
                schema.Transaction = transaction;
                schema.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'table' AND name = 'catalog_items';";
                definition = (string)(await schema.ExecuteScalarAsync(cancellationToken))!;
                schema.CommandText = "SELECT sql FROM sqlite_master WHERE type = 'index' AND tbl_name = 'catalog_items' AND sql IS NOT NULL;";
                await using var reader = await schema.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken)) indexes.Add(reader.GetString(0));
            }

            if (definition.Contains("UNIQUE (media_type, name_key)", StringComparison.Ordinal))
            {
                var rebuilt = definition.Replace("CREATE TABLE catalog_items", "CREATE TABLE catalog_items_v15", StringComparison.Ordinal)
                    .Replace("CREATE TABLE \"catalog_items\"", "CREATE TABLE catalog_items_v15", StringComparison.Ordinal)
                    .Replace("UNIQUE (media_type, name_key)", "UNIQUE (media_type, subtype_code, name_key)", StringComparison.Ordinal);
                await RunAsync(rebuilt + ";\n" + """
                    INSERT INTO catalog_items_v15 SELECT * FROM catalog_items;
                    DROP TABLE catalog_items;
                    ALTER TABLE catalog_items_v15 RENAME TO catalog_items;
                    """);
                foreach (var index in indexes) await RunAsync(index);
            }

            // Old databases held at most one book per name, so the stored
            // subtype unambiguously owns its existing sync records.
            var books = new List<(string Name, string Subtype, string? Id)>();
            await using (var select = connection.CreateCommand())
            {
                select.Transaction = transaction;
                select.CommandText = """
                    SELECT name, subtype_code, sync_id FROM catalog_items WHERE media_type = 'book'
                    UNION SELECT name, subtype_code, sync_id FROM catalog_tombstones WHERE media_type = 'book';
                    """;
                await using var reader = await select.ExecuteReaderAsync(cancellationToken);
                while (await reader.ReadAsync(cancellationToken))
                    books.Add((reader.GetString(0), reader.GetString(1), reader.IsDBNull(2) ? null : reader.GetString(2)));
            }
            foreach (var book in books)
            {
                var oldKey = ContentItemKey.Create(MediaType.Book, book.Name);
                var newKey = ContentItemKey.Create(MediaType.Book, book.Name, book.Subtype);
                if (oldKey == newKey) continue;
                await using var update = connection.CreateCommand();
                update.Transaction = transaction;
                update.CommandText = """
                    UPDATE user_sync_records SET record_key = $new || substr(record_key, length($old) + 1)
                    WHERE (record_kind IN ('progress', 'reader_preference', 'favorite_item') AND record_key = $old)
                        OR (record_kind = 'progress' AND (
                            substr(record_key, 1, length($old) + 9) = $old || ':chapter:' OR
                            substr(record_key, 1, length($old) + 9) = $old || ':episode:' OR
                            substr(record_key, 1, length($old) + 7) = $old || ':track:' OR
                            substr(record_key, 1, length($old) + 15) = $old || ':audio_chapter:'));
                    UPDATE user_sync_records SET record_key = substr(record_key, 1, 33) || $new
                    WHERE record_kind = 'list_item' AND substr(record_key, 34) = $old;
                    """;
                update.Parameters.AddWithValue("$old", oldKey);
                update.Parameters.AddWithValue("$new", newKey);
                await update.ExecuteNonQueryAsync(cancellationToken);
                if (Guid.TryParse(book.Id, out var id) && id == CatalogStableId.Create(MediaType.Book, book.Name))
                {
                    update.CommandText = """
                        UPDATE catalog_items SET sync_id = $newId WHERE sync_id = $id AND subtype_code = $subtype;
                        UPDATE catalog_tombstones SET sync_id = $newId WHERE sync_id = $id AND subtype_code = $subtype;
                        """;
                    update.Parameters.AddWithValue("$id", book.Id!);
                    update.Parameters.AddWithValue("$subtype", book.Subtype);
                    update.Parameters.AddWithValue("$newId", CatalogStableId.Create(MediaType.Book, book.Name, book.Subtype).ToString());
                    await update.ExecuteNonQueryAsync(cancellationToken);
                }
            }
            await using (var check = connection.CreateCommand())
            {
                check.Transaction = transaction;
                check.CommandText = "PRAGMA foreign_key_check;";
                await using var reader = await check.ExecuteReaderAsync(cancellationToken);
                if (await reader.ReadAsync(cancellationToken)) throw new InvalidDataException("Catalog identity migration left a broken reference.");
            }
            await RunAsync("UPDATE schema_info SET value = '15' WHERE key = 'schema_version'; PRAGMA user_version = 15;");
            await transaction.CommitAsync(cancellationToken);

            async ValueTask RunAsync(string sql)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = sql;
                await command.ExecuteNonQueryAsync(cancellationToken);
            }
        }
        finally
        {
            await ExecuteAsync(connection, "PRAGMA foreign_keys = ON;", CancellationToken.None);
        }
    }
}

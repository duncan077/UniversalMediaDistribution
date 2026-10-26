using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal static class SqliteCatalogMetadataUpdater
{
    public static async ValueTask UpdateAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        CatalogMetadataUpdate update,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentNullException.ThrowIfNull(transaction);
        ArgumentNullException.ThrowIfNull(update);

        var itemId = update.CatalogItemId.ToString("D");
        var nameKey = NameNormalizer.Normalize(update.Name);
        await EnsureTargetExistsAsync(
            connection,
            transaction,
            itemId,
            cancellationToken);
        await EnsureNameIsAvailableAsync(
            connection,
            transaction,
            itemId,
            nameKey,
            update.Name,
            cancellationToken);

        // A user edit is the one local change that must win across
        // devices, so it is the one that mints an item version.
        var device = await SqliteUserSyncMetadata.GetOrCreateDeviceAsync(
            connection,
            transaction,
            cancellationToken);
        var version = UserSyncDocumentMerger.CreateLegacyVersion(
            device.DeviceId,
            DateTimeOffset.UtcNow,
            "item-edit:" + itemId);

        await using (var command = CreateCommand(connection, transaction, """
                         UPDATE catalog_items
                         SET name = $name,
                             name_key = $name_key,
                             description = $description,
                             thumbnail_ref = $thumbnail_ref,
                             updated_at = $updated_at,
                             sync_clock = $sync_clock,
                             sync_device = $sync_device,
                             sync_operation = $sync_operation,
                             sync_modified = $sync_modified
                         WHERE id = $id;
                         """))
        {
            command.Parameters.AddWithValue("$id", itemId);
            command.Parameters.AddWithValue("$name", update.Name);
            command.Parameters.AddWithValue("$name_key", nameKey);
            command.Parameters.AddWithValue("$description", update.Description);
            command.Parameters.AddWithValue(
                "$thumbnail_ref",
                update.ThumbnailRef is null ? DBNull.Value : update.ThumbnailRef);
            command.Parameters.AddWithValue(
                "$updated_at",
                DateTimeOffset.UtcNow.ToString("O"));
            command.Parameters.AddWithValue("$sync_clock", version.Clock);
            command.Parameters.AddWithValue("$sync_device", version.DeviceId);
            command.Parameters.AddWithValue(
                "$sync_operation",
                version.OperationId.ToString());
            command.Parameters.AddWithValue(
                "$sync_modified",
                version.ModifiedAt.ToString(
                    "O",
                    System.Globalization.CultureInfo.InvariantCulture));
            if (await command.ExecuteNonQueryAsync(cancellationToken) != 1)
            {
                throw new KeyNotFoundException(
                    "The selected catalog item no longer exists.");
            }
        }

        await ReplaceAlternativeNamesAsync(
            connection,
            transaction,
            itemId,
            update.AlternativeNames,
            cancellationToken);
        await IncrementCatalogRevisionAsync(
            connection,
            transaction,
            cancellationToken);
    }

    private static async ValueTask EnsureTargetExistsAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(
            connection,
            transaction,
            "SELECT 1 FROM catalog_items WHERE id = $id;");
        command.Parameters.AddWithValue("$id", itemId);
        if (await command.ExecuteScalarAsync(cancellationToken) is null)
        {
            throw new KeyNotFoundException(
                "The selected catalog item no longer exists.");
        }
    }

    private static async ValueTask EnsureNameIsAvailableAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        string nameKey,
        string name,
        CancellationToken cancellationToken)
    {
        await using var command = CreateCommand(connection, transaction, """
            SELECT conflict.id
            FROM catalog_items target
            JOIN catalog_items conflict
              ON conflict.media_type = target.media_type
             AND conflict.name_key = $name_key
             AND conflict.id <> target.id
            WHERE target.id = $id
            LIMIT 1;
            """);
        command.Parameters.AddWithValue("$id", itemId);
        command.Parameters.AddWithValue("$name_key", nameKey);
        if (await command.ExecuteScalarAsync(cancellationToken) is not null)
        {
            throw new InvalidOperationException(
                $"Another catalog item already uses the title '{name}'.");
        }
    }

    private static async ValueTask ReplaceAlternativeNamesAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string itemId,
        IReadOnlyList<CatalogAlternativeName> alternativeNames,
        CancellationToken cancellationToken)
    {
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
            await using var command = CreateCommand(connection, transaction, """
                INSERT INTO catalog_alternative_names (
                    id, catalog_item_id, name, name_key, language, alias_type)
                VALUES ($id, $item_id, $name, $name_key, $language, $alias_type);
                """);
            command.Parameters.AddWithValue("$id", Guid.CreateVersion7().ToString("D"));
            command.Parameters.AddWithValue("$item_id", itemId);
            command.Parameters.AddWithValue("$name", alternativeName.Name);
            command.Parameters.AddWithValue("$name_key", alternativeName.NameKey);
            command.Parameters.AddWithValue("$language", alternativeName.Language);
            command.Parameters.AddWithValue("$alias_type", alternativeName.Kind);
            await command.ExecuteNonQueryAsync(cancellationToken);
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
}

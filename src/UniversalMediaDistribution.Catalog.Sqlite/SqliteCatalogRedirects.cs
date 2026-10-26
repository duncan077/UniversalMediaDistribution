using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite;

/// <summary>
/// The local copy of the account's identity redirects: forwarding notes left
/// when two items that carried different sync IDs turned out to be one title.
/// Kept newest-wins per losing ID, the same rule every other synced record
/// follows, so devices converge on the same forwarding table.
/// </summary>
internal static class SqliteCatalogRedirects
{
    public static async ValueTask<IReadOnlyList<UserSyncCatalogRedirect>>
        ReadAllAsync(
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT from_id, to_id, clock, device_id, operation_id, modified_at
            FROM catalog_redirects;
            """;
        var redirects = new List<UserSyncCatalogRedirect>();
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            redirects.Add(new UserSyncCatalogRedirect
            {
                FromId = Guid.Parse(reader.GetString(0)),
                ToId = Guid.Parse(reader.GetString(1)),
                SyncVersion = new UserSyncVersion
                {
                    Clock = reader.GetInt64(2),
                    DeviceId = reader.GetString(3),
                    OperationId = Guid.Parse(reader.GetString(4)),
                    ModifiedAt = DateTimeOffset.Parse(
                        reader.GetString(5),
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind)
                }
            });
        }

        return redirects;
    }

    public static async ValueTask UpsertAllAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        IReadOnlyList<UserSyncCatalogRedirect> redirects,
        CancellationToken cancellationToken)
    {
        foreach (var redirect in redirects)
        {
            await UpsertAsync(
                connection,
                transaction,
                redirect,
                cancellationToken);
        }
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        UserSyncCatalogRedirect redirect,
        CancellationToken cancellationToken)
    {
        await using (var read = connection.CreateCommand())
        {
            read.Transaction = transaction;
            read.CommandText = """
                SELECT clock, device_id, operation_id
                FROM catalog_redirects
                WHERE from_id = $from_id;
                """;
            read.Parameters.AddWithValue(
                "$from_id",
                redirect.FromId.ToString());
            await using var reader =
                await read.ExecuteReaderAsync(cancellationToken);
            if (await reader.ReadAsync(cancellationToken))
            {
                var stored = new UserSyncVersion
                {
                    Clock = reader.GetInt64(0),
                    DeviceId = reader.GetString(1),
                    OperationId = Guid.Parse(reader.GetString(2)),
                    ModifiedAt = redirect.SyncVersion.ModifiedAt
                };
                if (UserSyncDocumentMerger.CompareVersions(
                        redirect.SyncVersion,
                        stored) <= 0)
                {
                    return;
                }
            }
        }

        await using var write = connection.CreateCommand();
        write.Transaction = transaction;
        write.CommandText = """
            INSERT INTO catalog_redirects (
                from_id, to_id, clock, device_id, operation_id, modified_at)
            VALUES (
                $from_id, $to_id, $clock, $device_id, $operation_id,
                $modified_at)
            ON CONFLICT (from_id) DO UPDATE SET
                to_id = excluded.to_id,
                clock = excluded.clock,
                device_id = excluded.device_id,
                operation_id = excluded.operation_id,
                modified_at = excluded.modified_at;
            """;
        write.Parameters.AddWithValue("$from_id", redirect.FromId.ToString());
        write.Parameters.AddWithValue("$to_id", redirect.ToId.ToString());
        write.Parameters.AddWithValue("$clock", redirect.SyncVersion.Clock);
        write.Parameters.AddWithValue(
            "$device_id",
            redirect.SyncVersion.DeviceId);
        write.Parameters.AddWithValue(
            "$operation_id",
            redirect.SyncVersion.OperationId.ToString());
        write.Parameters.AddWithValue(
            "$modified_at",
            redirect.SyncVersion.ModifiedAt.ToString(
                "O",
                CultureInfo.InvariantCulture));
        await write.ExecuteNonQueryAsync(cancellationToken);
    }
}

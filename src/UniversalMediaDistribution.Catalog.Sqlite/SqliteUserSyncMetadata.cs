using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite;

internal sealed record SqliteUserSyncRecord(
    string Kind,
    string Key,
    UserSyncVersion Version,
    bool IsDeleted,
    string? ItemMediaType,
    string? ItemName);

internal sealed record SqliteUserSyncDevice(
    string DeviceId,
    string DeviceName);

internal static class SqliteUserSyncMetadata
{
    public const string ProgressKind = "progress";
    public const string ReaderPreferenceKind = "reader_preference";
    public const string FavoriteKind = "favorite";
    public const string FavoriteItemKind = "favorite_item";
    public const string ListKind = "list";
    public const string ListItemKind = "list_item";

    private const string DeviceIdKey = "remote_server_device_id";
    private const string DeviceNameKey = "remote_server_device_name";
    private const string ClockKey = "user_sync_logical_clock";

    public static string GetCompositeKey(string kind, string key) =>
        kind + "\n" + key;

    public static string GetListKey(Guid listId) =>
        listId.ToString("N");

    public static string GetListItemKey(
        Guid listId,
        string itemKey) =>
        GetListKey(listId) + "|" + itemKey;

    public static async ValueTask<
            IReadOnlyDictionary<string, SqliteUserSyncRecord>>
        ReadAllAsync(
            Guid profileId,
            SqliteConnection connection,
            CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                record_kind,
                record_key,
                sync_clock,
                sync_device_id,
                sync_operation_id,
                modified_at,
                is_deleted,
                item_media_type,
                item_name
            FROM user_sync_records
            WHERE profile_id = $profile;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        var result = new Dictionary<string, SqliteUserSyncRecord>(
            StringComparer.Ordinal);
        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var version = new UserSyncVersion
            {
                Clock = reader.GetInt64(2),
                DeviceId = reader.GetString(3),
                OperationId = Guid.Parse(reader.GetString(4)),
                ModifiedAt = DateTimeOffset.Parse(
                    reader.GetString(5),
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.RoundtripKind)
            };
            UserSyncDocumentValidator.ValidateSyncVersion(version);
            var record = new SqliteUserSyncRecord(
                reader.GetString(0),
                reader.GetString(1),
                version,
                reader.GetInt32(6) != 0,
                reader.IsDBNull(7) ? null : reader.GetString(7),
                reader.IsDBNull(8) ? null : reader.GetString(8));
            result.Add(
                GetCompositeKey(record.Kind, record.Key),
                record);
        }

        return result;
    }

    public static async ValueTask<UserSyncVersion>
        CreateLocalVersionAsync(
            SqliteConnection connection,
            SqliteTransaction transaction,
            DateTimeOffset modifiedAt,
            CancellationToken cancellationToken)
    {
        if (modifiedAt == default)
        {
            modifiedAt = DateTimeOffset.UtcNow;
        }

        var device = await GetOrCreateDeviceAsync(
            connection,
            transaction,
            cancellationToken);
        var current = await ReadClockAsync(
            connection,
            transaction,
            cancellationToken);
        var physical = Math.Max(
            1,
            modifiedAt.ToUnixTimeMilliseconds());
        var next = Math.Max(physical, checked(current + 1));
        await WriteSettingAsync(
            connection,
            transaction,
            ClockKey,
            next.ToString(CultureInfo.InvariantCulture),
            cancellationToken);
        return new UserSyncVersion
        {
            Clock = next,
            DeviceId = device.DeviceId,
            OperationId = Guid.CreateVersion7(),
            ModifiedAt = modifiedAt
        };
    }

    public static async ValueTask ObserveClockAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        long clock,
        CancellationToken cancellationToken)
    {
        if (clock <= 0)
        {
            return;
        }

        var current = await ReadClockAsync(
            connection,
            transaction,
            cancellationToken);
        if (clock > current)
        {
            await WriteSettingAsync(
                connection,
                transaction,
                ClockKey,
                clock.ToString(CultureInfo.InvariantCulture),
                cancellationToken);
        }
    }

    public static async ValueTask<SqliteUserSyncDevice>
        GetOrCreateDeviceAsync(
            SqliteConnection connection,
            SqliteTransaction? transaction,
            CancellationToken cancellationToken)
    {
        var values = new Dictionary<string, string>(StringComparer.Ordinal);
        await using (var command = connection.CreateCommand())
        {
            command.Transaction = transaction;
            command.CommandText = """
                SELECT key, value
                FROM application_settings
                WHERE key IN ($device_id, $device_name);
                """;
            command.Parameters.AddWithValue("$device_id", DeviceIdKey);
            command.Parameters.AddWithValue("$device_name", DeviceNameKey);
            await using var reader =
                await command.ExecuteReaderAsync(cancellationToken);
            while (await reader.ReadAsync(cancellationToken))
            {
                values[reader.GetString(0)] = reader.GetString(1);
            }
        }

        var deviceId = values.GetValueOrDefault(DeviceIdKey);
        try
        {
            UserSyncDocumentValidator.ValidateDeviceId(deviceId!);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                ArgumentNullException)
        {
            deviceId = Guid.CreateVersion7().ToString("N");
            await WriteSettingAsync(
                connection,
                transaction,
                DeviceIdKey,
                deviceId,
                cancellationToken);
        }

        var deviceName = values.GetValueOrDefault(DeviceNameKey);
        try
        {
            UserSyncDocumentValidator.ValidateDeviceName(deviceName!);
        }
        catch (Exception exception) when (
            exception is InvalidDataException or
                ArgumentNullException)
        {
            deviceName = NormalizeMachineName(Environment.MachineName);
            await WriteSettingAsync(
                connection,
                transaction,
                DeviceNameKey,
                deviceName,
                cancellationToken);
        }

        return new SqliteUserSyncDevice(deviceId!, deviceName!);
    }

    public static async ValueTask UpsertAsync(
        Guid profileId,
        SqliteConnection connection,
        SqliteTransaction transaction,
        string kind,
        string key,
        UserSyncVersion version,
        bool isDeleted,
        string? itemMediaType,
        string? itemName,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(kind);
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        UserSyncDocumentValidator.ValidateSyncVersion(version);
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO user_sync_records (
                profile_id,
                record_kind,
                record_key,
                sync_clock,
                sync_device_id,
                sync_operation_id,
                modified_at,
                is_deleted,
                item_media_type,
                item_name)
            VALUES (
                $profile,
                $record_kind,
                $record_key,
                $sync_clock,
                $sync_device_id,
                $sync_operation_id,
                $modified_at,
                $is_deleted,
                $item_media_type,
                $item_name)
            ON CONFLICT(profile_id, record_kind, record_key) DO UPDATE SET
                sync_clock = excluded.sync_clock,
                sync_device_id = excluded.sync_device_id,
                sync_operation_id = excluded.sync_operation_id,
                modified_at = excluded.modified_at,
                is_deleted = excluded.is_deleted,
                item_media_type = excluded.item_media_type,
                item_name = excluded.item_name;
            """;
        command.Parameters.AddWithValue("$profile", profileId.ToString("D"));
        command.Parameters.AddWithValue("$record_kind", kind);
        command.Parameters.AddWithValue("$record_key", key);
        command.Parameters.AddWithValue("$sync_clock", version.Clock);
        command.Parameters.AddWithValue(
            "$sync_device_id",
            version.DeviceId);
        command.Parameters.AddWithValue(
            "$sync_operation_id",
            version.OperationId.ToString("D"));
        command.Parameters.AddWithValue(
            "$modified_at",
            version.ModifiedAt.ToString("O"));
        command.Parameters.AddWithValue(
            "$is_deleted",
            isDeleted ? 1 : 0);
        command.Parameters.AddWithValue(
            "$item_media_type",
            (object?)itemMediaType ?? DBNull.Value);
        command.Parameters.AddWithValue(
            "$item_name",
            (object?)itemName ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async ValueTask<long> ReadClockAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT value
            FROM application_settings
            WHERE key = $key;
            """;
        command.Parameters.AddWithValue("$key", ClockKey);
        var value = Convert.ToString(
            await command.ExecuteScalarAsync(cancellationToken),
            CultureInfo.InvariantCulture);
        return string.IsNullOrWhiteSpace(value)
            ? 0
            : long.TryParse(
                value,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var clock) &&
              clock >= 0
                ? clock
                : throw new InvalidDataException(
                    "The local sync clock is invalid.");
    }

    private static async ValueTask WriteSettingAsync(
        SqliteConnection connection,
        SqliteTransaction? transaction,
        string key,
        string value,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO application_settings (key, value, updated_at)
            VALUES ($key, $value, $updated_at)
            ON CONFLICT(key) DO UPDATE SET
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

    private static string NormalizeMachineName(string value)
    {
        var normalized = new string(
                (value ?? string.Empty)
                .Where(static character => !char.IsControl(character))
                .ToArray())
            .Trim();
        return normalized.Length switch
        {
            0 => "This device",
            > UserSyncDocumentValidator.MaximumDeviceNameLength =>
                normalized[
                    ..UserSyncDocumentValidator.MaximumDeviceNameLength],
            _ => normalized
        };
    }
}

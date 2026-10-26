using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.Catalog.Sqlite;

/// <summary>
/// The server address and the account name, kept per profile rather than per
/// installation: with one server account behind each profile, whose account
/// this is changes when the person using the device changes.
/// </summary>
public sealed class SqliteRemoteServerSettingsStore(
    SqliteCatalogDatabase database) : IRemoteServerSettingsStore
{
    private const string ServiceUrlKey = "remote_server_url";
    private const string UsernameKey = "remote_server_username";
    private const string DeviceIdKey = "remote_server_device_id";
    private const string DeviceNameKey = "remote_server_device_name";
    private const string OnlineCatalogKey = "remote_online_catalog_enabled";

    public async ValueTask<RemoteServerSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            var profileId = database.ActiveProfileId.ToString("D");
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT key, value
                FROM profile_settings
                WHERE profile_id = $profile
                  AND key IN (
                    $service_url,
                    $username,
                    $device_id,
                    $device_name,
                    $online_catalog);
                """;
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$service_url", ServiceUrlKey);
            command.Parameters.AddWithValue("$username", UsernameKey);
            command.Parameters.AddWithValue("$device_id", DeviceIdKey);
            command.Parameters.AddWithValue("$device_name", DeviceNameKey);
            command.Parameters.AddWithValue(
                "$online_catalog",
                OnlineCatalogKey);

            var values = new Dictionary<string, string>(
                StringComparer.Ordinal);
            await using (var reader =
                         await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var generatedDeviceId =
                !values.TryGetValue(DeviceIdKey, out var deviceId) ||
                string.IsNullOrWhiteSpace(deviceId);
            if (generatedDeviceId)
            {
                deviceId = Guid.CreateVersion7().ToString("N");
            }

            var generatedDeviceName =
                !values.TryGetValue(DeviceNameKey, out var deviceName) ||
                string.IsNullOrWhiteSpace(deviceName);
            if (generatedDeviceName)
            {
                deviceName = Environment.MachineName;
            }

            var defaults = new RemoteServerSettings();
            var settings = new RemoteServerSettings
            {
                ServiceUrl = values.GetValueOrDefault(ServiceUrlKey) ??
                    defaults.ServiceUrl,
                Username = values.GetValueOrDefault(UsernameKey) ??
                    string.Empty,
                DeviceId = deviceId!,
                DeviceName = deviceName!,
                OnlineCatalogEnabled =
                    values.TryGetValue(
                        OnlineCatalogKey,
                        out var onlineCatalogEnabled)
                        ? string.Equals(
                            onlineCatalogEnabled,
                            "true",
                            StringComparison.OrdinalIgnoreCase)
                        : defaults.OnlineCatalogEnabled
            }.Normalize();

            if (generatedDeviceId ||
                generatedDeviceName ||
                !values.ContainsKey(ServiceUrlKey) ||
                !values.ContainsKey(OnlineCatalogKey))
            {
                await UpsertAsync(
                    connection,
                    profileId,
                    settings,
                    cancellationToken);
            }

            return settings;
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SaveAsync(
        RemoteServerSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await UpsertAsync(
                connection,
                database.ActiveProfileId.ToString("D"),
                normalized,
                cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    private static async ValueTask UpsertAsync(
        SqliteConnection connection,
        string profileId,
        RemoteServerSettings settings,
        CancellationToken cancellationToken)
    {
        await using var transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(
                cancellationToken);
        var values = new[]
        {
            (ServiceUrlKey, settings.ServiceUrl),
            (UsernameKey, settings.Username),
            (DeviceIdKey, settings.DeviceId),
            (DeviceNameKey, settings.DeviceName),
            (
                OnlineCatalogKey,
                settings.OnlineCatalogEnabled ? "true" : "false")
        };
        foreach (var (key, value) in values)
        {
            await using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO profile_settings
                    (profile_id, key, value, updated_at)
                VALUES ($profile, $key, $value, $updated_at)
                ON CONFLICT (profile_id, key)
                DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$profile", profileId);
            command.Parameters.AddWithValue("$key", key);
            command.Parameters.AddWithValue("$value", value);
            command.Parameters.AddWithValue(
                "$updated_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        await transaction.CommitAsync(cancellationToken);
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
}

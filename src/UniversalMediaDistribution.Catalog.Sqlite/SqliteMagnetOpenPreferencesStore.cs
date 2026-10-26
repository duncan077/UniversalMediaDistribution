using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteMagnetOpenPreferencesStore : IMagnetOpenPreferencesStore
{
    private const string SettingKey = "magnet_open_preferences";
    private readonly SqliteCatalogDatabase _database;

    public SqliteMagnetOpenPreferencesStore(SqliteCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<MagnetOpenPreferences> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = "SELECT value FROM application_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SettingKey);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not string json)
            {
                return new MagnetOpenPreferences();
            }

            try
            {
                return JsonSerializer.Deserialize(
                           json,
                           SqliteJsonSerializerContext.Default
                               .MagnetOpenPreferences) ??
                       new MagnetOpenPreferences();
            }
            catch (JsonException)
            {
                return new MagnetOpenPreferences();
            }
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask SaveAsync(
        MagnetOpenPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO application_settings (key, value, updated_at)
                VALUES ($key, $value, $updated_at)
                ON CONFLICT (key)
                DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", SettingKey);
            command.Parameters.AddWithValue(
                "$value",
                JsonSerializer.Serialize(
                    preferences,
                    SqliteJsonSerializerContext.Default
                        .MagnetOpenPreferences));
            command.Parameters.AddWithValue("$updated_at", DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken)
    {
        var connection = _database.CreateConnection();
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
}

using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.Metadata;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteMetadataProviderSettingsStore :
    IMetadataProviderSettingsStore
{
    private const string SettingKey = "metadata_provider_settings";
    private readonly SqliteCatalogDatabase _database;

    public SqliteMetadataProviderSettingsStore(SqliteCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<MetadataProviderSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT value FROM application_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SettingKey);
            var value = await command.ExecuteScalarAsync(cancellationToken);
            if (value is not string json)
            {
                return new MetadataProviderSettings();
            }

            try
            {
                return (JsonSerializer.Deserialize(
                        json,
                        SqliteJsonSerializerContext.Default
                            .MetadataProviderSettings) ??
                    new MetadataProviderSettings()).Normalize();
            }
            catch (Exception exception)
                when (exception is JsonException or ArgumentException)
            {
                return new MetadataProviderSettings();
            }
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask SaveAsync(
        MetadataProviderSettings settings,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(settings);
        var normalized = settings.Normalize();
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
                    normalized,
                    SqliteJsonSerializerContext.Default
                        .MetadataProviderSettings));
            command.Parameters.AddWithValue(
                "$updated_at",
                DateTimeOffset.UtcNow.ToString("O"));
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    private async ValueTask<SqliteConnection> OpenConnectionAsync(
        CancellationToken cancellationToken)
    {
        var connection = _database.CreateConnection();
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

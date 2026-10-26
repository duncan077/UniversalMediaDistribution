using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteTorrentDownloadPreferencesStore :
    ITorrentDownloadPreferencesStore
{
    private const string SettingKey = "torrent_download_preferences";
    private readonly SqliteCatalogDatabase _database;

    public SqliteTorrentDownloadPreferencesStore(SqliteCatalogDatabase database)
    {
        _database = database ?? throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<TorrentDownloadPreferences> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT value FROM application_settings WHERE key = $key;";
            command.Parameters.AddWithValue("$key", SettingKey);
            var value = Convert.ToString(
                await command.ExecuteScalarAsync(cancellationToken));
            return new TorrentDownloadPreferences(
                string.Equals(
                    value,
                    "delete",
                    StringComparison.OrdinalIgnoreCase)
                    ? TorrentSessionEndBehavior.DeleteDownloads
                    : TorrentSessionEndBehavior.KeepDownloads);
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask SaveAsync(
        TorrentDownloadPreferences preferences,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(preferences);
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection = await OpenConnectionAsync(
                cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO application_settings (key, value, updated_at)
                VALUES ($key, $value, $updated_at)
                ON CONFLICT (key)
                DO UPDATE SET
                    value = excluded.value,
                    updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue("$key", SettingKey);
            command.Parameters.AddWithValue(
                "$value",
                preferences.Normalize().SessionEndBehavior is
                    TorrentSessionEndBehavior.DeleteDownloads
                    ? "delete"
                    : "keep");
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

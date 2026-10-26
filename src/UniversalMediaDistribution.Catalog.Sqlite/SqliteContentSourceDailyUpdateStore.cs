using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Application.ContentSources;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteContentSourceDailyUpdateStore :
    IContentSourceDailyUpdateStore
{
    private const string SettingKeyPrefix =
        "content_source_daily_update.";
    private readonly SqliteCatalogDatabase _database;

    public SqliteContentSourceDailyUpdateStore(
        SqliteCatalogDatabase database)
    {
        _database = database ??
            throw new ArgumentNullException(nameof(database));
    }

    public async ValueTask<DateOnly?> LoadLastSuccessfulDateAsync(
        string sourceKey,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceKey(sourceKey);
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText =
                "SELECT value FROM application_settings WHERE key = $key;";
            command.Parameters.AddWithValue(
                "$key",
                SettingKeyPrefix + sourceKey);
            var value = await command.ExecuteScalarAsync(
                cancellationToken);
            return value is string date &&
                   DateOnly.TryParseExact(
                       date,
                       "yyyy-MM-dd",
                       CultureInfo.InvariantCulture,
                       DateTimeStyles.None,
                       out var parsed)
                ? parsed
                : null;
        }
        finally
        {
            _database.OperationGate.Release();
        }
    }

    public async ValueTask SaveLastSuccessfulDateAsync(
        string sourceKey,
        DateOnly date,
        CancellationToken cancellationToken = default)
    {
        ValidateSourceKey(sourceKey);
        await _database.InitializeAsync(cancellationToken);
        await _database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                INSERT INTO application_settings (key, value, updated_at)
                VALUES ($key, $value, $updated_at)
                ON CONFLICT (key)
                DO UPDATE SET value = excluded.value, updated_at = excluded.updated_at;
                """;
            command.Parameters.AddWithValue(
                "$key",
                SettingKeyPrefix + sourceKey);
            command.Parameters.AddWithValue(
                "$value",
                date.ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture));
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

    private static void ValidateSourceKey(string sourceKey)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceKey);
        if (sourceKey.Length > 200)
        {
            throw new ArgumentException(
                "A daily-update source key cannot exceed 200 characters.",
                nameof(sourceKey));
        }
    }
}

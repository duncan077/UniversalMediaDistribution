using System.Globalization;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.ContentFiltering.Subscriptions;

namespace UniversalMediaDistribution.Catalog.Sqlite;

public sealed class SqliteContentFilterSettingsStore(
    SqliteCatalogDatabase database) : IContentFilterSettingsStore
{
    private const string ModeKey = "content_filter_mode";
    private const string HideAdultContentKey = "content_filter_hide_adult";
    private const string LastRefreshKey = "content_filter_last_refresh";

    public async ValueTask<ContentFilterSettings> LoadAsync(
        CancellationToken cancellationToken = default)
    {
        await database.InitializeAsync(cancellationToken);
        await database.OperationGate.WaitAsync(cancellationToken);
        try
        {
            await using var connection =
                await OpenConnectionAsync(cancellationToken);
            await using var command = connection.CreateCommand();
            command.CommandText = """
                SELECT key, value
                FROM application_settings
                WHERE key IN ($mode, $hide_adult, $last_refresh);
                """;
            command.Parameters.AddWithValue("$mode", ModeKey);
            command.Parameters.AddWithValue("$hide_adult", HideAdultContentKey);
            command.Parameters.AddWithValue("$last_refresh", LastRefreshKey);

            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            await using (var reader =
                         await command.ExecuteReaderAsync(cancellationToken))
            {
                while (await reader.ReadAsync(cancellationToken))
                {
                    values[reader.GetString(0)] = reader.GetString(1);
                }
            }

            var defaults = new ContentFilterSettings();
            return new ContentFilterSettings
            {
                Mode =
                    values.TryGetValue(ModeKey, out var mode) &&
                    Enum.TryParse<FilteringMode>(mode, true, out var parsed)
                        ? parsed
                        : defaults.Mode,
                HideAdultContent =
                    values.TryGetValue(HideAdultContentKey, out var hideAdult) &&
                    bool.TryParse(hideAdult, out var parsedHideAdult)
                        ? parsedHideAdult
                        : defaults.HideAdultContent,
                LastRefreshUtc =
                    values.TryGetValue(LastRefreshKey, out var refreshed) &&
                    DateTimeOffset.TryParse(
                        refreshed,
                        CultureInfo.InvariantCulture,
                        DateTimeStyles.RoundtripKind,
                        out var timestamp)
                        ? timestamp
                        : null
            }.Normalize();
        }
        finally
        {
            database.OperationGate.Release();
        }
    }

    public async ValueTask SaveAsync(
        ContentFilterSettings settings,
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
            await using var transaction =
                (SqliteTransaction)await connection.BeginTransactionAsync(
                    cancellationToken);
            var values = new[]
            {
                (ModeKey, normalized.Mode.ToString()),
                (HideAdultContentKey, normalized.HideAdultContent.ToString()),
                (
                    LastRefreshKey,
                    normalized.LastRefreshUtc?.ToString(
                        "O",
                        CultureInfo.InvariantCulture) ?? string.Empty)
            };
            foreach (var (key, value) in values)
            {
                await using var command = connection.CreateCommand();
                command.Transaction = transaction;
                command.CommandText = """
                    INSERT INTO application_settings (key, value, updated_at)
                    VALUES ($key, $value, $updated_at)
                    ON CONFLICT (key)
                    DO UPDATE SET
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

            await transaction.CommitAsync(cancellationToken);
        }
        finally
        {
            database.OperationGate.Release();
        }
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

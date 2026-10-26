using System.Globalization;

namespace UniversalMediaDistribution.Server;

internal sealed record ProviderIntervalRequest(string? ProviderId, int IntervalSeconds);
internal sealed record ProviderIntervalResponse(string ProviderId, int IntervalSeconds, string Message);

/// <summary>Shared durable settings for the HTTP worker and separate cron process.</summary>
internal sealed class CatalogCrawlerSettingsStore(ServerDatabase database)
{
    internal const int DefaultIntervalSeconds = 2;
    internal const int MinimumIntervalSeconds = 1;
    internal const int MaximumIntervalSeconds = 60;

    internal Dictionary<string, int> ReadIntervals()
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT provider_id,interval_seconds FROM crawler_provider_settings;";
        using var reader = command.ExecuteReader();
        var intervals = new Dictionary<string, int>(StringComparer.Ordinal);
        while (reader.Read()) intervals.Add(reader.GetString(0), reader.GetInt32(1));
        return intervals;
    }

    internal void SetInterval(string providerId, int seconds)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerId);
        if (seconds is < MinimumIntervalSeconds or > MaximumIntervalSeconds)
            throw new ArgumentException("El intervalo debe ser un número entero entre 1 y 60 segundos.");
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO crawler_provider_settings(provider_id,interval_seconds,updated_at) VALUES($id,$seconds,$now)
            ON CONFLICT(provider_id) DO UPDATE SET interval_seconds=excluded.interval_seconds,updated_at=excluded.updated_at;
            """;
        command.Parameters.AddWithValue("$id", providerId);
        command.Parameters.AddWithValue("$seconds", seconds);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture));
        command.ExecuteNonQuery();
    }
}

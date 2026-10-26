using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Server;

internal sealed record CrawlProviderState(string? Cursor, bool Complete, DateTimeOffset? LastDiscovery, DateTimeOffset? RetryAt, int Failures, DateTimeOffset? LastAttempt, string? FailureKind);
internal sealed record CrawlTitle(string ExternalId, ApiContentSearchItem Item, int Failures);

internal sealed class CatalogCrawlerStore(ServerDatabase database)
{
    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    internal bool TryAcquireLease(string owner, DateTimeOffset now, TimeSpan lifetime)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO crawler_leases(name,owner,expires_at) VALUES('catalog',$owner,$expires)
            ON CONFLICT(name) DO UPDATE SET owner=excluded.owner,expires_at=excluded.expires_at WHERE crawler_leases.expires_at <= $now;
            """;
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$expires", Stamp(now + lifetime));
        command.Parameters.AddWithValue("$now", Stamp(now));
        return command.ExecuteNonQuery() == 1;
    }

    internal void ReleaseLease(string owner) => Execute("DELETE FROM crawler_leases WHERE name='catalog' AND owner=$owner;", ("$owner", owner));
    internal bool OwnsLease(string owner, DateTimeOffset now)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM crawler_leases WHERE name='catalog' AND owner=$owner AND expires_at > $now;";
        command.Parameters.AddWithValue("$owner", owner);
        command.Parameters.AddWithValue("$now", Stamp(now));
        return (long)command.ExecuteScalar()! == 1;
    }
    internal void StartRun(string id, DateTimeOffset now, string status = "running") => Execute("""
        INSERT INTO crawler_runs(id,started_at,status) VALUES($id,$now,$status)
        ON CONFLICT(id) DO UPDATE SET status=excluded.status;
        """, ("$id", id), ("$now", Stamp(now)), ("$status", status));
    internal void FinishRun(string id, string status, int changed, int failures, DateTimeOffset now) =>
        Execute("UPDATE crawler_runs SET finished_at=$now,status=$status,titles_updated=$changed,failures=$failures WHERE id=$id;",
            ("$id", id), ("$now", Stamp(now)), ("$status", status), ("$changed", changed), ("$failures", failures));

    internal CrawlProviderState State(string provider)
    {
        Execute("INSERT INTO crawler_providers(provider_id) VALUES($provider) ON CONFLICT DO NOTHING;", ("$provider", provider));
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT p.cursor,p.backfill_complete,p.last_discovery,p.retry_at,p.failures,t.last_attempt,p.failure_kind
            FROM crawler_providers p LEFT JOIN crawler_provider_turns t ON t.provider_id=p.provider_id
            WHERE p.provider_id=$provider;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        using var reader = command.ExecuteReader();
        reader.Read();
        return new(reader.IsDBNull(0) ? null : reader.GetString(0), reader.GetBoolean(1), Timestamp(reader, 2), Timestamp(reader, 3), reader.GetInt32(4), Timestamp(reader, 5), reader.IsDBNull(6) ? null : reader.GetString(6));
    }

    internal void ProviderAttempted(string provider, DateTimeOffset now) => Execute("""
        INSERT INTO crawler_provider_turns(provider_id,last_attempt) VALUES($provider,$now)
        ON CONFLICT(provider_id) DO UPDATE SET last_attempt=excluded.last_attempt;
        """, ("$provider", provider), ("$now", Stamp(now)));

    internal void Enqueue(string provider, IReadOnlyList<ApiContentSearchItem> items, DateTimeOffset now, bool updates, string? nextCursor = null, bool discovery = false, string? requestedCursor = null)
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        if (discovery)
        {
            var ids = items.Select(item => item.ExternalId).Distinct().Order(StringComparer.Ordinal);
            var fingerprint = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(string.Concat(ids.Select(id => id.Length + ":" + id)))));
            using var page = connection.CreateCommand();
            page.Transaction = transaction;
            page.CommandText = "SELECT fingerprint FROM crawler_page_state WHERE provider_id=$provider;";
            page.Parameters.AddWithValue("$provider", provider);
            if (requestedCursor is not null && page.ExecuteScalar() as string == fingerprint)
                throw new InvalidDataException("The provider repeated its previous catalog page; the cursor was not advanced.");
            page.CommandText = """
                INSERT INTO crawler_page_state(provider_id,fingerprint) VALUES($provider,$fingerprint)
                ON CONFLICT(provider_id) DO UPDATE SET fingerprint=excluded.fingerprint;
                """;
            page.Parameters.AddWithValue("$fingerprint", fingerprint);
            page.ExecuteNonQuery();
        }
        foreach (var item in items.DistinctBy(item => item.ExternalId))
        {
            // Persist a title identifier only, never UnitRoutes, player URLs,
            // art URLs, reader pages or a provider-specific chapter address.
            var preview = CatalogMetadataOnly.Sanitize(item.PreviewItem) with { Units = [], Seasons = [] };
            using var command = connection.CreateCommand();
            command.Transaction = transaction;
            command.CommandText = """
                INSERT INTO crawler_titles(provider_id,external_id,preview,discovered_at,last_seen,due_at)
                VALUES($provider,$external,$preview,$now,$now,$now)
                ON CONFLICT(provider_id,external_id) DO UPDATE SET preview=excluded.preview,last_seen=excluded.last_seen,
                    due_at=CASE WHEN $updates=1 AND crawler_titles.failures=0 THEN min(crawler_titles.due_at,excluded.due_at) ELSE crawler_titles.due_at END;
                """;
            command.Parameters.AddWithValue("$provider", provider);
            command.Parameters.AddWithValue("$external", item.ExternalId);
            command.Parameters.AddWithValue("$preview", JsonSerializer.Serialize(preview, ServerJsonSerializerContext.Default.CatalogTransferItem));
            command.Parameters.AddWithValue("$now", Stamp(now));
            command.Parameters.AddWithValue("$updates", updates ? 1 : 0);
            command.ExecuteNonQuery();
        }
        using var checkpoint = connection.CreateCommand();
        checkpoint.Transaction = transaction;
        checkpoint.CommandText = discovery ? """
            UPDATE crawler_providers SET cursor=$cursor,backfill_complete=$complete,last_discovery=$now WHERE provider_id=$provider;
            """ : "UPDATE crawler_providers SET last_updates=$now WHERE provider_id=$provider;";
        checkpoint.Parameters.AddWithValue("$cursor", (object?)nextCursor ?? DBNull.Value);
        checkpoint.Parameters.AddWithValue("$complete", nextCursor is null ? 1 : 0);
        checkpoint.Parameters.AddWithValue("$provider", provider);
        checkpoint.Parameters.AddWithValue("$now", Stamp(now));
        checkpoint.ExecuteNonQuery();
        transaction.Commit();
    }

    internal IReadOnlyList<CrawlTitle> Due(string provider, DateTimeOffset now, int limit, bool retryFailures = false,
        IReadOnlySet<string>? attempted = null)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        // Interleave never-loaded titles with due refreshes. Neither daily
        // updates nor the historical backfill may starve the other queue.
        command.CommandText = """
            SELECT external_id,preview,failures FROM (
                SELECT external_id,preview,failures,
                    row_number() OVER(PARTITION BY (last_success IS NULL) ORDER BY due_at,external_id) AS queue_rank
                FROM crawler_titles WHERE provider_id=$provider AND
                    (due_at <= $now OR ($retry=1 AND failures > 0 AND failure_kind <> 'throttled'))
                    AND external_id NOT IN (SELECT value FROM json_each($attempted))
            ) ORDER BY queue_rank,external_id LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$provider", provider);
        command.Parameters.AddWithValue("$now", Stamp(now));
        command.Parameters.AddWithValue("$limit", limit);
        command.Parameters.AddWithValue("$retry", retryFailures ? 1 : 0);
        command.Parameters.AddWithValue("$attempted", attempted is { Count: > 0 }
            ? JsonSerializer.Serialize(attempted.ToArray(), ServerJsonSerializerContext.Default.StringArray) : "[]");
        using var reader = command.ExecuteReader();
        var result = new List<CrawlTitle>();
        while (reader.Read())
        {
            var id = reader.GetString(0);
            result.Add(new(id, new ApiContentSearchItem(id,
                JsonSerializer.Deserialize(reader.GetString(1), ServerJsonSerializerContext.Default.CatalogTransferItem)!, RequireProviderTitle: true), reader.GetInt32(2)));
        }
        return result;
    }

    internal void TitleSucceeded(string provider, string external, Guid id, DateTimeOffset now) => Execute("""
        UPDATE crawler_titles SET item_id=$id,last_success=$now,due_at=$due,failures=0,failure_kind=NULL
        WHERE provider_id=$provider AND external_id=$external;
        """, ("$provider", provider), ("$external", external), ("$id", id.ToString("N")), ("$now", Stamp(now)), ("$due", Stamp(now.AddDays(7))));

    internal void TitleFailed(string provider, CrawlTitle title, string kind, DateTimeOffset now) => Execute("""
        UPDATE crawler_titles SET failures=failures+1,failure_kind=$kind,due_at=$due WHERE provider_id=$provider AND external_id=$external;
        """, ("$provider", provider), ("$external", title.ExternalId), ("$kind", kind), ("$due", Stamp(now + Backoff(title.Failures))));

    internal void TitleUnsupported(string provider, string external, DateTimeOffset now) => Execute("""
        UPDATE crawler_titles SET failures=0,failure_kind='unsupported',due_at=$due
        WHERE provider_id=$provider AND external_id=$external;
        """, ("$provider", provider), ("$external", external), ("$due", Stamp(now.AddDays(7))));

    internal void ProviderFailed(string provider, string kind, int failures, DateTimeOffset now, TimeSpan? retryAfter = null) => Execute("""
        UPDATE crawler_providers SET failures=failures+1,failure_kind=$kind,retry_at=$retry WHERE provider_id=$provider;
        """, ("$provider", provider), ("$kind", kind), ("$retry", Stamp(now + (retryAfter ?? Backoff(failures)))));

    internal void ProviderSucceeded(string provider, DateTimeOffset now) => Execute("""
        UPDATE crawler_providers SET last_success=$now,failures=0,failure_kind=NULL,retry_at=NULL WHERE provider_id=$provider;
        """, ("$provider", provider), ("$now", Stamp(now)));

    internal static TimeSpan Backoff(int previousFailures) => TimeSpan.FromHours(Math.Min(24, Math.Pow(2, Math.Clamp(previousFailures, 0, 5))));
    private static DateTimeOffset? Timestamp(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
    private void Execute(string sql, params (string Key, object Value)[] parameters)
    {
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        foreach (var (key, value) in parameters) command.Parameters.AddWithValue(key, value);
        command.ExecuteNonQuery();
    }
}

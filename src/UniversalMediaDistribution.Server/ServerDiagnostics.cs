using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Routing;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server;

internal sealed record ServerDiagnosticEvent(
    long Id, DateTimeOffset OccurredAt, string Level, string Source,
    string? ProviderId, string? RunId, string Stage, string Status,
    string Message, int? HttpStatus);

internal sealed record ServerDiagnosticLogPage(
    IReadOnlyList<ServerDiagnosticEvent> Items, long? Before);

internal sealed record ProviderDiagnostic(
    string Id, string Name, string Status, bool BackfillComplete,
    long Discovered, long Loaded, long Due, long FailedTitles,
    int ConsecutiveFailures, string? FailureKind, DateTimeOffset? LastAttempt,
    DateTimeOffset? LastSuccess, DateTimeOffset? LastDiscovery,
    DateTimeOffset? LastUpdates, DateTimeOffset? RetryAt, int IntervalSeconds = CatalogCrawlerSettingsStore.DefaultIntervalSeconds);

internal sealed record CrawlRunDiagnostic(
    string Id, DateTimeOffset StartedAt, DateTimeOffset? FinishedAt,
    string Status, long TitlesUpdated, long Failures);

internal sealed record CatalogDiagnostic(long Titles, long Revision, DateTimeOffset? UpdatedAt);

internal sealed record RuntimeDiagnostic(
    DateTimeOffset StartedAt, long WorkingSetBytes, long DatabaseBytes,
    long Requests, long FailedRequests);

internal sealed record ServerDiagnosticSnapshot(
    DateTimeOffset GeneratedAt, RuntimeDiagnostic Runtime, CatalogDiagnostic Catalog,
    bool CrawlerRunning, IReadOnlyList<ProviderDiagnostic> Providers,
    IReadOnlyList<CrawlRunDiagnostic> Runs);

internal sealed class ServerDiagnosticsStore(ServerDatabase database)
{
    internal const int MaximumEvents = 10_000;
    internal static readonly TimeSpan Retention = TimeSpan.FromDays(1);
    private static readonly Regex UrlRegex = new(
        @"https?://[^\s<>""']+", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    internal void Write(
        string level, string source, string stage, string status, string message,
        string? providerId = null, string? runId = null, int? httpStatus = null,
        DateTimeOffset? occurredAt = null)
    {
        if (level != "error") return;
        var now = DateTimeOffset.UtcNow;
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            INSERT INTO server_events(occurred_at,level,source,provider_id,run_id,stage,status,message,http_status)
            VALUES($now,$level,$source,$provider,$run,$stage,$status,$message,$http);
            """;
        command.Parameters.AddWithValue("$now", Stamp(occurredAt ?? now));
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$source", source);
        command.Parameters.AddWithValue("$provider", (object?)providerId ?? DBNull.Value);
        command.Parameters.AddWithValue("$run", (object?)runId ?? DBNull.Value);
        command.Parameters.AddWithValue("$stage", stage);
        command.Parameters.AddWithValue("$status", status);
        command.Parameters.AddWithValue("$message", SafeMessage(message));
        command.Parameters.AddWithValue("$http", (object?)httpStatus ?? DBNull.Value);
        command.ExecuteNonQuery();
        Prune(connection, transaction, now);
        transaction.Commit();
    }

    internal void Prune()
    {
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: false);
        Prune(connection, transaction, DateTimeOffset.UtcNow);
        transaction.Commit();
    }

    private static void Prune(SqliteConnection connection, SqliteTransaction transaction, DateTimeOffset now)
    {
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            DELETE FROM server_events WHERE level <> 'error' OR occurred_at <= $cutoff;
            DELETE FROM server_events WHERE id <= (
                SELECT id FROM server_events ORDER BY id DESC LIMIT 1 OFFSET $maximum
            );
            """;
        command.Parameters.AddWithValue("$cutoff", Stamp(now - Retention));
        command.Parameters.AddWithValue("$maximum", MaximumEvents);
        command.ExecuteNonQuery();
    }

    internal void TryPrune()
    {
        try { Prune(); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"diagnostics-cleanup-failed; type={exception.GetType().Name}");
        }
    }

    internal void TryWrite(
        string level, string source, string stage, string status, string message,
        string? providerId = null, string? runId = null, int? httpStatus = null)
    {
        try { Write(level, source, stage, status, message, providerId, runId, httpStatus); }
        catch (Exception exception) when (exception is SqliteException or IOException or UnauthorizedAccessException)
        {
            // Diagnostics must not turn a successful catalog/sync operation into a failure.
            Console.Error.WriteLine($"diagnostics-write-failed; type={exception.GetType().Name}");
        }
    }

    internal ServerDiagnosticLogPage ReadLogs(string? provider, string? level, long? before, int limit)
    {
        if (limit is < 1 or > 200 || before is <= 0 || provider?.Length > 128 ||
            level is not (null or "info" or "warning" or "error"))
            throw new ArgumentException("Invalid diagnostic log filters.");
        Prune();
        using var connection = database.Open();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT id,occurred_at,level,source,provider_id,run_id,stage,status,message,http_status
            FROM server_events WHERE level='error' AND ($provider IS NULL OR provider_id=$provider)
              AND ($level IS NULL OR level=$level) AND ($before IS NULL OR id < $before)
              AND occurred_at > $cutoff
            ORDER BY id DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$provider", (object?)provider ?? DBNull.Value);
        command.Parameters.AddWithValue("$level", (object?)level ?? DBNull.Value);
        command.Parameters.AddWithValue("$before", (object?)before ?? DBNull.Value);
        command.Parameters.AddWithValue("$limit", limit + 1);
        command.Parameters.AddWithValue("$cutoff", Stamp(DateTimeOffset.UtcNow - Retention));
        using var reader = command.ExecuteReader();
        var events = new List<ServerDiagnosticEvent>();
        while (reader.Read())
            events.Add(new(reader.GetInt64(0), Timestamp(reader, 1)!.Value, reader.GetString(2), reader.GetString(3),
                Text(reader, 4), Text(reader, 5), reader.GetString(6), reader.GetString(7), reader.GetString(8),
                reader.IsDBNull(9) ? null : reader.GetInt32(9)));
        return new(events.Take(limit).ToArray(), events.Count > limit ? events[limit - 1].Id : null);
    }

    internal ServerDiagnosticSnapshot Snapshot(
        IReadOnlyList<ApiContentProviderDescriptor> descriptors,
        IReadOnlySet<string> disabled, RuntimeDiagnostic runtime)
    {
        var now = DateTimeOffset.UtcNow;
        var intervals = new CatalogCrawlerSettingsStore(database).ReadIntervals();
        using var connection = database.Open();
        using var transaction = connection.BeginTransaction(deferred: true);
        using var command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = """
            SELECT (SELECT COUNT(*) FROM catalog_items),revision,updated_at FROM catalog_state WHERE singleton=1;
            """;
        CatalogDiagnostic catalog;
        using (var reader = command.ExecuteReader())
            catalog = reader.Read() ? new(reader.GetInt64(0), reader.GetInt64(1), Timestamp(reader, 2)) : new(0, 0, null);

        command.CommandText = "SELECT owner FROM crawler_leases WHERE name='catalog' AND expires_at > $now;";
        command.Parameters.AddWithValue("$now", Stamp(now));
        var activeRun = command.ExecuteScalar() as string;
        command.CommandText = """
            SELECT p.provider_id,p.backfill_complete,p.failures,p.failure_kind,t.last_attempt,
                p.last_success,p.last_discovery,p.last_updates,p.retry_at,
                COALESCE(q.total,0),COALESCE(q.loaded,0),COALESCE(q.due,0),COALESCE(q.failed,0)
            FROM crawler_providers p LEFT JOIN crawler_provider_turns t ON t.provider_id=p.provider_id
            LEFT JOIN (
                SELECT provider_id,COUNT(*) total,SUM(last_success IS NOT NULL) loaded,
                    SUM(due_at <= $now) due,SUM(failures > 0) failed FROM crawler_titles GROUP BY provider_id
            ) q ON q.provider_id=p.provider_id ORDER BY p.provider_id;
            """;
        var providers = new Dictionary<string, ProviderDiagnostic>(StringComparer.Ordinal);
        using (var reader = command.ExecuteReader())
            while (reader.Read())
            {
                var id = reader.GetString(0);
                var failures = reader.GetInt32(2);
                var failedTitles = reader.GetInt64(12);
                var kind = Text(reader, 3);
                var retry = Timestamp(reader, 8);
                var status = disabled.Contains(id) ? "disabled" : kind == "configuration_required" ? kind :
                    retry > now ? "backoff" : failures > 0 || failedTitles > 0 ? "attention" :
                    reader.IsDBNull(5) ? "pending" : "ok";
                providers.Add(id, new(id, id, status, reader.GetBoolean(1),
                    reader.GetInt64(9), reader.GetInt64(10), reader.GetInt64(11), failedTitles,
                    failures, kind, Timestamp(reader, 4), Timestamp(reader, 5), Timestamp(reader, 6),
                    Timestamp(reader, 7), retry, intervals.GetValueOrDefault(id, CatalogCrawlerSettingsStore.DefaultIntervalSeconds)));
            }
        foreach (var descriptor in descriptors)
        {
            if (providers.TryGetValue(descriptor.Id, out var provider))
                providers[descriptor.Id] = provider with { Name = descriptor.DisplayName };
            else
                providers.Add(descriptor.Id, new(descriptor.Id, descriptor.DisplayName,
                    disabled.Contains(descriptor.Id) ? "disabled" : "not_run", false,
                    0, 0, 0, 0, 0, null, null, null, null, null, null,
                    intervals.GetValueOrDefault(descriptor.Id, CatalogCrawlerSettingsStore.DefaultIntervalSeconds)));
        }
        command.CommandText = """
            SELECT id,started_at,finished_at,status,titles_updated,failures FROM crawler_runs
            ORDER BY started_at DESC,id DESC LIMIT 25;
            """;
        var runs = new List<CrawlRunDiagnostic>();
        using (var reader = command.ExecuteReader())
            while (reader.Read())
                runs.Add(new(reader.GetString(0), Timestamp(reader, 1)!.Value, Timestamp(reader, 2),
                    (reader.GetString(3) is "running" or "queued") && reader.GetString(0) != activeRun ? "interrupted" : reader.GetString(3),
                    reader.GetInt64(4), reader.GetInt64(5)));
        return new(now, runtime, catalog, activeRun is not null,
            providers.Values.OrderBy(provider => provider.Name, StringComparer.OrdinalIgnoreCase).ToArray(), runs);
    }

    internal static string DescribeFailure(Exception exception, string? title = null)
    {
        var message = exception switch
        {
            CatalogProviderThrottledException => "El proveedor pidió reducir la frecuencia de las solicitudes.",
            HttpRequestException { StatusCode: { } status } => $"HTTP {(int)status} ({status}).",
            HttpRequestException => "No se pudo conectar con el proveedor.",
            OperationCanceledException => "La solicitud superó el tiempo de espera o fue cancelada.",
            CatalogTransferValidationException validation => $"Datos del catálogo rechazados: {validation.Message}",
            JsonException json => $"JSON inválido; ruta={json.Path ?? "$"}; línea={json.LineNumber}; posición={json.BytePositionInLine}.",
            InvalidDataException data => data.Message,
            _ => $"La operación falló ({exception.GetType().Name})."
        };
        var context = string.IsNullOrWhiteSpace(title) ? "" : $"Título «{(title.Length > 200 ? title[..200] : title)}». ";
        // The same description reaches both the database and the cron's output.
        return SafeMessage(context + message);
    }

    private static string SafeMessage(string value)
    {
        var bounded = value.Length > 1024 ? value[..1024] : value;
        return UrlRegex.Replace(bounded, "[URL omitida]").Replace('\r', ' ').Replace('\n', ' ');
    }

    private static string Stamp(DateTimeOffset value) => value.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture);
    private static string? Text(SqliteDataReader reader, int index) => reader.IsDBNull(index) ? null : reader.GetString(index);
    private static DateTimeOffset? Timestamp(SqliteDataReader reader, int index) =>
        reader.IsDBNull(index) ? null : DateTimeOffset.Parse(reader.GetString(index), CultureInfo.InvariantCulture);
}

internal sealed class ServerDiagnosticsRetention(ServerDiagnosticsStore store, TimeSpan? interval = null) : BackgroundService
{
    public override Task StartAsync(CancellationToken cancellationToken)
    {
        store.TryPrune();
        return base.StartAsync(cancellationToken);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var timer = new PeriodicTimer(interval ?? TimeSpan.FromMinutes(1));
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken)) store.TryPrune();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { }
    }
}

internal sealed class ServerDiagnostics(ServerDatabase database)
{
    internal ServerDiagnosticsStore Store { get; } = new(database);
    internal void SetProviderInterval(string providerId, int seconds)
    {
        new CatalogCrawlerSettingsStore(database).SetInterval(providerId, seconds);
        Store.TryWrite("info", "server", "configuration", "updated",
            $"Intervalo del scraper guardado: {seconds} s. Se aplicará desde la próxima ejecución.", providerId);
    }
    private readonly DateTimeOffset _startedAt = DateTimeOffset.UtcNow;
    private long _requests;
    private long _failedRequests;

    internal RuntimeDiagnostic Runtime()
    {
        long bytes = 0;
        foreach (var suffix in new[] { "", "-wal", "-shm" })
        {
            var file = new FileInfo(database.DatabasePath + suffix);
            if (file.Exists) bytes += file.Length;
        }
        return new(_startedAt, Environment.WorkingSet, bytes,
            Interlocked.Read(ref _requests), Interlocked.Read(ref _failedRequests));
    }

    internal void RequestCompleted(HttpContext context, Exception? exception)
    {
        Interlocked.Increment(ref _requests);
        if (exception is OperationCanceledException && context.RequestAborted.IsCancellationRequested) return;
        if (exception is null && context.Response.StatusCode < 500) return;
        Interlocked.Increment(ref _failedRequests);
        var route = (context.GetEndpoint() as RouteEndpoint)?.RoutePattern.RawText ?? "unmatched-route";
        var status = exception is null ? context.Response.StatusCode : 500;
        Store.TryWrite("error", "http", "request", "http_error",
            $"{context.Request.Method} {route}: HTTP {status}. " +
            (exception is null ? "" : ServerDiagnosticsStore.DescribeFailure(exception)), httpStatus: status);
    }
}

internal static class ServerDiagnosticsEndpoints
{
    private static readonly IReadOnlyList<ApiContentProviderDescriptor> Descriptors = ReadDescriptors();
    private static readonly string Html = ReadResource("index.html");
    private static readonly string Script = ReadResource("app.js");
    private static readonly string Styles = ReadResource("style.css");

    internal static void Map(WebApplication app, ServerDiagnostics diagnostics, string? adminToken, ManualCatalogCrawler crawler)
    {
        app.MapGet("/admin", () => Results.Content(Html, "text/html; charset=utf-8"));
        app.MapGet("/admin/app.js", () => Results.Content(Script, "text/javascript; charset=utf-8"));
        app.MapGet("/admin/style.css", () => Results.Content(Styles, "text/css; charset=utf-8"));
        app.MapPut("/api/v1/admin/diagnostics/provider-interval", (HttpContext context, ProviderIntervalRequest request) =>
        {
            var denied = AdminAuthorization.TryAuthorize(context, adminToken);
            if (denied is not null) return denied;
            if (!Descriptors.Any(provider => provider.Id == request.ProviderId))
                return Results.BadRequest(new ErrorResponse("invalid_provider", "El proveedor seleccionado no existe."));
            try
            {
                diagnostics.SetProviderInterval(request.ProviderId!, request.IntervalSeconds);
                return Results.Json(new ProviderIntervalResponse(request.ProviderId!, request.IntervalSeconds,
                    "Intervalo guardado. Se aplicará desde la próxima ejecución manual o automática."),
                    ServerJsonSerializerContext.Default.ProviderIntervalResponse);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new ErrorResponse("invalid_provider_interval", exception.Message));
            }
        });
        app.MapPost("/api/v1/admin/diagnostics/crawl", (HttpContext context, ManualCrawlRequest request) =>
        {
            var denied = AdminAuthorization.TryAuthorize(context, adminToken);
            if (denied is not null) return denied;
            var response = crawler.Start(request);
            var status = response.Status switch
            {
                "accepted" => StatusCodes.Status202Accepted,
                "already_running" => StatusCodes.Status409Conflict,
                "stopping" => StatusCodes.Status503ServiceUnavailable,
                _ => StatusCodes.Status400BadRequest
            };
            return Results.Json(response, ServerJsonSerializerContext.Default.ManualCrawlResponse, statusCode: status);
        });
        app.MapGet("/api/v1/admin/diagnostics", (HttpContext context) =>
        {
            var denied = AdminAuthorization.TryAuthorize(context, adminToken);
            if (denied is not null) return denied;
            var disabled = (Environment.GetEnvironmentVariable("UMD_CRAWLER_DISABLED_PROVIDERS") ?? "")
                .Split(',', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).ToHashSet(StringComparer.Ordinal);
            return Results.Json(diagnostics.Store.Snapshot(Descriptors, disabled, diagnostics.Runtime()),
                ServerJsonSerializerContext.Default.ServerDiagnosticSnapshot);
        });
        app.MapGet("/api/v1/admin/diagnostics/logs", (HttpContext context, string? provider, string? level, long? before, int? limit) =>
        {
            var denied = AdminAuthorization.TryAuthorize(context, adminToken);
            if (denied is not null) return denied;
            try
            {
                return Results.Json(diagnostics.Store.ReadLogs(provider, level, before, limit ?? 100),
                    ServerJsonSerializerContext.Default.ServerDiagnosticLogPage);
            }
            catch (ArgumentException exception)
            {
                return Results.BadRequest(new ErrorResponse("invalid_diagnostic_filters", exception.Message));
            }
        });
    }

    private static string ReadResource(string file)
    {
        using var stream = typeof(ServerDiagnosticsEndpoints).Assembly.GetManifestResourceStream(
            "UniversalMediaDistribution.Server.Admin." + file)
            ?? throw new InvalidOperationException("The server administration page was not packaged.");
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }

    private static ApiContentProviderDescriptor[] ReadDescriptors()
    {
        using var client = new HttpClient();
        return BuiltInApiContentProviders.Create(client).Select(provider => provider.Descriptor).ToArray();
    }
}

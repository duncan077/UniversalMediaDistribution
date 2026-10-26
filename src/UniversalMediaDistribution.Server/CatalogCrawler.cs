using System.Diagnostics;
using System.Net;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Server;

internal sealed record CatalogCrawlerOptions
{
    public int PagesPerProvider { get; init; } = 2;
    public TimeSpan MinimumTitleInterval { get; init; } = TimeSpan.FromSeconds(2);
    public IReadOnlyDictionary<string, TimeSpan> ProviderIntervals { get; init; } = new Dictionary<string, TimeSpan>();
    public TimeSpan MaximumRunTime { get; init; } = TimeSpan.FromMinutes(45);
    public TimeSpan OperationTimeout { get; init; } = TimeSpan.FromMinutes(3);
    public IReadOnlyDictionary<string, Uri> BaseUrls { get; init; } = new Dictionary<string, Uri>();
    public IReadOnlySet<string> DisabledProviders { get; init; } = new HashSet<string>();
    public bool RetryFailures { get; init; }
}

internal sealed record CatalogCrawlResult(bool Acquired, string Status, int TitlesUpdated, int Failures);

internal sealed class CatalogCrawler(
    ServerDatabase database,
    IReadOnlyList<ApiContentProviderRegistration> providers,
    CatalogCrawlerOptions options,
    Action<string>? report = null)
{
    public async Task<CatalogCrawlResult> RunAsync(CancellationToken cancellationToken, string? reservedRunId = null)
    {
        if (options.PagesPerProvider is < 1 or > 100 || options.MinimumTitleInterval < TimeSpan.Zero ||
            options.MinimumTitleInterval > TimeSpan.FromMinutes(1) ||
            options.ProviderIntervals.Values.Any(interval => interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromMinutes(1)) ||
            options.MaximumRunTime <= TimeSpan.Zero || options.MaximumRunTime > TimeSpan.FromMinutes(50) ||
            options.OperationTimeout <= TimeSpan.Zero || options.OperationTimeout > TimeSpan.FromMinutes(20))
            throw new ArgumentException("Invalid crawler limits.");
        var store = new CatalogCrawlerStore(database);
        var diagnostics = new ServerDiagnosticsStore(database);
        var catalog = new SqliteOnlineCatalogStore(database.Root);
        var run = reservedRunId ?? Guid.CreateVersion7().ToString("N");
        if (reservedRunId is not null && !store.OwnsLease(run, DateTimeOffset.UtcNow))
            throw new InvalidOperationException("The reserved crawler lease is no longer owned by this run.");
        if (reservedRunId is null && !store.TryAcquireLease(run, DateTimeOffset.UtcNow, options.MaximumRunTime + TimeSpan.FromMinutes(5)))
        {
            diagnostics.TryWrite("info", "crawler", "run", "already_running", "Ya hay una ejecución del scraper activa.", runId: run);
            return new(false, "already_running", 0, 0);
        }
        var changed = 0;
        var failures = 0;
        var status = "completed";
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        budget.CancelAfter(options.MaximumRunTime);
        try
        {
            store.StartRun(run, DateTimeOffset.UtcNow);
            Log("info", "run", "running", "Ejecución del scraper iniciada.");
            // Oldest attempt first, including interrupted attempts and providers
            // whose directory is complete. Neither may monopolize later runs.
            var ready = providers.Select(provider => (Provider: provider, State: store.State(provider.Descriptor.Id)))
                .OrderBy(pair => pair.State.LastAttempt ?? DateTimeOffset.MinValue).ToArray();
            // One sequential worker per registered provider; a large queue or
            // slow site must not consume the entire budget before others run.
            await Task.WhenAll(ready.Select(async pair =>
            {
                try { await RunProviderAsync(pair.Provider, pair.State); }
                catch { await budget.CancelAsync(); throw; }
            }));

            async Task RunProviderAsync(ApiContentProviderRegistration registration, CrawlProviderState state)
            {
                budget.Token.ThrowIfCancellationRequested();
                var id = registration.Descriptor.Id;
                if (options.DisabledProviders.Contains(id) ||
                    state.RetryAt > DateTimeOffset.UtcNow &&
                    (!options.RetryFailures || state.FailureKind is "throttled" or "configuration_required")) return;
                var interval = options.ProviderIntervals.GetValueOrDefault(id, options.MinimumTitleInterval);
                using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(interval);
                store.ProviderAttempted(id, DateTimeOffset.UtcNow);
                var providerChanged = 0;
                var discovered = 0;
                var details = 0;
                Log("info", "provider", "running", $"Revisando proveedor; intervalo={interval.TotalSeconds:0.###} s.", id);
                var root = options.BaseUrls.GetValueOrDefault(id) ??
                    (Uri.TryCreate(registration.Descriptor.DefaultBaseUrl, UriKind.Absolute, out var configured) ? configured : null);
                if (root is null)
                {
                    store.ProviderFailed(id, "configuration_required", state.Failures, DateTimeOffset.UtcNow, TimeSpan.FromDays(1));
                    Log("warning", "configuration", "configuration_required", "Falta configurar la URL del proveedor.", id);
                    return;
                }
                try
                {
                    if (registration.GetProvider() is not IApiCatalogProvider provider)
                        throw new NotSupportedException("Provider has no catalog discovery implementation.");
                    var providerFailed = false;
                    Exception? discoveryFailure = null;
                    if (provider.SupportsDailyUpdates)
                    {
                        try
                        {
                            var updates = await Operation(token => provider.LoadDailyUpdatesAsync(root, token).AsTask(), budget.Token);
                            discovered += updates.Count;
                            if (updates.Count > 0) store.Enqueue(id, updates, DateTimeOffset.UtcNow, updates: true);
                        }
                        catch (Exception exception) when (!budget.IsCancellationRequested && exception is not CatalogProviderThrottledException)
                        {
                            Interlocked.Increment(ref failures);
                            providerFailed = true;
                            discoveryFailure = exception;
                            Log("error", "updates", FailureKind(exception), ServerDiagnosticsStore.DescribeFailure(exception), id, exception);
                        }
                    }
                    try
                    {
                        if (!state.Complete || state.LastDiscovery < DateTimeOffset.UtcNow.AddDays(-7))
                        {
                            var cursor = state.Complete ? null : state.Cursor;
                            for (var page = 0; page < options.PagesPerProvider; page++)
                            {
                                var result = await Operation(token => provider.ReadCatalogPageAsync(root, cursor, token).AsTask(), budget.Token);
                                if (result.IsTruncated || result.Items.Count == 0 || result.ContinuationToken is not null && result.ContinuationToken == cursor)
                                    throw new InvalidDataException("Catalog discovery did not advance safely.");
                                store.Enqueue(id, result.Items, DateTimeOffset.UtcNow, updates: false, result.ContinuationToken, discovery: true, requestedCursor: cursor);
                                discovered += result.Items.Count;
                                cursor = result.ContinuationToken;
                                if (cursor is null) break;
                            }
                        }
                    }
                    catch (Exception exception) when (!budget.IsCancellationRequested && exception is not CatalogProviderThrottledException)
                    {
                        Interlocked.Increment(ref failures);
                        providerFailed = true;
                        discoveryFailure = exception;
                        Log("error", "discovery", FailureKind(exception), ServerDiagnosticsStore.DescribeFailure(exception), id, exception);
                    }
                    var consecutiveFailures = 0;
                    long? previousTitleStart = null;
                    foreach (var title in PendingTitles(id))
                    {
                        budget.Token.ThrowIfCancellationRequested();
                        if (previousTitleStart is { } started)
                        {
                            var delay = interval - Stopwatch.GetElapsedTime(started);
                            if (delay > TimeSpan.Zero) await Task.Delay(delay, budget.Token);
                        }
                        budget.Token.ThrowIfCancellationRequested();
                        previousTitleStart = Stopwatch.GetTimestamp();
                        details++;
                        try
                        {
                            var item = await Operation(token => provider.LoadCatalogItemAsync(root, title.Item, token).AsTask(), budget.Token);
                            var result = catalog.MergeScraped(id, title.ExternalId, item, budget.Token);
                            store.TitleSucceeded(id, title.ExternalId, result.ItemId, DateTimeOffset.UtcNow);
                            if (result.Changed) { Interlocked.Increment(ref changed); providerChanged++; }
                            consecutiveFailures = 0;
                        }
                        catch (ApiCatalogItemUnsupportedException exception) when (!budget.IsCancellationRequested)
                        {
                            store.TitleUnsupported(id, title.ExternalId, DateTimeOffset.UtcNow);
                            consecutiveFailures = 0;
                            Log("info", "detail", "unsupported", exception.Message, id);
                        }
                        catch (Exception exception) when (!budget.IsCancellationRequested)
                        {
                            Interlocked.Increment(ref failures);
                            providerFailed = true;
                            store.TitleFailed(id, title, FailureKind(exception), DateTimeOffset.UtcNow);
                            Log("error", "detail", FailureKind(exception), ServerDiagnosticsStore.DescribeFailure(exception, title.Item.PreviewItem.Name), id, exception);
                            if (IsProviderFailure(exception) || ++consecutiveFailures >= 3) throw;
                        }
                    }
                    if (discoveryFailure is not null)
                        store.ProviderFailed(id, FailureKind(discoveryFailure), state.Failures, DateTimeOffset.UtcNow);
                    else store.ProviderSucceeded(id, DateTimeOffset.UtcNow);
                    Log(providerFailed ? "warning" : "info", "provider", providerFailed ? "partial" : "ok",
                        $"discovered={discovered}; details={details}; updated={providerChanged}", id);
                }
                catch (Exception exception) when (!budget.IsCancellationRequested)
                {
                    Interlocked.Increment(ref failures);
                    var kind = FailureKind(exception);
                    store.ProviderFailed(id, kind, state.Failures, DateTimeOffset.UtcNow, (exception as CatalogProviderThrottledException)?.RetryAfter);
                    Log("error", "provider", kind, ServerDiagnosticsStore.DescribeFailure(exception), id, exception);
                }
            }
        }
        catch (OperationCanceledException) when (budget.IsCancellationRequested)
        {
            status = cancellationToken.IsCancellationRequested ? "cancelled" : "budget_exhausted";
        }
        catch
        {
            status = "failed";
            throw;
        }
        finally
        {
            if (status == "completed" && failures > 0) status = "partial";
            try
            {
                store.FinishRun(run, status, changed, failures, DateTimeOffset.UtcNow);
                Log(status is "completed" ? "info" : "warning", "run", status,
                    $"updated={changed}; failures={failures}");
            }
            finally { store.ReleaseLease(run); }
        }
        return new(true, status, changed, failures);

        IEnumerable<CrawlTitle> PendingTitles(string provider)
        {
            // Bound preview allocations, not the amount of work. Excluding
            // attempts prevents explicit retry mode from looping over a failed
            // title again in the same run, even if its backoff is bypassed.
            var attempted = new HashSet<string>(StringComparer.Ordinal);
            while (true)
            {
                budget.Token.ThrowIfCancellationRequested();
                var batch = store.Due(provider, DateTimeOffset.UtcNow, 100, options.RetryFailures, attempted);
                if (batch.Count == 0) yield break;
                foreach (var title in batch)
                {
                    attempted.Add(title.ExternalId);
                    yield return title;
                }
            }
        }

        void Log(string level, string stage, string result, string message, string? provider = null, Exception? exception = null)
        {
            diagnostics.TryWrite(level, "crawler", stage, result, message, provider, run,
                exception is HttpRequestException { StatusCode: { } code } ? (int)code : null);
            report?.Invoke($"provider={provider ?? "catalog"}; stage={stage}; status={result}; {message}");
        }
    }

    private async Task<T> Operation<T>(Func<CancellationToken, Task<T>> action, CancellationToken token)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(options.OperationTimeout);
        return await action(timeout.Token);
    }

    private static bool IsProviderFailure(Exception exception) => exception is CatalogProviderThrottledException ||
        exception is HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized or HttpStatusCode.TooManyRequests };
    private static string FailureKind(Exception exception) => exception switch
    {
        CatalogProviderThrottledException => "throttled",
        OperationCanceledException => "timeout",
        HttpRequestException { StatusCode: HttpStatusCode.Forbidden or HttpStatusCode.Unauthorized } => "challenge_or_access_denied",
        HttpRequestException { StatusCode: HttpStatusCode.NotFound } => "not_found",
        HttpRequestException => "network",
        NotSupportedException => "unsupported",
        _ => "parse_or_storage_error"
    };
}

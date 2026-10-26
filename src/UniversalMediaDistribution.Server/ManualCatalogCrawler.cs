using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Server;

internal sealed record ManualCrawlRequest(string? ProviderId = null, bool RetryFailures = false);
internal sealed record ManualCrawlResponse(string Status, string? RunId, string? ProviderId, string Message);

/// <summary>Owns admin-triggered background runs through HTTP disconnects and orderly shutdown.</summary>
internal sealed class ManualCatalogCrawler : IHostedService, IDisposable
{
    private readonly ServerDatabase _database;
    private readonly IReadOnlyList<ApiContentProviderRegistration> _providers;
    private readonly Func<CatalogCrawlerOptions> _readOptions;
    private readonly HttpClient? _http;
    private readonly CancellationTokenSource _shutdown = new();
    private readonly object _gate = new();
    private Task? _running;
    private bool _stopping;

    public ManualCatalogCrawler(ServerDatabase database)
    {
        _database = database;
        _http = CatalogCrawlerHttpClient.Create();
        _providers = BuiltInApiContentProviders.Create(_http);
        _readOptions = () => CatalogCrawlerConfiguration.Read(_providers, database);
    }

    internal ManualCatalogCrawler(ServerDatabase database, IReadOnlyList<ApiContentProviderRegistration> providers,
        Func<CatalogCrawlerOptions> readOptions)
    {
        _database = database;
        _providers = providers;
        _readOptions = readOptions;
    }

    internal Task Completion { get { lock (_gate) return _running ?? Task.CompletedTask; } }

    internal ManualCrawlResponse Start(ManualCrawlRequest request)
    {
        var providerId = string.IsNullOrWhiteSpace(request.ProviderId) ? null : request.ProviderId.Trim();
        var selected = providerId is null ? _providers : _providers.Where(provider => provider.Descriptor.Id == providerId).ToArray();
        if (selected.Count == 0)
            return new("invalid_provider", null, providerId, "El proveedor seleccionado no existe.");
        CatalogCrawlerOptions options;
        try { options = _readOptions() with { RetryFailures = request.RetryFailures }; }
        catch (ArgumentException exception) { return new("invalid_configuration", null, providerId, exception.Message); }
        if (providerId is not null && options.DisabledProviders.Contains(providerId))
            return new("disabled_provider", null, providerId, "El proveedor está deshabilitado en la configuración del servidor.");
        if (providerId is not null && !options.BaseUrls.ContainsKey(providerId) && selected[0].Descriptor.DefaultBaseUrl.Length == 0)
            return new("configuration_required", null, providerId, "Falta configurar la URL de este proveedor en el servidor.");
        if (selected.All(provider => options.DisabledProviders.Contains(provider.Descriptor.Id)))
            return new("disabled_provider", null, providerId, "Todos los proveedores están deshabilitados.");

        lock (_gate)
        {
            if (_stopping) return new("stopping", null, providerId, "El servidor se está deteniendo.");
            if (_running is { IsCompleted: false }) return Busy(providerId);
            var run = Guid.CreateVersion7().ToString("N");
            var store = new CatalogCrawlerStore(_database);
            if (!store.TryAcquireLease(run, DateTimeOffset.UtcNow, options.MaximumRunTime + TimeSpan.FromMinutes(5)))
                return Busy(providerId);
            try
            {
                store.StartRun(run, DateTimeOffset.UtcNow, "queued");
                new ServerDiagnosticsStore(_database).TryWrite("info", "crawler", "run", "queued",
                    providerId is null ? "Ejecución manual solicitada para todos los proveedores habilitados." : "Ejecución manual solicitada para este proveedor.",
                    providerId, run);
                var stopping = _shutdown.Token;
                _running = Task.Run(() => ExecuteAsync(run, selected, options, stopping));
            }
            catch
            {
                store.ReleaseLease(run);
                throw;
            }
            return new("accepted", run, providerId, "Scraper iniciado. Puedes seguir su actividad en las ejecuciones y logs del panel.");
        }
    }

    private async Task ExecuteAsync(string run, IReadOnlyList<ApiContentProviderRegistration> selected,
        CatalogCrawlerOptions options, CancellationToken cancellationToken)
    {
        var store = new CatalogCrawlerStore(_database);
        try
        {
            await new CatalogCrawler(_database, selected, options, Console.WriteLine).RunAsync(cancellationToken, run);
        }
        catch (Exception exception)
        {
            new ServerDiagnosticsStore(_database).TryWrite("error", "crawler", "run", "failed",
                ServerDiagnosticsStore.DescribeFailure(exception), runId: run);
            try { store.FinishRun(run, "failed", 0, 1, DateTimeOffset.UtcNow); }
            catch (Exception storage) when (storage is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"manual-crawler-state-failed; type={storage.GetType().Name}"); }
        }
        finally
        {
            try { store.ReleaseLease(run); }
            catch (Exception storage) when (storage is Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
            { Console.Error.WriteLine($"manual-crawler-release-failed; type={storage.GetType().Name}"); }
        }
    }

    private static ManualCrawlResponse Busy(string? providerId) =>
        new("already_running", null, providerId, "Ya hay un scraper ejecutándose. Espera a que termine antes de iniciar otro.");

    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        Task? running;
        lock (_gate) { _stopping = true; running = _running; }
        await _shutdown.CancelAsync();
        if (running is not null) await running.WaitAsync(cancellationToken);
    }

    public void Dispose() { _shutdown.Dispose(); _http?.Dispose(); }
}

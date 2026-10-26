using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.Server;

internal static class ServerMaintenance
{
    internal static async Task<bool> TryRunAsync(string[] args)
    {
        if (args.Length == 0 || args[0] is not ("--initialize-database" or "--backup" or "--scrape-once")) return false;
        if (args.Length != (args[0] == "--backup" ? 2 : 1)) throw new ArgumentException("Invalid maintenance arguments.");
        var directory = Environment.GetEnvironmentVariable("UMD_SERVER_DATA_DIRECTORY");
        if (string.IsNullOrWhiteSpace(directory)) directory = Path.Combine(AppContext.BaseDirectory, "data");
        var database = new ServerDatabase(directory);
        database.Initialize();
        if (args[0] == "--initialize-database")
        {
            Console.WriteLine("Server database initialized; legacy files retained.");
            return true;
        }
        if (args[0] == "--backup")
        {
            database.Backup(args[1]);
            Console.WriteLine("Server database backup completed and verified.");
            return true;
        }
        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, eventArgs) => { eventArgs.Cancel = true; cancellation.Cancel(); };
        Console.CancelKeyPress += cancel;
        // systemd stops the one-shot with SIGTERM; save run status and release
        // the lease before exiting instead of waiting for the lease to expire.
        using var termination = OperatingSystem.IsWindows() ? null :
            System.Runtime.InteropServices.PosixSignalRegistration.Create(System.Runtime.InteropServices.PosixSignal.SIGTERM,
                context => { context.Cancel = true; cancellation.Cancel(); });
        try
        {
            using var http = CatalogCrawlerHttpClient.Create();
            var providers = BuiltInApiContentProviders.Create(http);
            var options = CatalogCrawlerConfiguration.Read(providers, database);
            var result = await new CatalogCrawler(database, providers, options, Console.WriteLine).RunAsync(cancellation.Token);
            Console.WriteLine($"catalog-run={result.Status}; updated={result.TitlesUpdated}; failures={result.Failures}");
            Environment.ExitCode = result.Status == "cancelled" ? 130 : result.Failures > 0 ? 2 : 0;
            return true;
        }
        finally { Console.CancelKeyPress -= cancel; }
    }

}

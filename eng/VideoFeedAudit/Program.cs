using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using System.Collections.Concurrent;
using UniversalMediaDistribution.Catalog.Transfer;

// Opt-in network diagnostic. Only host names and outcomes are printed: never
// cookies, query strings, signed media URLs, or scraped page bodies.
using var handler = new HttpClientHandler { AllowAutoRedirect = false };
using var client = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
IApiContentProvider[] providers =
[
    new AnimeTmoApiContentProvider(client),
    new JkAnimeApiContentProvider(client),
    new PelisPlusApiContentProvider(client),
    new DoramasflixApiContentProvider(client),
    new PelisPlusToApiContentProvider(client),
    new PelisflixHdApiContentProvider(client),
    new PoseidonHd2ApiContentProvider(client),
    new SeriesFlixApiContentProvider(client)
];
if (args.Contains("--help"))
{
    Console.WriteLine("Usage: dotnet run --project eng/VideoFeedAudit -- [provider-id ...] [--item=relative/title/route]\nAn explicit item requires one provider. Omit IDs to sample all video providers. No browser or player is started.");
    return;
}
var itemRoute = args.FirstOrDefault(a => a.StartsWith("--item=", StringComparison.Ordinal))?[7..];
var selected = args.Where(a => !a.StartsWith("--", StringComparison.Ordinal)).ToArray();
if (args.Any(a => !a.StartsWith("--item=", StringComparison.Ordinal) && !providers.Any(p => p.Descriptor.Id == a)) ||
    itemRoute is not null && (selected.Length != 1 || itemRoute.Contains("://", StringComparison.Ordinal) || itemRoute.StartsWith('/')))
    throw new ArgumentException("Unknown provider ID. Use --help for usage.");
var rows = new ConcurrentDictionary<string, byte>();
Console.WriteLine($"# Live video feed sample — {DateTimeOffset.UtcNow:yyyy-MM-dd HH:mm} UTC\n");
Console.WriteLine((itemRoute is null ? "Two recent titles per provider, one episode per title. " : "One explicitly selected title. ") +
    "Native means extraction returned a direct URL, not verified playback. No browser clearance is used.\n");
Console.WriteLine("| API provider | Embed host | Registered extractor | Result |\n|---|---|---|---|");
foreach (var provider in providers.Where(p => selected.Length == 0 || selected.Contains(p.Descriptor.Id)))
{
    var name = provider.Descriptor.Id;
    var observations = 0;
    var resolver = new VideoHosterResolver(client, observer: observation =>
    {
        Interlocked.Increment(ref observations);
        Row(name, observation.EmbedHost, observation.Extractor ?? "None", observation.Outcome);
    });
    var root = new Uri(provider.Descriptor.DefaultBaseUrl);
    using var budget = new CancellationTokenSource(TimeSpan.FromMinutes(3));
    try
    {
        IReadOnlyList<ApiContentSearchItem> titles = itemRoute is null
            ? await provider.LoadDailyUpdatesAsync(root, budget.Token)
            : [new ApiContentSearchItem(itemRoute, new CatalogTransferItem { MediaType = "movie", Subtype = "movie", Name = itemRoute })];
        if (titles.Count == 0) Row(name, "—", "—", "No daily titles returned");
        foreach (var title in titles.Take(2))
        {
            try
            {
                var item = await provider.LoadItemAsync(root, title, budget.Token);
                var units = item.CatalogItem.Units.Select(u => (Unit: u, Season: (int?)null))
                    .Concat(item.CatalogItem.Seasons.SelectMany(s => s.Units.Select(u => (Unit: u, Season: (int?)s.Number))));
                var first = units.FirstOrDefault();
                if (first.Unit is null) { Row(name, "—", "—", "No episodes returned"); continue; }
                var videos = await provider.LoadUnitVideosAsync(root, title,
                    new ApiContentUnitRequest(ContentUnitKind.Episode, first.Unit.DisplayNumber, first.Season, first.Unit.Name), budget.Token);
                if (videos is null) { Row(name, "—", "—", "No servers returned"); continue; }
                foreach (var source in videos.Sources.Take(12))
                {
                    var extractor = HosterFeedMap.Default().FirstOrDefault(e => e.Matches(source.PlaybackUri))?.Name;
                    var before = observations;
                    var result = await resolver.ResolveAsync([source], budget.Token);
                    if (observations == before) Row(name, source.PlaybackUri.IdnHost,
                        source.Kind == ApiContentVideoSourceKind.DirectMedia ? "Provider direct feed (no extractor needed)" : extractor ?? "Cached / delegated",
                        result.Any(s => s.Kind == ApiContentVideoSourceKind.DirectMedia) ? "Direct URL returned (not playback-tested)" : "Web fallback");
                }
            }
            catch (Exception ex) { Row(name, "—", "—", Failure(ex)); }
        }
    }
    catch (Exception ex) { Row(name, "—", "—", Failure(ex)); }
}

static string Failure(Exception ex) => ex is HttpRequestException http
    ? $"HTTP failure ({http.StatusCode?.ToString() ?? http.HttpRequestError.ToString()})"
    : ex is OperationCanceledException ? "Time budget exceeded" : ex.GetType().Name;
void Row(string provider, string host, string extractor, string result)
{
    var row = $"| {provider} | {host} | {extractor} | {result} |";
    if (rows.TryAdd(row, 0)) Console.WriteLine(row);
}

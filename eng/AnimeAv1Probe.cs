// Read-only live check: dotnet run eng/AnimeAv1Probe.cs [-- --resolve]
#:property TargetFramework=net11.0
#:project ../src/UniversalMediaDistribution.Catalog/UniversalMediaDistribution.Catalog.csproj

using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

using var client = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false })
    { Timeout = TimeSpan.FromSeconds(40) };
var root = new Uri("https://animeav1.com/");
var provider = new AnimeAv1ApiContentProvider(client);
var daily = await provider.LoadDailyUpdatesAsync(root);
Console.WriteLine($"Daily titles: {daily.Count}; covers: {daily.Count(x => x.PreviewItem.ThumbnailRef is not null)}");
var search = await provider.SearchAsync(root, "naruto");
Console.WriteLine($"Search results: {search.Count}");
var sources = new List<ApiContentVideoSource>();
foreach (var (slug, number) in new[] { ("naruto", "1"), ("one-piece", "1180"), ("the-last-naruto-the-movie", "1") })
{
    var candidate = new ApiContentSearchItem(slug,
        new CatalogTransferItem { MediaType = "movie", Subtype = "anime_series", Name = slug });
    var item = await provider.LoadItemAsync(root, candidate);
    CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [item.CatalogItem] });
    Console.WriteLine($"{slug}: {item.CatalogItem.Subtype}; episodes={item.CatalogItem.Units.Count}");
    var videos = await provider.LoadUnitVideosAsync(root, candidate, new ApiContentUnitRequest(ContentUnitKind.Episode, number, null));
    foreach (var source in videos?.Sources ?? [])
    {
        var extractors = HosterFeedMap.Default().Where(e => e.Matches(source.PlaybackUri)).Select(e => e.Name);
        Console.WriteLine($"  {source.Label} | {source.PlaybackUri.IdnHost} | extractors={string.Join(',', extractors)} | capture={VideoChallengeCapture.SupportsInteractiveCapture(source.PlaybackUri)}");
        sources.Add(source);
    }
}
if (args.Contains("--resolve"))
{
    var resolver = new VideoHosterResolver(client, observer: observation => Console.WriteLine(observation));
    foreach (var source in sources.DistinctBy(x => x.PlaybackUri.IdnHost))
    {
        var resolved = await resolver.ResolveAsync([source]);
        foreach (var result in resolved)
        {
            Console.WriteLine($"RESOLVED {source.PlaybackUri.IdnHost}: {result.Kind}; mediaHost={result.PlaybackUri.IdnHost}; mime={result.MimeType}");
            if (result.IsEmbeddedWeb) continue;
            using var request = new HttpRequestMessage(HttpMethod.Get, result.PlaybackUri);
            request.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 1023);
            request.Headers.Referrer = result.Referrer;
            request.Headers.TryAddWithoutValidation("User-Agent", result.UserAgent ?? "Mozilla/5.0");
            if (result.HttpOrigin is not null) request.Headers.TryAddWithoutValidation("Origin", result.HttpOrigin.GetLeftPart(UriPartial.Authority));
            using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
            await using var stream = await response.Content.ReadAsStreamAsync();
            var buffer = new byte[1024];
            var length = await stream.ReadAsync(buffer);
            var manifest = System.Text.Encoding.UTF8.GetString(buffer, 0, length).StartsWith("#EXTM3U", StringComparison.Ordinal);
            Console.WriteLine($"  Media probe: {(int)response.StatusCode}; type={response.Content.Headers.ContentType}; bytes={length}; HLS={manifest}; redirect={response.Headers.Location?.Host}");
        }
    }
}

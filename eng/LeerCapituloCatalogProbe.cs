// Run with: dotnet run eng/LeerCapituloCatalogProbe.cs
// Read-only live check; does not modify the application's catalog or settings.
#:property TargetFramework=net11.0
#:project ../src/UniversalMediaDistribution.Catalog/UniversalMediaDistribution.Catalog.csproj

using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
var provider = new LeerCapituloApiContentProvider(client);
var updates = await provider.LoadDailyUpdatesAsync(new Uri("https://www.leercapitulo.co/"));
Console.WriteLine($"Titles: {updates.Count}; covers: {updates.Count(item => !string.IsNullOrWhiteSpace(item.PreviewItem.ThumbnailRef))}; chapters: {updates.Sum(item => item.PreviewItem.Units.Count)}");
if (updates.Count == 0 || updates.Any(item =>
        string.IsNullOrWhiteSpace(item.PreviewItem.Name) ||
        string.IsNullOrWhiteSpace(item.PreviewItem.ThumbnailRef) ||
        item.PreviewItem.Units.Count == 0))
{
    throw new InvalidDataException("The live daily catalog is missing titles, covers, or chapters.");
}

foreach (var item in updates.Take(3))
{
    Console.WriteLine($"{item.PreviewItem.Name}: {string.Join(", ", item.PreviewItem.Units.Select(unit => unit.DisplayNumber))}");
}

var root = new Uri("https://www.leercapitulo.co/");
var query = args.FirstOrDefault() ?? "El juego del herrero genio";
var results = await provider.SearchAsync(root, query);
Console.WriteLine($"Search results: {results.Count}");
var selected = results.FirstOrDefault(item => string.Equals(item.PreviewItem.Name, query, StringComparison.OrdinalIgnoreCase))
    ?? throw new InvalidDataException("Expected title missing from search results.");
var details = await provider.LoadItemAsync(root, selected);
CatalogTransferValidator.Validate(new CatalogTransferDocument { Items = [details.CatalogItem] });
Console.WriteLine($"Details: {details.CatalogItem.Name}; chapters={details.CatalogItem.Units.Count}; aliases={details.CatalogItem.AlternativeNames.Count}; cover={details.CatalogItem.ThumbnailRef is not null}");
var chapter = details.CatalogItem.Units.Last();
var pages = await provider.LoadUnitImagesAsync(root, selected, new ApiContentUnitRequest(ContentUnitKind.Chapter, chapter.DisplayNumber, null))
    ?? throw new InvalidDataException("No reader pages.");
Console.WriteLine($"Chapter {chapter.DisplayNumber}: {pages.Pages.Count} ordered images");
using var imageRequest = new HttpRequestMessage(HttpMethod.Get, pages.Pages[0].Uri);
imageRequest.Headers.Referrer = pages.Pages[0].Referrer;
imageRequest.Headers.Range = new System.Net.Http.Headers.RangeHeaderValue(0, 127);
using var imageResponse = await client.SendAsync(imageRequest, HttpCompletionOption.ResponseHeadersRead);
imageResponse.EnsureSuccessStatusCode();
Console.WriteLine($"First image: {(int)imageResponse.StatusCode} {imageResponse.Content.Headers.ContentType}");
var filtered = await provider.SearchAsync(root, new ApiContentSearchRequest(genre: ApiContentGenre.Action));
Console.WriteLine($"Genre search: {filtered.Items.Count} titles; nextPage={filtered.ContinuationToken}");

using System.Globalization;
using System.Net;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server;

// Local pipe used by the Rust crawler. The existing adapters still own provider-specific
// discovery and metadata parsing. No credentials, database paths or playback requests cross it.
internal sealed record ProviderCommand(string Operation, string? Cursor, string? ExternalId, CatalogTransferItem? Preview);
internal sealed record ProviderPreview(string ExternalId, CatalogTransferItem Preview);
internal sealed record ProviderReply(ProviderPreview[]? Items = null, string? Next = null, CatalogTransferItem? Item = null,
    string? Error = null, int? RetryAfter = null);

internal static class ProviderWorker
{
    internal static async Task RunAsync(string[] args)
    {
        if (args.Length != 3 || !int.TryParse(args[2], CultureInfo.InvariantCulture, out var seconds) || seconds is < 1 or > 60)
            throw new ArgumentException("Expected provider ID and interval in seconds.");
        using var http = CatalogCrawlerHttpClient.Create();
        var registration = BuiltInApiContentProviders.Create(http).Single(p => p.Descriptor.Id == args[1]);
        var provider = (IApiCatalogProvider)registration.GetProvider();
        var configured = Environment.GetEnvironmentVariable("UMD_CRAWLER_BASE_" + args[1].Replace('-', '_').ToUpperInvariant());
        var root = new Uri(string.IsNullOrWhiteSpace(configured) ? registration.Descriptor.DefaultBaseUrl : configured);
        if (!CatalogCrawlerHttpClient.IsPublicUri(root)) throw new ArgumentException("Invalid public provider URL.");
        using var pacing = CatalogCrawlerHttpClient.UseProviderInterval(TimeSpan.FromSeconds(seconds));
        while (await Console.In.ReadLineAsync() is { } line)
        {
            ProviderReply response;
            try
            {
                var command = JsonSerializer.Deserialize(line, ServerJsonSerializerContext.Default.ProviderCommand)
                    ?? throw new InvalidDataException();
                response = command.Operation switch
                {
                    "page" => Page(await provider.ReadCatalogPageAsync(root, command.Cursor)),
                    "updates" => new(Items: provider.SupportsDailyUpdates ? Previews(await provider.LoadDailyUpdatesAsync(root)) : []),
                    "detail" when command.ExternalId != null && command.Preview != null => new(Item: CatalogMetadataOnly.Sanitize(
                        await provider.LoadCatalogItemAsync(root, new ApiContentSearchItem(command.ExternalId, command.Preview, RequireProviderTitle: true)))),
                    _ => throw new InvalidDataException()
                };
            }
            catch (CatalogProviderThrottledException ex) { response = new(Error: "throttled", RetryAfter: (int)Math.Clamp(ex.RetryAfter.TotalSeconds, 1, 86400)); }
            catch (ApiCatalogItemUnsupportedException) { response = new(Error: "unsupported"); }
            catch (HttpRequestException ex) when (ex.StatusCode is HttpStatusCode.NotFound or HttpStatusCode.Gone) { response = new(Error: "not_found"); }
            catch (HttpRequestException) { response = new(Error: "upstream"); }
            catch (OperationCanceledException) { response = new(Error: "timeout"); }
            catch (Exception) { response = new(Error: "invalid_data"); }
            await Console.Out.WriteLineAsync(JsonSerializer.Serialize(response, ServerJsonSerializerContext.Default.ProviderReply));
            await Console.Out.FlushAsync();
        }
    }

    private static ProviderPreview[] Previews(IEnumerable<ApiContentSearchItem> items) =>
        items.Select(i => new ProviderPreview(i.ExternalId, CatalogMetadataOnly.Sanitize(i.PreviewItem) with { Seasons = [], Units = [] })).ToArray();
    private static ProviderReply Page(ApiContentSearchPage page) => page.IsTruncated || page.Items.Count == 0
        ? new(Error: "invalid_page") : new(Items: Previews(page.Items), Next: page.ContinuationToken);
}

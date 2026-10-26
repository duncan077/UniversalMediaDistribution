using System.Globalization;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>A valid upstream title outside the content formats supported by this provider.</summary>
public sealed class ApiCatalogItemUnsupportedException(string message) : NotSupportedException(message);

/// <summary>Headless catalog discovery, independent of search result limits and playback.</summary>
public interface IApiCatalogProvider : IApiContentProvider
{
    string CatalogDiscoveryDescription { get; }

    ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(
        Uri baseUri, string? cursor, CancellationToken cancellationToken = default);

    async ValueTask<CatalogTransferItem> LoadCatalogItemAsync(
        Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default) =>
        (await LoadItemAsync(baseUri, CatalogDiscovery.FullTitle(item), cancellationToken)).CatalogItem;
}

internal static class CatalogDiscovery
{
    internal static ApiContentSearchItem FullTitle(ApiContentSearchItem item) =>
        new(item.ExternalId, item.PreviewItem, RequireProviderTitle: true);

    internal static int Page(string? cursor, int first = 1)
    {
        if (cursor is null) return first;
        if (!int.TryParse(cursor, NumberStyles.None, CultureInfo.InvariantCulture, out var value) ||
            value < first || value > 100_000)
            throw new ArgumentException("Invalid catalog cursor.", nameof(cursor));
        return value;
    }

    internal static string Next(int page) => checked(page + 1).ToString(CultureInfo.InvariantCulture);

    internal static ApiContentSearchPage RequiredPage(IEnumerable<ApiContentSearchItem> items, string? next)
    {
        var page = new ApiContentSearchPage(items, next);
        if (page.Items.Count == 0)
            throw new InvalidDataException("The catalog page contained no recognizable titles; its cursor was not advanced.");
        return page;
    }

    internal static ApiContentSearchPage Slice(IEnumerable<ApiContentSearchItem> items, string? cursor)
    {
        var offset = Page(cursor, 0);
        var page = items.OrderBy(item => item.ExternalId, StringComparer.Ordinal).Skip(offset).Take(501).ToArray();
        return RequiredPage(page.Take(500), page.Length > 500 ? (offset + 500).ToString(CultureInfo.InvariantCulture) : null);
    }

    // Follow only a known directory's next-page link. HTML must never choose
    // an arbitrary outbound URL or feed a player/reader route into the crawl.
    internal static bool HasPage(string html, Uri root, string expectedRoute)
    {
        var expected = new Uri(root, expectedRoute);
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            var href = TmoProviderText.GetAttribute(anchor.Attributes, "href");
            if (Uri.TryCreate(root, href, out var uri) &&
                uri.PathAndQuery.TrimEnd('/') == expected.PathAndQuery.TrimEnd('/') &&
                (TmoProviderUri.IsProviderHost(root, uri) || uri.Host == "pelisflixhd.blog"))
                return true;
        }
        return false;
    }
}

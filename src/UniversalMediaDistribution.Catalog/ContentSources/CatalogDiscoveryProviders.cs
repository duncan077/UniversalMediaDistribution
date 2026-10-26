using System.Globalization;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.ContentSources;

public sealed partial class AnimeTmoApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Full JSON index: wp-content/uploads/az-catalog.json";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        var items = await LoadCatalogAsync(root, cancellationToken);
        return CatalogDiscovery.Slice(items.Select(entry => CreateSearchItem(root, entry)), cursor);
    }
}

public sealed partial class OlympusXyzApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Full JSON index: api/series/list (comics supported by this provider)";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        using var document = JsonDocument.Parse(await GetSeriesListJsonAsync(root, cancellationToken));
        if (!TryGetProperty(document.RootElement, "data", JsonValueKind.Array, out var data))
            throw new InvalidDataException("Olympus returned an invalid catalog index.");
        var results = new List<ApiContentSearchItem>();
        foreach (var element in data.EnumerateArray())
        {
            if (!TryGetString(element, "slug", out var slug) || !SafeSlugRegex.IsMatch(slug) ||
                !TryGetString(element, "name", out var name) ||
                TryGetString(element, "type", out var type) && !type.Equals("comic", StringComparison.OrdinalIgnoreCase)) continue;
            results.Add(new ApiContentSearchItem(slug, new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = NormalizeText(name),
                Categories = ["Manga"]
            }));
        }
        return CatalogDiscovery.Slice(results.DistinctBy(item => item.ExternalId), cursor);
    }
}

public sealed partial class AnimeAv1ApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged SvelteKit JSON: catalogo/__data.json (upstream reported extent only)";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var page = CatalogDiscovery.Page(cursor);
        using var document = await ReadAsync(Root(baseUri), $"catalogo/__data.json?page={page.ToString(CultureInfo.InvariantCulture)}", cancellationToken);
        var data = Data(document, "results");
        var items = data.Get("results").Items().Select(MapItem).ToArray();
        var pagination = data.Get("pagination");
        var totalPages = pagination.Get("totalPages").Int;
        if (totalPages <= 0) totalPages = (pagination.Get("totalRecords").Int + 19) / 20;
        if (totalPages <= 0) throw new InvalidDataException("AnimeAV1 omitted its catalog pagination.");
        return CatalogDiscovery.RequiredPage(items, page < totalPages ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class CapibaraTraductorApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged JSON: api/manga-custom; detail endpoint supplies full chapter lists";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        var page = CatalogDiscovery.Page(cursor);
        using var document = await ReadJsonAsync(root,
            new Uri(root, $"api/manga-custom?page={page.ToString(CultureInfo.InvariantCulture)}&limit=50&order=latest&nsfw=false"),
            MaximumCatalogBytes, cancellationToken);
        return CatalogDiscovery.RequiredPage(ParseCatalog(document.RootElement, root),
            page < GetMaximumPage(document.RootElement) ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class ManhwaWebApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged JSON: dynamically discovered backend manhwa/library?page=N";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var page = CatalogDiscovery.Page(cursor, 0);
        var result = await ReadLibraryPageAsync(NormalizeRootUri(baseUri), null, null, page, new HashSet<string>(StringComparer.Ordinal), cancellationToken);
        return CatalogDiscovery.RequiredPage(result.Items, result.HasNext ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class LeerCapituloApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged HTML: manga/?page=N";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        var page = CatalogDiscovery.Page(cursor);
        var html = await ReadStringAsync(new Uri(root, $"manga/?page={page.ToString(CultureInfo.InvariantCulture)}"), "text/html", MaximumHtmlBytes, root, cancellationToken);
        return CatalogDiscovery.RequiredPage(ParseGenreCards(root, html, null, null), HasGenrePage(html, page + 1) ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class ZonaTmoApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged HTML: biblioteca?page=N";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        var page = CatalogDiscovery.Page(cursor);
        var html = await ReadLibraryAsync(root, new Uri(root, $"biblioteca?order_item=creation&order_dir=desc&page={page.ToString(CultureInfo.InvariantCulture)}"), cancellationToken);
        return CatalogDiscovery.RequiredPage(ParseLibraryCards(root, html, null, null), HasNextPage(html) ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class UchuujinMangasApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged HTML: manga/?order=update&page=N";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        var page = CatalogDiscovery.Page(cursor);
        var html = await ReadHtmlAsync(root, new Uri(root, $"manga/?order=update&page={page.ToString(CultureInfo.InvariantCulture)}"), root, MaximumCatalogBytes, cancellationToken);
        return CatalogDiscovery.RequiredPage(ParseCards(root, html, null), HasNextPage(html) ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class JkAnimeApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged HTML: directorio?p=N; episode lists use JSON";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        var page = CatalogDiscovery.Page(cursor);
        var html = await ReadStringAsync(new Uri(root, $"directorio?p={page.ToString(CultureInfo.InvariantCulture)}"), root,
            MaximumDirectoryBytes, "text/html", cancellationToken);
        return CatalogDiscovery.RequiredPage(ParseDirectoryCards(root, html, null, null), HasDirectoryPage(html, page + 1) ? CatalogDiscovery.Next(page) : null);
    }
}

public sealed partial class PelisPlusApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged HTML: peliculas and series (may require a browser challenge)";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        var parts = (cursor ?? "movies:1").Split(':');
        if (parts.Length != 2 || parts[0] is not ("movies" or "series")) throw new ArgumentException("Invalid catalog section.", nameof(cursor));
        var page = CatalogDiscovery.Page(parts[1]);
        var section = parts[0] == "movies" ? "peliculas" : "series";
        string Route(int number) => section + (number == 1 ? "/" : $"/page/{number.ToString(CultureInfo.InvariantCulture)}/");
        var html = await ReadStringAsync(new Uri(root, Route(page)), root, MaximumCatalogBytes, cancellationToken);
        var items = ReadCards(root, html).Select(card => new ApiContentSearchItem(card.ExternalId, CreatePreviewItem(card, includeLatestEpisode: false)));
        var next = CatalogDiscovery.HasPage(html, root, Route(page + 1)) ? $"{parts[0]}:{CatalogDiscovery.Next(page)}" :
            parts[0] == "movies" ? "series:1" : null;
        return CatalogDiscovery.RequiredPage(items, next);
    }

    public async ValueTask<CatalogTransferItem> LoadCatalogItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default) =>
        (await LoadItemCoreAsync(baseUri, CatalogDiscovery.FullTitle(item), null, includeUnitMedia: false, cancellationToken)).CatalogItem;
}

public sealed partial class DoramasflixApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => "Paged GraphQL: paginationDorama and paginationMovie";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        _ = NormalizeRoot(baseUri);
        var parts = (cursor ?? "series:1").Split(':');
        if (parts.Length != 2 || parts[0] is not ("movies" or "series")) throw new ArgumentException("Invalid catalog section.", nameof(cursor));
        var page = CatalogDiscovery.Page(parts[1]);
        var movies = parts[0] == "movies";
        var field = movies ? "paginationMovie" : "paginationDorama";
        var operation = movies ? "listMovies" : "listDoramas";
        var query = $$"""
            query {{operation}}($page: Int, $perPage: Int) {
              {{field}}(page: $page, perPage: $perPage, sort: UPDATEDAT_DESC, filter: {}) {
                items { _id slug name name_es poster_path poster }
                pageInfo { hasNextPage }
              }
            }
            """;
        using var document = await PostGraphQlAsync(operation,
            new System.Text.Json.Nodes.JsonObject { ["page"] = page, ["perPage"] = 50 }, query, cancellationToken);
        if (!TryGetData(document.RootElement, out var data) || !data.TryGetProperty(field, out var pagination) ||
            !pagination.TryGetProperty("items", out var items) || items.ValueKind != JsonValueKind.Array ||
            !pagination.TryGetProperty("pageInfo", out _)) throw new InvalidDataException("Doramasflix omitted its catalog page.");
        var results = new List<ApiContentSearchItem>();
        AddArrayResults(items, movies ? "peliculas-online/" : "doramas-online/", !movies, results);
        return CatalogDiscovery.RequiredPage(results, HasNextPage(pagination) ? $"{parts[0]}:{CatalogDiscovery.Next(page)}" :
            movies ? null : "movies:1");
    }

    public async ValueTask<CatalogTransferItem> LoadCatalogItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default) =>
        (await LoadItemCoreAsync(baseUri, CatalogDiscovery.FullTitle(item), null, includeUnitMedia: false, cancellationToken)).CatalogItem;
}

public partial class StreamflixHtmlApiContentProvider : IApiCatalogProvider
{
    public string CatalogDiscoveryDescription => _profile == StreamflixCatalogProfile.SeriesFlix ?
        "Paged HTML series directory; its movie navigation belongs to PelisflixHD" : "Paged HTML directories: movies and series";
    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        var parts = (cursor ?? (_profile == StreamflixCatalogProfile.SeriesFlix ? "series:1" : "movies:1")).Split(':');
        if (parts.Length != 2 || parts[0] is not ("movies" or "series")) throw new ArgumentException("Invalid catalog section.", nameof(cursor));
        // SeriesFlix links its movie navigation to PelisflixHD, whose own
        // registry entry already crawls it. There is no local movie directory.
        if (_profile == StreamflixCatalogProfile.SeriesFlix && parts[0] == "movies") parts = ["series", "1"];
        var page = CatalogDiscovery.Page(parts[1]);
        string Route(int number)
        {
            var section = parts[0] == "movies" ? "peliculas" : "series";
            if (_profile == StreamflixCatalogProfile.SeriesFlix)
                return number == 1 ? "series-online/" : $"peliculas-online/series-online/page/{number}";
            return number == 1 ? section : _profile == StreamflixCatalogProfile.PelisPlusTo ? $"{section}/{number}" : $"{section}/page/{number}";
        }
        var html = await ReadHtmlAsync(new Uri(root, Route(page)), root, cancellationToken);
        root = NormalizeRoot(baseUri);
        var items = ParseCards(root, html, null).Select(card => CreateSearchItem(card, null));
        return CatalogDiscovery.RequiredPage(items, CatalogDiscovery.HasPage(html, root, Route(page + 1)) ?
            $"{parts[0]}:{CatalogDiscovery.Next(page)}" : parts[0] == "movies" ? "series:1" : null);
    }

    public async ValueTask<CatalogTransferItem> LoadCatalogItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default) =>
        (await LoadItemCoreAsync(baseUri, CatalogDiscovery.FullTitle(item), null, cancellationToken, includeUnitMedia: false)).CatalogItem;
}

public sealed partial class UmdJsonApiContentProvider : IApiCatalogProvider
{
    private readonly SemaphoreSlim _discoveryGate = new(1, 1);
    private CatalogTransferDocument? _discoveryDocument;
    private string? _discoveryRoot;
    private DateTimeOffset _discoveryExpires;
    public string CatalogDiscoveryDescription => "Optional configured external UMD API: bounded current snapshot; no default upstream";

    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor, CancellationToken cancellationToken = default)
    {
        var document = await LoadDiscoverySnapshotAsync(baseUri, cancellationToken);
        return CatalogDiscovery.Slice(document.Items.Select(item => new ApiContentSearchItem(BuildExternalId(item), item)), cursor);
    }

    public async ValueTask<CatalogTransferItem> LoadCatalogItemAsync(Uri baseUri, ApiContentSearchItem item, CancellationToken cancellationToken = default)
    {
        // The durable crawl queue intentionally discards units from previews.
        // A UMD search normally needs no detail fetch, but backfill must reload
        // the complete item instead of treating that stripped preview as final.
        var document = await LoadDiscoverySnapshotAsync(baseUri, cancellationToken);
        return document.Items.FirstOrDefault(candidate => BuildExternalId(candidate) == item.ExternalId)
            ?? throw new InvalidDataException("The external UMD catalog no longer contains the queued title.");
    }

    private async Task<CatalogTransferDocument> LoadDiscoverySnapshotAsync(Uri baseUri, CancellationToken cancellationToken)
    {
        var root = new Uri(baseUri.AbsoluteUri.TrimEnd('/') + '/');
        await _discoveryGate.WaitAsync(cancellationToken);
        try
        {
            if (_discoveryDocument is not null && _discoveryRoot == root.AbsoluteUri && _discoveryExpires > DateTimeOffset.UtcNow)
                return _discoveryDocument;
            var document = await CatalogDocumentHttpReader.ReadAsync(_httpClient, new Uri(root, "current"), cancellationToken);
            _discoveryDocument = document;
            _discoveryRoot = root.AbsoluteUri;
            _discoveryExpires = DateTimeOffset.UtcNow.AddMinutes(10);
            return document;
        }
        finally { _discoveryGate.Release(); }
    }
}

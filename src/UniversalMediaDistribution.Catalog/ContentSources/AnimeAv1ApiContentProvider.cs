using System.Globalization;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Reads the same public SvelteKit data routes as AnimeAV1's website.
/// No JavaScript evaluation or reflection-based JSON serialization is needed.</summary>
public sealed partial class AnimeAv1ApiContentProvider(
    HttpClient httpClient,
    VideoHosterResolver? hosterResolver = null) : IApiContentProvider
{
    public const string ProviderId = "animeav1";
    private static readonly Uri ArtworkOrigin = new("https://cdn.animeav1.com/");
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    public static ApiContentProviderDescriptor DefaultDescriptor => BuiltInApiContentProviderDescriptors.AnimeAv1;
    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;
    public bool SupportsDailyUpdates => true;
    public ApiContentSearchCapabilities SearchCapabilities => ApiContentSearchCapabilities.TextOnly;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) =>
        [TmoProviderUri.GetOrigin(Root(baseUri)), ArtworkOrigin];

    public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri) =>
        artworkUri.Scheme == "https" && artworkUri.IdnHost == ArtworkOrigin.IdnHost
            ? Root(baseUri) : null;

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> LoadDailyUpdatesAsync(
        Uri baseUri, CancellationToken cancellationToken = default)
    {
        using var document = await ReadAsync(Root(baseUri), "__data.json", cancellationToken);
        var data = Data(document, "latestEpisodes");
        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var episode in data.Get("latestEpisodes").Items().Take(100))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = MapItem(episode.Get("media"));
            if (!seen.Add(item.ExternalId)) continue;
            results.Add(new ApiContentSearchItem(item.ExternalId, item.PreviewItem with
            {
                Units = [MapEpisode(episode, episode.Get("media").Get("id").Text, 0)]
            }));
            if (results.Count == 24) break;
        }
        return results;
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri, string query, MediaType? mediaType = null,
        CancellationToken cancellationToken = default) =>
        (await SearchAsync(baseUri, new ApiContentSearchRequest(query, mediaType), cancellationToken)).Items;

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri, ApiContentSearchRequest request, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not (null or MediaType.Movie) || !SearchCapabilities.CanSatisfy(request))
            return ApiContentSearchPage.Empty;
        var offset = 0;
        if (request.ContinuationToken is { } token &&
            (!int.TryParse(token, NumberStyles.None, CultureInfo.InvariantCulture, out offset) || offset > 100_000))
            throw new ArgumentException("Invalid AnimeAV1 continuation token.", nameof(request));
        // The public catalog has fixed 20-item pages. Preserve smaller caller
        // pages with an offset cursor instead of dropping unconsumed results.
        var page = offset / 20 + 1;
        using var document = await ReadAsync(Root(baseUri),
            $"catalogo/__data.json?search={Uri.EscapeDataString(request.Query!)}&page={page}", cancellationToken);
        var data = Data(document, "results");
        var rows = data.Get("results").Items().Take(20).ToArray();
        var items = rows.Skip(offset % 20).Take(request.PageSize).Select(MapItem).ToArray();
        var next = offset + items.Length;
        var total = data.Get("pagination").Get("totalRecords").Int;
        return new ApiContentSearchPage(items, items.Length > 0 && next < total
            ? next.ToString(CultureInfo.InvariantCulture) : null);
    }

    public async ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri, ApiContentSearchItem searchItem, CancellationToken cancellationToken = default)
    {
        var slug = Slug(searchItem.ExternalId);
        using var document = await ReadAsync(Root(baseUri), $"media/{slug}/__data.json", cancellationToken);
        var media = Data(document, "media").Get("media");
        var item = MapItem(media);
        if (item.ExternalId != slug) throw new InvalidDataException("AnimeAV1 returned a different title.");
        var episodes = media.Get("episodes").Items().Take(20_001).ToArray();
        if (episodes.Length > 20_000) throw new InvalidDataException("Too many AnimeAV1 episodes.");
        var units = episodes.Select((episode, index) => MapEpisode(episode, media.Get("id").Text, index))
            .DistinctBy(unit => unit.DisplayNumber)
            .OrderBy(unit => decimal.Parse(unit.DisplayNumber, CultureInfo.InvariantCulture))
            .Select((unit, index) => unit with { SortOrder = index }).ToArray();
        return new ApiContentItem(item.PreviewItem with { Units = units });
    }

    public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
        Uri baseUri, ApiContentSearchItem searchItem, ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        if (unit.Kind != ContentUnitKind.Episode || unit.SeasonNumber is not null) return null;
        var slug = Slug(searchItem.ExternalId);
        var number = EpisodeNumber(unit.DisplayNumber);
        var root = Root(baseUri);
        var page = new Uri(root, $"media/{slug}/{number}");
        using var document = await ReadAsync(root, $"media/{slug}/{number}/__data.json", cancellationToken);
        var data = Data(document, "embeds");
        if (data.Get("media").Get("slug").Text != slug ||
            !ApiContentUnitRequest.DisplayNumbersMatch(data.Get("episode").Get("number").Text, number))
            throw new InvalidDataException("AnimeAV1 returned a different episode.");
        var sources = new List<ApiContentVideoSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var language in data.Get("embeds").Properties().Take(10))
        {
            foreach (var server in language.Value.Items().Take(20))
            {
                var url = server.Get("url").Text;
                if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) ||
                    !HosterExtractorBase.IsSafePublicHttpUri(uri) ||
                    uri.AbsolutePath.Contains("embed-undef", StringComparison.OrdinalIgnoreCase) ||
                    !seen.Add(language.Key + "|" + uri.AbsoluteUri)) continue;
                var label = server.Get("server").Text ?? uri.IdnHost;
                var mime = HosterMimeTypeResolver.MimeTypeFromUrl(uri);
                sources.Add(new ApiContentVideoSource($"{label} · {language.Key}", uri,
                    mimeType: mime, referrer: page,
                    kind: mime is null ? ApiContentVideoSourceKind.EmbeddedWeb : ApiContentVideoSourceKind.DirectMedia));
            }
        }
        if (sources.Count == 0) return null;
        var resolved = hosterResolver is null ? sources.ToArray() :
            (await hosterResolver.ResolveAsync(sources, cancellationToken)).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        return resolved.Length == 0 ? null : new ApiContentVideoSet(unit.Kind, unit.DisplayNumber, null, resolved);
    }

    private static ApiContentSearchItem MapItem(Value media)
    {
        var slug = Slug(media.Get("slug").Text);
        var title = media.Get("title").Text;
        if (string.IsNullOrWhiteSpace(title)) throw new InvalidDataException("AnimeAV1 returned an empty title.");
        var id = media.Get("id").Text;
        var category = media.Get("category").Get("name").Text;
        var date = media.Get("startDate").Text;
        var aliases = media.Get("aka").Properties()
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Value.Text))
            .Select(pair => new CatalogTransferAlias { Name = pair.Value.Text!, Language = pair.Key })
            .ToArray();
        return new ApiContentSearchItem(slug, new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = media.Get("category").Get("id").Int == 2 ? "anime_movie" : "anime_series",
            Name = title,
            Description = media.Get("synopsis").Text,
            OriginalName = media.Get("aka").Get("ja-jp").Text,
            AlternativeNames = aliases,
            ReleaseDate = DateOnly.TryParseExact(date, "yyyy-MM-dd", CultureInfo.InvariantCulture,
                DateTimeStyles.None, out var parsedDate) ? parsedDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null,
            ThumbnailRef = Artwork(id, "covers"),
            BackdropRef = Artwork(id, "backdrops"),
            Categories = category is null ? ["Anime"] : ["Anime", category],
            Genres = media.Get("genres").Items().Select(genre => genre.Get("name").Text)
                .OfType<string>().Where(name => !string.IsNullOrWhiteSpace(name)).Distinct().ToArray()
        });
    }

    private static CatalogTransferUnit MapEpisode(Value episode, string? mediaId, int index)
    {
        var number = EpisodeNumber(episode.Get("number").Text);
        return new CatalogTransferUnit
        {
            Kind = "episode",
            DisplayNumber = number,
            SortOrder = index,
            Name = episode.Get("title").Text ?? $"Episode {number}",
            ThumbnailRef = Artwork(mediaId, "screenshots", $"/{number}")
        };
    }

    private static string? Artwork(string? id, string folder, string suffix = "") =>
        long.TryParse(id, NumberStyles.None, CultureInfo.InvariantCulture, out var numericId) && numericId > 0
            ? new Uri(ArtworkOrigin, $"{folder}/{numericId}{suffix}.jpg").AbsoluteUri : null;

    private static string Slug(string? value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 250 ||
            value.AsSpan().ContainsAnyExcept("abcdefghijklmnopqrstuvwxyz0123456789-"))
            throw new InvalidDataException("Invalid AnimeAV1 title slug.");
        return value;
    }

    private static string EpisodeNumber(string? value)
    {
        if (value is null || value.Length > 16 ||
            !decimal.TryParse(value, NumberStyles.AllowDecimalPoint, CultureInfo.InvariantCulture, out var number) ||
            number < 0 || number > 100_000)
            throw new InvalidDataException("Invalid AnimeAV1 episode number.");
        return number.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static Uri Root(Uri uri) => TmoProviderUri.NormalizeRoot(uri, "AnimeAV1");

    private async Task<JsonDocument> ReadAsync(Uri root, string path, CancellationToken cancellationToken)
    {
        var json = await TmoProviderHttp.ReadStringAsync(httpClient, new Uri(root, path), root,
            MaximumResponseBytes, "application/json", "AnimeAV1", cancellationToken);
        return JsonDocument.Parse(json);
    }

    private static Value Data(JsonDocument document, string requiredProperty)
    {
        if (document.RootElement.TryGetProperty("nodes", out var nodes) && nodes.ValueKind == JsonValueKind.Array)
        {
            foreach (var node in nodes.EnumerateArray())
            {
                if (node.ValueKind != JsonValueKind.Object || !node.TryGetProperty("data", out var table) ||
                    table.ValueKind != JsonValueKind.Array || table.GetArrayLength() is 0 or > 200_000) continue;
                var root = table[0];
                if (root.ValueKind == JsonValueKind.Object && root.TryGetProperty(requiredProperty, out _))
                    return new Value(table.EnumerateArray().ToArray(), root);
            }
        }
        throw new InvalidDataException($"AnimeAV1 response is missing {requiredProperty} data.");
    }

    // SvelteKit/devalue objects and arrays contain indexes into a flat table.
    // Resolve only explicitly requested fields: no recursive graph expansion,
    // so cycles and shared references cannot cause recursion or allocation bombs.
    private readonly record struct Value(JsonElement[] Table, JsonElement Element)
    {
        public string? Text => Element.ValueKind switch
        {
            JsonValueKind.String => Element.GetString(),
            JsonValueKind.Number => Element.GetRawText(),
            _ => null
        };
        public int Int => int.TryParse(Text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : 0;
        public Value Get(string key) => Element.ValueKind == JsonValueKind.Object && Element.TryGetProperty(key, out var reference)
            ? Resolve(reference) : default;
        private Value Resolve(JsonElement reference)
        {
            if (reference.ValueKind != JsonValueKind.Number || !reference.TryGetInt32(out var index) || index < -1 || index >= Table.Length)
                throw new InvalidDataException("Invalid AnimeAV1 data reference.");
            return index == -1 ? default : new Value(Table, Table[index]);
        }
        public IEnumerable<Value> Items()
        {
            if (Element.ValueKind != JsonValueKind.Array) yield break;
            foreach (var reference in Element.EnumerateArray()) yield return Resolve(reference);
        }
        public IEnumerable<KeyValuePair<string, Value>> Properties()
        {
            if (Element.ValueKind != JsonValueKind.Object) yield break;
            foreach (var property in Element.EnumerateObject()) yield return new(property.Name, Resolve(property.Value));
        }
    }
}

using System.Globalization;
using System.Net;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>The public JSON routes used by FramexTV's own client. Playback URLs
/// and subtitle tracks are resolved on demand, never stored in the catalog.</summary>
public sealed class FramexTvApiContentProvider(HttpClient httpClient) : IApiCatalogProvider
{
    public const string ProviderId = "framextv";
    private const int MaximumResponseBytes = 4 * 1024 * 1024;
    private static readonly Uri Website = new("https://framextv.xyz/");
    private static readonly Uri Artwork = new("https://image.tmdb.org/");
    private static readonly Uri[] Backends = [new("https://api.framextv.tech/"), new("https://api.framextv.xyz/"), new("https://api.framextv.cc/")];
    public ApiContentProviderDescriptor Descriptor => BuiltInApiContentProviderDescriptors.FramexTv;
    public bool SupportsDailyUpdates => true;
    public string CatalogDiscoveryDescription => "Public JSON discovery: api/discover, paginated movies then TV (upstream reported extent)";
    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) => [Artwork, Root(baseUri)];

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(Uri baseUri, string query,
        MediaType? mediaType = null, CancellationToken cancellationToken = default) =>
        (await SearchAsync(baseUri, new ApiContentSearchRequest(query, mediaType), cancellationToken)).Items;

    public async ValueTask<ApiContentSearchPage> SearchAsync(Uri baseUri, ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        if (request.MediaType is not (null or MediaType.Movie) || !ApiContentSearchCapabilities.TextOnly.CanSatisfy(request))
            return ApiContentSearchPage.Empty;
        var (page, offset) = Page(request.ContinuationToken);
        using var json = await ReadAsync(baseUri, $"api/search?q={Uri.EscapeDataString(request.Query!)}&type=multi&page={page}", cancellationToken);
        RequireSuccess(json.RootElement);
        var rows = Rows(json.RootElement, "results").ToArray();
        if (offset > rows.Length) throw new InvalidDataException("Invalid FramexTV search offset.");
        var consumed = Math.Min(request.PageSize, rows.Length - offset);
        var results = rows.Skip(offset).Take(consumed).Select(TryMap).OfType<ApiContentSearchItem>().ToArray();
        var next = offset + consumed < rows.Length ? $"{page}:{offset + consumed}" :
            page < Integer(json.RootElement, "totalPages") ? $"{page + 1}:0" : null;
        return new(results, next);
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> LoadDailyUpdatesAsync(Uri baseUri,
        CancellationToken cancellationToken = default)
    {
        using var json = await ReadAsync(baseUri, "api/home", cancellationToken);
        RequireSuccess(json.RootElement);
        var results = new List<ApiContentSearchItem>();
        if (TryMap(Property(json.RootElement, "hero")) is { } hero) results.Add(hero);
        var rails = Property(json.RootElement, "rails");
        if (rails.ValueKind == JsonValueKind.Object)
            foreach (var rail in rails.EnumerateObject().Take(30))
                results.AddRange(Rows(rail.Value, "results").Select(TryMap).OfType<ApiContentSearchItem>());
        return results.DistinctBy(item => item.ExternalId).Take(100).ToArray();
    }

    public async ValueTask<ApiContentSearchPage> ReadCatalogPageAsync(Uri baseUri, string? cursor,
        CancellationToken cancellationToken = default)
    {
        var type = "movie";
        var page = 1;
        if (cursor is not null)
        {
            var parts = cursor.Split(':');
            if (parts.Length != 2 || parts[0] is not ("movie" or "tv") || !Positive(parts[1], out page))
                throw new ArgumentException("Invalid FramexTV catalog cursor.", nameof(cursor));
            type = parts[0];
        }
        using var json = await ReadAsync(baseUri, $"api/discover?type={type}&page={page}", cancellationToken);
        RequireSuccess(json.RootElement);
        var total = Integer(json.RootElement, "totalPages");
        if (total is null or < 1 or > 100_000) throw new InvalidDataException("FramexTV omitted catalog pagination.");
        var next = page < total ? $"{type}:{page + 1}" : type == "movie" ? "tv:1" : null;
        return CatalogDiscovery.RequiredPage(Rows(json.RootElement, "results").Select(TryMap).OfType<ApiContentSearchItem>(), next);
    }

    public ValueTask<ApiContentItem> LoadItemAsync(Uri baseUri, ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default) => LoadDetailsAsync(baseUri, searchItem, null, cancellationToken);

    public ValueTask<ApiContentItem> LoadItemForUnitAsync(Uri baseUri, ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit, CancellationToken cancellationToken = default) =>
        LoadDetailsAsync(baseUri, searchItem, unit, cancellationToken);

    private async ValueTask<ApiContentItem> LoadDetailsAsync(Uri baseUri, ApiContentSearchItem searchItem,
        ApiContentUnitRequest? requested, CancellationToken cancellationToken)
    {
        var (type, id) = Identity(searchItem.ExternalId);
        using var json = await ReadAsync(baseUri, $"api/details?id={id}&type={type}", cancellationToken);
        RequireSuccess(json.RootElement);
        var mapped = TryMap(json.RootElement) ?? throw new InvalidDataException("FramexTV returned an invalid title.");
        if (mapped.ExternalId != searchItem.ExternalId) throw new InvalidDataException("FramexTV returned a different title.");
        var item = mapped.PreviewItem;
        if (type == "movie")
            return new(item with { Units = [new() { Kind = "episode", DisplayNumber = "1", Name = item.Name }] });
        var seasons = Rows(json.RootElement, "seasons").ToArray();
        if (seasons.Length > 100) throw new InvalidDataException("Too many FramexTV seasons.");
        var loaded = new List<CatalogTransferSeason>();
        var seen = new HashSet<int>();
        foreach (var season in seasons)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var number = Integer(season, "season_number") ?? Integer(season, "number");
            if (number is null or < 0 || !seen.Add(number.Value)) continue;
            if (requested is not null && number != (requested.SeasonNumber ?? 1)) continue;
            using var episodes = await ReadAsync(baseUri, $"api/season?id={id}&season={number}", cancellationToken);
            RequireSuccess(episodes.RootElement);
            var actual = Integer(episodes.RootElement, "season_number") ?? Integer(episodes.RootElement, "number");
            if (actual != number)
            {
                // The public API currently coerces season=0 to season 1.
                // An unavailable specials listing must not reject every regular
                // season during a full catalog import, nor be relabeled as 0.
                if (number == 0 && requested is null) continue;
                throw new InvalidDataException($"FramexTV returned season {actual?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} instead of requested season {number}.");
            }
            var units = Rows(episodes.RootElement, "episodes").Select((row, index) => Episode(row, number.Value, index)).ToArray();
            if (units.Length > 20_000 || loaded.Sum(s => s.Units.Count) + units.Length > 20_000)
                throw new InvalidDataException("Too many FramexTV episodes.");
            loaded.Add(new() { Number = number.Value, Units = units.DistinctBy(u => u.DisplayNumber).ToArray() });
        }
        return new(item with { Seasons = loaded });
    }

    public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(Uri baseUri, ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit, CancellationToken cancellationToken = default)
    {
        if (unit.Kind != ContentUnitKind.Episode) return null;
        var (type, id) = Identity(searchItem.ExternalId);
        var suffix = "";
        var route = $"embed/movie/{id}";
        if (type == "tv")
        {
            // FramexTV also treats season=0 as 1 for playback. Never offer that
            // endpoint (or its browser embed) for a special from another source.
            if (!Positive(unit.DisplayNumber, out var episode) || unit.SeasonNumber is < 1) return null;
            var season = unit.SeasonNumber ?? 1;
            suffix = $"&season={season}&episode={episode}";
            route = $"embed/tv/{id}/{season}/{episode}";
        }
        else if (unit.SeasonNumber is not null) return null;
        var reference = new Uri(WebRoot(baseUri), route);
        using var json = await ReadAsync(baseUri, $"api/stream?type={type}&id={id}{suffix}", cancellationToken);
        var body = Property(json.RootElement, "detail");
        if (body.ValueKind != JsonValueKind.Object) body = json.RootElement;
        var subtitles = ParseSubtitles(body);
        if (subtitles.Count == 0)
        {
            try
            {
                using var tracks = await ReadAsync(baseUri, $"api/subtitles?type={type}&id={id}{suffix}", cancellationToken);
                subtitles = ParseSubtitles(tracks.RootElement);
            }
            catch (Exception exception) when (exception is HttpRequestException or JsonException or InvalidDataException) { }
        }
        var headers = Property(body, "headers");
        var sources = new List<ApiContentVideoSource>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(body, "sources").Take(100))
        {
            if (!PublicUri(Text(row, "url"), out var uri) || !seen.Add(uri.AbsoluteUri)) continue;
            var sourceHeaders = Property(row, "headers");
            var referrer = HeaderUri(sourceHeaders, headers, "Referer") ?? reference;
            var origin = HeaderUri(sourceHeaders, headers, "Origin");
            if (origin is not null) origin = new Uri(origin.GetLeftPart(UriPartial.Authority) + "/");
            var mime = Text(row, "type")?.ToLowerInvariant() switch
            {
                "hls" or "m3u8" => "application/vnd.apple.mpegurl",
                "mp4" => "video/mp4",
                "dash" or "mpd" => "application/dash+xml",
                _ => HosterMimeTypeResolver.MimeTypeFromUrl(uri)
            };
            if (mime is null) continue;
            var label = Text(row, "server") ?? Text(body, "provider") ?? "FramexTV";
            label = $"{label} · {Text(row, "quality") ?? "Auto"} · {sources.Count + 1}";
            var tracks = ParseSubtitles(row);
            var agent = Header(sourceHeaders, "User-Agent") ?? Header(headers, "User-Agent");
            if (agent is not null && (agent.Length > 1024 || agent.Contains('\r') || agent.Contains('\n'))) agent = null;
            sources.Add(new(label, uri, mime, referrer, httpOrigin: origin, userAgent: agent,
                subtitles: tracks.Concat(subtitles).DistinctBy(t => t.Source.AbsoluteUri).Take(100).ToArray()));
        }
        if (sources.Count == 0)
            sources.Add(new("FramexTV · Web / captura nativa", reference, referrer: WebRoot(baseUri),
                kind: ApiContentVideoSourceKind.EmbeddedWeb, subtitles: subtitles));
        return new(unit.Kind, unit.DisplayNumber, unit.SeasonNumber, sources);
    }

    internal static IReadOnlyList<ApiContentSubtitleTrack> ParseSubtitles(JsonElement body)
    {
        var results = new List<ApiContentSubtitleTrack>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var row in Rows(body, "subtitles").Take(100))
        {
            if (!PublicUri(Text(row, "url"), out var uri) || !seen.Add(uri.AbsoluteUri)) continue;
            var language = Text(row, "lang") ?? Text(row, "language") ?? "und";
            var label = Text(row, "label") ?? language;
            if (string.IsNullOrWhiteSpace(label) || string.IsNullOrWhiteSpace(language) ||
                label.Contains('\r') || label.Contains('\n') || language.Contains('\r') || language.Contains('\n')) continue;
            results.Add(new(uri, label.Length > 200 ? label[..200] : label,
                language.Length > 80 ? "und" : language,
                Property(row, "isDefault").ValueKind == JsonValueKind.True && !results.Any(t => t.IsDefault)));
        }
        return results;
    }

    private static ApiContentSearchItem? TryMap(JsonElement row)
    {
        var id = Text(row, "id");
        var type = Text(row, "type");
        var name = Text(row, "title");
        if (!Positive(id, out _) || type is not ("movie" or "tv") || string.IsNullOrWhiteSpace(name)) return null;
        var runtime = Integer(row, "runtime");
        var year = Integer(row, "year");
        return new($"{type}/{id}", new()
        {
            MediaType = "movie",
            Subtype = type == "tv" ? "series" : "movie",
            Name = name,
            Description = Text(row, "overview"),
            OriginalName = Text(row, "original_title") ?? Text(row, "original_name"),
            ThumbnailRef = Art(Text(row, "poster") ?? Text(row, "image")),
            BackdropRef = Art(Text(row, "backdrop")),
            ReleaseYear = year is { } y && CatalogReleaseYear.IsPlausible(y) ? y : null,
            ReleaseDate = Date(Text(row, "release_date")),
            RuntimeSeconds = runtime is > 0 and < 10000 ? runtime * 60 : null,
            Genres = Rows(row, "genres").Select(g => g.ValueKind == JsonValueKind.String ? g.GetString() : Text(g, "name"))
                .OfType<string>().Where(g => !string.IsNullOrWhiteSpace(g)).DistinctBy(NameNormalizer.Normalize).ToArray(),
            Contributors = Rows(row, "cast").Take(100).Where(c => !string.IsNullOrWhiteSpace(Text(c, "name")))
                .DistinctBy(c => NameNormalizer.Normalize(Text(c, "name")!)).Select((c, i) => new CatalogTransferContributor
                {
                    Name = Text(c, "name")!,
                    ThumbnailRef = Art(Text(c, "profile")),
                    Credits = [new() { Role = "actor", CharacterName = Text(c, "character"), SortOrder = i }]
                }).ToArray()
        });
    }

    private static CatalogTransferUnit Episode(JsonElement row, int season, int index)
    {
        var number = Integer(row, "episode_number") ?? Integer(row, "number");
        if (number is null or < 1 || Integer(row, "season_number") is { } actual && actual != season)
            throw new InvalidDataException("FramexTV returned an invalid episode identity.");
        return new()
        {
            Kind = "episode",
            DisplayNumber = number.Value.ToString(CultureInfo.InvariantCulture),
            Name = Text(row, "name"),
            SortOrder = index,
            ThumbnailRef = Art(Text(row, "still")),
            ReleaseDate = Date(Text(row, "air_date"))
        };
    }

    private async Task<JsonDocument> ReadAsync(Uri baseUri, string path, CancellationToken cancellationToken)
    {
        var root = Root(baseUri);
        var roots = root.IdnHost is "framextv.xyz" or "www.framextv.xyz" ? Backends : [root];
        for (var i = 0; i < roots.Length; i++)
        {
            try
            {
                var text = await TmoProviderHttp.ReadStringAsync(httpClient, new Uri(roots[i], path), WebRoot(baseUri),
                    MaximumResponseBytes, "application/json", "FramexTV", cancellationToken);
                return JsonDocument.Parse(text);
            }
            catch (HttpRequestException exception) when (i + 1 < roots.Length &&
                (exception.StatusCode is null || (int)exception.StatusCode >= 500))
            { }
        }
        throw new InvalidDataException("FramexTV API unavailable.");
    }

    private static Uri Root(Uri uri) => TmoProviderUri.NormalizeRoot(uri, "FramexTV");
    private static Uri WebRoot(Uri uri) => uri.IdnHost.StartsWith("api.framextv.", StringComparison.Ordinal) ? Website : Root(uri);
    private static (string Type, string Id) Identity(string value)
    {
        var slash = value.AsSpan().IndexOf('/');
        if (slash < 1 || value[..slash] is not ("movie" or "tv") || !Positive(value[(slash + 1)..], out _))
            throw new ArgumentException("Invalid FramexTV title identity.", nameof(value));
        return (value[..slash], value[(slash + 1)..]);
    }
    private static (int Page, int Offset) Page(string? value)
    {
        if (value is null) return (1, 0);
        var parts = value.Split(':');
        if (parts.Length != 2 || !Positive(parts[0], out var page) ||
            !int.TryParse(parts[1], NumberStyles.None, CultureInfo.InvariantCulture, out var offset) || offset is < 0 or > 1000)
            throw new ArgumentException("Invalid FramexTV search cursor.", nameof(value));
        return (page, offset);
    }
    private static bool Positive(string? value, out int number) =>
        int.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out number) && number is > 0 and <= 100_000_000;
    private static JsonElement Property(JsonElement row, string key) =>
        row.ValueKind == JsonValueKind.Object && row.TryGetProperty(key, out var value) ? value : default;
    private static string? Text(JsonElement row, string key) => Property(row, key).ValueKind switch
    {
        JsonValueKind.String => Property(row, key).GetString(),
        JsonValueKind.Number => Property(row, key).GetRawText(),
        _ => null
    };
    private static int? Integer(JsonElement row, string key) =>
        int.TryParse(Text(row, key), NumberStyles.Integer, CultureInfo.InvariantCulture, out var value) ? value : null;
    private static IEnumerable<JsonElement> Rows(JsonElement row, string key) => Property(row, key).ValueKind == JsonValueKind.Array
        ? Property(row, key).EnumerateArray() : [];
    private static bool PublicUri(string? value, out Uri uri) => TmoProviderUri.TryCreatePublicHttpUri(value, out uri);
    private static string? Art(string? value) => PublicUri(value, out var uri) && uri.Scheme == "https" && uri.IdnHost == Artwork.IdnHost ? uri.AbsoluteUri : null;
    private static string? Date(string? value) => DateOnly.TryParseExact(value, "yyyy-MM-dd", CultureInfo.InvariantCulture,
        DateTimeStyles.None, out var date) ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) : null;
    private static Uri? HeaderUri(JsonElement local, JsonElement common, string key) =>
        PublicUri(Header(local, key) ?? Header(common, key), out var uri) ? uri : null;
    private static string? Header(JsonElement headers, string key)
    {
        if (headers.ValueKind == JsonValueKind.Object)
            foreach (var entry in headers.EnumerateObject())
                if (entry.Name.Equals(key, StringComparison.OrdinalIgnoreCase) && entry.Value.ValueKind == JsonValueKind.String)
                    return entry.Value.GetString();
        return null;
    }
    private static void RequireSuccess(JsonElement row)
    {
        if (Property(row, "success").ValueKind != JsonValueKind.True)
            throw new InvalidDataException("FramexTV did not return a successful catalog response.");
    }
}

using System.Buffers;
using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads JKAnime's Spanish anime catalog. The public HTML exposes a CSRF
/// token on every page; search and paginated episode listings go through
/// JSON AJAX endpoints that require that token. Episode pages embed the
/// playback servers inline: JKPlayer exposes the HLS source as a base64
/// value (or a raw <c>.m3u8</c> URL), and a known set of mirror hosts is
/// exposed as base64-encoded <c>remote</c> values inside a <c>var servers</c>
/// JSON array.
/// </summary>
public sealed partial class JkAnimeApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "jkanime";

    private const int MaximumSearchBytes = 4 * 1024 * 1024;
    private const int MaximumHomepageBytes = 8 * 1024 * 1024;
    private const int MaximumDirectoryBytes = 8 * 1024 * 1024;
    private const int MaximumItemBytes = 8 * 1024 * 1024;
    private const int MaximumEpisodeBytes = 8 * 1024 * 1024;
    private const int MaximumPlayerBytes = 4 * 1024 * 1024;
    private const int MaximumSearchResults = 500;
    private const int MaximumDailyUpdateResults = 24;
    private const int MaximumEpisodeCount = 20_000;
    private const int MaximumServers = 100;
    private const int MaximumNumericIdLength = 32;
    private const string EpisodeThumbnailBase =
        "https://cdn.jkdesa.com/assets/images/animes/video/image_thumb/";

    private static readonly TimeSpan AjaxTokenLifetime =
        TimeSpan.FromMinutes(30);
    private static readonly TimeSpan HtmlRegexTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly Regex TagNameRegex = new(
        "^[A-Za-z][A-Za-z0-9]*$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));

    private static readonly Regex SafeSlugRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_\\-]{0,239}$",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex SafeNumericIdRegex = new(
        "^[0-9]{1,32}$",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex SafeEpisodeNumberRegex = new(
        "^[0-9]{1,6}$",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex CsrfMetaRegex = new(
        """
        <meta\b(?=[^>]*\bname\s*=\s*(?:["']csrf-token["']))[^>]*\bcontent\s*=\s*(?:"(?<token>[^"]+)"|'(?<token>[^']+)')
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex AnchorRegex = new(
        """
        <a\b(?<attributes>[^>]*)>(?<body>.*?)</a>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ImageAttributeRegex = new(
        """
        \b(?<name>src|data-src|data-setbg|poster)\s*=\s*(?:"(?<value>[^"]+)"|'(?<value>[^']+)')
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex AttributeRegex = new(
        """
        (?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s>]+))
        """,
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ClassAttributeRegex = new(
        """
        \bclass\s*=\s*(?:"(?<value>[^"]+)"|'(?<value>[^']+)')
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex GenreSelectRegex = new(
        """
        <select\b(?=[^>]*\bname\s*=\s*["']genero["'])[^>]*>
        (?<body>.*?)
        </select>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex SelectOptionRegex = new(
        """
        <option\b(?=[^>]*\bvalue\s*=\s*(?:"(?<value>[^"]*)"|'(?<value>[^']*)'))[^>]*>
        (?<label>.*?)
        </option>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex DirectoryTitleRegex = new(
        """
        <(?<tag>h[1-6])\b
        (?=[^>]*\bclass\s*=\s*["'][^"']*\bcard-title\b[^"']*["'])
        [^>]*>(?<value>.*?)</\k<tag>>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex DirectoryBadgeRegex = new(
        """
        <span\b
        (?=[^>]*\bclass\s*=\s*["'][^"']*\bbadge\b[^"']*["'])
        [^>]*>(?<value>.*?)</span>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex DirectoryYearRegex = new(
        @"\b(?<year>(?:19|20)\d{2})\b",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex DirectoryPageRegex = new(
        @"(?:[?&]|&amp;)p=(?<page>[0-9]{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex TagRegex = new(
        "<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex JkPlayerAtobRegex = new(
        """
        url\s*:\s*atob\(\s*['"](?<value>[^'"]+)['"]\s*\)
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex JkPlayerRawHlsRegex = new(
        """
        (?:url|src|file|loadSource)\s*[:=(]\s*['"](?<value>https?://[^'"]+\.m3u8[^'"]*)['"]
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex JkPlayerInlineJsonRegex = new(
        """
        (?:video\[(?<index>\d+)\]|sources)\s*=\s*(?<value>'(?:\\.|[^'\\])*'|"(?:\\.|[^"\\])*"|\[[^\]]*\]|\{[^}]*\})
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ScriptElementRegex = new(
        "<script\\b[^>]*>(?<body>.*?)</script>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ServersAssignmentRegex = new(
        """
        var\s+servers\s*=\s*
        """,
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex AnimesAssignmentRegex = new(
        "var\\s+animes\\s*=\\s*",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);

    private static readonly HashSet<string> SupportedServerNames = new(
        StringComparer.OrdinalIgnoreCase)
    {
        "Streamwish",
        "VOE",
        "Vidhide",
        "Filemoon",
        "Mixdrop",
        "Mp4upload",
        "Streamtape",
        "Doodstream"
    };

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly VideoHosterResolver? _hosterResolver;
    private readonly ApiContentGenreMapCache _genreMaps = new();
    private readonly SemaphoreSlim _csrfGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _episodeUris =
        new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset _csrfTokenAcquiredAt = DateTimeOffset.MinValue;
    private string? _csrfToken;
    private string? _csrfTokenRoot;

    public JkAnimeApiContentProvider(
        HttpClient httpClient,
        TimeProvider? timeProvider = null,
        VideoHosterResolver? hosterResolver = null)
    {
        _hosterResolver = hosterResolver;
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.JkAnime;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = NormalizeRootUri(baseUri);
        return _trustedArtworkOrigins.Values
            .Prepend(GetOrigin(root))
            .Prepend(new Uri("https://cdn.jkdesa.com/"))
            .DistinctBy(
                static uri =>
                    $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    /// <summary>
    /// The directory publishes its genre taxonomy and filters natively through
    /// the "genero" and "fecha" fields. It also accepts filter-only browsing,
    /// with ordinary page links carried forward as continuation tokens.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.Native,
        releaseYear: ApiContentFilterSupport.Native,
        supportsFilterOnlySearch: true);

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (mediaType is not null and not MediaType.Movie)
        {
            return [];
        }

        var payload = await PostJsonAsync(
            root,
            new Uri(root, "ajax_search"),
            query,
            cancellationToken);
        try
        {
            using var document = JsonDocument.Parse(payload);
            if (document.RootElement.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            var results = new List<ApiContentSearchItem>();
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in document.RootElement.EnumerateArray())
            {
                if (results.Count >= MaximumSearchResults)
                {
                    break;
                }

                if (entry.ValueKind is not JsonValueKind.Object ||
                    !entry.TryGetProperty("slug", out var slugElement) ||
                    slugElement.ValueKind is not JsonValueKind.String ||
                    !entry.TryGetProperty("title", out var titleElement) ||
                    titleElement.ValueKind is not JsonValueKind.String)
                {
                    continue;
                }

                var slug = NormalizeSlug(slugElement.GetString() ?? string.Empty);
                if (!SafeSlugRegex.IsMatch(slug) || !seen.Add(slug))
                {
                    continue;
                }

                var title = NormalizeText(titleElement.GetString() ?? string.Empty);
                if (title.Length == 0)
                {
                    continue;
                }

                string? image = null;
                if (entry.TryGetProperty("image", out var imageElement) &&
                    imageElement.ValueKind is JsonValueKind.String)
                {
                    image = imageElement.GetString();
                }
                else if (entry.TryGetProperty("thumbnail", out var thumbElement) &&
                         thumbElement.ValueKind is JsonValueKind.String)
                {
                    image = thumbElement.GetString();
                }

                var isMovie = false;
                if (entry.TryGetProperty("type", out var typeElement) &&
                    typeElement.ValueKind is JsonValueKind.String)
                {
                    var type = (typeElement.GetString() ?? string.Empty)
                        .Trim();
                    isMovie = string.Equals(
                                  type,
                                  "Pelicula",
                                  StringComparison.OrdinalIgnoreCase) ||
                              string.Equals(
                                  type,
                                  "Movie",
                                  StringComparison.OrdinalIgnoreCase);
                }

                results.Add(new ApiContentSearchItem(
                    slug,
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = isMovie ? "anime_movie" : "anime_series",
                        Name = title,
                        PublicationStatus =
                            ReadJsonString(entry, "status") ??
                            ReadJsonString(entry, "estado"),
                        ThumbnailRef = ResolveArtworkReference(root, image),
                        Categories = ["Anime"]
                    }));
            }

            return results;
        }
        catch (JsonException)
        {
            return [];
        }
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Movie)
        {
            return ApiContentSearchPage.Empty;
        }

        if (!request.HasFilters)
        {
            if (request.ContinuationToken is not null)
            {
                return ApiContentSearchPage.Empty;
            }

            return new ApiContentSearchPage(await SearchAsync(
                root,
                request.Query!,
                request.MediaType,
                cancellationToken));
        }

        string? genreToken = null;
        if (request.Genre is { } genre)
        {
            var genres = await LoadGenreMapAsync(root, cancellationToken);
            if (!genres.TryGetToken(genre, out genreToken))
            {
                return ApiContentSearchPage.Empty;
            }
        }

        var page = ParseDirectoryPageToken(request.ContinuationToken);
        var parameters = new List<string>(3);
        if (genreToken is not null)
        {
            parameters.Add($"genero={Uri.EscapeDataString(genreToken)}");
        }

        if (request.ReleaseYear is { } year)
        {
            parameters.Add(
                $"fecha={year.ToString(CultureInfo.InvariantCulture)}");
        }

        if (page > 1)
        {
            parameters.Add(
                $"p={page.ToString(CultureInfo.InvariantCulture)}");
        }

        var endpoint = new Uri(
            root,
            "directorio" +
            (parameters.Count == 0
                ? string.Empty
                : $"?{string.Join('&', parameters)}"));
        var html = await ReadStringAsync(
            endpoint,
            root,
            MaximumDirectoryBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            cancellationToken);
        var items = ParseDirectoryCards(
            root,
            html,
            request.Query,
            request.Genre);
        var nextPage = HasDirectoryPage(html, page + 1)
            ? (page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        return new ApiContentSearchPage(items, nextPage);
    }

    private ValueTask<ApiContentGenreMap> LoadGenreMapAsync(
        Uri root,
        CancellationToken cancellationToken) =>
        _genreMaps.GetAsync(
            root,
            async token => ParseGenreMap(await ReadStringAsync(
                new Uri(root, "directorio"),
                root,
                MaximumDirectoryBytes,
                "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
                token)),
            cancellationToken);

    internal static ApiContentGenreMap ParseGenreMap(string html)
    {
        var select = GenreSelectRegex.Match(html);
        if (!select.Success)
        {
            return ApiContentGenreMap.Empty;
        }

        var entries = new List<(string Label, string Token)>();
        foreach (Match option in SelectOptionRegex.Matches(
                     select.Groups["body"].Value))
        {
            var token = DecodeHtml(option.Groups["value"].Value);
            if (!SafeSlugRegex.IsMatch(token))
            {
                continue;
            }

            entries.Add((
                NormalizeText(option.Groups["label"].Value),
                token));
        }

        return ApiContentGenreMap.FromUpstreamLabels(entries);
    }

    private IReadOnlyList<ApiContentSearchItem> ParseDirectoryCards(
        Uri root,
        string html,
        string? query,
        ApiContentGenre? filteredGenre)
    {
        var scriptedResults = ParseDirectoryJson(
            root,
            html,
            query,
            filteredGenre);
        if (scriptedResults.Count > 0)
        {
            return scriptedResults;
        }

        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            if (results.Count >= MaximumSearchResults)
            {
                break;
            }

            var body = anchor.Groups["body"].Value;
            if (!body.Contains(
                    "card-title",
                    StringComparison.OrdinalIgnoreCase) &&
                !body.Contains(
                    "d-thumb",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var href = ReadHref(anchor.Groups["attributes"].Value);
            if (string.IsNullOrWhiteSpace(href) ||
                !Uri.TryCreate(root, href, out var itemUri) ||
                !IsSafeRemoteHttpUri(itemUri) ||
                !string.Equals(
                    root.IdnHost,
                    itemUri.IdnHost,
                    StringComparison.OrdinalIgnoreCase) ||
                root.Port != itemUri.Port)
            {
                continue;
            }

            var slug = NormalizeSlug(itemUri.AbsolutePath);
            if (!SafeSlugRegex.IsMatch(slug) || !seen.Add(slug))
            {
                continue;
            }

            var titleMatch = DirectoryTitleRegex.Match(body);
            var title = titleMatch.Success
                ? NormalizeText(titleMatch.Groups["value"].Value)
                : NormalizeText(ReadImageAttribute(body) ?? string.Empty);
            if (title.Length == 0 ||
                query is not null && !Contains(title, query))
            {
                continue;
            }

            var badges = DirectoryBadgeRegex.Matches(body)
                .Select(static badge =>
                    NormalizeText(badge.Groups["value"].Value))
                .Where(static value => value.Length > 0)
                .ToArray();
            var type = badges.FirstOrDefault() ?? string.Empty;
            var publicationStatus = badges.FirstOrDefault(status =>
                CatalogTransferMapper.ParsePublicationStatus(status) is not
                    PublicationStatus.Unknown);
            var yearMatch = DirectoryYearRegex.Match(NormalizeText(body));
            int? releaseYear = yearMatch.Success &&
                               int.TryParse(
                                   yearMatch.Groups["year"].Value,
                                   NumberStyles.None,
                                   CultureInfo.InvariantCulture,
                                   out var parsedYear)
                ? parsedYear
                : null;
            var image = ReadImageAttribute(body);
            results.Add(new ApiContentSearchItem(
                slug,
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = NormalizeLookupText(type).Contains(
                        "pelicula",
                        StringComparison.Ordinal)
                        ? "anime_movie"
                        : "anime_series",
                    Name = title,
                    PublicationStatus = publicationStatus,
                    ReleaseYear = releaseYear,
                    ThumbnailRef = ResolveArtworkReference(root, image),
                    Genres = filteredGenre is null
                        ? []
                        :
                        [
                            filteredGenre.Aliases.FirstOrDefault() ??
                            filteredGenre.Key
                        ],
                    Categories = type.Length == 0
                        ? ["Anime"]
                        : ["Anime", type]
                }));
        }

        return results;
    }

    private IReadOnlyList<ApiContentSearchItem> ParseDirectoryJson(
        Uri root,
        string html,
        string? query,
        ApiContentGenre? filteredGenre)
    {
        foreach (Match match in AnimesAssignmentRegex.Matches(html))
        {
            var remaining = html.AsSpan(match.Index + match.Length);
            if (!TryReadJavaScriptAssignedValue(remaining, out var literal))
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(literal.ToString());
                if (document.RootElement.ValueKind is not JsonValueKind.Object ||
                    !document.RootElement.TryGetProperty("data", out var data) ||
                    data.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                var results = new List<ApiContentSearchItem>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var entry in data.EnumerateArray())
                {
                    if (results.Count >= MaximumSearchResults ||
                        entry.ValueKind is not JsonValueKind.Object)
                    {
                        break;
                    }

                    var slug = NormalizeSlug(ReadJsonString(entry, "slug") ?? string.Empty);
                    var title = NormalizeText(ReadJsonString(entry, "title") ?? string.Empty);
                    if (!SafeSlugRegex.IsMatch(slug) ||
                        !seen.Add(slug) ||
                        title.Length == 0 ||
                        query is not null && !Contains(title, query))
                    {
                        continue;
                    }

                    var type = ReadJsonString(entry, "tipo") ??
                        ReadJsonString(entry, "type") ??
                        string.Empty;
                    var normalizedType = NormalizeLookupText(type);
                    var isMovie = normalizedType.Contains(
                                      "pelicula",
                                      StringComparison.Ordinal) ||
                                  normalizedType.Contains(
                                      "movie",
                                      StringComparison.Ordinal);
                    results.Add(new ApiContentSearchItem(
                        slug,
                        new CatalogTransferItem
                        {
                            MediaType = "movie",
                            Subtype = isMovie ? "anime_movie" : "anime_series",
                            Name = title,
                            PublicationStatus =
                                ReadJsonString(entry, "status") ??
                                ReadJsonString(entry, "estado"),
                            ThumbnailRef = ResolveArtworkReference(
                                root,
                                ReadJsonString(entry, "image")),
                            Genres = filteredGenre is null
                                ? []
                                :
                                [
                                    filteredGenre.Aliases.FirstOrDefault() ??
                                    filteredGenre.Key
                                ],
                            Categories = type.Length == 0
                                ? ["Anime"]
                                : ["Anime", type]
                        }));
                }

                return results;
            }
            catch (JsonException)
            {
                // Fall through to the server-rendered card parser. JKAnime
                // still serves that older shape on some cached pages.
            }
        }

        return [];
    }

    private static int ParseDirectoryPageToken(string? token)
    {
        if (token is null)
        {
            return 1;
        }

        if (!int.TryParse(
                token,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var page) ||
            page is < 1 or > 99_999)
        {
            throw new ArgumentException(
                "JKAnime returned an invalid directory page token.",
                nameof(token));
        }

        return page;
    }

    private static bool HasDirectoryPage(string html, int expectedPage)
    {
        foreach (Match match in DirectoryPageRegex.Matches(html))
        {
            if (int.TryParse(
                    match.Groups["page"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var page) &&
                page == expectedPage)
            {
                return true;
            }
        }

        return false;
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        LoadDailyUpdatesAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        var html = await ReadStringAsync(
            root,
            root,
            MaximumHomepageBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            cancellationToken);
        var updates = new List<DailyAnimeUpdate>();
        foreach (Match match in AnchorRegex.Matches(html))
        {
            if (updates.Count >= MaximumDailyUpdateResults)
            {
                break;
            }

            var href = ReadHref(match.Groups["attributes"].Value);
            if (string.IsNullOrWhiteSpace(href))
            {
                continue;
            }

            if (!TryParseEpisodeRoute(root, href, out var slug, out var number))
            {
                continue;
            }

            if (!SafeSlugRegex.IsMatch(slug))
            {
                continue;
            }

            var body = match.Groups["body"].Value;
            var title = NormalizeText(ReadTextContent(body, "h1, h2, h3, h4, h5, .title") ?? string.Empty);
            if (title.Length == 0)
            {
                continue;
            }

            var image = ReadImageAttribute(body);
            var episodeUri = new Uri(
                root,
                $"{slug}/{number.ToString(CultureInfo.InvariantCulture)}/");
            updates.Add(new DailyAnimeUpdate(
                slug,
                title,
                image,
                number,
                episodeUri));
        }

        return updates
            .GroupBy(
                static update => update.Slug,
                StringComparer.OrdinalIgnoreCase)
            .Take(MaximumDailyUpdateResults)
            .Select(group =>
            {
                var latest = group
                    .OrderByDescending(static update => update.Number)
                    .First();
                foreach (var update in group)
                {
                    _episodeUris[BuildEpisodeKey(
                        root,
                        update.Slug,
                        update.Number)] = update.EpisodeUri;
                }

                return new ApiContentSearchItem(
                    latest.Slug,
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = "anime_series",
                        Name = latest.Title,
                        ThumbnailRef = ResolveArtworkReference(
                            root,
                            latest.Thumbnail),
                        Categories = ["Anime"],
                        Units = group
                            .OrderBy(static update => update.Number)
                            .Select(static (entry, index) =>
                                new CatalogTransferUnit
                                {
                                    Kind = "episode",
                                    DisplayNumber = entry.Number
                                        .ToString(
                                            CultureInfo.InvariantCulture),
                                    SortOrder = index,
                                    ThumbnailRef = entry.Thumbnail
                                })
                            .ToArray()
                    });
            })
            .ToArray();
    }

    public ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default) =>
        LoadItemCoreAsync(
            baseUri,
            searchItem,
            targetUnit: null,
            cancellationToken);

    public ValueTask<ApiContentItem> LoadItemForUnitAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return LoadItemCoreAsync(
            baseUri,
            searchItem,
            unit,
            cancellationToken);
    }

    private async ValueTask<ApiContentItem> LoadItemCoreAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest? targetUnit,
        CancellationToken cancellationToken)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        var slug = ValidateSlug(searchItem.ExternalId);
        var itemUri = new Uri(root, $"{slug}/");
        var html = await ReadStringAsync(
            itemUri,
            root,
            MaximumItemBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            cancellationToken);
        var details = ReadAnimeDetails(html, slug);
        var subtype = details.IsMovie ? "anime_movie" : "anime_series";

        if (details.IsMovie)
        {
            var movieUnit = new CatalogTransferUnit
            {
                Kind = "episode",
                DisplayNumber = "1",
                SortOrder = 0,
                Name = details.Title,
                ThumbnailRef = ResolveArtworkReference(
                    root,
                    details.Poster)
            };
            _episodeUris[BuildEpisodeKey(root, slug, 1)] = new Uri(
                root,
                $"{slug}/1/");
            return new ApiContentItem(new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = subtype,
                Name = details.Title,
                Description = details.Overview,
                ReleaseDate = details.ReleaseDate,
                PublicationStatus = details.PublicationStatus,
                RuntimeSeconds = details.RuntimeSeconds,
                ContentRating = details.Quality,
                ThumbnailRef = ResolveArtworkReference(
                    root,
                    details.Poster),
                BackdropRef = ResolveArtworkReference(
                    root,
                    details.Poster),
                Genres = details.Genres,
                Categories = ["Anime"],
                Units = [movieUnit],
                AlternativeNames = details.OriginalTitle is null
                    ? []
                    : [new CatalogTransferAlias
                      {
                          Name = details.OriginalTitle
                      }]
            });
        }

        var episodes = TryUseKnownEpisodeRoute(
            root,
            slug,
            searchItem.UnitRoutes,
            out var knownEpisodes)
            ? knownEpisodes
            : await LoadEpisodesAsync(
                root,
                itemUri,
                slug,
                details.NumericId,
                targetUnit?.DisplayNumber,
                cancellationToken);
        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = subtype,
            Name = details.Title,
            Description = details.Overview,
            ReleaseDate = details.ReleaseDate,
            PublicationStatus = details.PublicationStatus,
            RuntimeSeconds = details.RuntimeSeconds,
            ContentRating = details.Quality,
            ThumbnailRef = ResolveArtworkReference(
                root,
                details.Poster),
            BackdropRef = ResolveArtworkReference(
                root,
                details.Poster),
            Genres = details.Genres,
            Categories = ["Anime"],
            AlternativeNames = details.OriginalTitle is null
                ? []
                : [new CatalogTransferAlias
                  {
                      Name = details.OriginalTitle
                  }],
            Units = episodes
        });
    }

    public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit)
    {
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode ||
            unit.SeasonNumber is not null)
        {
            return [];
        }

        var root = NormalizeRootUri(baseUri);
        var slug = ValidateSlug(searchItem.ExternalId);
        var number = ValidateEpisodeNumber(unit.DisplayNumber);
        return _episodeUris.TryGetValue(
                   BuildEpisodeKey(root, slug, number),
                   out var episodeUri) &&
               HasSameOrigin(root, episodeUri)
            ?
            [
                new ApiProviderUnitRoute(
                    ContentUnitKind.Episode,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    episodeUri.PathAndQuery)
            ]
            : [];
    }

    public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode)
        {
            return null;
        }

        var slug = ValidateSlug(searchItem.ExternalId);
        var number = ValidateEpisodeNumber(unit.DisplayNumber);
        var episodeKey = BuildEpisodeKey(root, slug, number);
        if (!_episodeUris.TryGetValue(episodeKey, out var episodeUri))
        {
            _ = await LoadItemForUnitAsync(
                root,
                searchItem,
                unit,
                cancellationToken);
            if (!_episodeUris.TryGetValue(episodeKey, out episodeUri))
            {
                return null;
            }
        }

        var html = await ReadStringAsync(
            episodeUri,
            new Uri(root, $"{slug}/"),
            MaximumEpisodeBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            cancellationToken);
        var servers = await ReadServersAsync(
            root,
            episodeUri,
            html,
            cancellationToken);
        if (servers.Count == 0)
        {
            return null;
        }

        var resolvedServers = await ResolveHosterSourcesAsync(
            servers,
            cancellationToken);
        if (resolvedServers.Count == 0)
        {
            return null;
        }

        return new ApiContentVideoSet(
            ContentUnitKind.Episode,
            number.ToString(CultureInfo.InvariantCulture),
            unit.SeasonNumber,
            resolvedServers);
    }

    private async ValueTask<IReadOnlyList<ApiContentVideoSource>>
        ResolveHosterSourcesAsync(
            IReadOnlyList<ApiContentVideoSource> sources,
            CancellationToken cancellationToken)
    {
        if (_hosterResolver is null || sources.Count == 0)
        {
            return sources;
        }

        return await _hosterResolver.ResolveAsync(sources, cancellationToken);
    }

    private async Task<List<ApiContentVideoSource>> ReadServersAsync(
        Uri root,
        Uri episodeUri,
        string html,
        CancellationToken cancellationToken)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var servers = new List<ApiContentVideoSource>();
        foreach (Match scriptMatch in ScriptElementRegex.Matches(html))
        {
            var script = scriptMatch.Groups["body"].Value;
            if (script.Length == 0)
            {
                continue;
            }

            foreach (Match iframeMatch in JkPlayerInlineJsonRegex.Matches(script))
            {
                var raw = DecodeJavaScriptLiteral(
                    iframeMatch.Groups["value"].ValueSpan);
                foreach (Match attrMatch in AttributeRegex.Matches(raw))
                {
                    var name = attrMatch.Groups["name"].Value;
                    if (!name.Equals("src", StringComparison.OrdinalIgnoreCase) &&
                        !name.Equals("data-src", StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    var url = DecodeHtml(GetAttributeValue(attrMatch));
                    if (Uri.TryCreate(
                            url,
                            UriKind.Absolute,
                            out var inlineUri) &&
                        IsSafeRemoteHttpUri(inlineUri) &&
                        seen.Add(inlineUri.AbsoluteUri))
                    {
                        var index = iframeMatch.Groups["index"].Success
                            ? iframeMatch.Groups["index"].Value
                            : (servers.Count + 1).ToString(
                                CultureInfo.InvariantCulture);
                        servers.Add(new ApiContentVideoSource(
                            $"JKPlayer {index}",
                            inlineUri,
                            mimeType: null,
                            referrer: episodeUri,
                            kind: ApiContentVideoSourceKind.EmbeddedWeb));
                        if (servers.Count >= MaximumServers)
                        {
                            return servers;
                        }
                    }
                }
            }

            foreach (Match serversMatch in ServersAssignmentRegex.Matches(script))
            {
                var assignmentValue = script.AsSpan(
                    serversMatch.Index + serversMatch.Length);
                if (!TryReadJavaScriptAssignedValue(
                        assignmentValue,
                        out var literal))
                {
                    continue;
                }

                var json = DecodeJavaScriptLiteral(literal);
                try
                {
                    using var document = JsonDocument.Parse(json);
                    if (document.RootElement.ValueKind is JsonValueKind.Array)
                    {
                        foreach (var element in document.RootElement.EnumerateArray())
                        {
                            if (element.ValueKind is not JsonValueKind.Object)
                            {
                                continue;
                            }

                            if (!element.TryGetProperty("server", out var nameElement) ||
                                !element.TryGetProperty("remote", out var remoteElement))
                            {
                                continue;
                            }

                            var name = nameElement.GetString()?.Trim() ?? string.Empty;
                            var remote = remoteElement.GetString() ?? string.Empty;
                            if (name.Length == 0 ||
                                remote.Length == 0 ||
                                !SupportedServerNames.Contains(name) ||
                                !seen.Add(remote))
                            {
                                continue;
                            }

                            var decoded = TryDecodeBase64(remote);
                            if (decoded is null ||
                                !Uri.TryCreate(
                                    decoded,
                                    UriKind.Absolute,
                                    out var decodedUri) ||
                                !IsSafeRemoteHttpUri(decodedUri))
                            {
                                continue;
                            }

                            servers.Add(new ApiContentVideoSource(
                                name,
                                decodedUri,
                                mimeType: null,
                                referrer: episodeUri,
                                kind: ApiContentVideoSourceKind.EmbeddedWeb));
                            if (servers.Count >= MaximumServers)
                            {
                                return servers;
                            }
                        }
                    }
                }
                catch (JsonException)
                {
                    // The site occasionally ships malformed JSON in
                    // `var servers`; fall back to the JKPlayer extractor
                    // for that script below instead of failing the load.
                }
            }
        }

        var jkPlayer = await TryResolveJkPlayerAsync(
            root,
            episodeUri,
            html,
            servers,
            cancellationToken);
        if (jkPlayer is not null && seen.Add(jkPlayer.PlaybackUri.AbsoluteUri))
        {
            servers.Insert(
                0,
                new ApiContentVideoSource(
                    "JKPlayer",
                    jkPlayer.PlaybackUri,
                    "application/vnd.apple.mpegurl",
                    jkPlayer.Referrer));
        }

        return servers;
    }

    private async ValueTask<ResolvedMedia?> TryResolveJkPlayerAsync(
        Uri root,
        Uri episodeUri,
        string html,
        IReadOnlyList<ApiContentVideoSource> sources,
        CancellationToken cancellationToken)
    {
        var inline = TryReadJkPlayerMedia(html);
        if (inline is not null)
        {
            return await ValidateJkPlayerMediaAsync(
                inline,
                new Uri(root, "jkplayer/"),
                cancellationToken);
        }

        foreach (var playerUri in sources
                     .Select(static source => source.PlaybackUri)
                     .Where(uri =>
                         string.Equals(
                             uri.IdnHost,
                             root.IdnHost,
                             StringComparison.OrdinalIgnoreCase) &&
                         uri.Port == root.Port &&
                         uri.AbsolutePath.StartsWith(
                             "/jkplayer/",
                             StringComparison.OrdinalIgnoreCase))
                     .DistinctBy(
                         static uri => uri.AbsoluteUri,
                         StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                var playerHtml = await ReadStringAsync(
                    playerUri,
                    episodeUri,
                    MaximumPlayerBytes,
                    "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
                    cancellationToken);
                var resolved = TryReadJkPlayerMedia(playerHtml);
                if (resolved is null)
                {
                    continue;
                }

                var media = await ValidateJkPlayerMediaAsync(
                    resolved,
                    playerUri,
                    cancellationToken);
                if (media is not null)
                {
                    return media;
                }
            }
            catch (HttpRequestException)
            {
                // A player mirror can be unavailable while the next one works.
            }
            catch (InvalidDataException)
            {
                // Ignore an oversized or invalid player document and continue.
            }
        }

        return null;
    }

    private static string? TryReadJkPlayerMedia(string html)
    {
        var atobMatch = JkPlayerAtobRegex.Match(html);
        if (atobMatch.Success)
        {
            var decoded = TryDecodeBase64(atobMatch.Groups["value"].Value);
            if (!string.IsNullOrWhiteSpace(decoded))
            {
                return decoded;
            }
        }

        var rawHlsMatch = JkPlayerRawHlsRegex.Match(html);
        return rawHlsMatch.Success
            ? rawHlsMatch.Groups["value"].Value.Replace(
                "\\/",
                "/",
                StringComparison.Ordinal)
            : null;
    }

    private async ValueTask<ResolvedMedia?> ValidateJkPlayerMediaAsync(
        string resolved,
        Uri referrer,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(resolved, UriKind.Absolute, out var playbackUri) ||
            !IsSafeRemoteHttpUri(playbackUri))
        {
            return null;
        }

        // JKPlayer HLS responses stream from the embedded player URL itself,
        // so we only need to confirm the source is reachable.
        if (!await ReachabilityProbeAsync(
                playbackUri,
                referrer,
                cancellationToken))
        {
            return null;
        }

        return new ResolvedMedia(playbackUri, referrer);
    }

    private async ValueTask<bool> ReachabilityProbeAsync(
        Uri endpoint,
        Uri referrer,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRemoteHttpUri(endpoint))
        {
            return false;
        }

        try
        {
            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                endpoint);
            request.Headers.Accept.ParseAdd(
                "application/vnd.apple.mpegurl, */*;q=0.5");
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)");
            // The player page declares a no-referrer policy, so the media CDN
            // sees only the origin. Sending the full player URL is a request
            // shape it never receives from a browser.
            request.Headers.Referrer =
                new Uri(referrer.GetLeftPart(UriPartial.Authority) + "/");
            request.Headers.Add("Origin", referrer.GetLeftPart(UriPartial.Authority));
            using var response = await ApiProviderHttp.SendAsync(
                _httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            return response.IsSuccessStatusCode;
        }
        catch (HttpRequestException)
        {
            return false;
        }
        catch (TaskCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            return false;
        }
    }

    private async ValueTask<IReadOnlyList<CatalogTransferUnit>> LoadEpisodesAsync(
        Uri root,
        Uri itemUri,
        string slug,
        string numericId,
        string? targetDisplayNumber,
        CancellationToken cancellationToken)
    {
        var episodes = new List<CatalogTransferUnit>();
        var seen = new HashSet<int>();
        int? targetNumber = null;
        if (!string.IsNullOrWhiteSpace(targetDisplayNumber))
        {
            targetNumber = ValidateEpisodeNumber(targetDisplayNumber);
        }

        // Without this the empty id builds "ajax/episodes//1", and the failure
        // surfaces as an opaque transport error rather than naming the page
        // the id could not be read from.
        if (numericId.Length == 0)
        {
            throw new InvalidDataException(
                "JKAnime did not expose a numeric anime id for this title.");
        }

        for (var page = 1; page <= 1_000; page++)
        {
            var pagePayload = await PostJsonAsync(
                root,
                new Uri(root, $"ajax/episodes/{numericId}/{page}"),
                query: null,
                cancellationToken);
            using var document = JsonDocument.Parse(pagePayload);
            if (!document.RootElement.TryGetProperty(
                    "data",
                    out var data) ||
                data.ValueKind is not JsonValueKind.Array)
            {
                break;
            }

            var before = episodes.Count;
            foreach (var entry in data.EnumerateArray())
            {
                if (entry.ValueKind is not JsonValueKind.Object)
                {
                    continue;
                }

                if (!entry.TryGetProperty("number", out var numberElement) ||
                    !numberElement.TryGetInt32(out var number) ||
                    number <= 0 ||
                    !seen.Add(number))
                {
                    continue;
                }

                string? title = null;
                if (entry.TryGetProperty("title", out var titleElement) &&
                    titleElement.ValueKind is JsonValueKind.String)
                {
                    title = NormalizeText(titleElement.GetString() ?? string.Empty);
                    if (title.Length == 0)
                    {
                        title = null;
                    }
                }

                string? thumbnail = null;
                if (entry.TryGetProperty("image", out var imageElement) &&
                    imageElement.ValueKind is JsonValueKind.String)
                {
                    thumbnail = ResolveEpisodeThumbnail(imageElement.GetString());
                }

                var episodeUri = new Uri(
                    root,
                    $"{slug}/{number.ToString(CultureInfo.InvariantCulture)}/");
                _episodeUris[BuildEpisodeKey(root, slug, number)] = episodeUri;

                episodes.Add(new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber =
                        number.ToString(CultureInfo.InvariantCulture),
                    SortOrder = episodes.Count,
                    Name = title ?? $"Episodio {number.ToString(CultureInfo.InvariantCulture)}",
                    ThumbnailRef = ResolveArtworkReference(
                        root,
                        thumbnail)
                });
                if (episodes.Count >= MaximumEpisodeCount)
                {
                    return episodes;
                }
            }

            if (targetNumber is { } expected &&
                seen.Contains(expected))
            {
                break;
            }

            var lastPage = document.RootElement.TryGetProperty(
                "last_page",
                out var lastPageElement) &&
                lastPageElement.TryGetInt32(out var lastPageValue)
                ? lastPageValue
                : page;
            if (episodes.Count == before || page >= lastPage)
            {
                break;
            }
        }

        return episodes
            .Where(episode =>
                targetNumber is null ||
                episode.DisplayNumber ==
                targetNumber.Value.ToString(CultureInfo.InvariantCulture))
            .OrderBy(static episode =>
                int.Parse(
                    episode.DisplayNumber,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture))
            .Select((episode, index) => episode with { SortOrder = index })
            .ToArray();
    }

    private bool TryUseKnownEpisodeRoute(
        Uri root,
        string slug,
        IReadOnlyList<ApiProviderUnitRoute> routeHints,
        out IReadOnlyList<CatalogTransferUnit> episodes)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Episode ||
                hint.SeasonNumber is not null ||
                hint.DisplayNumber is not { } displayNumber ||
                !SafeEpisodeNumberRegex.IsMatch(displayNumber) ||
                !int.TryParse(
                    displayNumber,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number) ||
                number <= 0 ||
                !Uri.TryCreate(root, hint.Route, out var episodeUri) ||
                !IsSafeRemoteHttpUri(episodeUri) ||
                !HasSameOrigin(root, episodeUri) ||
                episodeUri.Fragment.Length > 0)
            {
                continue;
            }

            var expectedPath = new Uri(
                root,
                $"{slug}/{number.ToString(CultureInfo.InvariantCulture)}/")
                .AbsolutePath;
            if (!string.Equals(
                    episodeUri.AbsolutePath,
                    expectedPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            _episodeUris[
                BuildEpisodeKey(root, slug, number)] = episodeUri;
            episodes =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = displayNumber,
                    SortOrder = 0
                }
            ];
            return true;
        }

        episodes = [];
        return false;
    }

    private async ValueTask<string> PostJsonAsync(
        Uri root,
        Uri endpoint,
        string? query,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRemoteHttpUri(endpoint))
        {
            throw new InvalidDataException(
                "JKAnime returned an unsafe AJAX endpoint.");
        }

        var token = await EnsureCsrfTokenAsync(root, cancellationToken);
        var response = await SendJsonAsync(
            endpoint,
            token,
            query,
            cancellationToken);
        if (response.StatusCode == HttpStatusCode.ExpectationFailed ||
            (int)response.StatusCode == 419)
        {
            response.Dispose();
            token = await RefreshCsrfTokenAsync(
                root,
                rejectedToken: token,
                cancellationToken);
            response = await SendJsonAsync(
                endpoint,
                token,
                query,
                cancellationToken);
        }

        using (response)
        {
            if (!response.IsSuccessStatusCode)
            {
                throw new InvalidDataException(
                    $"JKAnime AJAX request failed ({(int)response.StatusCode}) for {endpoint}.");
            }

            return await ReadBoundedUtf8Async(
                response.Content,
                endpoint,
                MaximumSearchBytes,
                cancellationToken);
        }
    }

    private async ValueTask<HttpResponseMessage> SendJsonAsync(
        Uri endpoint,
        string token,
        string? query,
        CancellationToken cancellationToken)
    {
        KeyValuePair<string, string>[] values =
            string.IsNullOrWhiteSpace(query)
                ? [new("_token", token)]
                :
                [
                    new("_token", token),
                    new("q", query.Trim())
                ];

        using var request = new HttpRequestMessage(HttpMethod.Post, endpoint)
        {
            Content = new FormUrlEncodedContent(values)
        };
        request.Headers.Accept.ParseAdd(
            "application/json, text/javascript, */*; q=0.01");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)");
        request.Headers.Add("X-Requested-With", "XMLHttpRequest");
        request.Headers.Referrer = endpoint;
        return await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
    }

    private async ValueTask<string> EnsureCsrfTokenAsync(
        Uri root,
        CancellationToken cancellationToken)
    {
        if (TryGetFreshCsrfToken(root, out var token))
        {
            return token;
        }

        return await RefreshCsrfTokenAsync(
            root,
            rejectedToken: null,
            cancellationToken);
    }

    private async ValueTask<string> RefreshCsrfTokenAsync(
        Uri root,
        string? rejectedToken,
        CancellationToken cancellationToken)
    {
        await _csrfGate.WaitAsync(cancellationToken);
        try
        {
            if (TryGetFreshCsrfToken(root, out var cachedToken) &&
                (rejectedToken is null ||
                 !string.Equals(
                     cachedToken,
                     rejectedToken,
                     StringComparison.Ordinal)))
            {
                return cachedToken;
            }

            var html = await ReadStringAsync(
                root,
                root,
                MaximumHomepageBytes,
                "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
                cancellationToken);
            var match = CsrfMetaRegex.Match(html);
            if (!match.Success)
            {
                throw new InvalidDataException(
                    "JKAnime did not return a CSRF token.");
            }

            var token = match.Groups["token"].Value.Trim();
            if (token.Length == 0 || token.Length > 256)
            {
                throw new InvalidDataException(
                    "JKAnime returned an invalid CSRF token.");
            }

            _csrfToken = token;
            _csrfTokenRoot = GetOrigin(root).AbsoluteUri;
            _csrfTokenAcquiredAt = _timeProvider.GetUtcNow();
            return token;
        }
        finally
        {
            _csrfGate.Release();
        }
    }

    private bool TryGetFreshCsrfToken(Uri root, out string token)
    {
        token = _csrfToken ?? string.Empty;
        return token.Length > 0 &&
               string.Equals(
                   _csrfTokenRoot,
                   GetOrigin(root).AbsoluteUri,
                   StringComparison.OrdinalIgnoreCase) &&
               _timeProvider.GetUtcNow() - _csrfTokenAcquiredAt <
                   AjaxTokenLifetime;
    }

    private async ValueTask<string> ReadStringAsync(
        Uri endpoint,
        Uri referrer,
        int maximumBytes,
        string accept,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRemoteHttpUri(endpoint))
        {
            throw new InvalidDataException(
                "JKAnime returned an unsafe HTTP endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)");
        request.Headers.Referrer = referrer;
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        return await ReadBoundedUtf8Async(
            response.Content,
            endpoint,
            maximumBytes,
            cancellationToken);
    }

    private static async ValueTask<string> ReadBoundedUtf8Async(
        HttpContent content,
        Uri endpoint,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var contentLength = content.Headers.ContentLength;
        if (contentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"JKAnime response from {endpoint.Host} exceeds the " +
                $"{maximumBytes / 1024} KiB limit.");
        }

        await using var input =
            await content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(
            contentLength >= 0 && contentLength <= maximumBytes
                ? checked((int)contentLength.Value)
                : 0);
        var buffer = ArrayPool<byte>.Shared.Rent(16 * 1024);
        try
        {
            while (true)
            {
                var read = await input.ReadAsync(
                    buffer.AsMemory(0, 16 * 1024),
                    cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"JKAnime response from {endpoint.Host} exceeds the " +
                        $"{maximumBytes / 1024} KiB limit.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }

        return Encoding.UTF8.GetString(
            output.GetBuffer().AsSpan(
                0,
                checked((int)output.Length)));
    }

    private AnimeDetails ReadAnimeDetails(string html, string slug)
    {
        var numericId = ReadNumericId(html);
        var title = ReadAnimeTitle(html);
        if (title.Length == 0)
        {
            title = NormalizeText(ExtractTitleFromHtml(html));
        }

        if (title.Length == 0)
        {
            throw new InvalidDataException(
                $"JKAnime did not return a title for {slug}.");
        }

        var overview = ReadTextContent(
            html,
            ".sinopsis, .anime_info .sinopsis, .anime_info p.scroll") ?? string.Empty;
        if (overview.Length == 0)
        {
            overview = ReadMeta(html, "name", "description") ?? string.Empty;
        }

        var poster = ReadImageFromSelectors(
            html,
            ".poster, .anime_pic img, .movpic img, .anime_info img");
        if (poster is null)
        {
            poster = ReadMeta(html, "property", "og:image") ??
                ReadMeta(html, "name", "twitter:image");
        }

        var runtimeText = ReadMetadataValue(html, "Duracion");
        int? runtime = null;
        if (runtimeText is not null)
        {
            var digits = Regex.Replace(
                runtimeText,
                "[^0-9]",
                string.Empty,
                RegexOptions.CultureInvariant,
                HtmlRegexTimeout);
            if (int.TryParse(
                    digits,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var minutes) &&
                minutes > 0)
            {
                runtime = minutes * 60;
            }
        }

        var quality = ReadMetadataValue(html, "Calidad");
        var publicationStatus = ReadMetadataValue(html, "Estado");
        var genres = ReadGenres(html);
        var releaseDate = ReadReleaseDate(html);
        var isMovie = IsMoviePage(html);

        return new AnimeDetails(
            numericId,
            title,
            overview.Length == 0 ? null : overview,
            poster,
            string.IsNullOrEmpty(releaseDate) ? null : releaseDate,
            runtime,
            quality,
            publicationStatus,
            genres,
            null,
            isMovie);
    }

    private string ReadAnimeTitle(string html)
    {
        var h1 = ReadTextContent(html, ".anime_info h3, .anime_info h1") ?? string.Empty;
        if (h1.Length > 0)
        {
            return NormalizeText(h1);
        }

        return NormalizeText(
            ReadMeta(html, "property", "og:title") ?? string.Empty);
    }

    private string ReadNumericId(string html)
    {
        var match = Regex.Match(
            html,
            """
            \bdata-anime\s*=\s*["'](?<id>[0-9]{1,32})["']
            """,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            HtmlRegexTimeout);
        if (match.Success)
        {
            return match.Groups["id"].Value;
        }

        match = Regex.Match(
            html,
            """
            \bid\s*=\s*["'](?:guardar[_-]?anime|anime)[_-]?(?<id>[0-9]{1,32})["']
            """,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            HtmlRegexTimeout);
        return match.Success ? match.Groups["id"].Value : string.Empty;
    }

    private bool IsMoviePage(string html)
    {
        var section = ExtractSection(html, "anime_data", "anime_info_body");
        if (section.Length == 0)
        {
            section = html;
        }

        var type = ReadMetadataValue(section, "Tipo");
        if (type is null)
        {
            type = ReadMetadataValue(html, "Tipo");
        }

        if (type is not null)
        {
            var normalized = NormalizeLookupText(type);
            if (normalized.Contains("pelicula", StringComparison.Ordinal) ||
                normalized.Contains("movie", StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }

    private IReadOnlyList<string> ReadGenres(string html)
    {
        var genreValues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var genreBlock = ExtractSection(html, "anime_data", "anime_data_body");
        if (genreBlock.Length == 0)
        {
            genreBlock = html;
        }

        foreach (Match match in Regex.Matches(
                     genreBlock,
                     """
                     <li\b[^>]*>\s*<span\b[^>]*>\s*Generos?\s*</span>(?<body>.*?)</li>
                     """,
                     RegexOptions.IgnoreCase | RegexOptions.Singleline |
                         RegexOptions.CultureInvariant,
                     HtmlRegexTimeout))
        {
            var body = match.Groups["body"].Value;
            foreach (Match anchor in Regex.Matches(
                         body,
                         """
                         <a\b[^>]*>(?<value>.*?)</a>
                         """,
                         RegexOptions.IgnoreCase | RegexOptions.Singleline |
                             RegexOptions.CultureInvariant,
                         HtmlRegexTimeout))
            {
                var text = NormalizeText(anchor.Groups["value"].Value);
                if (text.Length > 0 && seen.Add(text))
                {
                    genreValues.Add(text);
                }
            }

            if (genreValues.Count > 0)
            {
                break;
            }
        }

        return genreValues;
    }

    private string? ReadReleaseDate(string html)
    {
        var value = ReadMetadataValue(html, "Estreno") ??
            ReadMetadataValue(html, "Emision") ??
            ReadMetadataValue(html, "Fecha");
        if (value is null)
        {
            return null;
        }

        var trimmed = NormalizeText(value);
        if (trimmed.Length == 0)
        {
            return null;
        }

        var yearMatch = Regex.Match(
            trimmed,
            @"\b(?<year>(?:19|20)\d{2})\b",
            RegexOptions.CultureInvariant,
            HtmlRegexTimeout);
        return yearMatch.Success ? yearMatch.Groups["year"].Value : null;
    }

    private string? ReadMetadataValue(string html, string label)
    {
        var pattern = new Regex(
            $$"""
            <li\b[^>]*>\s*<span\b[^>]*>\s*{{Regex.Escape(label)}}\b[^<]*</span>(?<body>.*?)</li>
            """,
            RegexOptions.IgnoreCase | RegexOptions.Singleline |
                RegexOptions.CultureInvariant,
            HtmlRegexTimeout);
        var match = pattern.Match(html);
        if (!match.Success)
        {
            return null;
        }

        return NormalizeText(match.Groups["body"].Value);
    }

    private static string? ReadImageFromSelectors(
        string html,
        string selectors)
    {
        foreach (var selector in selectors.Split(',', StringSplitOptions.RemoveEmptyEntries))
        {
            var (className, descendantTag) = ParseSelector(selector);
            if (className is null)
            {
                continue;
            }

            var scope = html;
            if (descendantTag is not null)
            {
                var start = FindClassElementEnd(html, className);
                if (start < 0)
                {
                    continue;
                }

                scope = html[start..];
            }

            var pattern = new Regex(
                descendantTag is null
                    ? $$"""
                        <img\b(?<attributes>(?=[^>]*\bclass\s*=\s*["'][^"']*\b{{Regex.Escape(className)}}\b[^"']*["'])[^>]*)>
                        """
                    : """
                        <img\b(?<attributes>[^>]*)>
                        """,
                RegexOptions.IgnoreCase | RegexOptions.Singleline |
                    RegexOptions.CultureInvariant,
                HtmlRegexTimeout);
            var match = pattern.Match(scope);
            if (match.Success)
            {
                return ReadImageAttributeFrom(match.Groups["attributes"].Value);
            }
        }

        return null;
    }

    private string? ReadTextContent(string html, string selectorList)
    {
        foreach (var selector in selectorList.Split(
                     ',',
                     StringSplitOptions.RemoveEmptyEntries))
        {
            var (className, descendantTag) = ParseSelector(selector);
            if (className is null)
            {
                continue;
            }

            var scope = html;
            if (descendantTag is not null)
            {
                var start = FindClassElementEnd(html, className);
                if (start < 0)
                {
                    continue;
                }

                scope = html[start..];
            }

            var pattern = new Regex(
                descendantTag is null
                    ? $$"""
                        <[A-Za-z][A-Za-z0-9]*\b(?=[^>]*\bclass\s*=\s*["'][^"']*\b{{Regex.Escape(className)}}\b[^"']*["'])[^>]*>(?<body>.*?)</[A-Za-z][A-Za-z0-9]*>
                        """
                    : $$"""
                        <{{Regex.Escape(descendantTag)}}\b[^>]*>(?<body>.*?)</{{Regex.Escape(descendantTag)}}\s*>
                        """,
                RegexOptions.IgnoreCase | RegexOptions.Singleline |
                    RegexOptions.CultureInvariant,
                HtmlRegexTimeout);
            var match = pattern.Match(scope);
            if (match.Success)
            {
                var value = NormalizeText(match.Groups["body"].Value);
                if (value.Length > 0)
                {
                    return value;
                }
            }
        }

        return string.Empty;
    }

    private static string? ExtractEffectiveClassName(string selector) =>
        ParseSelector(selector).ClassName;

    /// <summary>
    /// Splits a selector into the class to match and an optional descendant
    /// tag to look for inside it.
    /// <para>
    /// A descendant selector such as <c>.anime_info h3</c> names a container
    /// class and a child element. Taking only the last token treated "h3" as a
    /// class name, so the selector matched nothing and the title silently fell
    /// back to the page's SEO <c>og:title</c> — which carries the site name.
    /// </para>
    /// </summary>
    private static (string? ClassName, string? DescendantTag) ParseSelector(
        string selector)
    {
        var trimmed = selector.Trim();
        if (trimmed.Length == 0 || !trimmed.Contains('.'))
        {
            return (null, null);
        }

        var segments = trimmed.Split(
            ' ',
            StringSplitOptions.RemoveEmptyEntries);
        string? descendantTag = null;
        var classSegment = segments[^1];
        if (segments.Length > 1 && !classSegment.StartsWith('.'))
        {
            // ".container tag" — the class is the last dotted segment.
            descendantTag = classSegment;
            classSegment = segments
                .Take(segments.Length - 1)
                .LastOrDefault(static segment => segment.StartsWith('.')) ??
                string.Empty;
        }

        var className = classSegment.TrimStart('.');
        if (className.Length == 0 ||
            (descendantTag is not null &&
                !TagNameRegex.IsMatch(descendantTag)))
        {
            return (null, null);
        }

        var dotIndex = className.IndexOf('.');
        return (
            dotIndex >= 0 ? className[(dotIndex + 1)..] : className,
            descendantTag);
    }

    /// <summary>
    /// Finds where the element carrying <paramref name="className"/> starts,
    /// or -1.
    /// </summary>
    private static int FindClassElementEnd(string html, string className)
    {
        var match = new Regex(
            $$"""
            <[A-Za-z][A-Za-z0-9]*\b(?=[^>]*\bclass\s*=\s*["'][^"']*\b{{Regex.Escape(className)}}\b[^"']*["'])[^>]*>
            """,
            RegexOptions.IgnoreCase | RegexOptions.Singleline |
                RegexOptions.CultureInvariant,
            HtmlRegexTimeout).Match(html);
        return match.Success ? match.Index + match.Length : -1;
    }

    private static string? ReadHref(string attributes)
    {
        foreach (Match match in AttributeRegex.Matches(attributes))
        {
            if (string.Equals(
                    match.Groups["name"].Value,
                    "href",
                    StringComparison.OrdinalIgnoreCase))
            {
                return DecodeHtml(GetAttributeValue(match));
            }
        }

        return null;
    }

    /// <summary>
    /// Reads the anime slug and episode number out of an episode link.
    /// <para>
    /// The home page emits absolute hrefs. Splitting one of those directly
    /// puts the host in the segment the episode number is read from, so every
    /// card was rejected and the daily feed came back empty. Resolving against
    /// the site root first makes both link forms decompose the same way.
    /// </para>
    /// </summary>
    private static bool TryParseEpisodeRoute(
        Uri root,
        string href,
        out string slug,
        out int number)
    {
        slug = string.Empty;
        number = 0;
        if (Uri.TryCreate(root, href.Trim(), out var episodeUri))
        {
            if (!HasSameOrigin(root, episodeUri))
            {
                return false;
            }

            href = episodeUri.AbsolutePath;
        }

        var queryIndex = href.IndexOf('?');
        if (queryIndex >= 0)
        {
            href = href[..queryIndex];
        }

        var trimmed = href.Trim().Trim('/');
        if (trimmed.Length == 0)
        {
            return false;
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2)
        {
            return false;
        }

        var candidate = NormalizeSlug(parts[0]);
        if (!int.TryParse(
                parts[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed <= 0 ||
            candidate.Length == 0)
        {
            return false;
        }

        slug = candidate;
        number = parsed;
        return true;
    }

    private string? ReadImageAttribute(string body)
    {
        foreach (Match match in ImageAttributeRegex.Matches(body))
        {
            var name = match.Groups["name"].Value;
            if (name.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("data-src", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("poster", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeHtml(match.Groups["value"].Value);
            }
        }

        return null;
    }

    private static string? ReadImageAttributeFrom(string attributes)
    {
        foreach (Match match in AttributeRegex.Matches(attributes))
        {
            var name = match.Groups["name"].Value;
            if (name.Equals("src", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("data-src", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("poster", StringComparison.OrdinalIgnoreCase) ||
                name.Equals("data-setbg", StringComparison.OrdinalIgnoreCase))
            {
                return DecodeHtml(GetAttributeValue(match));
            }
        }

        return null;
    }

    private string? ReadMeta(string html, string attributeName, string attributeValue)
    {
        var pattern = new Regex(
            $$"""
            <meta\b(?=[^>]*\b{{Regex.Escape(attributeName)}}\s*=\s*["']{{Regex.Escape(attributeValue)}}["'])[^>]*\bcontent\s*=\s*(?:"(?<value>[^"]+)"|'(?<value>[^']+)')
            """,
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            HtmlRegexTimeout);
        var match = pattern.Match(html);
        return match.Success ? DecodeHtml(match.Groups["value"].Value) : null;
    }

    private static string ExtractTitleFromHtml(string html)
    {
        var titleMatch = Regex.Match(
            html,
            """
            <title\b[^>]*>(?<value>.*?)</title>
            """,
            RegexOptions.IgnoreCase | RegexOptions.Singleline |
                RegexOptions.CultureInvariant,
            HtmlRegexTimeout);
        return titleMatch.Success ? titleMatch.Groups["value"].Value : string.Empty;
    }

    private static string ExtractSection(string html, params string[] classes)
    {
        foreach (var className in classes)
        {
            var pattern = new Regex(
                $$"""
                <(?<tag>[A-Za-z][A-Za-z0-9]*)\b(?=[^>]*\bclass\s*=\s*["'][^"']*\b{{Regex.Escape(className)}}\b[^"']*["'])[^>]*>(?<body>.*?)</\k<tag>>
                """,
                RegexOptions.IgnoreCase | RegexOptions.Singleline |
                    RegexOptions.CultureInvariant,
                HtmlRegexTimeout);
            var match = pattern.Match(html);
            if (match.Success)
            {
                return match.Groups["body"].Value;
            }
        }

        return string.Empty;
    }

    private static string ExtractBalancedJson(
        string source,
        string marker,
        char opening,
        char closing)
    {
        var markerIndex = source.IndexOf(
            marker,
            StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return string.Empty;
        }

        var start = source.IndexOf(opening, markerIndex + marker.Length);
        if (start < 0)
        {
            return string.Empty;
        }

        var depth = 0;
        var inString = false;
        var escaped = false;
        for (var index = start; index < source.Length; index++)
        {
            var character = source[index];
            if (inString)
            {
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == '"')
                {
                    inString = false;
                }
                continue;
            }

            switch (character)
            {
                case '"':
                    inString = true;
                    break;
                case var c when c == opening:
                    depth++;
                    break;
                case var c when c == closing:
                    depth--;
                    if (depth == 0)
                    {
                        return source[start..(index + 1)];
                    }
                    break;
            }
        }

        return string.Empty;
    }

    /// <summary>
    /// Expands the bare file name the episode feed returns into a CDN URL.
    /// <para>
    /// The episode list answers with just <c>jkvideo_&lt;hash&gt;.jpg</c>, which
    /// the site joins to its thumbnail CDN path. Resolving it against the site
    /// root instead produced a dead URL for every episode thumbnail. Short or
    /// blank values mean "no still", matching the site's own guard.
    /// </para>
    /// </summary>
    private static string? ResolveEpisodeThumbnail(string? value)
    {
        var trimmed = value?.Trim();
        if (trimmed is not { Length: > 5 })
        {
            return null;
        }

        return trimmed.Contains('/', StringComparison.Ordinal)
            ? trimmed
            : EpisodeThumbnailBase + trimmed;
    }

    private string? ResolveArtworkReference(Uri root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var trimmed = value.Trim();
        if (trimmed.StartsWith("//", StringComparison.Ordinal))
        {
            trimmed = $"https:{trimmed}";
        }

        if (Uri.TryCreate(root, trimmed, out var absolute) &&
            IsSafeRemoteHttpUri(absolute))
        {
            _trustedArtworkOrigins[absolute.Host] = GetOrigin(absolute);
            return absolute.AbsoluteUri;
        }

        if (Uri.TryCreate(trimmed, UriKind.Absolute, out var direct) &&
            IsSafeRemoteHttpUri(direct))
        {
            _trustedArtworkOrigins[direct.Host] = GetOrigin(direct);
            return direct.AbsoluteUri;
        }

        return null;
    }

    private string ValidateSlug(string value)
    {
        var slug = NormalizeSlug(value);
        if (!SafeSlugRegex.IsMatch(slug))
        {
            throw new ArgumentException(
                "JKAnime requires a safe anime slug.",
                nameof(value));
        }

        return slug;
    }

    private int ValidateEpisodeNumber(string value)
    {
        var trimmed = value.Trim();
        if (!SafeEpisodeNumberRegex.IsMatch(trimmed) ||
            !int.TryParse(
                trimmed,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var number) ||
            number <= 0)
        {
            throw new ArgumentException(
                "JKAnime requires a positive numeric episode.",
                nameof(value));
        }

        return number;
    }

    private static string NormalizeSlug(string value)
    {
        var trimmed = value.Trim().Trim('/');
        var queryIndex = trimmed.IndexOf('?');
        if (queryIndex >= 0)
        {
            trimmed = trimmed[..queryIndex];
        }

        if (trimmed.Contains("jkanime.net/", StringComparison.OrdinalIgnoreCase))
        {
            trimmed = trimmed[(trimmed.IndexOf(
                "jkanime.net/",
                StringComparison.OrdinalIgnoreCase) + "jkanime.net/".Length)..];
        }

        var parts = trimmed.Split('/', StringSplitOptions.RemoveEmptyEntries)
            .Where(part =>
                !part.Equals("ver", StringComparison.OrdinalIgnoreCase) &&
                !part.Equals("anime", StringComparison.OrdinalIgnoreCase) &&
                !part.Equals("directorio", StringComparison.OrdinalIgnoreCase) &&
                !part.Equals("genero", StringComparison.OrdinalIgnoreCase) &&
                !part.Equals("categoria", StringComparison.OrdinalIgnoreCase))
            .ToArray();
        if (parts.Length == 0)
        {
            return string.Empty;
        }

        return parts[0];
    }

    private static Uri NormalizeRootUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            baseUri.Scheme is not ("http" or "https") ||
            baseUri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "JKAnime requires an absolute HTTP or HTTPS base URL.",
                nameof(baseUri));
        }

        var builder = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += '/';
        }

        var normalized = builder.Uri;
        if (!IsSafeRemoteHttpUri(normalized))
        {
            throw new ArgumentException(
                "JKAnime requires a public HTTP or HTTPS base URL.",
                nameof(baseUri));
        }

        return normalized;
    }

    private static string GetAttributeValue(Match match)
    {
        if (match.Groups["double"].Success)
        {
            return match.Groups["double"].Value;
        }

        if (match.Groups["single"].Success)
        {
            return match.Groups["single"].Value;
        }

        return match.Groups["bare"].Value;
    }

    private static string DecodeHtml(string value)
    {
        var working = value;
        if (working.Contains("&amp;", StringComparison.Ordinal))
        {
            working = WebUtility.HtmlDecode(working);
        }

        return working
            .Replace("\\/", "/", StringComparison.Ordinal)
            .Replace("&quot;", "\"", StringComparison.Ordinal)
            .Trim();
    }

    /// <summary>
    /// Reads the value after a JavaScript assignment. JKAnime historically
    /// wrapped its server JSON in a quoted JavaScript string, but now publishes
    /// values as raw arrays or objects. A small scanner handles all shapes
    /// without applying a nested, potentially backtracking regex to an entire
    /// episode page.
    /// </summary>
    private static bool TryReadJavaScriptAssignedValue(
        ReadOnlySpan<char> source,
        out ReadOnlySpan<char> value)
    {
        value = default;
        source = source.TrimStart();
        if (source.IsEmpty)
        {
            return false;
        }

        if (source[0] is '\'' or '"')
        {
            var quote = source[0];
            var escaped = false;
            for (var index = 1; index < source.Length; index++)
            {
                var character = source[index];
                if (escaped)
                {
                    escaped = false;
                }
                else if (character == '\\')
                {
                    escaped = true;
                }
                else if (character == quote)
                {
                    value = source[..(index + 1)];
                    return true;
                }
            }

            return false;
        }

        var opening = source[0];
        if (opening is not ('[' or '{'))
        {
            return false;
        }

        var closing = opening == '[' ? ']' : '}';

        var depth = 0;
        var stringQuote = '\0';
        var stringEscaped = false;
        for (var index = 0; index < source.Length; index++)
        {
            var character = source[index];
            if (stringQuote != '\0')
            {
                if (stringEscaped)
                {
                    stringEscaped = false;
                }
                else if (character == '\\')
                {
                    stringEscaped = true;
                }
                else if (character == stringQuote)
                {
                    stringQuote = '\0';
                }

                continue;
            }

            if (character is '\'' or '"')
            {
                stringQuote = character;
            }
            else if (character == opening)
            {
                depth++;
            }
            else if (character == closing && --depth == 0)
            {
                value = source[..(index + 1)];
                return true;
            }
        }

        return false;
    }

    private static string? ReadJsonString(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static string DecodeJavaScriptLiteral(ReadOnlySpan<char> literal)
    {
        if (literal.Length >= 2 &&
            (literal[0] == '\'' && literal[^1] == '\'' ||
             literal[0] == '"' && literal[^1] == '"'))
        {
            var inner = literal[1..^1];
            var builder = new StringBuilder(inner.Length);
            for (var index = 0; index < inner.Length; index++)
            {
                var character = inner[index];
                if (character != '\\' || index + 1 >= inner.Length)
                {
                    builder.Append(character);
                    continue;
                }

                var escaped = inner[++index];
                switch (escaped)
                {
                    case 'n':
                        builder.Append('\n');
                        break;
                    case 'r':
                        builder.Append('\r');
                        break;
                    case 't':
                        builder.Append('\t');
                        break;
                    case '"':
                        builder.Append('"');
                        break;
                    case '\'':
                        builder.Append('\'');
                        break;
                    case '\\':
                        builder.Append('\\');
                        break;
                    case '/':
                        builder.Append('/');
                        break;
                    default:
                        builder.Append(escaped);
                        break;
                }
            }

            return builder.ToString();
        }

        return literal.ToString();
    }

    private static string? TryDecodeBase64(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 64 * 1024)
        {
            return null;
        }

        var encoded = value.AsSpan().Trim();
        var maximumBytes = checked(((encoded.Length + 3) / 4) * 3);
        if (maximumBytes <= 512)
        {
            Span<byte> stackBuffer = stackalloc byte[maximumBytes];
            return DecodeBase64Utf8(encoded, stackBuffer);
        }

        var rented = ArrayPool<byte>.Shared.Rent(maximumBytes);
        try
        {
            return DecodeBase64Utf8(encoded, rented);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static string? DecodeBase64Utf8(
        ReadOnlySpan<char> encoded,
        Span<byte> buffer)
    {
        if (!Convert.TryFromBase64Chars(
                encoded,
                buffer,
                out var bytesWritten))
        {
            return null;
        }

        var text = Encoding.UTF8.GetString(buffer[..bytesWritten]).Trim();
        return text.Length > 0 ? text : null;
    }

    private static string NormalizeText(string value)
    {
        var withoutTags = TagRegex.Replace(value, " ");
        var decoded = WebUtility.HtmlDecode(withoutTags)
            .Replace('\u00a0', ' ')
            .Replace("\u200b", string.Empty, StringComparison.Ordinal);
        return WhitespaceRegex.Replace(decoded, " ").Trim();
    }

    private static string NormalizeLookupText(string value) =>
        NormalizeText(value).ToLowerInvariant();

    private static Uri GetOrigin(Uri uri) =>
        new UriBuilder(
            uri.Scheme,
            uri.IdnHost,
            uri.IsDefaultPort ? -1 : uri.Port,
            "/").Uri;

    private static bool HasSameOrigin(Uri left, Uri right) =>
        string.Equals(
            left.Scheme,
            right.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.IdnHost,
            right.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static bool IsSafeRemoteHttpUri(Uri uri) =>
        HosterExtractorBase.IsSafePublicHttpUri(uri);

    private static string BuildEpisodeKey(
        Uri root,
        string slug,
        int number) =>
        ApiContentProviderCacheKey.Create(
            root,
            slug,
            seasonNumber: null,
            number.ToString(CultureInfo.InvariantCulture));

    private static bool Contains(string value, string query) =>
        NormalizeLookupText(value).Contains(
            NormalizeLookupText(query),
            StringComparison.Ordinal);

    private sealed record DailyAnimeUpdate(
        string Slug,
        string Title,
        string? Thumbnail,
        int Number,
        Uri EpisodeUri);

    private sealed record ResolvedMedia(Uri PlaybackUri, Uri Referrer);

    private sealed record AnimeDetails(
        string NumericId,
        string Title,
        string? Overview,
        string? Poster,
        string? ReleaseDate,
        int? RuntimeSeconds,
        string? Quality,
        string? PublicationStatus,
        IReadOnlyList<string> Genres,
        string? OriginalTitle,
        bool IsMovie);
}

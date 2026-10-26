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

public enum StreamflixCatalogProfile
{
    PelisPlusTo,
    PelisflixHd,
    PoseidonHd2,
    SeriesFlix
}

/// <summary>
/// Catalog adapter for the HTML and Next.js providers ported from the
/// Apache-2.0 Streamflix project. Provider-specific routes remain explicit;
/// shared parsing is intentionally limited to common cards, metadata, seasons,
/// episodes, and encoded player links.
/// </summary>
public partial class StreamflixHtmlApiContentProvider : IApiContentProvider
{
    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int MaximumResults = 500;
    private const int MaximumUnits = 20_000;
    private static readonly Regex AnchorRegex = new(
        @"<a\b(?<attributes>[^>]*)>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex ImageRegex = new(
        @"<img\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex MetaRegex = new(
        @"<meta\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex HeadingRegex = new(
        @"<h1\b[^>]*>(?<value>.*?)</h1>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex CardHeadingRegex = new(
        @"<h[1-6]\b[^>]*>(?<value>.*?)</h[1-6]>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ParagraphRegex = new(
        @"<p\b[^>]*>(?<value>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex YearRegex = new(
        @"\b(?<value>(?:19|20)\d{2})\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex SeasonRegex = new(
        @"(?:temporada[\s/-]*|season[\s/-]*)(?<value>\d+)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex EpisodeRegex = new(
        @"(?:episodio[\s/-]*|episode[\s/-]*|x)(?<value>\d+(?:[.,]\d+)?)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex OriginalTitleTextRegex = new(
        @"^(?:T[i\u00ED]tulo|Nombre)\s+Original\s*:\s*(?<value>.+)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex EncodedServerRegex = new(
        """
        <[A-Za-z][^>]*\b
        (?<name>data-server|data-url)\s*=\s*
        (?:"(?<value>[^"]+)"|'(?<value>[^']+)'|(?<value>[^\s>]+))
        [^>]*>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex IframeRegex = new(
        """
        <iframe\b(?<attributes>[^>]*)>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex JsonServerRegex = new(
        """
        ["'](?:link|result)["']\s*:\s*["'](?<value>https?:\\?/\\?/[^"']+)["']
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex SeasonsJsonRegex = new(
        """
        const\s+seasonsJson\s*=\s*(?<value>\{.*?\})\s*;
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex NextDataRegex = new(
        """
        <script\b(?=[^>]*\bid\s*=\s*["']__NEXT_DATA__["'])[^>]*>
        (?<value>.*?)
        </script>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex GenreTokenRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HttpClient _httpClient;
    private readonly VideoHosterResolver? _hosterResolver;
    private readonly StreamflixCatalogProfile _profile;
    private readonly ApiContentGenreMapCache _genreMaps = new();
    private readonly ConcurrentDictionary<string, Uri> _episodeLocations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _seasonLocations =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _artworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _effectiveRoots =
        new(StringComparer.OrdinalIgnoreCase);

    public StreamflixHtmlApiContentProvider(
        HttpClient httpClient,
        ApiContentProviderDescriptor descriptor,
        StreamflixCatalogProfile profile,
        VideoHosterResolver? hosterResolver = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        Descriptor =
            descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _profile = profile;
        _hosterResolver = hosterResolver;
    }

    public ApiContentProviderDescriptor Descriptor { get; }

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) =>
        _artworkOrigins.Values
            .Prepend(GetOrigin(NormalizeRoot(baseUri)))
            .DistinctBy(
                static uri => uri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        LoadDailyUpdatesAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        var html = await ReadHtmlAsync(root, root, cancellationToken);

        // Re-resolved after the read so a domain move is honoured on the very
        // first call, not only on the next one.
        return ParseCards(NormalizeRoot(baseUri), html, mediaType: null)
            .Take(100)
            .Select(static card => CreateSearchItem(card, filteredGenre: null))
            .ToArray();
    }

    /// <summary>
    /// Genre shelves use the same cards as search and are filtered by the site.
    /// Result cards carry the year, so that criterion is verified locally
    /// without a detail request. Both routes can be entered without query text.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.Native,
        releaseYear: ApiContentFilterSupport.VerifiedLocal,
        supportsFilterOnlySearch: true);

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        var page = await SearchAsync(
            baseUri,
            new ApiContentSearchRequest(query, mediaType),
            cancellationToken);
        return page.Items;
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Movie)
        {
            return ApiContentSearchPage.Empty;
        }

        var root = NormalizeRoot(baseUri);
        string? genreToken = null;
        var page = 1;
        Uri endpoint;
        if (request.Genre is { } genre)
        {
            var genres = await LoadGenreMapAsync(root, cancellationToken);
            if (!genres.TryGetToken(genre, out genreToken))
            {
                return ApiContentSearchPage.Empty;
            }

            page = ParseGenrePageToken(request.ContinuationToken);
            root = NormalizeRoot(baseUri);
            endpoint = BuildGenreUri(root, genreToken, page);
        }
        else
        {
            if (request.ContinuationToken is not null)
            {
                return ApiContentSearchPage.Empty;
            }

            var searchText = request.Query ??
                request.ReleaseYear?.ToString(CultureInfo.InvariantCulture);
            if (searchText is null)
            {
                return ApiContentSearchPage.Empty;
            }

            endpoint = BuildSearchUri(root, searchText);
        }

        var html = await ReadHtmlAsync(endpoint, root, cancellationToken);
        var cards = ParseCards(
                NormalizeRoot(baseUri),
                html,
                request.MediaType)
            .Where(card =>
                (request.Query is null ||
                 Contains(card.Name, request.Query)) &&
                MatchesYear(card, request.ReleaseYear))
            .Take(MaximumResults)
            .ToArray();
        // Poseidon's search treats common articles as broad OR terms. A title
        // beginning with "El" can be buried hundreds of pages deep. At most
        // one narrower query is made, and its results must still match the
        // ORIGINAL title and filters; this is not a looser identity match.
        if (_profile is StreamflixCatalogProfile.PoseidonHd2 && request.Genre is null &&
            request.Query is { } query &&
            !cards.Any(card => NameNormalizer.Normalize(card.Name) == NameNormalizer.Normalize(query)) &&
            TryNarrowPoseidonQuery(query, out var narrowerQuery))
        {
            var narrowerHtml = await ReadHtmlAsync(BuildSearchUri(NormalizeRoot(baseUri), narrowerQuery),
                NormalizeRoot(baseUri), cancellationToken);
            cards = cards.Concat(ParseCards(NormalizeRoot(baseUri), narrowerHtml, request.MediaType)
                    .Where(card => Contains(card.Name, query) && MatchesYear(card, request.ReleaseYear)))
                .DistinctBy(card => card.Route, StringComparer.OrdinalIgnoreCase)
                .Take(MaximumResults).ToArray();
        }
        var nextPage = genreToken is not null &&
                       HasGenrePage(
                           NormalizeRoot(baseUri),
                           html,
                           genreToken,
                           page + 1)
            ? (page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        var truncated = genreToken is null &&
                        request.HasFilters &&
                        cards.Length > 0;
        return new ApiContentSearchPage(
            cards.Select(card =>
                    CreateSearchItem(card, request.Genre))
                .ToArray(),
            nextPage,
            isTruncated: truncated);
    }

    /// <summary>
    /// Exact original release year. A card whose year could not be read is
    /// dropped rather than let through, because an unknown year is not
    /// evidence of a matching one.
    /// </summary>
    private static bool MatchesYear(Card card, int? releaseYear) =>
        releaseYear is not { } year ||
        CatalogReleaseYear.FromReleaseDate(card.ReleaseDate) == year;

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
        CancellationToken cancellationToken,
        bool includeUnitMedia = true)
    {
        ArgumentNullException.ThrowIfNull(searchItem);
        var root = NormalizeRoot(baseUri);
        var route = ValidateRoute(searchItem.ExternalId);
        var detailUri = new Uri(root, route);
        var html = await ReadHtmlAsync(
            detailUri,
            root,
            cancellationToken);
        root = NormalizeRoot(baseUri);
        var isSeries = IsSeriesRoute(route);
        var metadata = ReadMetadata(
            root,
            route,
            html,
            searchItem.PreviewItem,
            isSeries,
            searchItem.RequireProviderTitle);
        if (!isSeries)
        {
            var unit = new CatalogTransferUnit
            {
                Kind = "episode",
                DisplayNumber = "1",
                SortOrder = 0,
                Name = metadata.Name
            };
            var videos = targetUnit is null && includeUnitMedia
                ? await ReadVideoSetAsync(
                    root,
                    detailUri,
                    ContentUnitKind.Episode,
                    "1",
                    seasonNumber: null,
                    cancellationToken)
                : null;
            return new ApiContentItem(
                metadata with { Units = [unit] },
                videoSets: videos is null ? [] : [videos]);
        }

        var seasons = await ReadSeasonsAsync(
            root,
            route,
            detailUri,
            html,
            searchItem.UnitRoutes,
            targetUnit?.SeasonNumber,
            cancellationToken);
        return new ApiContentItem(metadata with { Seasons = seasons });
    }

    public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode)
        {
            return null;
        }

        var root = NormalizeRoot(baseUri);
        var route = ValidateRoute(searchItem.ExternalId);
        if (!IsSeriesRoute(route))
        {
            return await ReadVideoSetAsync(
                root,
                new Uri(root, route),
                unit.Kind,
                "1",
                seasonNumber: null,
                cancellationToken);
        }

        var key = EpisodeKey(
            root,
            route,
            unit.SeasonNumber,
            unit.DisplayNumber);
        if (!_episodeLocations.TryGetValue(key, out var episodeUri))
        {
            await LoadItemForUnitAsync(
                baseUri,
                searchItem,
                unit,
                cancellationToken);
            _episodeLocations.TryGetValue(key, out episodeUri);
        }

        return episodeUri is null
            ? null
            : await ReadVideoSetAsync(
                root,
                episodeUri,
                unit.Kind,
                unit.DisplayNumber,
                unit.SeasonNumber,
                cancellationToken);
    }

    public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit)
    {
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode ||
            unit.SeasonNumber is not { } seasonNumber)
        {
            return [];
        }

        var root = NormalizeRoot(baseUri);
        var route = ValidateRoute(searchItem.ExternalId);
        var routes = new List<ApiProviderUnitRoute>(2);
        if (_episodeLocations.TryGetValue(
                EpisodeKey(
                    root,
                    route,
                    seasonNumber,
                    unit.DisplayNumber),
                out var episodeUri) &&
            SameOrigin(root, episodeUri))
        {
            routes.Add(
                new ApiProviderUnitRoute(
                    ContentUnitKind.Episode,
                    unit.DisplayNumber,
                    seasonNumber,
                    episodeUri.PathAndQuery));
        }

        if (_seasonLocations.TryGetValue(
                SeasonKey(root, route, seasonNumber),
                out var seasonUri) &&
            SameOrigin(root, seasonUri))
        {
            routes.Add(
                new ApiProviderUnitRoute(
                    ContentUnitKind.Episode,
                    displayNumber: null,
                    seasonNumber,
                    seasonUri.PathAndQuery));
        }

        return routes;
    }

    private async Task<IReadOnlyList<CatalogTransferSeason>>
        ReadSeasonsAsync(
            Uri root,
            string route,
            Uri detailUri,
            string detailHtml,
            IReadOnlyList<ApiProviderUnitRoute> routeHints,
            int? targetSeasonNumber,
            CancellationToken cancellationToken)
    {
        var knownSeasons = await TryLoadKnownSeasonsAsync(
            root,
            route,
            detailUri,
            routeHints,
            cancellationToken);
        if (knownSeasons is not null)
        {
            return knownSeasons;
        }

        if (_profile is StreamflixCatalogProfile.PelisPlusTo)
        {
            return FilterSeasons(
                ReadPelisPlusToSeasons(root, route, detailHtml),
                targetSeasonNumber);
        }

        if (_profile is StreamflixCatalogProfile.PoseidonHd2)
        {
            var nextSeasons = ReadPoseidonSeasons(root, route, detailHtml);
            if (nextSeasons.Count > 0)
            {
                return FilterSeasons(nextSeasons, targetSeasonNumber);
            }
        }

        var links = ParseLinks(root, detailHtml)
            .Where(static link =>
                link.Route.Contains(
                    "temporada/",
                    StringComparison.OrdinalIgnoreCase) ||
                link.Route.Contains(
                    "season/",
                    StringComparison.OrdinalIgnoreCase))
            .Select(link => new
            {
                Link = link,
                Number = ReadSeasonNumber(link.Route, link.Text)
            })
            .Where(static item => item.Number is not null)
            .Where(item =>
                targetSeasonNumber is null ||
                item.Number == targetSeasonNumber)
            .DistinctBy(
                static item => item.Link.Uri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
        foreach (var item in links)
        {
            _seasonLocations[
                SeasonKey(root, route, item.Number!.Value)] = item.Link.Uri;
        }

        var seasonResults = await Task.WhenAll(
            links.Select(
                async item =>
                {
                    var html = await ReadHtmlAsync(
                        item.Link.Uri,
                        detailUri,
                        cancellationToken);
                    var units = ReadEpisodeUnits(
                        root,
                        route,
                        item.Number!.Value,
                        html);
                    return new CatalogTransferSeason
                    {
                        Number = item.Number.Value,
                        SortOrder = item.Number.Value,
                        DisplayName = $"Temporada {item.Number.Value}",
                        Units = units
                    };
                }));
        if (seasonResults.Length > 0)
        {
            return seasonResults.OrderBy(static season => season.Number)
                .ToArray();
        }

        var unitsOnDetail = ReadEpisodeUnits(
            root,
            route,
            seasonNumber: 1,
            detailHtml);
        return unitsOnDetail.Count == 0 ||
               targetSeasonNumber is not null and not 1
            ? []
            :
            [
                new CatalogTransferSeason
                {
                    Number = 1,
                    SortOrder = 1,
                    DisplayName = "Temporada 1",
                    Units = unitsOnDetail
                }
            ];
    }

    private static IReadOnlyList<CatalogTransferSeason> FilterSeasons(
        IReadOnlyList<CatalogTransferSeason> seasons,
        int? targetSeasonNumber) =>
        targetSeasonNumber is null
            ? seasons
            : seasons
                .Where(season => season.Number == targetSeasonNumber)
                .ToArray();

    private async Task<IReadOnlyList<CatalogTransferSeason>?>
        TryLoadKnownSeasonsAsync(
            Uri root,
            string route,
            Uri detailUri,
            IReadOnlyList<ApiProviderUnitRoute> routeHints,
            CancellationToken cancellationToken)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Episode ||
                hint.SeasonNumber is not { } seasonNumber)
            {
                continue;
            }

            if (hint.DisplayNumber is { } displayNumber)
            {
                if (!TryResolveUnitRouteHint(
                        root,
                        hint.Route,
                        isSeasonRoute: false,
                        out var episodeUri))
                {
                    continue;
                }

                _episodeLocations[
                    EpisodeKey(root, route, seasonNumber, displayNumber)] =
                    episodeUri;
                return
                [
                    new CatalogTransferSeason
                    {
                        Number = seasonNumber,
                        SortOrder = seasonNumber,
                        DisplayName = $"Temporada {seasonNumber}",
                        Units =
                        [
                            new CatalogTransferUnit
                            {
                                Kind = "episode",
                                DisplayNumber = displayNumber,
                                SortOrder = 0,
                                Name = $"Episodio {displayNumber}"
                            }
                        ]
                    }
                ];
            }

            if (!TryResolveUnitRouteHint(
                    root,
                    hint.Route,
                    isSeasonRoute: true,
                    out var seasonUri))
            {
                continue;
            }

            var html = await ReadHtmlAsync(
                seasonUri,
                detailUri,
                cancellationToken);
            var units = ReadEpisodeUnits(
                root,
                route,
                seasonNumber,
                html);
            if (units.Count == 0)
            {
                continue;
            }

            _seasonLocations[SeasonKey(root, route, seasonNumber)] = seasonUri;
            return
            [
                new CatalogTransferSeason
                {
                    Number = seasonNumber,
                    SortOrder = seasonNumber,
                    DisplayName = $"Temporada {seasonNumber}",
                    Units = units
                }
            ];
        }

        return null;
    }

    private static bool TryResolveUnitRouteHint(
        Uri root,
        string route,
        bool isSeasonRoute,
        out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(root, route, out var candidate) ||
            !HosterExtractorBase.IsSafePublicHttpUri(candidate) ||
            !SameOrigin(root, candidate) ||
            candidate.Fragment.Length > 0)
        {
            return false;
        }

        var normalizedRoute = NormalizeRoute(candidate);
        var isExpectedRoute = isSeasonRoute
            ? normalizedRoute.Contains(
                  "temporada/",
                  StringComparison.OrdinalIgnoreCase) ||
              normalizedRoute.Contains(
                  "season/",
                  StringComparison.OrdinalIgnoreCase)
            : normalizedRoute.Contains(
                  "episodio/",
                  StringComparison.OrdinalIgnoreCase) ||
              normalizedRoute.Contains(
                  "episode/",
                  StringComparison.OrdinalIgnoreCase);
        if (!isExpectedRoute)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private IReadOnlyList<CatalogTransferSeason> ReadPelisPlusToSeasons(
        Uri root,
        string route,
        string html)
    {
        var match = SeasonsJsonRegex.Match(html);
        if (!match.Success)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(
                WebUtility.HtmlDecode(match.Groups["value"].Value));
            var seasons = new List<CatalogTransferSeason>();
            foreach (var property in document.RootElement.EnumerateObject())
            {
                if (!int.TryParse(
                        property.Name,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var seasonNumber) ||
                    property.Value.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                var units = new List<CatalogTransferUnit>();
                foreach (var episode in property.Value.EnumerateArray())
                {
                    if (!TryGetNumber(episode, "episode", out var number))
                    {
                        continue;
                    }

                    var display = FormatNumber(number);
                    var name = GetString(episode, "title");
                    var image = NormalizeArtwork(
                        root,
                        GetString(episode, "image"));
                    var episodeUri = new Uri(
                        root,
                        $"{route.TrimEnd('/')}/season/{seasonNumber}/episode/{display}");
                    _episodeLocations[
                        EpisodeKey(root, route, seasonNumber, display)] =
                        episodeUri;
                    units.Add(
                        new CatalogTransferUnit
                        {
                            Kind = "episode",
                            DisplayNumber = display,
                            SortOrder = units.Count,
                            Name = name,
                            ThumbnailRef = image?.AbsoluteUri
                        });
                }

                seasons.Add(
                    new CatalogTransferSeason
                    {
                        Number = seasonNumber,
                        SortOrder = seasonNumber,
                        DisplayName = $"Temporada {seasonNumber}",
                        Units = units
                    });
            }

            return seasons.OrderBy(static season => season.Number).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private IReadOnlyList<CatalogTransferSeason> ReadPoseidonSeasons(
        Uri root,
        string route,
        string html)
    {
        var next = NextDataRegex.Match(html);
        if (!next.Success)
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(
                WebUtility.HtmlDecode(next.Groups["value"].Value));
            var series = FindObjectWithProperty(
                document.RootElement,
                "seasons");
            if (series is null ||
                !series.Value.TryGetProperty(
                    "seasons",
                    out var seasonsElement) ||
                seasonsElement.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            var seasons = new List<CatalogTransferSeason>();
            foreach (var season in seasonsElement.EnumerateArray())
            {
                if (!TryGetNumber(season, "number", out var numberValue))
                {
                    continue;
                }

                var seasonNumber = decimal.ToInt32(numberValue);
                var units = new List<CatalogTransferUnit>();
                if (season.TryGetProperty(
                        "episodes",
                        out var episodes) &&
                    episodes.ValueKind is JsonValueKind.Array)
                {
                    foreach (var episode in episodes.EnumerateArray())
                    {
                        var title = GetString(episode, "title");
                        if (!TryGetNumber(
                                episode,
                                "number",
                                out var episodeNumber) &&
                            (!TryReadEpisodeCoordinate(
                                 title,
                                 out var titleSeasonNumber,
                                 out episodeNumber) ||
                             titleSeasonNumber != seasonNumber))
                        {
                            continue;
                        }

                        var display = FormatNumber(episodeNumber);
                        var slug = FindString(episode, "slug");
                        var episodeUri = !string.IsNullOrWhiteSpace(slug)
                            ? new Uri(
                                root,
                                "serie/" +
                                slug.RemovePrefix("series/")
                                    .Replace(
                                        "/seasons/",
                                        "/temporada/",
                                        StringComparison.OrdinalIgnoreCase)
                                    .Replace(
                                        "/episodes/",
                                        "/episodio/",
                                        StringComparison.OrdinalIgnoreCase))
                            : new Uri(
                                root,
                                $"{route.TrimEnd('/')}/temporada/{seasonNumber}/episodio/{display}");
                        _episodeLocations[
                            EpisodeKey(root, route, seasonNumber, display)] =
                            episodeUri;
                        units.Add(
                            new CatalogTransferUnit
                            {
                                Kind = "episode",
                                DisplayNumber = display,
                                SortOrder = units.Count,
                                Name = title,
                                ThumbnailRef = NormalizeArtwork(
                                        root,
                                        GetString(episode, "image"))
                                    ?.AbsoluteUri,
                                ReleaseDate =
                                    GetString(episode, "releaseDate")
                            });
                    }
                }

                seasons.Add(
                    new CatalogTransferSeason
                    {
                        Number = seasonNumber,
                        SortOrder = seasonNumber,
                        DisplayName = $"Temporada {seasonNumber}",
                        Units = units
                    });
            }

            return seasons.OrderBy(static season => season.Number).ToArray();
        }
        catch (JsonException)
        {
            return [];
        }
    }

    private IReadOnlyList<CatalogTransferUnit> ReadEpisodeUnits(
        Uri root,
        string route,
        int seasonNumber,
        string html)
    {
        var units = new List<CatalogTransferUnit>();
        foreach (var link in ParseLinks(root, html)
                     .Where(static link =>
                         link.Route.Contains(
                             "episodio/",
                             StringComparison.OrdinalIgnoreCase) ||
                         link.Route.Contains(
                             "episode/",
                             StringComparison.OrdinalIgnoreCase))
                     .DistinctBy(
                         static link => link.Uri.AbsoluteUri,
                         StringComparer.OrdinalIgnoreCase)
                     .Take(MaximumUnits))
        {
            var number = ReadEpisodeNumber(link.Route, link.Text);
            if (number is null)
            {
                continue;
            }

            var display = FormatNumber(number.Value);
            _episodeLocations[
                EpisodeKey(root, route, seasonNumber, display)] = link.Uri;
            units.Add(
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = display,
                    SortOrder = units.Count,
                    Name = string.IsNullOrWhiteSpace(link.Text)
                        ? $"Episodio {display}"
                        : link.Text,
                    ThumbnailRef = link.Artwork?.AbsoluteUri
                });
        }

        return units;
    }

    private async ValueTask<ApiContentVideoSet?> ReadVideoSetAsync(
        Uri root,
        Uri pageUri,
        ContentUnitKind kind,
        string displayNumber,
        int? seasonNumber,
        CancellationToken cancellationToken)
    {
        var html = await ReadHtmlAsync(
            pageUri,
            root,
            cancellationToken);
        var sources = ReadSources(pageUri, html, _profile);
        if (_profile is StreamflixCatalogProfile.SeriesFlix)
        {
            sources = PrioritizeSeriesFlixSources(sources);
        }

        if (_hosterResolver is not null)
        {
            sources = await _hosterResolver.ResolveAsync(
                sources,
                cancellationToken);
        }

        var unique = sources
            .DistinctBy(
                static source => source.PlaybackUri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .Select(
                (source, index) =>
                    new ApiContentVideoSource(
                        $"{source.Label} {index + 1}",
                        source.PlaybackUri,
                        source.MimeType,
                        source.Referrer,
                        source.Kind,
                        source.DecryptionKeys,
                        source.HttpOrigin))
            .Take(100)
            .ToArray();
        return unique.Length == 0
            ? null
            : new ApiContentVideoSet(
                kind,
                displayNumber,
                seasonNumber,
                unique);
    }

    private static IReadOnlyList<ApiContentVideoSource>
        PrioritizeSeriesFlixSources(
            IReadOnlyList<ApiContentVideoSource> sources)
    {
        if (sources.Count < 2)
        {
            return sources;
        }

        const byte directPriority = 0;
        const byte delegatedPriority = 1;
        const byte fallbackPriority = 2;
        Span<byte> priorities = sources.Count <= 256
            ? stackalloc byte[sources.Count]
            : new byte[sources.Count];
        var directCount = 0;
        var delegatedCount = 0;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (source.Kind is ApiContentVideoSourceKind.DirectMedia)
            {
                priorities[index] = directPriority;
                directCount++;
            }
            else if (IsSupportedDelegatedSource(source))
            {
                priorities[index] = delegatedPriority;
                delegatedCount++;
            }
            else
            {
                priorities[index] = fallbackPriority;
            }
        }

        // SeriesFlix currently puts a bare Nupload "Principal" entry before
        // an Nupload wrapper whose target is the real supported hoster. Keep
        // the principal entry as a fallback, but try direct media and the
        // explicit delegated player first.
        if (delegatedCount == 0)
        {
            return sources;
        }

        var prioritized = new ApiContentVideoSource[sources.Count];
        var directIndex = 0;
        var delegatedIndex = directCount;
        var fallbackIndex = directCount + delegatedCount;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            switch (priorities[index])
            {
                case directPriority:
                    prioritized[directIndex++] = source;
                    break;
                case delegatedPriority:
                    prioritized[delegatedIndex++] = source;
                    break;
                default:
                    prioritized[fallbackIndex++] = source;
                    break;
            }
        }

        return prioritized;
    }

    private static bool IsSupportedDelegatedSource(
        ApiContentVideoSource source)
    {
        if (source.Kind is not ApiContentVideoSourceKind.EmbeddedWeb)
        {
            return false;
        }

        var target = EmbeddedWebTarget.Unwrap(
            source.PlaybackUri,
            static candidate =>
                HosterExtractorBase.IsSafePublicHttpUri(candidate) &&
                HosterFeedMap.Supports(candidate));
        return !ReferenceEquals(target, source.PlaybackUri);
    }

    private static IReadOnlyList<ApiContentVideoSource> ReadSources(
        Uri pageUri,
        string html,
        StreamflixCatalogProfile profile)
    {
        var values = new List<string>();
        foreach (Match match in EncodedServerRegex.Matches(html))
        {
            var value = WebUtility.HtmlDecode(
                match.Groups["value"].Value);
            if (profile is StreamflixCatalogProfile.PelisPlusTo)
            {
                // This site does not publish a link at all: the attribute
                // holds a token, and its player route is that token encoded a
                // second time. Decoding it once and treating the result as a
                // URL — which is what every other profile here wants — turned
                // each server into a same-origin address that resolves to
                // nothing, so every title arrived with four dead sources.
                values.Add(
                    "/player/" +
                    Convert.ToBase64String(Encoding.UTF8.GetBytes(value)));
                continue;
            }

            values.Add(TryDecodeBase64(value) ?? value);
        }

        foreach (Match match in IframeRegex.Matches(html))
        {
            var attributes = match.Groups["attributes"].Value;
            var value =
                ReadAttribute(attributes, "src") ??
                ReadAttribute(attributes, "data-src");
            if (!string.IsNullOrWhiteSpace(value))
            {
                values.Add(value);
            }
        }

        foreach (Match match in JsonServerRegex.Matches(html))
        {
            values.Add(
                match.Groups["value"].Value.Replace(
                    "\\/",
                    "/",
                    StringComparison.Ordinal));
        }

        var sources = new List<ApiContentVideoSource>();
        foreach (var value in values)
        {
            if (!Uri.TryCreate(pageUri, value.Trim(), out var uri) ||
                !HosterExtractorBase.IsSafePublicHttpUri(uri))
            {
                continue;
            }

            var direct =
                uri.AbsolutePath.EndsWith(
                    ".m3u8",
                    StringComparison.OrdinalIgnoreCase) ||
                uri.AbsolutePath.EndsWith(
                    ".mp4",
                    StringComparison.OrdinalIgnoreCase);
            sources.Add(
                new ApiContentVideoSource(
                    uri.IdnHost,
                    uri,
                    direct
                        ? HosterMimeTypeResolver.MimeTypeFromUrl(uri)
                        : null,
                    pageUri,
                    direct
                        ? ApiContentVideoSourceKind.DirectMedia
                        : ApiContentVideoSourceKind.EmbeddedWeb));
        }

        return sources;
    }

    private CatalogTransferItem ReadMetadata(
        Uri root,
        string route,
        string html,
        CatalogTransferItem preview,
        bool isSeries,
        bool requireProviderTitle)
    {
        var name = CleanText(
            HeadingRegex.Match(html).Groups["value"].Value);
        name = Regex.Replace(
                name,
                @"^(?:Ver\s+)?(?:Película|Pelicula|Serie)\s+",
                string.Empty,
                RegexOptions.IgnoreCase,
                TimeSpan.FromSeconds(1))
            .Trim();
        if (string.IsNullOrWhiteSpace(name))
        {
            if (requireProviderTitle)
            {
                throw new InvalidDataException(
                    "The provider detail page did not include a title.");
            }

            name = preview.Name;
        }

        var thumbnail = ReadMeta(root, html, "og:image") ??
            preview.ThumbnailRef;
        if (Uri.TryCreate(
                thumbnail,
                UriKind.Absolute,
                out var thumbnailUri) &&
            HosterExtractorBase.IsSafePublicHttpUri(thumbnailUri))
        {
            var origin = GetOrigin(thumbnailUri);
            _artworkOrigins[origin.AbsoluteUri] = origin;
        }
        var description = ReadMeta(root, html, "og:description");
        if (string.IsNullOrWhiteSpace(description))
        {
            description = ParagraphRegex.Matches(html)
                .Select(static match =>
                    CleanText(match.Groups["value"].Value))
                .FirstOrDefault(static text => text.Length >= 40);
        }

        var release = YearRegex.Match(CleanText(html));
        var originalName = ReadOriginalTitle(html) ?? ReadPoseidonOriginalTitle(html, name, isSeries);
        if (string.Equals(
                name,
                originalName,
                StringComparison.OrdinalIgnoreCase))
        {
            originalName = null;
        }

        return new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = isSeries ? "series" : "movie",
            Name = name,
            OriginalName = originalName,
            Description = description ?? preview.Description,
            ReleaseDate = release.Success
                ? release.Groups["value"].Value
                : preview.ReleaseDate,
            OriginalLanguage = "es",
            ThumbnailRef = thumbnail,
            BackdropRef = ReadBackgroundArtwork(root, html) ??
                preview.BackdropRef,
            Genres = ReadGenreLabels(root, html),
            AlternativeNames = originalName is null
                ? []
                :
                [
                    new CatalogTransferAlias
                    {
                        Name = originalName,
                        AliasType = "original"
                    }
                ]
        };
    }

    private static string? ReadOriginalTitle(string html)
    {
        foreach (Match paragraph in ParagraphRegex.Matches(html))
        {
            var text = CleanText(paragraph.Groups["value"].Value);
            var match = OriginalTitleTextRegex.Match(text);
            if (match.Success)
            {
                var value = match.Groups["value"].Value.Trim();
                return value.Length == 0 ? null : value;
            }
        }

        return null;
    }

    private string? ReadPoseidonOriginalTitle(string html, string displayedName, bool isSeries)
    {
        if (_profile is not StreamflixCatalogProfile.PoseidonHd2) return null;
        var next = NextDataRegex.Match(html);
        if (!next.Success) return null;
        try
        {
            using var document = JsonDocument.Parse(WebUtility.HtmlDecode(next.Groups["value"].Value));
            if (document.RootElement.ValueKind is JsonValueKind.Object && document.RootElement.TryGetProperty("props", out var props) &&
                props.ValueKind is JsonValueKind.Object && props.TryGetProperty("pageProps", out var page) &&
                page.ValueKind is JsonValueKind.Object &&
                page.TryGetProperty(isSeries ? "thisSerie" : "thisMovie", out var title) &&
                title.ValueKind is JsonValueKind.Object && title.TryGetProperty("titles", out var titles) &&
                titles.ValueKind is JsonValueKind.Object &&
                NameNormalizer.Normalize(GetString(titles, "name") ?? string.Empty) == NameNormalizer.Normalize(displayedName) &&
                titles.TryGetProperty("original", out var original) && original.ValueKind is JsonValueKind.Object)
            {
                var name = CleanText(GetString(original, "name"));
                return string.IsNullOrWhiteSpace(name) ? null : name;
            }
        }
        catch (JsonException) { }
        return null;
    }

    private static bool TryNarrowPoseidonQuery(string query, out string narrower)
    {
        narrower = string.Empty;
        var text = query.AsSpan().Trim();
        var space = text.IndexOf(' ');
        if (space < 1) return false;
        var article = text[..space];
        if (!(article.Equals("el", StringComparison.OrdinalIgnoreCase) || article.Equals("la", StringComparison.OrdinalIgnoreCase) ||
              article.Equals("los", StringComparison.OrdinalIgnoreCase) || article.Equals("las", StringComparison.OrdinalIgnoreCase) ||
              article.Equals("un", StringComparison.OrdinalIgnoreCase) || article.Equals("una", StringComparison.OrdinalIgnoreCase) ||
              article.Equals("the", StringComparison.OrdinalIgnoreCase) || article.Equals("a", StringComparison.OrdinalIgnoreCase) ||
              article.Equals("an", StringComparison.OrdinalIgnoreCase))) return false;
        var remaining = text[(space + 1)..].Trim();
        if (remaining.Length < 2) return false;
        narrower = remaining.ToString();
        return true;
    }

    private IReadOnlyList<Card> ParseCards(
        Uri root,
        string html,
        MediaType? mediaType)
    {
        var cards = new List<Card>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var link in ParseLinks(root, html))
        {
            var route = link.Route;
            var isMovie = IsMovieRoute(route);
            var isSeries = IsSeriesRoute(route);
            if ((!isMovie && !isSeries) ||
                mediaType is not null and not MediaType.Movie ||
                string.IsNullOrWhiteSpace(link.Text) ||
                !seen.Add(route))
            {
                continue;
            }

            cards.Add(
                new Card(
                    route,
                    link.Text,
                    isSeries,
                    link.Artwork?.AbsoluteUri,
                    YearRegex.Match(link.RawText) is { Success: true } year
                        ? year.Groups["value"].Value
                        : null));
            if (link.Artwork is not null)
            {
                var origin = GetOrigin(link.Artwork);
                _artworkOrigins[origin.AbsoluteUri] = origin;
            }
        }

        return cards;
    }

    private static IReadOnlyList<Link> ParseLinks(Uri root, string html)
    {
        var links = new List<Link>();
        foreach (Match match in AnchorRegex.Matches(html))
        {
            var href = ReadAttribute(
                match.Groups["attributes"].Value,
                "href");
            if (string.IsNullOrWhiteSpace(href) ||
                !Uri.TryCreate(root, href, out var uri) ||
                !HosterExtractorBase.IsSafePublicHttpUri(uri) ||
                !SameOrigin(root, uri))
            {
                continue;
            }

            var body = match.Groups["body"].Value;
            var image = ImageRegex.Match(body);
            var imageAttributes = image.Success
                ? image.Groups["attributes"].Value
                : string.Empty;
            var rawText = CleanText(body);
            var heading = CardHeadingRegex.Match(body);
            var headingText = heading.Success
                ? CleanText(heading.Groups["value"].Value)
                : null;
            var text = FirstNonBlank(
                headingText,
                ReadAttribute(imageAttributes, "alt"),
                ReadAttribute(match.Groups["attributes"].Value, "title"),
                rawText);
            text = Regex.Replace(
                    text ?? string.Empty,
                    @"^(?:Poster|Ver)\s+",
                    string.Empty,
                    RegexOptions.IgnoreCase,
                    TimeSpan.FromSeconds(1))
                .Trim();
            var artworkValue =
                ReadAttribute(imageAttributes, "data-src") ??
                ReadAttribute(imageAttributes, "src");
            Uri? artwork = null;
            if (Uri.TryCreate(root, artworkValue, out var parsedArtwork) &&
                HosterExtractorBase.IsSafePublicHttpUri(parsedArtwork))
            {
                artwork = UnwrapNextImage(parsedArtwork);
            }

            links.Add(
                new Link(
                    NormalizeRoute(uri),
                    uri,
                    text,
                    rawText,
                    artwork));
        }

        return links;
    }

    private Uri BuildSearchUri(Uri root, string query)
    {
        var encoded = Uri.EscapeDataString(query.Trim());
        return _profile switch
        {
            StreamflixCatalogProfile.PelisPlusTo =>
                new Uri(root, $"search/{encoded}"),
            StreamflixCatalogProfile.PelisflixHd =>
                new Uri(root, $"busqueda/{encoded}"),
            StreamflixCatalogProfile.PoseidonHd2 =>
                new Uri(root, $"search?q={encoded}"),
            StreamflixCatalogProfile.SeriesFlix =>
                new Uri(root, $"?s={encoded}"),
            _ => throw new InvalidOperationException()
        };
    }

    private ValueTask<ApiContentGenreMap> LoadGenreMapAsync(
        Uri root,
        CancellationToken cancellationToken) =>
        _genreMaps.GetAsync(
            root,
            async token =>
            {
                var html = await ReadHtmlAsync(root, root, token);
                return ParseGenreMap(NormalizeRoot(root), html);
            },
            cancellationToken);

    internal static ApiContentGenreMap ParseGenreMap(
        Uri root,
        string html)
    {
        var entries = new List<(string Label, string Token)>();
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = ReadAttribute(
                anchor.Groups["attributes"].Value,
                "href");
            if (!TryReadGenreToken(root, href, out var token))
            {
                continue;
            }

            var label = CleanGenreLabel(anchor.Groups["body"].Value);
            if (label.Length > 0)
            {
                entries.Add((label, token));
            }
        }

        return ApiContentGenreMap.FromUpstreamLabels(entries);
    }

    private static IReadOnlyList<string> ReadGenreLabels(
        Uri root,
        string html)
    {
        var labels = new List<string>();
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = ReadAttribute(
                anchor.Groups["attributes"].Value,
                "href");
            if (!TryReadGenreToken(root, href, out _) ||
                !HasGenreMarkerBefore(html, anchor.Index))
            {
                continue;
            }

            var label = CleanGenreLabel(anchor.Groups["body"].Value);
            if (label.Length > 0 &&
                !labels.Contains(
                    label,
                    StringComparer.CurrentCultureIgnoreCase))
            {
                labels.Add(label);
            }
        }

        return labels;
    }

    private static string CleanGenreLabel(string value)
    {
        var label = CleanText(value);
        var trimmed = label.AsSpan().TrimEnd();
        while (!trimmed.IsEmpty &&
               trimmed[^1] is ',' or ';')
        {
            trimmed = trimmed[..^1].TrimEnd();
        }

        return trimmed.Length == label.Length
            ? label
            : trimmed.ToString();
    }

    private static bool HasGenreMarkerBefore(
        string html,
        int anchorIndex)
    {
        var start = Math.Max(0, anchorIndex - 512);
        var prefix = html.AsSpan(start, anchorIndex - start);
        return prefix.LastIndexOf(
                   "Género:",
                   StringComparison.OrdinalIgnoreCase) >= 0 ||
               prefix.LastIndexOf(
                   "Genero:",
                   StringComparison.OrdinalIgnoreCase) >= 0;
    }

    private static bool TryReadGenreToken(
        Uri root,
        string? href,
        out string token)
    {
        token = string.Empty;
        if (string.IsNullOrWhiteSpace(href) ||
            !Uri.TryCreate(root, WebUtility.HtmlDecode(href), out var uri) ||
            !HosterExtractorBase.IsSafePublicHttpUri(uri) ||
            !SameOrigin(root, uri))
        {
            return false;
        }

        string[] segments;
        try
        {
            segments = uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
        }
        catch (UriFormatException)
        {
            return false;
        }

        if (segments.Length < 2 ||
            !string.Equals(
                segments[0],
                "genero",
                StringComparison.OrdinalIgnoreCase) ||
            !GenreTokenRegex.IsMatch(segments[1]))
        {
            return false;
        }

        token = segments[1];
        return true;
    }

    private Uri BuildGenreUri(
        Uri root,
        string genreToken,
        int page) =>
        new(
            root,
            page <= 1
                ? $"genero/{Uri.EscapeDataString(genreToken)}/"
                : $"genero/{Uri.EscapeDataString(genreToken)}/page/" +
                  page.ToString(CultureInfo.InvariantCulture) +
                  (_profile is StreamflixCatalogProfile.SeriesFlix
                      ? string.Empty
                      : "/"));

    private static int ParseGenrePageToken(string? token)
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
                "The provider returned an invalid genre page token.",
                nameof(token));
        }

        return page;
    }

    private static bool HasGenrePage(
        Uri root,
        string html,
        string genreToken,
        int expectedPage)
    {
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = ReadAttribute(
                anchor.Groups["attributes"].Value,
                "href");
            if (string.IsNullOrWhiteSpace(href) ||
                !Uri.TryCreate(root, href, out var uri) ||
                !SameOrigin(root, uri))
            {
                continue;
            }

            string[] segments;
            try
            {
                segments = uri.AbsolutePath
                    .Split('/', StringSplitOptions.RemoveEmptyEntries)
                    .Select(Uri.UnescapeDataString)
                    .ToArray();
            }
            catch (UriFormatException)
            {
                continue;
            }

            if (segments.Length >= 4 &&
                string.Equals(
                    segments[0],
                    "genero",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    segments[1],
                    genreToken,
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    segments[2],
                    "page",
                    StringComparison.OrdinalIgnoreCase) &&
                int.TryParse(
                    segments[3],
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

    private async Task<string> ReadHtmlAsync(
        Uri endpoint,
        Uri referrer,
        CancellationToken cancellationToken)
    {
        var currentEndpoint = endpoint;
        var currentReferrer = referrer;
        for (var redirectCount = 0; redirectCount <= 1; redirectCount++)
        {
            if (!HosterExtractorBase.IsSafePublicHttpUri(currentEndpoint))
            {
                throw new InvalidDataException(
                    $"{Descriptor.DisplayName} returned an unsafe URL.");
            }

            using var request = new HttpRequestMessage(
                HttpMethod.Get,
                currentEndpoint);
            request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
            request.Headers.Referrer = currentReferrer;
            request.Headers.Accept.ParseAdd(
                "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7");
            using var response = await ApiProviderHttp.SendAsync(
                _httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var responseUri =
                response.RequestMessage?.RequestUri ?? currentEndpoint;
            if (!HosterExtractorBase.IsSafePublicHttpUri(responseUri))
            {
                throw new InvalidDataException(
                    $"{Descriptor.DisplayName} returned an unsafe URL.");
            }

            if (IsRedirectStatusCode(response.StatusCode))
            {
                if (redirectCount == 1 ||
                    response.Headers.Location is null)
                {
                    throw new InvalidDataException(
                        $"{Descriptor.DisplayName} returned an invalid or " +
                        "excessive HTTP redirect chain.");
                }

                var redirectedEndpoint =
                    response.Headers.Location.IsAbsoluteUri
                        ? response.Headers.Location
                        : new Uri(responseUri, response.Headers.Location);
                if (!HosterExtractorBase.IsSafePublicHttpUri(
                        redirectedEndpoint))
                {
                    throw new InvalidDataException(
                        $"{Descriptor.DisplayName} redirected to an unsafe " +
                        "URL.");
                }

                currentEndpoint = redirectedEndpoint;
                currentReferrer = responseUri;
                continue;
            }

            response.EnsureSuccessStatusCode();
            RecordEffectiveRoot(endpoint, responseUri);
            await using var input =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var read = await input.ReadAsync(buffer, cancellationToken);
                if (read == 0)
                {
                    break;
                }

                if (output.Length + read > MaximumResponseBytes)
                {
                    throw new InvalidDataException(
                        $"{Descriptor.DisplayName} response exceeded " +
                        $"{MaximumResponseBytes / (1024 * 1024)} MiB.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, read),
                    cancellationToken);
            }

            var html = Encoding.UTF8.GetString(
                output.GetBuffer(),
                0,
                checked((int)output.Length));
            // The new PelisflixHD mirror still publishes catalog links with
            // the retired origin. Do not relax same-origin validation for
            // arbitrary sites or rewrite artwork/CDN subdomains.
            if (_profile == StreamflixCatalogProfile.PelisflixHd)
            {
                var origin = GetOrigin(responseUri).AbsoluteUri;
                foreach (var retired in Descriptor.RetiredHosts)
                {
                    html = html.Replace($"https://{retired}/", origin, StringComparison.OrdinalIgnoreCase)
                        .Replace($"http://{retired}/", origin, StringComparison.OrdinalIgnoreCase);
                }
            }
            return html;
        }

        throw new InvalidDataException(
            $"{Descriptor.DisplayName} returned an excessive HTTP redirect " +
            "chain.");
    }

    private static bool IsRedirectStatusCode(HttpStatusCode statusCode) =>
        statusCode is
            HttpStatusCode.MovedPermanently or
            HttpStatusCode.Found or
            HttpStatusCode.SeeOther or
            HttpStatusCode.TemporaryRedirect or
            HttpStatusCode.PermanentRedirect;

    private static ApiContentSearchItem CreateSearchItem(
        Card card,
        ApiContentGenre? filteredGenre) =>
        new(
            card.Route,
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = card.IsSeries ? "series" : "movie",
                Name = card.Name,
                ReleaseDate = card.ReleaseDate,
                OriginalLanguage = "es",
                ThumbnailRef = card.Artwork,
                Genres = filteredGenre is null
                    ? []
                    :
                    [
                        filteredGenre.Aliases.FirstOrDefault() ??
                        filteredGenre.Key
                    ]
            });

    private static string? ReadMeta(
        Uri root,
        string html,
        string property)
    {
        foreach (Match match in MetaRegex.Matches(html))
        {
            var attributes = match.Groups["attributes"].Value;
            var key =
                ReadAttribute(attributes, "property") ??
                ReadAttribute(attributes, "name");
            if (!string.Equals(
                    key,
                    property,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var content = ReadAttribute(attributes, "content");
            if (property.EndsWith(
                    ":image",
                    StringComparison.OrdinalIgnoreCase) &&
                Uri.TryCreate(root, content, out var uri) &&
                HosterExtractorBase.IsSafePublicHttpUri(uri))
            {
                return UnwrapNextImage(uri).AbsoluteUri;
            }

            return content;
        }

        return null;
    }

    private static string? ReadBackgroundArtwork(Uri root, string html)
    {
        var match = Regex.Match(
            html,
            @"(?:background-image\s*:|data-backdrop\s*=)\s*(?:url\()?[""']?(?<value>https?://[^""')\s]+)",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        return match.Success &&
               Uri.TryCreate(
                   WebUtility.HtmlDecode(
                       match.Groups["value"].Value),
                   UriKind.Absolute,
                   out var uri) &&
               HosterExtractorBase.IsSafePublicHttpUri(uri)
            ? uri.AbsoluteUri
            : null;
    }

    private Uri? NormalizeArtwork(Uri root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('/'))
        {
            value = value.StartsWith("/_next/", StringComparison.Ordinal)
                ? value
                : "https://image.tmdb.org/t/p/w500" + value;
        }

        if (!Uri.TryCreate(root, value, out var uri) ||
            !HosterExtractorBase.IsSafePublicHttpUri(uri))
        {
            return null;
        }

        uri = UnwrapNextImage(uri);
        _artworkOrigins[GetOrigin(uri).AbsoluteUri] = GetOrigin(uri);
        return uri;
    }

    private static Uri UnwrapNextImage(Uri uri)
    {
        if (!uri.AbsolutePath.Contains(
                "/_next/image",
                StringComparison.OrdinalIgnoreCase))
        {
            return uri;
        }

        var query = uri.Query.TrimStart('?')
            .Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Select(static part => part.Split('=', 2))
            .FirstOrDefault(static pair =>
                pair.Length == 2 &&
                string.Equals(
                    pair[0],
                    "url",
                    StringComparison.OrdinalIgnoreCase));
        return query is not null &&
               Uri.TryCreate(
                   Uri.UnescapeDataString(query[1]),
                   UriKind.Absolute,
                   out var unwrapped) &&
               HosterExtractorBase.IsSafePublicHttpUri(unwrapped)
            ? unwrapped
            : uri;
    }

    private static JsonElement? FindObjectWithProperty(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            if (element.TryGetProperty(propertyName, out _))
            {
                return element;
            }

            foreach (var property in element.EnumerateObject())
            {
                var found = FindObjectWithProperty(
                    property.Value,
                    propertyName);
                if (found is not null)
                {
                    return found;
                }
            }
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var found = FindObjectWithProperty(item, propertyName);
                if (found is not null)
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? FindString(
        JsonElement element,
        string propertyName)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            if (element.TryGetProperty(
                    propertyName,
                    out var property) &&
                property.ValueKind is JsonValueKind.String)
            {
                return property.GetString();
            }

            foreach (var child in element.EnumerateObject())
            {
                var found = FindString(child.Value, propertyName);
                if (!string.IsNullOrWhiteSpace(found))
                {
                    return found;
                }
            }
        }

        return null;
    }

    private static string? GetString(
        JsonElement element,
        string propertyName) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(propertyName, out var property) &&
        property.ValueKind is JsonValueKind.String
            ? property.GetString()
            : null;

    private static bool TryGetNumber(
        JsonElement element,
        string propertyName,
        out decimal number)
    {
        number = 0;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               (property.ValueKind is JsonValueKind.Number &&
                property.TryGetDecimal(out number) ||
                property.ValueKind is JsonValueKind.String &&
                decimal.TryParse(
                    property.GetString(),
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out number));
    }

    private static string? TryDecodeBase64(string value)
    {
        if (value.Length is < 8 or > 16_384)
        {
            return null;
        }

        try
        {
            var normalized = value.Trim();
            normalized += new string(
                '=',
                (4 - normalized.Length % 4) % 4);
            var decoded = Encoding.UTF8.GetString(
                Convert.FromBase64String(normalized));
            return decoded.StartsWith(
                "http",
                StringComparison.OrdinalIgnoreCase)
                ? decoded.Trim()
                : null;
        }
        catch (FormatException)
        {
            return null;
        }
    }

    private static int? ReadSeasonNumber(
        string route,
        string text)
    {
        var match = SeasonRegex.Match(route + " " + text);
        if (!match.Success)
        {
            match = Regex.Match(
                route,
                @"-(?<value>\d+)/?$",
                RegexOptions.CultureInvariant,
                TimeSpan.FromSeconds(1));
        }

        return match.Success &&
               int.TryParse(
                   match.Groups["value"].Value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var number)
            ? number
            : null;
    }

    private static decimal? ReadEpisodeNumber(
        string route,
        string text)
    {
        var match = EpisodeRegex.Match(route + " " + text);
        return match.Success &&
               decimal.TryParse(
                   match.Groups["value"].Value.Replace(',', '.'),
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out var number)
            ? number
            : null;
    }

    private static bool TryReadEpisodeCoordinate(
        string? value,
        out int seasonNumber,
        out decimal episodeNumber)
    {
        seasonNumber = 0;
        episodeNumber = 0;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var span = value.AsSpan().Trim();
        for (var separator = 1;
             separator < span.Length - 1;
             separator++)
        {
            if (span[separator] is not ('x' or 'X'))
            {
                continue;
            }

            var seasonEnd = separator;
            while (seasonEnd > 0 &&
                   char.IsWhiteSpace(span[seasonEnd - 1]))
            {
                seasonEnd--;
            }

            var seasonStart = seasonEnd;
            while (seasonStart > 0 &&
                   char.IsAsciiDigit(span[seasonStart - 1]))
            {
                seasonStart--;
            }

            var episodeStart = separator + 1;
            while (episodeStart < span.Length &&
                   char.IsWhiteSpace(span[episodeStart]))
            {
                episodeStart++;
            }

            var episodeEnd = episodeStart;
            while (episodeEnd < span.Length &&
                   (char.IsAsciiDigit(span[episodeEnd]) ||
                    span[episodeEnd] == '.'))
            {
                episodeEnd++;
            }

            if (seasonStart == seasonEnd ||
                episodeStart == episodeEnd ||
                !int.TryParse(
                    span[seasonStart..seasonEnd],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out seasonNumber) ||
                !decimal.TryParse(
                    span[episodeStart..episodeEnd],
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out episodeNumber))
            {
                continue;
            }

            return true;
        }

        return false;
    }

    private static string FormatNumber(decimal number) =>
        number.ToString("0.############################", CultureInfo.InvariantCulture);

    private static bool IsMovieRoute(string route) =>
        route.StartsWith("pelicula/", StringComparison.OrdinalIgnoreCase) ||
        route.StartsWith(
            "peliculas-online/",
            StringComparison.OrdinalIgnoreCase);

    private static bool IsSeriesRoute(string route) =>
        route.StartsWith("serie/", StringComparison.OrdinalIgnoreCase) ||
        route.StartsWith(
            "doramas-online/",
            StringComparison.OrdinalIgnoreCase);

    private static string ValidateRoute(string value)
    {
        var route = value.Trim().TrimStart('/');
        if (route.Length is 0 or > 1_024 ||
            route.Contains("..", StringComparison.Ordinal) ||
            route.Contains('\\') ||
            Uri.TryCreate(route, UriKind.Absolute, out _))
        {
            throw new ArgumentException(
                "The provider content route is invalid.",
                nameof(value));
        }

        return route;
    }

    private static string NormalizeRoute(Uri uri) =>
        uri.AbsolutePath.TrimStart('/');

    /// <summary>
    /// The origin to treat as the site, honouring a domain move.
    /// <para>
    /// These catalogues change domain and leave the old one redirecting. Every
    /// link and image the pages carry is then on the new origin, and the
    /// same-origin checks below would throw all of them away — the site would
    /// appear to return nothing rather than appear broken. Once a redirect has
    /// been observed the new origin is used in place of the configured one, so
    /// a move costs one redirect and nothing else.
    /// </para>
    /// </summary>
    private Uri NormalizeRoot(Uri value)
    {
        var configured = NormalizeConfiguredRoot(value);
        return _effectiveRoots.TryGetValue(
            configured.AbsoluteUri,
            out var effective)
            ? effective
            : configured;
    }

    private static Uri NormalizeConfiguredRoot(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!HosterExtractorBase.IsSafePublicHttpUri(value))
        {
            throw new ArgumentException(
                "Provider URLs must use public HTTP or HTTPS.",
                nameof(value));
        }

        return new Uri(
            value.GetLeftPart(UriPartial.Authority) + "/",
            UriKind.Absolute);
    }

    /// <summary>
    /// Learns where a request actually landed. Only a cross-origin redirect is
    /// recorded; following one within the same site says nothing about a move.
    /// </summary>
    private void RecordEffectiveRoot(Uri requested, Uri? finalUri)
    {
        if (finalUri is null ||
            !HosterExtractorBase.IsSafePublicHttpUri(finalUri))
        {
            return;
        }

        var requestedOrigin = GetOrigin(requested);
        var finalOrigin = GetOrigin(finalUri);
        if (SameOrigin(requestedOrigin, finalOrigin))
        {
            return;
        }

        _effectiveRoots[requestedOrigin.AbsoluteUri] = finalOrigin;

        // Artwork on the new origin has to be trusted too, or every poster is
        // blocked while the titles themselves load.
        _artworkOrigins[finalOrigin.AbsoluteUri] = finalOrigin;
    }

    private static Uri GetOrigin(Uri value) =>
        new(value.GetLeftPart(UriPartial.Authority) + "/", UriKind.Absolute);

    private static bool SameOrigin(Uri left, Uri right) =>
        string.Equals(
            left.Scheme,
            right.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.IdnHost,
            right.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static string EpisodeKey(
        Uri root,
        string route,
        int? season,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            route,
            season,
            ApiContentProviderCacheKey.NormalizeDisplayNumber(displayNumber));

    private static string SeasonKey(
        Uri root,
        string route,
        int season) =>
        ApiContentProviderCacheKey.Create(
            root,
            route,
            season,
            normalizedDisplayNumber: "@season");

    private static bool Contains(string value, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            value,
            query.Trim(),
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static string CleanText(string? value) =>
        WhitespaceRegex.Replace(
                WebUtility.HtmlDecode(
                    TagRegex.Replace(value ?? string.Empty, " ")),
                " ")
            .Trim();

    private static string? FirstNonBlank(
        string? first,
        string? second,
        string? third,
        string? fourth)
    {
        if (!string.IsNullOrWhiteSpace(first))
        {
            return first;
        }

        if (!string.IsNullOrWhiteSpace(second))
        {
            return second;
        }

        return !string.IsNullOrWhiteSpace(third)
            ? third
            : fourth;
    }

    private static string? ReadAttribute(
        string attributes,
        string name)
    {
        var match = Regex.Match(
            attributes,
            $"""
             \b{Regex.Escape(name)}\s*=\s*
             (?:"(?<value>[^"]*)"|'(?<value>[^']*)'|(?<value>[^\s>]+))
             """,
            RegexOptions.IgnoreCase | RegexOptions.IgnorePatternWhitespace |
                RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value.Trim())
            : null;
    }

    private sealed record Card(
        string Route,
        string Name,
        bool IsSeries,
        string? Artwork,
        string? ReleaseDate);

    private sealed record Link(
        string Route,
        Uri Uri,
        string Text,
        string RawText,
        Uri? Artwork);
}

internal static class StringRouteExtensions
{
    public static string RemovePrefix(this string value, string prefix) =>
        value.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            ? value[prefix.Length..]
            : value;
}

public sealed class PelisPlusToApiContentProvider(
    HttpClient httpClient,
    VideoHosterResolver? hosterResolver = null)
    : StreamflixHtmlApiContentProvider(
        httpClient,
        DefaultDescriptor,
        StreamflixCatalogProfile.PelisPlusTo,
        hosterResolver)
{
    public const string ProviderId = "pelisplus-to";

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.PelisPlusTo;
}

public sealed class PelisflixHdApiContentProvider(
    HttpClient httpClient,
    VideoHosterResolver? hosterResolver = null)
    : StreamflixHtmlApiContentProvider(
        httpClient,
        DefaultDescriptor,
        StreamflixCatalogProfile.PelisflixHd,
        hosterResolver)
{
    public const string ProviderId = "pelisflix-hd";

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.PelisflixHd;
}

public sealed class PoseidonHd2ApiContentProvider(
    HttpClient httpClient,
    VideoHosterResolver? hosterResolver = null)
    : StreamflixHtmlApiContentProvider(
        httpClient,
        DefaultDescriptor,
        StreamflixCatalogProfile.PoseidonHd2,
        hosterResolver)
{
    public const string ProviderId = "poseidon-hd2";

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.PoseidonHd2;
}

public sealed class SeriesFlixApiContentProvider(
    HttpClient httpClient,
    VideoHosterResolver? hosterResolver = null)
    : StreamflixHtmlApiContentProvider(
        httpClient,
        DefaultDescriptor,
        StreamflixCatalogProfile.SeriesFlix,
        hosterResolver)
{
    public const string ProviderId = "seriesflix";

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.SeriesFlix;
}

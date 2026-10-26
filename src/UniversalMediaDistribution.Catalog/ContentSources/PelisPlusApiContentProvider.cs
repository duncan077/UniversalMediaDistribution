using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads the public PelisPlus HTML catalog. The website does not expose a
/// separate JSON API: catalog metadata is rendered into HTML and playback
/// iframe URLs are stored as Base64 values in data-server attributes.
/// </summary>
public sealed partial class PelisPlusApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "pelisplus";

    private const int MaximumCatalogBytes = 8 * 1024 * 1024;
    private const int MaximumDetailBytes = 8 * 1024 * 1024;
    private const int MaximumSeasonBytes = 8 * 1024 * 1024;
    private const int MaximumEpisodeBytes = 8 * 1024 * 1024;
    private const int MaximumSearchResults = 500;
    private const int MaximumDailyUpdateResults = 60;
    private const int MaximumSeasonCount = 100;
    private const int MaximumEpisodeCount = 20_000;
    private const int MaximumServers = 100;
    private const int MaximumEncodedServerLength = 8 * 1024;

    private static readonly Regex SafeSiteRouteRegex = new(
        @"^(?:pelicula|serie|temporada|episodio)/[A-Za-z0-9._~%+\-]{1,220}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex InvalidPercentEncodingRegex = new(
        @"%(?![0-9A-Fa-f]{2})",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MovieItemRegex = new(
        @"<div\b(?<containerAttributes>(?=[^>]*\bclass\s*=\s*(?:""(?:[^""]*\s)?movie-item(?:\s[^""]*)?""|'(?:[^']*\s)?movie-item(?:\s[^']*)?'))[^>]*)>\s*<a\b(?<attributes>[^>]*)>(?<body>.*?)</a>\s*</div>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex AnchorRegex = new(
        @"<a\b(?<attributes>[^>]*)>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex ImageRegex = new(
        @"<img\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MetaRegex = new(
        @"<meta\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex SpanRegex = new(
        @"<span\b(?<attributes>[^>]*)>(?<body>.*?)</span>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ParagraphRegex = new(
        @"<p\b[^>]*>(?<body>.*?)</p>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ListItemRegex = new(
        @"<li\b[^>]*>(?<body>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex StartTagRegex = new(
        @"<[A-Za-z][A-Za-z0-9]*\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex DataServerRegex = new(
        @"<li\b(?<attributes>(?=[^>]*\bdata-server\s*=)[^>]*)>(?<body>.*?)</li>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(3));
    private static readonly Regex BackgroundUrlRegex = new(
        @"\burl\s*\(\s*(?:""(?<url>[^""]+)""|'(?<url>[^']+)'|(?<url>[^)'""\s]+))\s*\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex EpisodeSuffixRegex = new(
        @"^(?<name>.+?)\s+(?<season>\d+)\s*x\s*(?<episode>\d+(?:[.,]\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex EpisodeRouteRegex = new(
        @"-(?<season>\d+)x(?<episode>\d+(?:[.,]\d+)?)$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex SeasonNumberRegex = new(
        @"(?:temporada\s+|[-/])(?<number>\d+)\s*$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex YearRegex = new(
        @"\b(?<year>(?:19|20)\d{2})\b",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex GenreTokenRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,99}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex HoursRegex = new(
        @"(?<value>\d+)\s*h",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex MinutesRegex = new(
        @"(?<value>\d+)\s*m(?:in)?",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex TagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly UTF8Encoding StrictUtf8 =
        new(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly HttpClient _httpClient;
    private readonly VideoHosterResolver? _hosterResolver;
    private readonly ApiContentGenreMapCache _genreMaps = new();
    private readonly ConcurrentDictionary<string, EpisodeLocation>
        _episodeLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);

    public PelisPlusApiContentProvider(
        HttpClient httpClient,
        VideoHosterResolver? hosterResolver = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hosterResolver = hosterResolver;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.PelisPlus;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = NormalizeRootUri(baseUri);
        return _trustedArtworkOrigins.Values
            .Prepend(GetOrigin(root))
            .DistinctBy(
                static uri =>
                    $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
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
            MaximumCatalogBytes,
            cancellationToken);
        var order = new List<string>();
        var entries = new Dictionary<string, CatalogCard>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var card in ReadCards(root, html))
        {
            if (!entries.TryGetValue(card.ExternalId, out var existing))
            {
                order.Add(card.ExternalId);
                entries.Add(card.ExternalId, card);
                continue;
            }

            entries[card.ExternalId] = MergeDailyCards(existing, card);
        }

        return order
            .Take(MaximumDailyUpdateResults)
            .Select(externalId => new ApiContentSearchItem(
                externalId,
                CreatePreviewItem(entries[externalId], includeLatestEpisode: true)))
            .ToArray();
    }

    /// <summary>
    /// The live catalogue exposes genre shelves at
    /// <c>/genero/{slug}</c>. Years remain locally verified: ordinary shelf
    /// cards carry a year, while search cards decorate the title with the
    /// release year.
    /// <para>
    /// Both the genre shelves and the site's year links can be entered without
    /// query text. No detail request per candidate is needed.
    /// </para>
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
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Movie)
        {
            return ApiContentSearchPage.Empty;
        }

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

            endpoint = new Uri(
                root,
                $"busqueda/{Uri.EscapeDataString(searchText)}");
        }

        var html = await ReadStringAsync(
            endpoint,
            root,
            MaximumCatalogBytes,
            cancellationToken);
        var cards = ReadCards(root, html)
            .Where(card =>
                (request.Query is null ||
                 Contains(card.Name, request.Query)) &&
                MatchesYear(card, request.ReleaseYear))
            .DistinctBy(
                static card => card.ExternalId,
                StringComparer.OrdinalIgnoreCase)
            .Take(MaximumSearchResults)
            .ToArray();
        var nextPage = genreToken is not null &&
                       HasGenrePage(root, html, genreToken, page + 1)
            ? (page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        var truncated = genreToken is null &&
                        request.HasFilters &&
                        cards.Length > 0;
        return new ApiContentSearchPage(
            cards
            .Select(card => new ApiContentSearchItem(
                card.ExternalId,
                CreatePreviewItem(card, includeLatestEpisode: false)))
            .ToArray(),
            nextPage,
            isTruncated: truncated);
    }

    /// <summary>
    /// Exact original release year. A card whose year could not be read is
    /// dropped rather than let through, because an unknown year is not
    /// evidence of a matching one.
    /// </summary>
    private static bool MatchesYear(CatalogCard card, int? releaseYear) =>
        releaseYear is not { } year ||
        CatalogReleaseYear.FromReleaseDate(card.ReleaseDate) == year;

    private ValueTask<ApiContentGenreMap> LoadGenreMapAsync(
        Uri root,
        CancellationToken cancellationToken) =>
        _genreMaps.GetAsync(
            root,
            async token =>
            {
                var html = await ReadStringAsync(
                    root,
                    root,
                    MaximumCatalogBytes,
                    token);
                return ParseGenreMap(root, html);
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
            if (!TryReadGenreRoute(root, href, out var token, out _))
            {
                continue;
            }

            var label = NormalizeText(anchor.Groups["body"].Value);
            if (label.Length > 0)
            {
                entries.Add((label, token));
            }
        }

        return ApiContentGenreMap.FromUpstreamLabels(entries);
    }

    private static Uri BuildGenreUri(
        Uri root,
        string genreToken,
        int page) =>
        new(
            root,
            page <= 1
                ? $"genero/{Uri.EscapeDataString(genreToken)}"
                : $"genero/{Uri.EscapeDataString(genreToken)}/page/" +
                  page.ToString(CultureInfo.InvariantCulture));

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
            if (TryReadGenreRoute(
                    root,
                    href,
                    out var token,
                    out var page) &&
                page == expectedPage &&
                string.Equals(
                    token,
                    genreToken,
                    StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool TryReadGenreRoute(
        Uri root,
        string? href,
        out string token,
        out int page)
    {
        token = string.Empty;
        page = 1;
        if (!TryResolveHttpUri(root, href, out var uri) ||
            !SameOrigin(root, uri))
        {
            return false;
        }

        // Parse the few route components as spans. Genre shelves can contain
        // hundreds of links, so this avoids a Split/Select/ToArray chain for
        // every anchor while still allocating the one token we retain.
        var path = uri.AbsolutePath.AsSpan().Trim('/');
        const string genrePrefix = "genero/";
        if (!path.StartsWith(
                genrePrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remainder = path[genrePrefix.Length..];
        var separator = remainder.IndexOf('/');
        var encodedToken = separator < 0
            ? remainder
            : remainder[..separator];
        if (encodedToken.IsEmpty)
        {
            return false;
        }

        if (separator >= 0)
        {
            var pageRoute = remainder[(separator + 1)..].Trim('/');
            const string pagePrefix = "page/";
            if (!pageRoute.StartsWith(
                    pagePrefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            var pageValue = pageRoute[pagePrefix.Length..];
            if (pageValue.IsEmpty ||
                pageValue.Contains('/') ||
                !int.TryParse(
                    pageValue,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out page) ||
                page is < 1 or > 99_999)
            {
                return false;
            }
        }

        try
        {
            token = Uri.UnescapeDataString(encodedToken.ToString());
        }
        catch (UriFormatException)
        {
            return false;
        }

        return GenreTokenRegex.IsMatch(token);
    }

    public ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default) =>
        LoadItemCoreAsync(
            baseUri,
            searchItem,
            targetUnit: null,
            includeUnitMedia: true,
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
            includeUnitMedia: false,
            cancellationToken);
    }

    private async ValueTask<ApiContentItem> LoadItemCoreAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest? targetUnit,
        bool includeUnitMedia,
        CancellationToken cancellationToken)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        var externalId = ValidateContentRoute(searchItem.ExternalId);
        var detailUri = new Uri(root, externalId + "/");
        var html = await ReadStringAsync(
            detailUri,
            root,
            MaximumDetailBytes,
            cancellationToken);
        var isSeries = externalId.StartsWith(
            "serie/",
            StringComparison.OrdinalIgnoreCase);
        var metadata = ReadDetailMetadata(
            root,
            html,
            searchItem.PreviewItem,
            isSeries,
            searchItem.RequireProviderTitle);

        if (isSeries)
        {
            if (targetUnit is not null &&
                TryUseKnownEpisodeRoute(
                    root,
                    externalId,
                    targetUnit,
                    searchItem.UnitRoutes,
                    out var knownSeason))
            {
                return new ApiContentItem(metadata with
                {
                    Subtype = "series",
                    Categories = ["Series"],
                    Seasons = [knownSeason]
                });
            }

            var seasonLinks = ReadKnownSeasonLinks(
                root,
                searchItem.UnitRoutes);
            if (seasonLinks.Count == 0)
            {
                seasonLinks = ReadSeasonLinks(root, html);
            }

            if (targetUnit?.SeasonNumber is { } targetSeason)
            {
                seasonLinks = seasonLinks
                    .Where(season => season.Number == targetSeason)
                    .ToArray();
            }

            var seasons = await LoadSeasonsAsync(
                root,
                detailUri,
                externalId,
                seasonLinks,
                cancellationToken);
            return new ApiContentItem(metadata with
            {
                Subtype = "series",
                Categories = ["Series"],
                Seasons = seasons
            });
        }

        var unit = new CatalogTransferUnit
        {
            Kind = "episode",
            DisplayNumber = "1",
            SortOrder = 0,
            Name = "Full movie",
            ThumbnailRef = metadata.ThumbnailRef,
            DurationSeconds = metadata.RuntimeSeconds
        };
        IReadOnlyList<ApiContentVideoSet> videoSets = [];
        if (includeUnitMedia)
        {
            var sources = ReadVideoSources(html, detailUri);
            var resolvedSources = await ResolveHosterSourcesAsync(
                sources,
                cancellationToken);
            videoSets = resolvedSources.Count == 0
                ? []
                :
                [
                    new ApiContentVideoSet(
                        ContentUnitKind.Episode,
                        "1",
                        seasonNumber: null,
                        resolvedSources)
                ];
        }
        return new ApiContentItem(
            metadata with
            {
                Subtype = "movie",
                PublicationStatus = "completed",
                Categories = ["Movies"],
                Units = [unit]
            },
            videoSets: videoSets);
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

        var externalId = ValidateContentRoute(searchItem.ExternalId);
        if (externalId.StartsWith(
                "pelicula/",
                StringComparison.OrdinalIgnoreCase))
        {
            if (unit.SeasonNumber is not null ||
                !ApiContentUnitRequest.DisplayNumbersMatch(
                    unit.DisplayNumber,
                    "1"))
            {
                return null;
            }

            var movieUri = new Uri(root, externalId + "/");
            var html = await ReadStringAsync(
                movieUri,
                root,
                MaximumDetailBytes,
                cancellationToken);
            var sources = ReadVideoSources(html, movieUri);
            var resolvedSources = await ResolveHosterSourcesAsync(
                sources,
                cancellationToken);
            return resolvedSources.Count == 0
                ? null
                : new ApiContentVideoSet(
                    ContentUnitKind.Episode,
                    "1",
                    seasonNumber: null,
                    resolvedSources);
        }

        if (unit.SeasonNumber is null)
        {
            return null;
        }

        var key = BuildEpisodeKey(
            root,
            externalId,
            unit.SeasonNumber.Value,
            unit.DisplayNumber);
        if (!_episodeLocations.TryGetValue(key, out var location))
        {
            _ = await LoadItemForUnitAsync(
                root,
                searchItem,
                unit,
                cancellationToken);
            if (!_episodeLocations.TryGetValue(key, out location))
            {
                return null;
            }
        }

        var episodeHtml = await ReadStringAsync(
            location.EpisodeUri,
            location.SeasonUri,
            MaximumEpisodeBytes,
            cancellationToken);
        var episodeSources = ReadVideoSources(
            episodeHtml,
            location.EpisodeUri);
        var resolvedEpisodeSources = await ResolveHosterSourcesAsync(
            episodeSources,
            cancellationToken);
        if (resolvedEpisodeSources.Count == 0)
        {
            throw new InvalidDataException(
                "The PelisPlus episode did not expose any playback servers.");
        }

        return new ApiContentVideoSet(
            ContentUnitKind.Episode,
            unit.DisplayNumber,
            unit.SeasonNumber,
            resolvedEpisodeSources);
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

        var root = NormalizeRootUri(baseUri);
        var externalId = ValidateContentRoute(searchItem.ExternalId);
        if (!_episodeLocations.TryGetValue(
                BuildEpisodeKey(
                    root,
                    externalId,
                    seasonNumber,
                    unit.DisplayNumber),
                out var location) ||
            !SameOrigin(root, location.EpisodeUri) ||
            !SameOrigin(root, location.SeasonUri))
        {
            return [];
        }

        return
        [
            new ApiProviderUnitRoute(
                ContentUnitKind.Episode,
                unit.DisplayNumber,
                seasonNumber,
                location.EpisodeUri.PathAndQuery),
            new ApiProviderUnitRoute(
                ContentUnitKind.Episode,
                displayNumber: null,
                seasonNumber,
                location.SeasonUri.PathAndQuery)
        ];
    }

    private bool TryUseKnownEpisodeRoute(
        Uri root,
        string externalId,
        ApiContentUnitRequest targetUnit,
        IReadOnlyList<ApiProviderUnitRoute> routeHints,
        out CatalogTransferSeason season)
    {
        season = null!;
        if (targetUnit.Kind is not ContentUnitKind.Episode ||
            targetUnit.SeasonNumber is not { } seasonNumber)
        {
            return false;
        }

        Uri? episodeUri = null;
        Uri? seasonUri = null;
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Episode ||
                hint.SeasonNumber != seasonNumber)
            {
                continue;
            }

            if (hint.DisplayNumber is { } displayNumber &&
                ApiContentUnitRequest.DisplayNumbersMatch(
                    displayNumber,
                    targetUnit.DisplayNumber) &&
                TryResolveSiteRoute(
                    root,
                    hint.Route,
                    "episodio",
                    out _,
                    out var knownEpisodeUri))
            {
                episodeUri = knownEpisodeUri;
            }
            else if (hint.DisplayNumber is null &&
                     TryResolveSiteRoute(
                         root,
                         hint.Route,
                         "temporada",
                         out _,
                         out var knownSeasonUri))
            {
                seasonUri = knownSeasonUri;
            }
        }

        if (episodeUri is null || seasonUri is null)
        {
            return false;
        }

        _episodeLocations[
            BuildEpisodeKey(
                root,
                externalId,
                seasonNumber,
                targetUnit.DisplayNumber)] =
            new EpisodeLocation(episodeUri, seasonUri);
        season = new CatalogTransferSeason
        {
            Number = seasonNumber,
            SortOrder = seasonNumber,
            DisplayName = $"Temporada {seasonNumber}",
            Units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = targetUnit.DisplayNumber,
                    SortOrder = 0,
                    Name = targetUnit.Name ??
                        $"Episodio {targetUnit.DisplayNumber}"
                }
            ]
        };
        return true;
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

    private async ValueTask<IReadOnlyList<CatalogTransferSeason>>
        LoadSeasonsAsync(
            Uri root,
            Uri seriesUri,
            string externalId,
            IReadOnlyList<SeasonLink> seasonLinks,
            CancellationToken cancellationToken)
    {
        if (seasonLinks.Count > MaximumSeasonCount)
        {
            throw new InvalidDataException(
                "The PelisPlus title contains too many seasons.");
        }

        var seasons = new List<CatalogTransferSeason>(seasonLinks.Count);
        var totalEpisodes = 0;
        foreach (var batch in seasonLinks.Chunk(4))
        {
            var requests = batch.Select(async season =>
            {
                var html = await ReadStringAsync(
                    season.Uri,
                    seriesUri,
                    MaximumSeasonBytes,
                    cancellationToken);
                return new SeasonPage(season, html);
            });
            var pages = await Task.WhenAll(requests);
            foreach (var page in pages)
            {
                var units = ReadEpisodes(
                    root,
                    externalId,
                    page.Link,
                    page.Html);
                totalEpisodes += units.Count;
                if (totalEpisodes > MaximumEpisodeCount)
                {
                    throw new InvalidDataException(
                        "The PelisPlus title contains too many episodes.");
                }

                seasons.Add(new CatalogTransferSeason
                {
                    Number = page.Link.Number,
                    SortOrder = Math.Max(0, page.Link.Number - 1),
                    DisplayName = $"Season {page.Link.Number}",
                    Units = units
                });
            }
        }

        return seasons
            .OrderBy(static season => season.SortOrder)
            .ThenBy(static season => season.Number)
            .ToArray();
    }

    private IReadOnlyList<CatalogTransferUnit> ReadEpisodes(
        Uri root,
        string externalId,
        SeasonLink season,
        string html)
    {
        var episodes = new Dictionary<string, EpisodeEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = ReadAttribute(
                anchor.Groups["attributes"].Value,
                "href");
            if (!TryResolveSiteRoute(
                    root,
                    href,
                    "episodio",
                    out _,
                    out var episodeUri))
            {
                continue;
            }

            var route = GetEscapedRoute(episodeUri);
            var routeMatch = EpisodeRouteRegex.Match(route);
            if (!routeMatch.Success)
            {
                continue;
            }

            var displayNumber = NormalizeDisplayNumber(
                routeMatch.Groups["episode"].Value);
            if (displayNumber.Length == 0)
            {
                continue;
            }

            var body = anchor.Groups["body"].Value;
            var name = ReadSpanByItemProp(body, "name");
            var thumbnail = ResolveArtworkReference(
                root,
                ReadImageAttribute(body, "src") ??
                ReadImageAttribute(body, "data-src"));
            var normalizedKey = NormalizeDisplayKey(displayNumber);
            episodes.TryAdd(
                normalizedKey,
                new EpisodeEntry(
                    displayNumber,
                    NormalizeOptionalText(name),
                    thumbnail,
                    episodeUri));
        }

        var ordered = episodes.Values
            .OrderBy(static episode =>
                ParseDisplayNumber(episode.DisplayNumber))
            .ThenBy(
                static episode => episode.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var episode = ordered[index];
            _episodeLocations[BuildEpisodeKey(
                root,
                externalId,
                season.Number,
                episode.DisplayNumber)] =
                new EpisodeLocation(episode.Uri, season.Uri);
        }

        return ordered
            .Select(static (episode, index) =>
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = episode.DisplayNumber,
                    SortOrder = index,
                    Name = episode.Name,
                    ThumbnailRef = episode.ThumbnailRef
                })
            .ToArray();
    }

    private static IReadOnlyList<SeasonLink> ReadSeasonLinks(
        Uri root,
        string html)
    {
        var links = new Dictionary<string, SeasonLink>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRegex.Matches(html))
        {
            var href = ReadAttribute(
                anchor.Groups["attributes"].Value,
                "href");
            if (!TryResolveSiteRoute(
                    root,
                    href,
                    "temporada",
                    out var route,
                    out var seasonUri))
            {
                continue;
            }

            var text = NormalizeText(anchor.Groups["body"].Value);
            var match = SeasonNumberRegex.Match(text);
            if (!match.Success)
            {
                match = SeasonNumberRegex.Match(route);
            }

            if (!match.Success ||
                !int.TryParse(
                    match.Groups["number"].Value,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var number) ||
                number < 0)
            {
                continue;
            }

            links.TryAdd(
                route,
                new SeasonLink(number, seasonUri));
        }

        return links.Values
            .OrderBy(static season => season.Number)
            .Take(MaximumSeasonCount + 1)
            .ToArray();
    }

    private static IReadOnlyList<SeasonLink> ReadKnownSeasonLinks(
        Uri root,
        IReadOnlyList<ApiProviderUnitRoute> routeHints)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Episode ||
                hint.DisplayNumber is not null ||
                hint.SeasonNumber is not { } seasonNumber ||
                !TryResolveSiteRoute(
                    root,
                    hint.Route,
                    "temporada",
                    out _,
                    out var seasonUri))
            {
                continue;
            }

            return [new SeasonLink(seasonNumber, seasonUri)];
        }

        return [];
    }

    private CatalogTransferItem ReadDetailMetadata(
        Uri root,
        string html,
        CatalogTransferItem preview,
        bool isSeries,
        bool requireProviderTitle)
    {
        var providerName = ReadClassText(html, "title") ??
            ReadMeta(html, "property", "og:title");
        if (requireProviderTitle &&
            string.IsNullOrWhiteSpace(providerName))
        {
            throw new InvalidDataException(
                "The PelisPlus detail page did not include a title.");
        }

        var name = providerName ?? preview.Name;
        name = TrimDetailTitle(NormalizeText(name), isSeries);
        if (name.Length == 0)
        {
            throw new InvalidDataException(
                "PelisPlus did not return a title.");
        }

        var description = ReadParagraphFromClass(html, "description") ??
            ReadMeta(html, "name", "description") ??
            preview.Description;
        description = NormalizeOptionalText(description);
        var year = ReadYear(
                ReadSpanByItemProp(html, "datePublished")) ??
            ReadYear(preview.ReleaseDate);
        var durationText = ReadSpanByItemProp(html, "duration");
        var runtimeSeconds = ParseRuntimeSeconds(durationText) ??
            preview.RuntimeSeconds;
        var thumbnail = ReadImageByItemProp(html, "image") ??
            preview.ThumbnailRef;
        var thumbnailRef = ResolveArtworkReference(root, thumbnail);
        var backdrop = ReadBackgroundImage(html, "backdrop-image") ??
            preview.BackdropRef;
        var backdropRef = ResolveArtworkReference(root, backdrop);
        var genres = ReadGenres(html);

        return new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = isSeries ? "series" : "movie",
            Name = name,
            Description = description,
            ReleaseDate = year,
            RuntimeSeconds = runtimeSeconds,
            ThumbnailRef = thumbnailRef,
            BackdropRef = backdropRef,
            Genres = genres.Count == 0 ? preview.Genres : genres,
            Categories = isSeries ? ["Series"] : ["Movies"]
        };
    }

    private IEnumerable<CatalogCard> ReadCards(Uri root, string html)
    {
        foreach (Match cardMatch in MovieItemRegex.Matches(html))
        {
            var href = ReadAttribute(
                cardMatch.Groups["attributes"].Value,
                "href");
            if (!TryResolveContentRoute(
                    root,
                    href,
                    out var externalId,
                    out _))
            {
                continue;
            }

            var body = cardMatch.Groups["body"].Value;
            var name = ReadClassText(body, "item-detail") ??
                TrimPosterPrefix(ReadImageAttribute(body, "alt"));
            name = NormalizeText(name ?? string.Empty);
            if (name.Length == 0)
            {
                continue;
            }

            var decoratedYear = ReadDecoratedReleaseYear(ref name);
            int? latestSeason = null;
            string? latestEpisode = null;
            var isEpisodeCard = HasClass(
                ReadAttribute(
                    cardMatch.Groups["containerAttributes"].Value,
                    "class"),
                "episode-item");
            if (isEpisodeCard &&
                externalId.StartsWith(
                    "serie/",
                    StringComparison.OrdinalIgnoreCase))
            {
                var episodeMatch = EpisodeSuffixRegex.Match(name);
                if (episodeMatch.Success &&
                    int.TryParse(
                        episodeMatch.Groups["season"].Value,
                        NumberStyles.None,
                        CultureInfo.InvariantCulture,
                        out var seasonNumber))
                {
                    var episodeNumber = NormalizeDisplayNumber(
                        episodeMatch.Groups["episode"].Value);
                    if (episodeNumber.Length > 0)
                    {
                        name = episodeMatch.Groups["name"].Value.Trim();
                        latestSeason = seasonNumber;
                        latestEpisode = episodeNumber;
                    }
                }
            }

            var thumbnail = ResolveArtworkReference(
                root,
                ReadImageAttribute(body, "src") ??
                ReadImageAttribute(body, "data-src"));
            var backdrop = ResolveArtworkReference(
                root,
                ReadAttributeFromClass(
                    body,
                    "card-hover-backdrop",
                    "data-backdrop"));
            var year = ReadYear(ReadClassText(body, "year")) ??
                decoratedYear;
            var description = NormalizeOptionalText(
                ReadClassText(body, "card-hover-info-overview"));
            var genres = ReadListItemsFromClass(
                body,
                "card-hover-info-genre");
            yield return new CatalogCard(
                externalId,
                name,
                externalId.StartsWith(
                    "serie/",
                    StringComparison.OrdinalIgnoreCase),
                year,
                description,
                thumbnail,
                backdrop,
                genres,
                latestSeason,
                latestEpisode);
        }
    }

    private static CatalogTransferItem CreatePreviewItem(
        CatalogCard card,
        bool includeLatestEpisode)
    {
        IReadOnlyList<CatalogTransferSeason> seasons = [];
        IReadOnlyList<CatalogTransferUnit> units = [];
        if (includeLatestEpisode &&
            card.LatestSeason is not null &&
            card.LatestEpisode is not null)
        {
            seasons =
            [
                new CatalogTransferSeason
                {
                    Number = card.LatestSeason.Value,
                    SortOrder = Math.Max(
                        0,
                        card.LatestSeason.Value - 1),
                    DisplayName = $"Season {card.LatestSeason.Value}",
                    Units =
                    [
                        new CatalogTransferUnit
                        {
                            Kind = "episode",
                            DisplayNumber = card.LatestEpisode,
                            SortOrder = GetEpisodeSortOrder(
                                card.LatestEpisode)
                        }
                    ]
                }
            ];
        }
        else if (!card.IsSeries)
        {
            units =
            [
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = "1",
                    SortOrder = 0,
                    Name = "Full movie",
                    ThumbnailRef = card.ThumbnailRef
                }
            ];
        }

        return new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = card.IsSeries ? "series" : "movie",
            Name = card.Name,
            Description = card.Description,
            ReleaseDate = card.ReleaseDate,
            ThumbnailRef = card.ThumbnailRef,
            BackdropRef = card.BackdropRef,
            Genres = card.Genres,
            Categories = card.IsSeries ? ["Series"] : ["Movies"],
            Seasons = seasons,
            Units = units
        };
    }

    private static CatalogCard MergeDailyCards(
        CatalogCard current,
        CatalogCard candidate)
    {
        var latest = IsLaterEpisode(candidate, current)
            ? candidate
            : current;
        return current with
        {
            ReleaseDate = Prefer(
                current.ReleaseDate,
                candidate.ReleaseDate),
            Description = Prefer(
                current.Description,
                candidate.Description),
            ThumbnailRef = Prefer(
                current.ThumbnailRef,
                candidate.ThumbnailRef),
            BackdropRef = Prefer(
                current.BackdropRef,
                candidate.BackdropRef),
            Genres = current.Genres.Count > 0
                ? current.Genres
                : candidate.Genres,
            LatestSeason = latest.LatestSeason,
            LatestEpisode = latest.LatestEpisode
        };
    }

    private static bool IsLaterEpisode(
        CatalogCard candidate,
        CatalogCard current)
    {
        if (candidate.LatestSeason is null ||
            string.IsNullOrWhiteSpace(candidate.LatestEpisode))
        {
            return false;
        }

        if (current.LatestSeason is null ||
            string.IsNullOrWhiteSpace(current.LatestEpisode))
        {
            return true;
        }

        var seasonComparison = candidate.LatestSeason.Value.CompareTo(
            current.LatestSeason.Value);
        if (seasonComparison != 0)
        {
            return seasonComparison > 0;
        }

        var candidateIsNumeric = decimal.TryParse(
            candidate.LatestEpisode.AsSpan(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var candidateNumber);
        var currentIsNumeric = decimal.TryParse(
            current.LatestEpisode.AsSpan(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var currentNumber);
        if (candidateIsNumeric && currentIsNumeric)
        {
            return candidateNumber > currentNumber;
        }

        if (candidateIsNumeric != currentIsNumeric)
        {
            return candidateIsNumeric;
        }

        return string.Compare(
                   candidate.LatestEpisode,
                   current.LatestEpisode,
                   StringComparison.OrdinalIgnoreCase) > 0;
    }

    private static IReadOnlyList<ApiContentVideoSource> ReadVideoSources(
        string html,
        Uri referrer)
    {
        var sources = new List<ApiContentVideoSource>();
        foreach (Match server in DataServerRegex.Matches(html))
        {
            if (sources.Count >= MaximumServers)
            {
                break;
            }

            var encoded = ReadAttribute(
                server.Groups["attributes"].Value,
                "data-server");
            if (!TryDecodePlaybackUri(encoded, out var playbackUri))
            {
                continue;
            }

            var label = NormalizeText(server.Groups["body"].Value);
            if (label.EndsWith(
                    "Reproducir",
                    StringComparison.OrdinalIgnoreCase))
            {
                label = label[..^"Reproducir".Length].Trim();
            }

            if (label.Length == 0)
            {
                label = $"Server {sources.Count + 1}";
            }

            sources.Add(new ApiContentVideoSource(
                label,
                playbackUri,
                mimeType: null,
                referrer,
                ApiContentVideoSourceKind.EmbeddedWeb));
        }

        return MakeLabelsUnique(sources);
    }

    private static IReadOnlyList<ApiContentVideoSource> MakeLabelsUnique(
        IReadOnlyList<ApiContentVideoSource> sources)
    {
        var labels = new Dictionary<string, int>(
            StringComparer.OrdinalIgnoreCase);
        var result = new List<ApiContentVideoSource>(sources.Count);
        foreach (var source in sources)
        {
            labels.TryGetValue(source.Label, out var count);
            count++;
            labels[source.Label] = count;
            if (count == 1)
            {
                result.Add(source);
                continue;
            }

            result.Add(new ApiContentVideoSource(
                $"{source.Label} ({count})",
                source.PlaybackUri,
                source.MimeType,
                source.Referrer,
                source.Kind,
                source.DecryptionKeys,
                source.HttpOrigin));
        }

        return result;
    }

    private static bool TryDecodePlaybackUri(
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var encoded = WebUtility.HtmlDecode(value).Trim();
        if (encoded.Length == 0 ||
            encoded.Length > MaximumEncodedServerLength)
        {
            return false;
        }

        encoded = encoded.Replace('-', '+').Replace('_', '/');
        var remainder = encoded.Length % 4;
        if (remainder == 1)
        {
            return false;
        }

        if (remainder > 0)
        {
            encoded = encoded.PadRight(
                encoded.Length + (4 - remainder),
                '=');
        }

        string decoded;
        try
        {
            decoded = StrictUtf8.GetString(
                Convert.FromBase64String(encoded));
        }
        catch (Exception exception)
            when (exception is FormatException or DecoderFallbackException)
        {
            return false;
        }

        if (decoded.Length == 0 ||
            decoded.Length > 4 * 1024 ||
            !Uri.TryCreate(decoded.Trim(), UriKind.Absolute, out var candidate) ||
            !IsSafeRemoteHttpUri(candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private async ValueTask<string> ReadStringAsync(
        Uri endpoint,
        Uri referrer,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        if (!IsSafeRemoteHttpUri(endpoint))
        {
            throw new InvalidDataException(
                "PelisPlus returned an unsafe HTTP endpoint.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd(
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7");
        request.Headers.AcceptLanguage.ParseAdd(
            "es-ES,es;q=0.9,en;q=0.7");
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (Windows NT 10.0; Win64; x64) " +
            "AppleWebKit/537.36 (KHTML, like Gecko) " +
            "Chrome/138.0.0.0 Safari/537.36");
        request.Headers.Referrer = referrer;
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        if (response.StatusCode is
            HttpStatusCode.Forbidden or
            HttpStatusCode.TooManyRequests or
            HttpStatusCode.ServiceUnavailable)
        {
            throw new HttpRequestException(
                "PelisPlus rejected the catalog request with its browser challenge. Try again later.",
                inner: null,
                response.StatusCode);
        }

        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"PelisPlus response exceeds the {maximumBytes / 1024} KiB limit.");
        }

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

            if (output.Length + read > maximumBytes)
            {
                throw new InvalidDataException(
                    $"PelisPlus response exceeds the {maximumBytes / 1024} KiB limit.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        var html = Encoding.UTF8.GetString(
            output.GetBuffer(),
            0,
            checked((int)output.Length));
        if (LooksLikeBrowserChallenge(html))
        {
            throw new HttpRequestException(
                "PelisPlus returned a browser challenge instead of catalog data.");
        }

        return html;
    }

    private static bool LooksLikeBrowserChallenge(string html) =>
        html.Contains(
            "<title>Just a moment",
            StringComparison.OrdinalIgnoreCase) ||
        html.Contains(
            "cf-chl-",
            StringComparison.OrdinalIgnoreCase) ||
        html.Contains(
            "Enable JavaScript and cookies to continue",
            StringComparison.OrdinalIgnoreCase);

    private static IReadOnlyList<string> ReadGenres(string html)
    {
        var genres = new HashSet<string>(
            StringComparer.CurrentCultureIgnoreCase);
        foreach (Match span in SpanRegex.Matches(html))
        {
            var itemProp = ReadAttribute(
                span.Groups["attributes"].Value,
                "itemprop");
            if (!string.Equals(
                    itemProp,
                    "genre",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var value = NormalizeText(span.Groups["body"].Value)
                .Trim()
                .TrimEnd(',')
                .Trim();
            if (value.Length > 0)
            {
                genres.Add(value);
            }
        }

        return genres
            .Order(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> ReadListItemsFromClass(
        string html,
        string className)
    {
        var body = ReadClassBody(html, className);
        if (body is null)
        {
            return [];
        }

        return ListItemRegex.Matches(body)
            .Select(match => NormalizeText(match.Groups["body"].Value))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string? ReadParagraphFromClass(
        string html,
        string className)
    {
        var body = ReadClassBody(html, className);
        if (body is null)
        {
            return null;
        }

        var paragraph = ParagraphRegex.Match(body);
        return paragraph.Success
            ? NormalizeOptionalText(paragraph.Groups["body"].Value)
            : NormalizeOptionalText(body);
    }

    private static string? ReadClassText(
        string html,
        string className) =>
        NormalizeOptionalText(ReadClassBody(html, className));

    private static string? ReadClassBody(
        string html,
        string className)
    {
        var escaped = Regex.Escape(className);
        var match = Regex.Match(
            html,
            $@"<(?<tag>[A-Za-z][A-Za-z0-9]*)\b(?=[^>]*\bclass\s*=\s*(?:""(?:[^""]*\s)?{escaped}(?:\s[^""]*)?""|'(?:[^']*\s)?{escaped}(?:\s[^']*)?'))[^>]*>(?<body>.*?)</\k<tag>\s*>",
            RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(2));
        return match.Success
            ? match.Groups["body"].Value
            : null;
    }

    private static string? ReadAttributeFromClass(
        string html,
        string className,
        string attributeName)
    {
        foreach (Match tag in StartTagRegex.Matches(html))
        {
            var attributes = tag.Groups["attributes"].Value;
            if (HasClass(
                    ReadAttribute(attributes, "class"),
                    className))
            {
                return ReadAttribute(attributes, attributeName);
            }
        }

        return null;
    }

    private static bool HasClass(string? classes, string className) =>
        !string.IsNullOrWhiteSpace(classes) &&
        classes.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries |
                StringSplitOptions.TrimEntries)
            .Contains(className, StringComparer.OrdinalIgnoreCase);

    private static string? ReadBackgroundImage(
        string html,
        string className)
    {
        var style = ReadAttributeFromClass(html, className, "style");
        if (string.IsNullOrWhiteSpace(style))
        {
            return null;
        }

        var match = BackgroundUrlRegex.Match(style);
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["url"].Value.Trim())
            : null;
    }

    private static string? ReadSpanByItemProp(
        string html,
        string itemProp)
    {
        foreach (Match span in SpanRegex.Matches(html))
        {
            if (string.Equals(
                    ReadAttribute(
                        span.Groups["attributes"].Value,
                        "itemprop"),
                    itemProp,
                    StringComparison.OrdinalIgnoreCase))
            {
                return NormalizeOptionalText(span.Groups["body"].Value);
            }
        }

        return null;
    }

    private static string? ReadImageByItemProp(
        string html,
        string itemProp)
    {
        foreach (Match image in ImageRegex.Matches(html))
        {
            var attributes = image.Groups["attributes"].Value;
            if (string.Equals(
                    ReadAttribute(attributes, "itemprop"),
                    itemProp,
                    StringComparison.OrdinalIgnoreCase))
            {
                return ReadAttribute(attributes, "src") ??
                    ReadAttribute(attributes, "data-src");
            }
        }

        return null;
    }

    private static string? ReadImageAttribute(
        string html,
        string attributeName)
    {
        foreach (Match image in ImageRegex.Matches(html))
        {
            var value = ReadAttribute(
                image.Groups["attributes"].Value,
                attributeName);
            if (!string.IsNullOrWhiteSpace(value))
            {
                return WebUtility.HtmlDecode(value.Trim());
            }
        }

        return null;
    }

    private static string? ReadMeta(
        string html,
        string keyAttribute,
        string keyValue)
    {
        foreach (Match meta in MetaRegex.Matches(html))
        {
            var attributes = meta.Groups["attributes"].Value;
            if (!string.Equals(
                    ReadAttribute(attributes, keyAttribute),
                    keyValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return NormalizeOptionalText(
                ReadAttribute(attributes, "content"));
        }

        return null;
    }

    private static string? ReadAttribute(
        string attributes,
        string attributeName)
    {
        var match = Regex.Match(
            attributes,
            $@"\b{Regex.Escape(attributeName)}\s*=\s*(?:""(?<value>[^""]*)""|'(?<value>[^']*)'|(?<value>[^\s>]+))",
            RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.CultureInvariant,
            TimeSpan.FromSeconds(1));
        return match.Success
            ? WebUtility.HtmlDecode(match.Groups["value"].Value)
            : null;
    }

    private string? ResolveArtworkReference(
        Uri root,
        string? value)
    {
        if (!TryResolveHttpUri(root, value, out var uri) ||
            !IsSafeRemoteHttpUri(uri))
        {
            return null;
        }

        var origin = GetOrigin(uri);
        _trustedArtworkOrigins.TryAdd(origin.AbsoluteUri, origin);
        return uri.AbsoluteUri;
    }

    private static bool TryResolveContentRoute(
        Uri root,
        string? value,
        out string route,
        out Uri uri)
    {
        route = string.Empty;
        uri = null!;
        if (!TryResolveHttpUri(root, value, out var candidate) ||
            !SameOrigin(root, candidate))
        {
            return false;
        }

        var escapedRoute = GetEscapedRoute(candidate);
        if (!IsSafeSiteRoute(escapedRoute) ||
            !escapedRoute.StartsWith(
                "pelicula/",
                StringComparison.OrdinalIgnoreCase) &&
            !escapedRoute.StartsWith(
                "serie/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        route = escapedRoute;
        uri = candidate;
        return true;
    }

    private static bool TryResolveSiteRoute(
        Uri root,
        string? value,
        string prefix,
        out string route,
        out Uri uri)
    {
        route = string.Empty;
        uri = null!;
        if (!TryResolveHttpUri(root, value, out var candidate) ||
            !SameOrigin(root, candidate))
        {
            return false;
        }

        var escapedRoute = GetEscapedRoute(candidate);
        if (!IsSafeSiteRoute(escapedRoute) ||
            !escapedRoute.StartsWith(
                prefix + "/",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        route = escapedRoute;
        uri = candidate;
        return true;
    }

    private static string ValidateContentRoute(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var route = value.Trim().Trim('/');
        if (!IsSafeSiteRoute(route) ||
            !route.StartsWith(
                "pelicula/",
                StringComparison.OrdinalIgnoreCase) &&
            !route.StartsWith(
                "serie/",
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "PelisPlus requires a safe movie or series route.",
                nameof(value));
        }

        return route;
    }

    private static bool IsSafeSiteRoute(string route)
    {
        if (!SafeSiteRouteRegex.IsMatch(route) ||
            InvalidPercentEncodingRegex.IsMatch(route) ||
            route.Contains("..", StringComparison.Ordinal) ||
            route.Contains('\\', StringComparison.Ordinal))
        {
            return false;
        }

        string decoded;
        try
        {
            decoded = Uri.UnescapeDataString(route);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return decoded.Count(static character => character == '/') == 1 &&
               !decoded.Contains('\\', StringComparison.Ordinal) &&
               !decoded.Contains("..", StringComparison.Ordinal);
    }

    private static string GetEscapedRoute(Uri uri) =>
        uri.GetComponents(
                UriComponents.Path,
                UriFormat.UriEscaped)
            .Trim('/');

    private static Uri NormalizeRootUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            !IsSafeRemoteHttpUri(baseUri))
        {
            throw new ArgumentException(
                "PelisPlus requires an absolute public HTTP or HTTPS base URL.",
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

        return builder.Uri;
    }

    private static bool TryResolveHttpUri(
        Uri baseUri,
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value) ||
            !Uri.TryCreate(baseUri, value.Trim(), out var candidate) ||
            !candidate.IsAbsoluteUri ||
            candidate.Scheme is not ("http" or "https") ||
            candidate.UserInfo.Length > 0)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool IsSafeRemoteHttpUri(Uri uri)
    {
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0 ||
            string.Equals(
                uri.IdnHost,
                "localhost",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (!IPAddress.TryParse(uri.IdnHost, out var address))
        {
            return true;
        }

        if (IPAddress.IsLoopback(address))
        {
            return false;
        }

        var bytes = address.GetAddressBytes();
        if (address.AddressFamily is
            System.Net.Sockets.AddressFamily.InterNetwork)
        {
            return bytes[0] switch
            {
                10 => false,
                127 => false,
                169 when bytes[1] == 254 => false,
                172 when bytes[1] is >= 16 and <= 31 => false,
                192 when bytes[1] == 168 => false,
                _ => true
            };
        }

        return !address.IsIPv6LinkLocal &&
               !address.IsIPv6SiteLocal &&
               !address.Equals(IPAddress.IPv6Any) &&
               !address.Equals(IPAddress.IPv6None);
    }

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

    private static Uri GetOrigin(Uri uri) =>
        new UriBuilder(
            uri.Scheme,
            uri.IdnHost,
            uri.IsDefaultPort ? -1 : uri.Port,
            "/").Uri;

    private static string TrimDetailTitle(string value, bool isSeries)
    {
        string[] prefixes = isSeries
            ? ["Ver Serie "]
            : ["Ver Película ", "Ver Pelicula "];
        foreach (var prefix in prefixes)
        {
            if (value.StartsWith(
                    prefix,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = value[prefix.Length..].Trim();
                break;
            }
        }

        string[] suffixes =
        [
            " - Pelisplus",
            " | Pelisplus"
        ];
        foreach (var suffix in suffixes)
        {
            if (value.EndsWith(
                    suffix,
                    StringComparison.OrdinalIgnoreCase))
            {
                value = value[..^suffix.Length].Trim();
                break;
            }
        }

        return value;
    }

    private static string? TrimPosterPrefix(string? value)
    {
        var normalized = NormalizeOptionalText(value);
        if (normalized is null)
        {
            return null;
        }

        return normalized.StartsWith(
            "Poster ",
            StringComparison.OrdinalIgnoreCase)
            ? normalized["Poster ".Length..].Trim()
            : normalized;
    }

    private static string? ReadYear(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var match = YearRegex.Match(value);
        return match.Success
            ? match.Groups["year"].Value
            : null;
    }

    private static string? ReadDecoratedReleaseYear(ref string name)
    {
        var value = name.AsSpan().TrimEnd();
        if (value.Length < 6 ||
            value[^6] != '(' ||
            value[^1] != ')')
        {
            return null;
        }

        var year = value[^5..^1];
        if (!int.TryParse(
                year,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsed) ||
            parsed is < 1900 or > 2099)
        {
            return null;
        }

        var title = value[..^6].TrimEnd();
        if (title.IsEmpty)
        {
            return null;
        }

        name = title.ToString();
        return year.ToString();
    }

    private static int? ParseRuntimeSeconds(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var hours = ReadInteger(HoursRegex, value);
        var minutes = ReadInteger(MinutesRegex, value);
        if (hours is null && minutes is null)
        {
            return null;
        }

        var total = (long)(hours ?? 0) * 3600L +
            (long)(minutes ?? 0) * 60L;
        return total is >= 0 and <= int.MaxValue
            ? (int)total
            : null;
    }

    private static int? ReadInteger(Regex regex, string value)
    {
        var match = regex.Match(value);
        return match.Success &&
               int.TryParse(
                   match.Groups["value"].Value,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var number)
            ? number
            : null;
    }

    private static string NormalizeDisplayNumber(string value)
    {
        var normalized = value.Trim().Replace(',', '.');
        return decimal.TryParse(
            normalized,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var number)
            ? number.ToString(
                "0.############################",
                CultureInfo.InvariantCulture)
            : string.Empty;
    }

    private static string NormalizeDisplayKey(string value) =>
        ApiContentProviderCacheKey.NormalizeDisplayNumber(value);

    private static decimal ParseDisplayNumber(string value) =>
        decimal.TryParse(
            value.AsSpan(),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    private static int GetEpisodeSortOrder(string displayNumber)
    {
        var parsed = ParseDisplayNumber(displayNumber);
        if (parsed is < 1 or > int.MaxValue)
        {
            return 0;
        }

        return (int)decimal.Truncate(parsed) - 1;
    }

    private static string BuildEpisodeKey(
        Uri root,
        string externalId,
        int seasonNumber,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            externalId,
            seasonNumber,
            NormalizeDisplayKey(displayNumber));

    private static string NormalizeText(string value)
    {
        var withoutTags = TagRegex.Replace(value, " ");
        return WhitespaceRegex.Replace(
                WebUtility.HtmlDecode(withoutTags)
                    .Replace('\u00a0', ' '),
                " ")
            .Trim();
    }

    private static string? NormalizeOptionalText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        var normalized = NormalizeText(value);
        return normalized.Length == 0 ? null : normalized;
    }

    private static string NormalizeLookupText(string value)
    {
        var normalized = NormalizeText(value)
            .Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is not
                UnicodeCategory.NonSpacingMark)
            {
                builder.Append(char.ToLowerInvariant(character));
            }
        }

        return builder.ToString()
            .Normalize(NormalizationForm.FormC);
    }

    private static bool Contains(string value, string query) =>
        NormalizeLookupText(value).Contains(
            NormalizeLookupText(query),
            StringComparison.Ordinal);

    private static string? Prefer(params string?[] values) =>
        values.FirstOrDefault(
            static value => !string.IsNullOrWhiteSpace(value))?.Trim();

    private sealed record CatalogCard(
        string ExternalId,
        string Name,
        bool IsSeries,
        string? ReleaseDate,
        string? Description,
        string? ThumbnailRef,
        string? BackdropRef,
        IReadOnlyList<string> Genres,
        int? LatestSeason,
        string? LatestEpisode);

    private sealed record SeasonLink(int Number, Uri Uri);

    private sealed record SeasonPage(SeasonLink Link, string Html);

    private sealed record EpisodeEntry(
        string DisplayNumber,
        string? Name,
        string? ThumbnailRef,
        Uri Uri);

    private sealed record EpisodeLocation(Uri EpisodeUri, Uri SeasonUri);
}

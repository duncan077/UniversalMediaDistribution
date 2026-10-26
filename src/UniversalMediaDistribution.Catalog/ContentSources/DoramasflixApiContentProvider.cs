using System.Collections.Concurrent;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Doramasflix GraphQL/Next.js provider adapted from Streamflix's
/// Apache-2.0 DoramasflixProvider.
/// </summary>
public sealed partial class DoramasflixApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "doramasflix";

    private const int MaximumResponseBytes = 8 * 1024 * 1024;
    private const int FilteredPageSize = 32;
    private const int MaximumFilteredPages = 4;
    private const int MaximumFilteredResults = 200;
    private const string DoramasGoAndroidAppId = "com.asiapp.doramasgo";

    // Confirmed against the live GraphQL schema. The singular fields avoid
    // allocating one-element arrays for filters that accept a single value.
    private const string GenreFilterField = "genreId";
    private const string YearFilterField = "year";

    private readonly ApiContentGenreMapCache _genreMaps = new();
    private static readonly Uri GraphQlEndpoint =
        new("https://sv1.fluxcedene.net/api/gql");
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
    private static readonly Regex OriginalTitleRegex = new(
        """
        <dt\b[^>]*>\s*
        (?:T[i\u00ED]tulo|Nombre)\s+original
        \s*</dt>\s*
        <dd\b[^>]*>(?<value>.*?)</dd>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex PublicationStatusRegex = new(
        """
        <dt\b[^>]*>\s*Estado\b[^<]*</dt>\s*
        <dd\b[^>]*>(?<value>.*?)</dd>
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex DetailNamesRegex = new(
        """
        <h1\b[^>]*>(?<name>.*?)</h1>
        \s*
        (?:<p\b[^>]*>(?<localized>.*?)</p>)?
        """,
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
            RegexOptions.IgnorePatternWhitespace |
            RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex HtmlTagRegex = new(
        @"<[^>]+>",
        RegexOptions.Singleline | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex WhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex SafeEpisodeSlugRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9._-]{0,511}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex ScriptSourceRegex = new(
        """
        <script\b[^>]*\bsrc\s*=\s*(?:"(?<value>[^"]+)"|'(?<value>[^']+)')
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex DecodePlayerActionRegex = new(
        """
        createServerReference\)\(\s*["'](?<id>[A-Fa-f0-9]{32,64})["']
        .{0,512}?["']decodePlayerToken["']
        """,
        RegexOptions.Singleline | RegexOptions.CultureInvariant |
            RegexOptions.IgnorePatternWhitespace,
        TimeSpan.FromSeconds(2));
    private readonly HttpClient _httpClient;
    private readonly VideoHosterResolver? _hosterResolver;
    private readonly ConcurrentDictionary<string, string> _episodeSlugs =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _episodeIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _artworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _fkPlayerActionIds =
        new(StringComparer.OrdinalIgnoreCase);

    public DoramasflixApiContentProvider(
        HttpClient httpClient,
        VideoHosterResolver? hosterResolver = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hosterResolver = hosterResolver;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.Doramasflix;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) =>
        _artworkOrigins.Values
            .Prepend(NormalizeRoot(baseUri))
            .Append(new Uri("https://image.tmdb.org/"))
            .DistinctBy(
                static uri => uri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        LoadDailyUpdatesAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
    {
        _ = NormalizeRoot(baseUri);
        var doramas = await LoadListAsync(
            movies: false,
            cancellationToken);
        var movies = await LoadListAsync(
            movies: true,
            cancellationToken);
        return doramas.Concat(movies).Take(100).ToArray();
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        _ = NormalizeRoot(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (mediaType is not null and not MediaType.Movie)
        {
            return [];
        }

        const string graphQuery = """
            query searchAll($input: String!) {
              searchDorama(input: $input, limit: 32) {
                _id slug name name_es poster_path poster
              }
              searchMovie(input: $input, limit: 32) {
                _id slug name name_es poster_path poster
              }
            }
            """;
        using var document = await PostGraphQlAsync(
            "searchAll",
            new JsonObject { ["input"] = query.Trim() },
            graphQuery,
            cancellationToken);
        if (!TryGetData(document.RootElement, out var data))
        {
            return [];
        }

        var results = new List<ApiContentSearchItem>();
        AddSearchResults(
            data,
            "searchDorama",
            "doramas-online/",
            isSeries: true,
            results);
        AddSearchResults(
            data,
            "searchMovie",
            "peliculas-online/",
            isSeries: false,
            results);
        return results
            .Where(result => ContainsTitle(
                result.PreviewItem,
                query))
            .DistinctBy(
                static result => result.ExternalId,
                StringComparer.OrdinalIgnoreCase)
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
        var root = NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        var route = ValidateRoute(searchItem.ExternalId);
        var isSeries = route.StartsWith(
            "doramas-online/",
            StringComparison.OrdinalIgnoreCase);
        var detailUri = new Uri(root, route);
        var html = await ReadHtmlAsync(
            detailUri,
            root,
            cancellationToken);
        using var nextData = TryParseNextData(html);
        var media = nextData is null
            ? null
            : FindApolloMedia(
                nextData.RootElement,
                isSeries ? ["Dorama:", "Movie:"] : ["Movie:"]);
        var item = CreateMetadata(
            root,
            searchItem.PreviewItem,
            media,
            html,
            isSeries,
            searchItem.RequireProviderTitle);
        if (!isSeries)
        {
            var unit = new CatalogTransferUnit
            {
                Kind = "episode",
                DisplayNumber = "1",
                SortOrder = 0,
                Name = item.Name
            };
            var videoSet = includeUnitMedia
                ? await ReadVideosAsync(
                    root,
                    detailUri,
                    mediaId: null,
                    isMovie: true,
                    "1",
                    seasonNumber: null,
                    cancellationToken)
                : null;
            return new ApiContentItem(
                item with { Units = [unit] },
                videoSets: videoSet is null ? [] : [videoSet]);
        }

        if (TryUseKnownEpisodeRoute(
                root,
                route,
                searchItem.UnitRoutes,
                out var knownSeason))
        {
            return new ApiContentItem(item with { Seasons = [knownSeason] });
        }

        var seriesId = media is null
            ? ReadSeriesId(html)
            : GetString(media.Value, "_id") ?? ReadSeriesId(html);
        if (string.IsNullOrWhiteSpace(seriesId))
        {
            return new ApiContentItem(item);
        }

        var seasons = await LoadSeasonsAsync(
            root,
            route,
            seriesId,
            targetUnit?.SeasonNumber,
            cancellationToken);
        return new ApiContentItem(item with { Seasons = seasons });
    }

    public async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode)
        {
            return null;
        }

        var route = ValidateRoute(searchItem.ExternalId);
        if (route.StartsWith(
                "peliculas-online/",
                StringComparison.OrdinalIgnoreCase))
        {
            return await ReadVideosAsync(
                root,
                new Uri(root, route),
                mediaId: null,
                isMovie: true,
                "1",
                seasonNumber: null,
                cancellationToken);
        }

        var key = EpisodeKey(
            root,
            route,
            unit.SeasonNumber,
            unit.DisplayNumber);
        if (!_episodeSlugs.TryGetValue(key, out var slug))
        {
            await LoadItemForUnitAsync(
                baseUri,
                searchItem,
                unit,
                cancellationToken);
            _episodeSlugs.TryGetValue(key, out slug);
        }

        _episodeIds.TryGetValue(key, out var episodeId);

        return string.IsNullOrWhiteSpace(slug)
            ? null
            : await ReadVideosAsync(
                root,
                new Uri(root, $"episodios/{slug.TrimStart('/')}"),
                episodeId,
                isMovie: false,
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
        if (!_episodeSlugs.TryGetValue(
                EpisodeKey(
                    root,
                    route,
                    seasonNumber,
                    unit.DisplayNumber),
                out var slug) ||
            !TryValidateEpisodeSlug(slug, out var normalizedSlug))
        {
            return [];
        }

        return
        [
            new ApiProviderUnitRoute(
                ContentUnitKind.Episode,
                unit.DisplayNumber,
                seasonNumber,
                $"episodios/{normalizedSlug}")
        ];
    }

    private bool TryUseKnownEpisodeRoute(
        Uri root,
        string titleRoute,
        IReadOnlyList<ApiProviderUnitRoute> routeHints,
        out CatalogTransferSeason season)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Episode ||
                hint.DisplayNumber is not { } displayNumber ||
                hint.SeasonNumber is not { } seasonNumber ||
                !TryReadEpisodeRoute(hint.Route, out var slug))
            {
                continue;
            }

            _episodeSlugs[
                EpisodeKey(
                    root,
                    titleRoute,
                    seasonNumber,
                    displayNumber)] = slug;
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
                        DisplayNumber = displayNumber,
                        SortOrder = 0,
                        Name = $"Episodio {displayNumber}"
                    }
                ]
            };
            return true;
        }

        season = null!;
        return false;
    }

    /// <summary>
    /// The GraphQL catalogue filters on both, and it paginates without a search
    /// term, so neither filter needs local verification.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.Native,
        releaseYear: ApiContentFilterSupport.Native,
        supportsFilterOnlySearch: true);

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Movie)
        {
            return ApiContentSearchPage.Empty;
        }

        if (!request.HasFilters)
        {
            // Nothing to filter on, so the text search endpoint answers better
            // than paging the whole catalogue.
            var found = await SearchAsync(
                baseUri,
                request.Query!,
                request.MediaType,
                cancellationToken);
            return new ApiContentSearchPage(found);
        }

        string? genreId = null;
        if (request.Genre is { } genre)
        {
            var genres = await LoadGenreMapAsync(cancellationToken);
            if (!genres.TryGetToken(genre, out genreId))
            {
                // The catalogue does not publish this genre, so answering
                // without it would widen the search that was asked for.
                return ApiContentSearchPage.Empty;
            }
        }

        var results = new List<ApiContentSearchItem>();
        var truncated = false;
        foreach (var movies in (bool[])[false, true])
        {
            var (items, stoppedEarly) = await LoadFilteredListAsync(
                movies,
                genreId,
                request.ReleaseYear,
                request.Query,
                cancellationToken);
            results.AddRange(items);
            truncated |= stoppedEarly;
        }

        return new ApiContentSearchPage(
            results
                .DistinctBy(
                    static result => result.ExternalId,
                    StringComparer.OrdinalIgnoreCase)
                .Take(MaximumFilteredResults)
                .ToArray(),
            isTruncated: truncated);
    }

    /// <summary>
    /// Pages the filtered catalogue, verifying the query text locally because
    /// the GraphQL search and the GraphQL filter are separate entry points and
    /// cannot be combined in one call.
    /// </summary>
    private async Task<(IReadOnlyList<ApiContentSearchItem> Items,
        bool StoppedEarly)> LoadFilteredListAsync(
            bool movies,
            string? genreId,
            int? releaseYear,
            string? query,
            CancellationToken cancellationToken)
    {
        var operation = movies ? "filterMovies" : "filterDoramas";
        var field = movies ? "paginationMovie" : "paginationDorama";
        // GraphQL rejects an operation that declares a variable it never
        // references, so a query filtered on only one of these — or on neither,
        // which is how a plain catalogue browse arrives here — must not declare
        // the other. Declaring both unconditionally failed validation and made
        // every filtered and filter-only search return nothing.
        var variableParts = new List<string>(4)
        {
            "$page: Int",
            "$perPage: Int"
        };
        var filterParts = new List<string>(2);
        if (genreId is not null)
        {
            variableParts.Add("$genre: MongoID");
            filterParts.Add($"{GenreFilterField}: $genre");
        }

        if (releaseYear is not null)
        {
            variableParts.Add("$year: String");
            filterParts.Add($"{YearFilterField}: $year");
        }

        var graphQuery = $$"""
            query {{operation}}({{string.Join(", ", variableParts)}}) {
              {{field}}(page: $page, perPage: $perPage,
                sort: UPDATEDAT_DESC,
                filter: { {{string.Join(", ", filterParts)}} }) {
                items {
                  _id slug name name_es poster_path poster
                }
                pageInfo { hasNextPage }
              }
            }
            """;

        var results = new List<ApiContentSearchItem>();
        var stoppedEarly = false;
        for (var page = 1; page <= MaximumFilteredPages; page++)
        {
            var variables = new JsonObject
            {
                ["page"] = page,
                ["perPage"] = FilteredPageSize
            };
            if (genreId is not null)
            {
                variables["genre"] = genreId;
            }

            if (releaseYear is not null)
            {
                variables["year"] = releaseYear.Value.ToString(
                    CultureInfo.InvariantCulture);
            }

            using var document = await PostGraphQlAsync(
                operation,
                variables,
                graphQuery,
                cancellationToken);
            if (!TryGetData(document.RootElement, out var data) ||
                !data.TryGetProperty(field, out var pagination) ||
                !pagination.TryGetProperty("items", out var items) ||
                items.ValueKind is not JsonValueKind.Array)
            {
                break;
            }

            var pageResults = new List<ApiContentSearchItem>();
            AddArrayResults(
                items,
                movies ? "peliculas-online/" : "doramas-online/",
                isSeries: !movies,
                pageResults);
            results.AddRange(query is null
                ? pageResults
                : pageResults.Where(result =>
                    ContainsTitle(result.PreviewItem, query)));

            if (!HasNextPage(pagination))
            {
                break;
            }

            if (page == MaximumFilteredPages)
            {
                // The bound was reached with more waiting upstream, so the
                // caller must not read this as the complete answer.
                stoppedEarly = true;
            }
        }

        return (results, stoppedEarly);
    }

    private static bool HasNextPage(JsonElement pagination) =>
        pagination.TryGetProperty("pageInfo", out var pageInfo) &&
        pageInfo.TryGetProperty("hasNextPage", out var hasNext) &&
        hasNext.ValueKind is JsonValueKind.True;

    /// <summary>
    /// Resolves genre IDs from the live catalogue. The IDs are database keys
    /// and are never written down in code: a stale one silently filters to
    /// nothing or, worse, to the wrong shelf.
    /// </summary>
    private ValueTask<ApiContentGenreMap> LoadGenreMapAsync(
        CancellationToken cancellationToken) =>
        _genreMaps.GetAsync(
            GraphQlEndpoint,
            async token =>
            {
                // Genre carries no name_es any more, and asking for it fails
                // the whole query with a validation error rather than
                // returning null. Its name is already the Spanish label
                // ("Drama", "Documental", "Comedia"), which is what the shared
                // genre aliases were written against.
                const string genreQuery = """
                    query listGenres {
                      listGenres { _id name slug }
                    }
                    """;
                using var document = await PostGraphQlAsync(
                    "listGenres",
                    new JsonObject(),
                    genreQuery,
                    token);
                if (!TryGetData(document.RootElement, out var data) ||
                    !data.TryGetProperty("listGenres", out var genres) ||
                    genres.ValueKind is not JsonValueKind.Array)
                {
                    return ApiContentGenreMap.Empty;
                }

                var options = new List<(string Label, string Token)>();
                foreach (var genre in genres.EnumerateArray())
                {
                    if (!TryReadString(genre, "_id", out var id))
                    {
                        continue;
                    }

                    foreach (var field in (string[])["name", "slug"])
                    {
                        if (TryReadString(genre, field, out var label))
                        {
                            options.Add((label, id));
                        }
                    }
                }

                return ApiContentGenreMap.FromUpstreamLabels(options);
            },
            cancellationToken);

    private static bool TryReadString(
        JsonElement element,
        string property,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? value)
    {
        if (element.ValueKind is JsonValueKind.Object &&
            element.TryGetProperty(property, out var found) &&
            found.ValueKind is JsonValueKind.String)
        {
            value = found.GetString() ?? string.Empty;
            return value.Length > 0;
        }

        value = null;
        return false;
    }

    private async Task<IReadOnlyList<ApiContentSearchItem>> LoadListAsync(
        bool movies,
        CancellationToken cancellationToken)
    {
        var operation = movies ? "listMovies" : "listDoramas";
        var field = movies ? "paginationMovie" : "paginationDorama";
        var graphQuery = $$"""
            query {{operation}}($page: Int, $perPage: Int) {
              {{field}}(page: $page, perPage: $perPage,
                sort: UPDATEDAT_DESC, filter: {}) {
                items {
                  _id slug name name_es poster_path poster
                }
              }
            }
            """;
        using var document = await PostGraphQlAsync(
            operation,
            new JsonObject { ["page"] = 1, ["perPage"] = 32 },
            graphQuery,
            cancellationToken);
        if (!TryGetData(document.RootElement, out var data) ||
            !data.TryGetProperty(field, out var pagination) ||
            !pagination.TryGetProperty("items", out var items) ||
            items.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var results = new List<ApiContentSearchItem>();
        AddArrayResults(
            items,
            movies ? "peliculas-online/" : "doramas-online/",
            isSeries: !movies,
            results);
        return results;
    }

    private async Task<IReadOnlyList<CatalogTransferSeason>>
        LoadSeasonsAsync(
            Uri root,
            string route,
            string seriesId,
            int? targetSeasonNumber,
            CancellationToken cancellationToken)
    {
        const string seasonQuery = """
            query listSeasons($serie_id: MongoID!) {
              listSeasons(sort: NUMBER_ASC,
                filter: {serie_id: $serie_id}) {
                slug season_number poster_path
              }
            }
            """;
        using var seasonDocument = await PostGraphQlAsync(
            "listSeasons",
            new JsonObject { ["serie_id"] = seriesId },
            seasonQuery,
            cancellationToken);
        if (!TryGetData(seasonDocument.RootElement, out var data) ||
            !data.TryGetProperty("listSeasons", out var seasons) ||
            seasons.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var tasks = seasons.EnumerateArray()
            .Select(
                season => new
                {
                    Number = GetInt32(season, "season_number"),
                    Artwork = NormalizeArtwork(
                        root,
                        GetString(season, "poster_path"))
                })
            .Where(static season => season.Number is not null)
            .Where(season =>
                targetSeasonNumber is null ||
                season.Number == targetSeasonNumber)
            .Take(100)
            .Select(
                async season =>
                {
                    var units = await LoadEpisodesAsync(
                        root,
                        route,
                        seriesId,
                        season.Number!.Value,
                        cancellationToken);
                    return new CatalogTransferSeason
                    {
                        Number = season.Number.Value,
                        SortOrder = season.Number.Value,
                        DisplayName =
                            $"Temporada {season.Number.Value}",
                        ThumbnailRef = season.Artwork?.AbsoluteUri,
                        Units = units
                    };
                })
            .ToArray();
        return (await Task.WhenAll(tasks))
            .OrderBy(static season => season.Number)
            .ToArray();
    }

    private async Task<IReadOnlyList<CatalogTransferUnit>>
        LoadEpisodesAsync(
            Uri root,
            string route,
            string seriesId,
            int seasonNumber,
            CancellationToken cancellationToken)
    {
        const string episodeQuery = """
            query listEpisodes($season_number: Float!, $serie_id: MongoID!) {
              listEpisodes(sort: NUMBER_ASC,
                filter: {type_serie: "dorama",
                  serie_id: $serie_id,
                  season_number: $season_number}) {
                _id name slug episode_number season_number still_path
              }
            }
            """;
        using var document = await PostGraphQlAsync(
            "listEpisodes",
            new JsonObject
            {
                ["serie_id"] = seriesId,
                ["season_number"] = seasonNumber
            },
            episodeQuery,
            cancellationToken);
        if (!TryGetData(document.RootElement, out var data) ||
            !data.TryGetProperty("listEpisodes", out var episodes) ||
            episodes.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        var units = new List<CatalogTransferUnit>();
        foreach (var episode in episodes.EnumerateArray().Take(20_000))
        {
            var number = GetDecimal(episode, "episode_number");
            var slug = GetString(episode, "slug");
            if (number is null || string.IsNullOrWhiteSpace(slug))
            {
                continue;
            }

            var display = number.Value.ToString(
                "0.############################",
                CultureInfo.InvariantCulture);
            _episodeSlugs[
                EpisodeKey(root, route, seasonNumber, display)] = slug;
            var episodeId = GetString(episode, "_id");
            if (!string.IsNullOrWhiteSpace(episodeId))
            {
                _episodeIds[
                    EpisodeKey(root, route, seasonNumber, display)] = episodeId;
            }
            var episodeName = GetString(episode, "name");
            units.Add(
                new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = display,
                    SortOrder = units.Count,
                    Name = string.IsNullOrWhiteSpace(episodeName)
                        ? $"Episodio {display}"
                        : $"Episodio {display}: {episodeName}",
                    ThumbnailRef = NormalizeArtwork(
                            root,
                            GetString(episode, "still_path"))
                        ?.AbsoluteUri
                });
        }

        return units;
    }

    private async ValueTask<ApiContentVideoSet?> ReadVideosAsync(
        Uri root,
        Uri pageUri,
        string? mediaId,
        bool isMovie,
        string displayNumber,
        int? seasonNumber,
        CancellationToken cancellationToken)
    {
        var html = await ReadHtmlAsync(pageUri, root, cancellationToken);
        var links = new List<(string Url, string? Language)>();
        using (var nextData = TryParseNextData(html))
        {
            if (nextData is not null)
            {
                CollectLinkObjects(nextData.RootElement, links);
            }
        }

        if (links.Count == 0)
        {
            await LoadCurrentLinksAsync(
                pageUri,
                mediaId,
                isMovie,
                links,
                cancellationToken);
        }

        var sources = new List<ApiContentVideoSource>();
        foreach (var (url, language) in links
                     .DistinctBy(
                         static link => link.Url,
                         StringComparer.OrdinalIgnoreCase)
                     .Take(100))
        {
            var resolvedLink = await ResolveFkPlayerAsync(
                root,
                url,
                cancellationToken);
            if (!Uri.TryCreate(
                    resolvedLink.Url,
                    UriKind.Absolute,
                    out var uri) ||
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
                    BuildServerLabel(uri, language),
                    uri,
                    direct
                        ? HosterMimeTypeResolver.MimeTypeFromUrl(uri)
                        : null,
                    resolvedLink.Referrer ?? pageUri,
                    direct
                        ? ApiContentVideoSourceKind.DirectMedia
                        : ApiContentVideoSourceKind.EmbeddedWeb));
        }

        IReadOnlyList<ApiContentVideoSource> resolvedSources = sources;
        if (_hosterResolver is not null)
        {
            resolvedSources = await _hosterResolver.ResolveAsync(
                sources,
                cancellationToken);
        }

        var unique = resolvedSources
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
            .ToArray();
        return unique.Length == 0
            ? null
            : new ApiContentVideoSet(
                ContentUnitKind.Episode,
                displayNumber,
                seasonNumber,
                unique);
    }

    private async Task LoadCurrentLinksAsync(
        Uri pageUri,
        string? mediaId,
        bool isMovie,
        List<(string Url, string? Language)> links,
        CancellationToken cancellationToken)
    {
        var slug = ReadLastPathSegment(pageUri);
        if (!SafeEpisodeSlugRegex.IsMatch(slug))
        {
            return;
        }

        mediaId ??= await LoadMediaIdBySlugAsync(
            slug,
            isMovie,
            cancellationToken);
        if (string.IsNullOrWhiteSpace(mediaId))
        {
            return;
        }

        var operationName = isMovie
            ? "MovieLinks"
            : "EpisodeLinksOnline";
        var variableName = isMovie ? "movie_id" : "episode_id";
        var fieldName = isMovie ? "getMovieLinks" : "getEpisodeLinks";
        var graphQuery = $$"""
            query {{operationName}}(${{variableName}}: MongoID!) {
              {{fieldName}}(id: ${{variableName}}, app: "{{DoramasGoAndroidAppId}}") {
                links_online
              }
            }
            """;
        using var document = await PostGraphQlAsync(
            operationName,
            new JsonObject { [variableName] = mediaId },
            graphQuery,
            cancellationToken);
        if (!TryGetData(document.RootElement, out var data) ||
            !data.TryGetProperty(fieldName, out var result) ||
            result.ValueKind is not JsonValueKind.Object ||
            !result.TryGetProperty("links_online", out var linksOnline))
        {
            return;
        }

        CollectLinkObjects(linksOnline, links);
        if (isMovie)
        {
            await CollectMovieCatalogLinksAsync(slug, links, cancellationToken);
        }
    }

    /// <summary>
    /// Adds the servers <c>getMovieLinks</c> leaves out.
    /// <para>
    /// That resolver returns only one link per language, so a movie offering
    /// several Spanish servers surfaced just one of them while the site listed
    /// them all. The movie record's own <c>links_online</c> carries the
    /// complete set, and as direct hoster URLs rather than player tokens, so it
    /// needs no decoding step. Episodes are unaffected — their resolver already
    /// returns everything — so this runs for movies only.
    /// </para>
    /// </summary>
    private async Task CollectMovieCatalogLinksAsync(
        string slug,
        List<(string Url, string? Language)> links,
        CancellationToken cancellationToken)
    {
        const string graphQuery = """
            query MovieCatalogLinks($slug: String!) {
              paginationMovie(page: 1, perPage: 1,
                filter: { slug: $slug }) {
                items { links_online }
              }
            }
            """;
        try
        {
            using var document = await PostGraphQlAsync(
                "MovieCatalogLinks",
                new JsonObject { ["slug"] = slug },
                graphQuery,
                cancellationToken);
            if (TryGetData(document.RootElement, out var data) &&
                data.TryGetProperty("paginationMovie", out var pagination) &&
                pagination.TryGetProperty("items", out var items) &&
                items.ValueKind is JsonValueKind.Array)
            {
                CollectLinkObjects(items, links);
            }
        }
        catch (HttpRequestException)
        {
            // The links already collected remain usable.
        }
        catch (InvalidDataException)
        {
        }
    }

    private async Task<string?> LoadMediaIdBySlugAsync(
        string slug,
        bool isMovie,
        CancellationToken cancellationToken)
    {
        var operationName = isMovie
            ? "DetailMovieSlug"
            : "EpisodeDetailSlug";
        var fieldName = isMovie ? "detailMovie" : "detailEpisode";
        var graphQuery = $$"""
            query {{operationName}}($slug: String!) {
              {{fieldName}}(filter: { slug: $slug }) { _id }
            }
            """;
        using var document = await PostGraphQlAsync(
            operationName,
            new JsonObject { ["slug"] = slug },
            graphQuery,
            cancellationToken);
        return TryGetData(document.RootElement, out var data) &&
               data.TryGetProperty(fieldName, out var result) &&
               result.ValueKind is JsonValueKind.Object
            ? GetString(result, "_id")
            : null;
    }

    private static string ReadLastPathSegment(Uri uri)
    {
        var path = uri.AbsolutePath.AsSpan().Trim('/');
        var separator = path.LastIndexOf('/');
        return (separator < 0 ? path : path[(separator + 1)..]).ToString();
    }

    private async Task<(string Url, Uri? Referrer)> ResolveFkPlayerAsync(
        Uri root,
        string value,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) ||
            !HosterExtractorBase.IsSafePublicHttpUri(uri) ||
            !(string.Equals(
                  uri.IdnHost,
                  "fkplayer.xyz",
                  StringComparison.OrdinalIgnoreCase) ||
              uri.IdnHost.EndsWith(
                  ".fkplayer.xyz",
                  StringComparison.OrdinalIgnoreCase)))
        {
            return (value, null);
        }

        try
        {
            var html = await ReadHtmlAsync(uri, root, cancellationToken);
            using var nextData = TryParseNextData(html);
            if (nextData is not null)
            {
                var legacyToken = FindString(nextData.RootElement, "token");
                if (string.IsNullOrWhiteSpace(legacyToken))
                {
                    return (value, null);
                }

                var legacyResult = await DecodeLegacyFkPlayerTokenAsync(
                    uri,
                    legacyToken,
                    value,
                    cancellationToken);
                return (legacyResult, string.Equals(
                    legacyResult,
                    value,
                    StringComparison.Ordinal) ? null : uri);
            }

            var currentResult = await DecodeCurrentFkPlayerTokenAsync(
                uri,
                html,
                value,
                cancellationToken);
            return (currentResult, string.Equals(
                currentResult,
                value,
                StringComparison.Ordinal) ? null : uri);
        }
        catch (HttpRequestException)
        {
            return (value, null);
        }
        catch (InvalidDataException)
        {
            return (value, null);
        }
        catch (JsonException)
        {
            return (value, null);
        }
    }

    private async Task<string> DecodeLegacyFkPlayerTokenAsync(
        Uri playerUri,
        string token,
        string fallback,
        CancellationToken cancellationToken)
    {
        var decodingEndpoint = new Uri("https://fkplayer.xyz/api/decoding");
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            decodingEndpoint)
        {
            Content = JsonNodeContent(new JsonObject { ["token"] = token })
        };
        request.Headers.Referrer = playerUri;
        request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var decodedBytes =
            await HosterExtractorBase.ReadBoundedResponseBytesAsync(
                response.Content,
                decodingEndpoint,
                MaximumResponseBytes,
                cancellationToken);
        using var decoded = JsonDocument.Parse(decodedBytes);
        var encoded = FindString(decoded.RootElement, "link");
        if (string.IsNullOrWhiteSpace(encoded))
        {
            return fallback;
        }

        try
        {
            return Encoding.UTF8.GetString(
                Convert.FromBase64String(encoded));
        }
        catch (FormatException)
        {
            return fallback;
        }
    }

    private async Task<string> DecodeCurrentFkPlayerTokenAsync(
        Uri playerUri,
        string html,
        string fallback,
        CancellationToken cancellationToken)
    {
        string? actionId = null;
        var scriptCacheKeys = new List<string>();
        foreach (Match scriptMatch in ScriptSourceRegex.Matches(html).Take(24))
        {
            var source = WebUtility.HtmlDecode(
                scriptMatch.Groups["value"].Value);
            if (!Uri.TryCreate(playerUri, source, out var scriptUri) ||
                !HosterExtractorBase.IsSafePublicHttpUri(scriptUri) ||
                !string.Equals(
                    scriptUri.IdnHost,
                    playerUri.IdnHost,
                    StringComparison.OrdinalIgnoreCase) ||
                scriptUri.Port != playerUri.Port ||
                !scriptUri.AbsolutePath.StartsWith(
                    "/_next/static/",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            scriptCacheKeys.Add(scriptUri.AbsoluteUri);
            if (_fkPlayerActionIds.TryGetValue(
                    scriptUri.AbsoluteUri,
                    out actionId))
            {
                break;
            }

            var script = await ReadHtmlAsync(
                scriptUri,
                playerUri,
                cancellationToken);
            var actionMatch = DecodePlayerActionRegex.Match(script);
            if (actionMatch.Success)
            {
                actionId = actionMatch.Groups["id"].Value;
                break;
            }
        }

        if (actionId is null)
        {
            return fallback;
        }

        foreach (var cacheKey in scriptCacheKeys)
        {
            _fkPlayerActionIds[cacheKey] = actionId;
        }

        var routeToken = ReadLastPathSegment(playerUri);
        if (routeToken.Length == 0)
        {
            return fallback;
        }

        using var request = new HttpRequestMessage(HttpMethod.Post, playerUri)
        {
            Content = new StringContent(
                new JsonArray(routeToken).ToJsonString(),
                Encoding.UTF8,
                "text/plain")
        };
        request.Headers.Accept.ParseAdd("text/x-component");
        request.Headers.Referrer = playerUri;
        request.Headers.TryAddWithoutValidation("Next-Action", actionId);
        request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseBytes =
            await HosterExtractorBase.ReadBoundedResponseBytesAsync(
                response.Content,
                playerUri,
                MaximumResponseBytes,
                cancellationToken);
        var responseText = Encoding.UTF8.GetString(responseBytes);
        foreach (var line in responseText.Split(
                     '\n',
                     StringSplitOptions.RemoveEmptyEntries |
                     StringSplitOptions.TrimEntries))
        {
            var separator = line.IndexOf(':');
            if (separator < 0 || separator == line.Length - 1)
            {
                continue;
            }

            try
            {
                using var document = JsonDocument.Parse(line[(separator + 1)..]);
                var iframeUrl = FindString(
                    document.RootElement,
                    "iframeUrl");
                if (Uri.TryCreate(
                        iframeUrl,
                        UriKind.Absolute,
                        out var iframeUri) &&
                    HosterExtractorBase.IsSafePublicHttpUri(iframeUri))
                {
                    return iframeUri.AbsoluteUri;
                }
            }
            catch (JsonException)
            {
                // Other React Flight rows can contain references rather than
                // standalone JSON objects; only the action result is needed.
            }
        }

        return fallback;
    }

    private static StringContent JsonNodeContent(JsonNode payload) =>
        new(payload.ToJsonString(), Encoding.UTF8, "application/json");

    /// <summary>
    /// Builds the request body as a <see cref="JsonNode"/> tree rather than
    /// serializing an anonymous type. <c>JsonContent.Create</c> on an anonymous
    /// type needs the reflection-based serializer, which a Native AOT build has
    /// no way to provide: it warns as IL3050 at publish and throws at runtime,
    /// so this provider was the one that stopped working there. Writing a node
    /// out takes no reflection at all.
    /// </summary>
    private async Task<JsonDocument> PostGraphQlAsync(
        string operationName,
        JsonObject variables,
        string query,
        CancellationToken cancellationToken)
    {
        var payload = new JsonObject
        {
            ["operationName"] = operationName,
            ["variables"] = variables,
            ["query"] = query
        };
        using var request = new HttpRequestMessage(
            HttpMethod.Post,
            GraphQlEndpoint)
        {
            Content = JsonNodeContent(payload)
        };
        request.Headers.Accept.ParseAdd(
            "application/json, text/plain, */*");
        request.Headers.TryAddWithoutValidation("platform", "doramasflix");
        request.Headers.TryAddWithoutValidation("authorization", "Bear");
        request.Headers.TryAddWithoutValidation("x-access-jwt-token", "");
        request.Headers.TryAddWithoutValidation(
            "x-access-platform",
            "RxARncfg1S_MdpSrCvreoLu_SikCGMzE1NzQzODc3NjE2MQ==");
        request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        var responseBytes =
            await HosterExtractorBase.ReadBoundedResponseBytesAsync(
                response.Content,
                GraphQlEndpoint,
                MaximumResponseBytes,
                cancellationToken);
        return JsonDocument.Parse(responseBytes);
    }

    private async Task<string> ReadHtmlAsync(
        Uri endpoint,
        Uri referrer,
        CancellationToken cancellationToken)
    {
        if (!HosterExtractorBase.IsSafePublicHttpUri(endpoint))
        {
            throw new InvalidDataException("Doramasflix returned an unsafe URL.");
        }

        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Referrer = referrer;
        request.Headers.UserAgent.ParseAdd(HosterBrowserHeaders.UserAgent);
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
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
                    "Doramasflix response exceeded 8 MiB.");
            }

            await output.WriteAsync(
                buffer.AsMemory(0, read),
                cancellationToken);
        }

        return Encoding.UTF8.GetString(
            output.GetBuffer(),
            0,
            checked((int)output.Length));
    }

    private CatalogTransferItem CreateMetadata(
        Uri root,
        CatalogTransferItem preview,
        JsonElement? media,
        string html,
        bool isSeries,
        bool requireProviderTitle)
    {
        var detailNames = ReadDetailNames(html);
        var providerName = detailNames.Name ??
            (media is null
                ? null
                : GetString(media.Value, "name"));
        var localizedName = detailNames.LocalizedName ??
            (media is null
                ? null
                : GetString(media.Value, "name_es"));
        var name = !string.IsNullOrWhiteSpace(providerName)
            ? providerName
            : !string.IsNullOrWhiteSpace(localizedName)
                ? localizedName
                : preview.Name;
        var originalName = ReadOriginalTitle(html) ??
            (media is null
                ? null
                : GetString(media.Value, "original_name") ??
                  GetString(media.Value, "original_title"));
        if (requireProviderTitle &&
            string.IsNullOrWhiteSpace(providerName) &&
            string.IsNullOrWhiteSpace(localizedName) &&
            string.IsNullOrWhiteSpace(originalName))
        {
            throw new InvalidDataException(
                "The Doramasflix detail page did not include a title.");
        }

        if (string.Equals(
                name,
                originalName,
                StringComparison.OrdinalIgnoreCase))
        {
            originalName = null;
        }

        var artwork = media is null
            ? null
            : NormalizeArtwork(
                root,
                GetString(media.Value, "poster_path") ??
                GetString(media.Value, "poster"));
        return new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = isSeries ? "series" : "movie",
            Name = name,
            OriginalName = originalName,
            Description = media is null
                ? preview.Description
                : GetString(media.Value, "overview") ??
                  preview.Description,
            PublicationStatus = ReadPublicationStatus(html) ??
                (media is null
                    ? null
                    : GetString(media.Value, "status")) ??
                preview.PublicationStatus,
            OriginalLanguage = "es",
            ThumbnailRef = artwork?.AbsoluteUri ?? preview.ThumbnailRef,
            AlternativeNames = CreateAlternativeNames(
                name,
                preview.AlternativeNames,
                providerName,
                localizedName,
                originalName)
        };
    }

    private void AddSearchResults(
        JsonElement data,
        string property,
        string routePrefix,
        bool isSeries,
        List<ApiContentSearchItem> output)
    {
        if (data.TryGetProperty(property, out var array) &&
            array.ValueKind is JsonValueKind.Array)
        {
            AddArrayResults(array, routePrefix, isSeries, output);
        }
    }

    private void AddArrayResults(
        JsonElement array,
        string routePrefix,
        bool isSeries,
        List<ApiContentSearchItem> output)
    {
        foreach (var item in array.EnumerateArray())
        {
            var slug = GetString(item, "slug");
            var title = BuildTitle(item);
            if (string.IsNullOrWhiteSpace(slug) ||
                string.IsNullOrWhiteSpace(title))
            {
                continue;
            }

            var artwork = NormalizeArtwork(
                new Uri(Descriptor.DefaultBaseUrl),
                GetString(item, "poster_path") ??
                GetString(item, "poster"));
            output.Add(
                new ApiContentSearchItem(
                    routePrefix + slug.Trim('/'),
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = isSeries ? "series" : "movie",
                        Name = title,
                        OriginalLanguage = "es",
                        ThumbnailRef = artwork?.AbsoluteUri,
                        AlternativeNames = CreateAlternativeNames(
                            title,
                            [],
                            GetString(item, "name"),
                            GetString(item, "name_es"),
                            originalName: null)
                    }));
        }
    }

    private Uri? NormalizeArtwork(Uri root, string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        if (value.StartsWith('/'))
        {
            value = "https://image.tmdb.org/t/p/w500" + value;
        }

        if (!Uri.TryCreate(root, value, out var uri) ||
            !HosterExtractorBase.IsSafePublicHttpUri(uri))
        {
            return null;
        }

        var origin = new Uri(
            uri.GetLeftPart(UriPartial.Authority) + "/");
        _artworkOrigins[origin.AbsoluteUri] = origin;
        return uri;
    }

    private static JsonDocument ParseNextData(string html)
    {
        return TryParseNextData(html) ??
            throw new InvalidDataException(
                "Doramasflix did not include its Next.js data.");
    }

    private static JsonDocument? TryParseNextData(string html)
    {
        var match = NextDataRegex.Match(html);
        if (!match.Success)
        {
            return null;
        }

        try
        {
            return JsonDocument.Parse(
                WebUtility.HtmlDecode(match.Groups["value"].Value));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    private static JsonElement? FindApolloMedia(
        JsonElement root,
        IReadOnlyList<string> prefixes)
    {
        var state = FindProperty(root, "apolloState");
        if (state is not { ValueKind: JsonValueKind.Object })
        {
            return null;
        }

        foreach (var property in state.Value.EnumerateObject())
        {
            if (prefixes.Any(prefix =>
                    property.Name.StartsWith(
                        prefix,
                        StringComparison.OrdinalIgnoreCase)) &&
                property.Value.ValueKind is JsonValueKind.Object)
            {
                return property.Value;
            }
        }

        return null;
    }

    private static JsonElement? FindProperty(
        JsonElement element,
        string name)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            if (element.TryGetProperty(name, out var found))
            {
                return found;
            }

            foreach (var property in element.EnumerateObject())
            {
                var nested = FindProperty(property.Value, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                var nested = FindProperty(item, name);
                if (nested is not null)
                {
                    return nested;
                }
            }
        }

        return null;
    }

    private static string? FindString(JsonElement element, string name)
    {
        var found = FindProperty(element, name);
        return found is { ValueKind: JsonValueKind.String }
            ? found.Value.GetString()
            : null;
    }

    private static void CollectLinkObjects(
        JsonElement element,
        List<(string Url, string? Language)> links)
    {
        if (element.ValueKind is JsonValueKind.Object)
        {
            if (element.TryGetProperty("link", out var link) &&
                link.ValueKind is JsonValueKind.String &&
                link.GetString() is { Length: > 0 } value)
            {
                links.Add(
                    (value, GetString(element, "lang")));
            }

            foreach (var property in element.EnumerateObject())
            {
                CollectLinkObjects(property.Value, links);
            }
        }
        else if (element.ValueKind is JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
            {
                CollectLinkObjects(item, links);
            }
        }
    }

    private static bool TryGetData(
        JsonElement root,
        out JsonElement data)
    {
        data = default;
        return root.ValueKind is JsonValueKind.Object &&
               root.TryGetProperty("data", out data) &&
               data.ValueKind is JsonValueKind.Object;
    }

    private static string BuildTitle(JsonElement item)
    {
        var name = GetString(item, "name")?.Trim();
        var spanish = GetString(item, "name_es")?.Trim();
        return !string.IsNullOrWhiteSpace(name)
            ? name
            : spanish ?? string.Empty;
    }

    private static IReadOnlyList<CatalogTransferAlias>
        CreateAlternativeNames(
            string primaryName,
            IEnumerable<CatalogTransferAlias> existing,
            string? providerName,
            string? localizedName,
            string? originalName)
    {
        var aliases = new List<CatalogTransferAlias>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            primaryName.Trim()
        };
        foreach (var alias in existing)
        {
            Add(alias);
        }

        AddValue(providerName, language: null, aliasType: "provider");
        AddValue(localizedName, language: "es", aliasType: "localized");
        AddValue(originalName, language: null, aliasType: "original");
        return aliases;

        void AddValue(
            string? value,
            string? language,
            string aliasType)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            Add(new CatalogTransferAlias
            {
                Name = value.Trim(),
                Language = language,
                AliasType = aliasType
            });
        }

        void Add(CatalogTransferAlias alias)
        {
            if (!string.IsNullOrWhiteSpace(alias.Name) &&
                seen.Add(alias.Name.Trim()))
            {
                aliases.Add(alias);
            }
        }
    }

    private static (string? Name, string? LocalizedName)
        ReadDetailNames(string html)
    {
        var match = DetailNamesRegex.Match(html);
        return match.Success
            ? (
                CleanHtmlText(match.Groups["name"].Value),
                CleanHtmlText(match.Groups["localized"].Value))
            : (null, null);
    }

    private static string? ReadOriginalTitle(string html)
    {
        var match = OriginalTitleRegex.Match(html);
        return match.Success
            ? CleanHtmlText(match.Groups["value"].Value)
            : null;
    }

    private static string? ReadPublicationStatus(string html)
    {
        var match = PublicationStatusRegex.Match(html);
        return match.Success
            ? CleanHtmlText(match.Groups["value"].Value)
            : null;
    }

    private static string? CleanHtmlText(string value)
    {
        var cleaned = WhitespaceRegex.Replace(
                WebUtility.HtmlDecode(
                    HtmlTagRegex.Replace(value, " ")),
                " ")
            .Trim();
        return cleaned.Length == 0 ? null : cleaned;
    }

    private static string? ReadSeriesId(string html)
    {
        const string fieldName = "serie_id";
        var remaining = html.AsSpan();
        while (true)
        {
            var fieldIndex = remaining.IndexOf(
                fieldName,
                StringComparison.OrdinalIgnoreCase);
            if (fieldIndex < 0)
            {
                return null;
            }

            remaining = remaining[(fieldIndex + fieldName.Length)..];
            var colonIndex = remaining.IndexOf(':');
            if (colonIndex < 0)
            {
                return null;
            }

            if (colonIndex > 16)
            {
                continue;
            }

            var value = remaining[(colonIndex + 1)..];
            var valueStart = 0;
            var sawQuote = false;
            while (valueStart < value.Length)
            {
                var character = value[valueStart];
                if (character is '"' or '\'')
                {
                    sawQuote = true;
                    valueStart++;
                    continue;
                }

                if (character == '\\' || char.IsWhiteSpace(character))
                {
                    valueStart++;
                    continue;
                }

                break;
            }

            if (!sawQuote)
            {
                continue;
            }

            var length = 0;
            while (valueStart + length < value.Length &&
                   char.IsAsciiHexDigit(value[valueStart + length]))
            {
                length++;
            }

            if (length == 24)
            {
                return value.Slice(valueStart, length).ToString();
            }
        }
    }

    private static string BuildServerLabel(
        Uri uri,
        string? language)
    {
        var tag = language switch
        {
            "36" => "[ENG]",
            "37" => "[CAST]",
            "38" => "[LAT]",
            "192" => "[SUB]",
            "1327" => "[POR]",
            "13109" => "[COR]",
            "13110" => "[JAP]",
            "13111" => "[MAN]",
            "13112" => "[TAI]",
            "13113" => "[FIL]",
            "13114" => "[IND]",
            "343422" => "[VIET]",
            _ => null
        };
        return string.IsNullOrWhiteSpace(tag)
            ? uri.IdnHost
            : $"{uri.IdnHost} {tag}";
    }

    private static string? GetString(
        JsonElement element,
        string name) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String
            ? value.GetString()
            : null;

    private static int? GetInt32(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetInt32(out var number)
            ? number
            : null;

    private static decimal? GetDecimal(
        JsonElement element,
        string name) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.TryGetDecimal(out var number)
            ? number
            : null;

    private static bool TryReadEpisodeRoute(
        string value,
        out string slug)
    {
        var route = value.Trim().TrimStart('/');
        const string Prefix = "episodios/";
        if (!route.StartsWith(Prefix, StringComparison.OrdinalIgnoreCase))
        {
            slug = string.Empty;
            return false;
        }

        return TryValidateEpisodeSlug(route[Prefix.Length..], out slug);
    }

    private static bool TryValidateEpisodeSlug(
        string value,
        out string slug)
    {
        slug = value.Trim().Trim('/');
        return SafeEpisodeSlugRegex.IsMatch(slug) &&
               !slug.Contains("..", StringComparison.Ordinal);
    }

    private static string ValidateRoute(string value)
    {
        var route = value.Trim().TrimStart('/');
        if ((!route.StartsWith(
                 "doramas-online/",
                 StringComparison.OrdinalIgnoreCase) &&
             !route.StartsWith(
                 "peliculas-online/",
                 StringComparison.OrdinalIgnoreCase)) ||
            route.Length > 1_024 ||
            route.Contains("..", StringComparison.Ordinal) ||
            route.Contains('\\'))
        {
            throw new ArgumentException(
                "The Doramasflix content route is invalid.",
                nameof(value));
        }

        return route;
    }

    private static Uri NormalizeRoot(Uri value)
    {
        ArgumentNullException.ThrowIfNull(value);
        if (!HosterExtractorBase.IsSafePublicHttpUri(value))
        {
            throw new ArgumentException(
                "Provider URLs must use public HTTP or HTTPS.",
                nameof(value));
        }

        return new Uri(
            value.GetLeftPart(UriPartial.Authority) + "/");
    }

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

    private static bool Contains(string value, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            value,
            query.Trim(),
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace) >= 0;

    private static bool ContainsTitle(
        CatalogTransferItem item,
        string query) =>
        Contains(item.Name, query) ||
        item.AlternativeNames.Any(alias => Contains(alias.Name, query));
}

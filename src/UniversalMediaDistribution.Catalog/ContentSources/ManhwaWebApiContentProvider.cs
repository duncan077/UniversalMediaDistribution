using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads ManhwaWeb's public catalog and reader API. The API deployment URL is
/// discovered from the site's current application script and cached for one day.
/// </summary>
public sealed partial class ManhwaWebApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "manhwaweb";

    private const int MaximumHomepageBytes = 2 * 1024 * 1024;
    private const int MaximumScriptBytes = 5 * 1024 * 1024;
    private const int MaximumSearchBytes = 8 * 1024 * 1024;
    private const int MaximumItemBytes = 24 * 1024 * 1024;
    private const int MaximumReaderBytes = 24 * 1024 * 1024;
    private const int MaximumSearchResults = 500;
    private const int MaximumDailyUpdateResults = 24;
    private const int MaximumSearchPages = 50;
    private const int MaximumChapterCount = 20_000;
    private const int MaximumPageCount = 2_000;

    private static readonly TimeSpan ApiEndpointCacheLifetime =
        TimeSpan.FromDays(1);
    private static readonly TimeSpan RegexTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly Regex ScriptSourceRegex = new(
        """
        <script\b[^>]*\bsrc\s*=\s*(?:"(?<double>[^"]+)"|'(?<single>[^']+)')
        """,
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex AssignedUrlRegex = new(
        """
        (?:(?:const|let|var)\s+)?(?<name>[A-Za-z_$][A-Za-z0-9_$]*)\s*=\s*(?:"(?<double>https://[^"]+)"|'(?<single>https://[^']+)')
        """,
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex AbsoluteUrlRegex = new(
        """https://[A-Za-z0-9.-]+(?::\d{1,5})?""",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex SafeExternalIdRegex = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}_.-]{0,399}$",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex SafeChapterRouteRegex = new(
        @"^[\p{L}\p{N}][\p{L}\p{N}_.-]{0,499}$",
        RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex ChapterNumberRegex = new(
        @"\d+(?:[.,]\d+)?",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _endpointCacheGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _chapterRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);
    private ApiEndpointCacheEntry? _endpointCache;

    public ManhwaWebApiContentProvider(
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.ManhwaWeb;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = NormalizeRootUri(baseUri);
        var cached = Volatile.Read(ref _endpointCache);
        var origins = _trustedArtworkOrigins.Values
            .Prepend(GetOrigin(root));
        if (cached is not null &&
            SameOrigin(cached.SiteRoot, root))
        {
            origins = origins.Append(GetOrigin(cached.ApiRoot));
        }

        return origins
            .DistinctBy(
                static uri =>
                    $"{uri.Scheme}://{uri.IdnHost}:{uri.Port}",
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(artworkUri);
        return artworkUri.IsAbsoluteUri &&
               GetTrustedArtworkOrigins(root).Any(origin =>
                   SameOrigin(origin, artworkUri))
            ? root
            : null;
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        LoadDailyUpdatesAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        var json = await ReadApiStringAsync(
            root,
            "latest/new-manhwa",
            MaximumSearchBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!TryGetProperty(
                document.RootElement,
                "manhwas",
                JsonValueKind.Object,
                out var groups) ||
            !TryGetProperty(
                groups,
                "manhwas_esp",
                JsonValueKind.Array,
                out var updates))
        {
            throw new InvalidDataException(
                "ManhwaWeb returned an invalid daily-update response.");
        }

        var entries = new List<DailyUpdateEntry>();
        foreach (var element in updates.EnumerateArray())
        {
            if (element.ValueKind is not JsonValueKind.Object ||
                !TryGetString(element, "name_manhwa", out var rawName) ||
                !TryGetScalarString(element, "chapter", out var rawChapter))
            {
                continue;
            }

            var externalId =
                GetOptionalString(element, "id_rel") ??
                GetOptionalString(element, "id_manhwa") ??
                string.Empty;
            var name = NormalizeText(rawName);
            var displayNumber =
                NormalizeChapterDisplayValue(rawChapter);
            if (!SafeExternalIdRegex.IsMatch(externalId) ||
                name.Length == 0 ||
                displayNumber.Length == 0)
            {
                continue;
            }

            entries.Add(new DailyUpdateEntry(
                externalId,
                name,
                GetArtworkReference(root, element, "img"),
                BuildCategories(element),
                ResolveSubtype(element),
                displayNumber,
                TryGetInt64(element, "create", out var createdAt)
                    ? createdAt
                    : 0));
        }

        return entries
            .OrderByDescending(static entry => entry.CreatedAt)
            .GroupBy(
                static entry => entry.ExternalId,
                StringComparer.Ordinal)
            .Take(MaximumDailyUpdateResults)
            .Select(group =>
            {
                var latest = group.First();
                var units = group
                    .GroupBy(
                        static entry =>
                            NormalizeChapterKey(
                                entry.DisplayNumber),
                        StringComparer.OrdinalIgnoreCase)
                    .Select(static values => values.First())
                    .OrderBy(static entry =>
                        ParseChapterNumber(entry.DisplayNumber))
                    .ThenBy(
                        static entry => entry.DisplayNumber,
                        StringComparer.OrdinalIgnoreCase)
                    .Select(static (entry, index) =>
                        new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = entry.DisplayNumber,
                            SortOrder = index,
                            ReleaseDate =
                                FormatUnixDate(entry.CreatedAt)
                        })
                    .ToArray();
                return new ApiContentSearchItem(
                    latest.ExternalId,
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = latest.Subtype,
                        Name = latest.Name,
                        ThumbnailRef = latest.ThumbnailRef,
                        Categories = latest.Categories,
                        Units = units
                    });
            })
            .ToArray();
    }

    /// <summary>
    /// The application bundle publishes the labels for the library endpoint's
    /// opaque numeric "generes" IDs. They are parsed from the same script that
    /// is already downloaded to discover the API root, so genre filtering is
    /// native without another request or a hardcoded ID table. No original
    /// release year is exposed.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.Native,
        supportsFilterOnlySearch: true);

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (mediaType is not null and not MediaType.Book)
        {
            return [];
        }

        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var page = 0;
        var hasNext = true;
        while (hasNext &&
               page < MaximumSearchPages &&
               results.Count < MaximumSearchResults)
        {
            var libraryPage = await ReadLibraryPageAsync(
                root,
                query.Trim(),
                genreToken: null,
                page,
                seen,
                cancellationToken);
            foreach (var item in libraryPage.Items)
            {
                if (results.Count >= MaximumSearchResults)
                {
                    break;
                }

                results.Add(item);
            }

            hasNext = libraryPage.HasNext;
            page++;
        }

        return results;
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Book ||
            request.ReleaseYear is not null)
        {
            return ApiContentSearchPage.Empty;
        }

        if (request.Genre is null)
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

        var endpoint = await GetApiEndpointAsync(
            root,
            forceRefresh: false,
            cancellationToken);
        if (!endpoint.Genres.Map.TryGetToken(
                request.Genre,
                out var genreToken))
        {
            return ApiContentSearchPage.Empty;
        }

        var page = ParseLibraryPageToken(request.ContinuationToken);
        var libraryPage = await ReadLibraryPageAsync(
            root,
            request.Query,
            genreToken,
            page,
            new HashSet<string>(StringComparer.Ordinal),
            cancellationToken);
        var nextPage = page + 1;
        var canContinue =
            libraryPage.HasNext && nextPage < MaximumSearchPages;
        return new ApiContentSearchPage(
            libraryPage.Items,
            canContinue
                ? nextPage.ToString(CultureInfo.InvariantCulture)
                : null,
            isTruncated: libraryPage.HasNext && !canContinue);
    }

    private async ValueTask<LibrarySearchPage> ReadLibraryPageAsync(
        Uri root,
        string? query,
        string? genreToken,
        int page,
        HashSet<string> seen,
        CancellationToken cancellationToken)
    {
        var relative =
            "manhwa/library" +
            $"?buscar={Uri.EscapeDataString(query?.Trim() ?? string.Empty)}" +
            "&estado=&tipo=&erotico=&demografia=" +
            "&order_item=alfabetico&order_dir=desc" +
            $"&page={page.ToString(CultureInfo.InvariantCulture)}" +
            $"&generes={Uri.EscapeDataString(genreToken ?? string.Empty)}";
        var json = await ReadApiStringAsync(
            root,
            relative,
            MaximumSearchBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!TryGetProperty(
                document.RootElement,
                "data",
                JsonValueKind.Array,
                out var data))
        {
            throw new InvalidDataException(
                "ManhwaWeb returned an invalid search response.");
        }

        var cached = Volatile.Read(ref _endpointCache);
        var genreCatalog = cached is not null &&
                           SameOrigin(cached.SiteRoot, root)
            ? cached.Genres
            : ManhwaGenreCatalog.Empty;
        var results = new List<ApiContentSearchItem>();
        foreach (var element in data.EnumerateArray())
        {
            if (results.Count >= MaximumSearchResults ||
                !TryGetString(element, "_id", out var externalId) ||
                !SafeExternalIdRegex.IsMatch(externalId) ||
                !seen.Add(externalId))
            {
                continue;
            }

            var names = GetTitleNames(element);
            if (names.Count == 0 ||
                query is not null &&
                !names.Any(name => Contains(name, query)))
            {
                continue;
            }

            var canonicalName = ChooseCanonicalName(element, names);
            results.Add(new ApiContentSearchItem(
                externalId,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = ResolveSubtype(element),
                    Name = canonicalName,
                    ThumbnailRef =
                        GetArtworkReference(root, element, "_imagen"),
                    Genres = ExtractGenres(element, genreCatalog),
                    Categories = BuildCategories(element),
                    AlternativeNames = CreateAliases(names, canonicalName)
                }));
        }

        var hasNext =
            document.RootElement.TryGetProperty("next", out var next) &&
            next.ValueKind is JsonValueKind.True;
        return new LibrarySearchPage(results, hasNext);
    }

    private static int ParseLibraryPageToken(string? token)
    {
        if (token is null)
        {
            return 0;
        }

        if (!int.TryParse(
                token,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var page) ||
            page is < 0 or >= MaximumSearchPages)
        {
            throw new ArgumentException(
                "ManhwaWeb returned an invalid library page token.",
                nameof(token));
        }

        return page;
    }

    public async ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        var externalId = ValidateExternalId(searchItem.ExternalId);
        var json = await ReadApiStringAsync(
            root,
            $"manhwa/see/{Uri.EscapeDataString(externalId)}",
            MaximumItemBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var detail = document.RootElement;
        if (TryGetProperty(
                detail,
                "data",
                JsonValueKind.Object,
                out var wrapped))
        {
            detail = wrapped;
        }

        if (detail.ValueKind is not JsonValueKind.Object)
        {
            throw new InvalidDataException(
                "ManhwaWeb returned an invalid title response.");
        }

        var names = GetTitleNames(detail);
        if (searchItem.RequireProviderTitle && names.Count == 0)
        {
            throw new InvalidDataException(
                "The ManhwaWeb title did not include a name.");
        }

        var name = names.Count > 0
            ? ChooseCanonicalName(detail, names)
            : searchItem.PreviewItem.Name;
        if (string.IsNullOrWhiteSpace(name))
        {
            throw new InvalidDataException(
                "The ManhwaWeb title did not include a name.");
        }

        var description =
            TryGetString(detail, "_sinopsis", out var synopsis)
                ? NormalizeText(synopsis)
                : string.Empty;
        var thumbnail = GetArtworkReference(root, detail, "_imagen") ??
            searchItem.PreviewItem.ThumbnailRef;
        var status =
            TryGetString(detail, "_status", out var statusValue)
                ? MapPublicationStatus(statusValue)
                : "unknown";
        var units = TryUseKnownChapterRoute(
            root,
            externalId,
            searchItem.UnitRoutes,
            out var knownUnits)
            ? knownUnits
            : ExtractChapters(
                root,
                externalId,
                detail);
        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = ResolveSubtype(detail, searchItem.PreviewItem.Subtype),
            Name = name,
            OriginalName = GetOriginalName(detail, name),
            Description = description.Length == 0 ? null : description,
            PublicationStatus = status,
            ThumbnailRef = thumbnail,
            Genres = ExtractGenres(detail),
            Categories = BuildCategories(detail, searchItem.PreviewItem.Subtype),
            AlternativeNames = CreateAliases(names, name),
            Units = units
        });
    }

    public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit)
    {
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Chapter ||
            unit.SeasonNumber is not null)
        {
            return [];
        }

        var root = NormalizeRootUri(baseUri);
        var externalId = ValidateExternalId(searchItem.ExternalId);
        return _chapterRoutes.TryGetValue(
                   BuildChapterKey(
                       root,
                       externalId,
                       unit.DisplayNumber),
                   out var chapterRoute) &&
               SafeChapterRouteRegex.IsMatch(chapterRoute)
            ?
            [
                new ApiProviderUnitRoute(
                    ContentUnitKind.Chapter,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    chapterRoute)
            ]
            : [];
    }

    public async ValueTask<ApiContentImageSet?> LoadUnitImagesAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Chapter ||
            unit.SeasonNumber is not null)
        {
            return null;
        }

        var externalId = ValidateExternalId(searchItem.ExternalId);
        var key = BuildChapterKey(root, externalId, unit.DisplayNumber);
        if (!_chapterRoutes.TryGetValue(key, out var chapterRoute))
        {
            _ = await LoadItemAsync(root, searchItem, cancellationToken);
            if (!_chapterRoutes.TryGetValue(key, out chapterRoute))
            {
                return null;
            }
        }

        chapterRoute = ValidateChapterRoute(chapterRoute);
        var json = await ReadApiStringAsync(
            root,
            $"chapters/see/{Uri.EscapeDataString(chapterRoute)}",
            MaximumReaderBytes,
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        var reader = document.RootElement;
        if (TryGetProperty(
                reader,
                "data",
                JsonValueKind.Object,
                out var wrapped))
        {
            reader = wrapped;
        }

        if (!TryGetProperty(
                reader,
                "chapter",
                JsonValueKind.Object,
                out var chapter) ||
            !TryGetProperty(
                chapter,
                "img",
                JsonValueKind.Array,
                out var pages))
        {
            throw new InvalidDataException(
                "The ManhwaWeb chapter did not contain reader pages.");
        }

        var imageUris = new List<Uri>();
        foreach (var page in pages.EnumerateArray())
        {
            if (imageUris.Count >= MaximumPageCount)
            {
                throw new InvalidDataException(
                    "The ManhwaWeb chapter contains too many reader pages.");
            }

            if (page.ValueKind is not JsonValueKind.String ||
                !TryParseHttpUri(page.GetString(), out var uri))
            {
                throw new InvalidDataException(
                    "The ManhwaWeb chapter contained an invalid reader-page URL.");
            }

            imageUris.Add(uri);
            AddTrustedArtworkOrigin(uri);
        }

        if (imageUris.Count == 0)
        {
            throw new InvalidDataException(
                "The ManhwaWeb chapter did not contain any reader pages.");
        }

        return new ApiContentImageSet(
            ContentUnitKind.Chapter,
            unit.DisplayNumber,
            seasonNumber: null,
            imageUris.Select(
                (uri, index) =>
                    new ApiContentImagePage(index, uri, root)));
    }

    private IReadOnlyList<CatalogTransferUnit> ExtractChapters(
        Uri siteRoot,
        string externalId,
        JsonElement detail)
    {
        var chapters = new Dictionary<string, ChapterEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var propertyName in new[]
                 {
                     "chapters",
                     "chapters_esp",
                     "chapters_raw",
                     "_chapters"
                 })
        {
            if (!TryGetProperty(
                    detail,
                    propertyName,
                    JsonValueKind.Array,
                    out var values))
            {
                continue;
            }

            foreach (var element in values.EnumerateArray())
            {
                if (chapters.Count >= MaximumChapterCount)
                {
                    throw new InvalidDataException(
                        "The ManhwaWeb title contains too many chapters.");
                }

                if (!TryGetScalarString(
                        element,
                        "chapter",
                        out var rawNumber))
                {
                    continue;
                }

                var displayNumber =
                    NormalizeChapterDisplayValue(rawNumber);
                if (displayNumber.Length == 0 ||
                    !TryGetString(element, "link", out var link) ||
                    !TryParseChapterRoute(
                        siteRoot,
                        link,
                        out var chapterRoute))
                {
                    continue;
                }

                var timestamp = TryGetInt64(
                    element,
                    "create",
                    out var create)
                    ? create
                    : 0;
                var thumbnail = TryGetFirstArtworkReference(
                    siteRoot,
                    element,
                    "img");
                var normalized = NormalizeChapterKey(displayNumber);
                var candidate = new ChapterEntry(
                    displayNumber,
                    chapterRoute,
                    thumbnail,
                    timestamp);
                if (!chapters.TryGetValue(normalized, out var existing) ||
                    candidate.CreatedAt > existing.CreatedAt)
                {
                    chapters[normalized] = candidate;
                }
            }
        }

        var ordered = chapters.Values
            .OrderBy(static entry =>
                ParseChapterNumber(entry.DisplayNumber))
            .ThenBy(
                static entry => entry.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var chapter = ordered[index];
            _chapterRoutes[BuildChapterKey(
                siteRoot,
                externalId,
                chapter.DisplayNumber)] = chapter.Route;
        }

        return ordered
            .Select(static (entry, index) =>
                new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = entry.DisplayNumber,
                    SortOrder = index,
                    ThumbnailRef = entry.Thumbnail,
                    ReleaseDate = FormatUnixDate(entry.CreatedAt)
                })
            .ToArray();
    }

    private bool TryUseKnownChapterRoute(
        Uri root,
        string externalId,
        IReadOnlyList<ApiProviderUnitRoute> routeHints,
        out IReadOnlyList<CatalogTransferUnit> units)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Chapter ||
                hint.SeasonNumber is not null ||
                hint.DisplayNumber is not { } displayNumber ||
                !SafeChapterRouteRegex.IsMatch(hint.Route))
            {
                continue;
            }

            _chapterRoutes[
                BuildChapterKey(root, externalId, displayNumber)] = hint.Route;
            units =
            [
                new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = displayNumber,
                    SortOrder = 0
                }
            ];
            return true;
        }

        units = [];
        return false;
    }

    private async ValueTask<string> ReadApiStringAsync(
        Uri siteRoot,
        string relativePathAndQuery,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        var endpoint = await GetApiEndpointAsync(
            siteRoot,
            forceRefresh: false,
            cancellationToken);
        try
        {
            return await ReadStringAsync(
                new Uri(endpoint.ApiRoot, relativePathAndQuery),
                siteRoot,
                "application/json",
                maximumBytes,
                cancellationToken);
        }
        catch (HttpRequestException)
        {
            var refreshed = await GetApiEndpointAsync(
                siteRoot,
                forceRefresh: true,
                cancellationToken);
            if (SameOrigin(endpoint.ApiRoot, refreshed.ApiRoot))
            {
                throw;
            }

            return await ReadStringAsync(
                new Uri(refreshed.ApiRoot, relativePathAndQuery),
                siteRoot,
                "application/json",
                maximumBytes,
                cancellationToken);
        }
    }

    private async ValueTask<ApiEndpointCacheEntry> GetApiEndpointAsync(
        Uri siteRoot,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var cached = Volatile.Read(ref _endpointCache);
        if (!forceRefresh &&
            IsFreshEndpoint(cached, siteRoot, now))
        {
            return cached!;
        }

        await _endpointCacheGate.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            cached = Volatile.Read(ref _endpointCache);
            if (!forceRefresh &&
                IsFreshEndpoint(cached, siteRoot, now))
            {
                return cached!;
            }

            try
            {
                var html = await ReadStringAsync(
                    siteRoot,
                    siteRoot,
                    "text/html",
                    MaximumHomepageBytes,
                    cancellationToken);
                var scriptUri = FindApplicationScriptUri(
                    html,
                    siteRoot);
                var script = await ReadStringAsync(
                    scriptUri,
                    siteRoot,
                    "application/javascript, text/javascript, */*;q=0.1",
                    MaximumScriptBytes,
                    cancellationToken);
                var apiRoot = FindApiRoot(script, siteRoot);
                var entry = new ApiEndpointCacheEntry(
                    siteRoot,
                    scriptUri,
                    now,
                    apiRoot,
                    ParseGenreCatalog(script));
                Volatile.Write(ref _endpointCache, entry);
                return entry;
            }
            catch (Exception exception)
                when (!forceRefresh &&
                      exception is not OperationCanceledException &&
                      cached is not null &&
                      SameOrigin(cached.SiteRoot, siteRoot))
            {
                return cached;
            }
        }
        finally
        {
            _endpointCacheGate.Release();
        }
    }

    private async ValueTask<string> ReadStringAsync(
        Uri endpoint,
        Uri siteRoot,
        string accept,
        int maximumBytes,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd(accept);
        request.Headers.UserAgent.ParseAdd(
            "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)");
        request.Headers.Referrer = siteRoot;
        request.Headers.TryAddWithoutValidation(
            "Origin",
            GetOrigin(siteRoot).AbsoluteUri.TrimEnd('/'));
        using var response = await ApiProviderHttp.SendAsync(
            _httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength > maximumBytes)
        {
            throw new InvalidDataException(
                $"ManhwaWeb response exceeds the {maximumBytes / 1024} KiB limit.");
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
                    $"ManhwaWeb response exceeds the {maximumBytes / 1024} KiB limit.");
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

    private string? GetArtworkReference(
        Uri root,
        JsonElement element,
        string propertyName)
    {
        if (!TryGetString(element, propertyName, out var value) ||
            !TryResolveHttpUri(root, value, out var uri))
        {
            return null;
        }

        AddTrustedArtworkOrigin(uri);
        return uri.AbsoluteUri;
    }

    private string? TryGetFirstArtworkReference(
        Uri root,
        JsonElement element,
        string propertyName)
    {
        if (!TryGetProperty(
                element,
                propertyName,
                JsonValueKind.Array,
                out var values))
        {
            return null;
        }

        foreach (var value in values.EnumerateArray())
        {
            if (value.ValueKind is JsonValueKind.String &&
                TryResolveHttpUri(root, value.GetString(), out var uri))
            {
                AddTrustedArtworkOrigin(uri);
                return uri.AbsoluteUri;
            }
        }

        return null;
    }

    private void AddTrustedArtworkOrigin(Uri uri)
    {
        var origin = GetOrigin(uri);
        _trustedArtworkOrigins.TryAdd(origin.AbsoluteUri, origin);
    }

    private static Uri FindApplicationScriptUri(
        string html,
        Uri siteRoot)
    {
        foreach (Match match in ScriptSourceRegex.Matches(html))
        {
            var source = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Value;
            if (!source.Contains("/assets/index-", StringComparison.Ordinal) ||
                !source.Contains(".js", StringComparison.OrdinalIgnoreCase) ||
                !TryResolveHttpUri(siteRoot, source, out var scriptUri) ||
                !SameOrigin(scriptUri, siteRoot))
            {
                continue;
            }

            return scriptUri;
        }

        throw new InvalidDataException(
            "The current ManhwaWeb application script could not be found.");
    }

    private static Uri FindApiRoot(string script, Uri siteRoot)
    {
        var candidates = new List<ApiRootCandidate>();
        foreach (Match match in AssignedUrlRegex.Matches(script))
        {
            var name = match.Groups["name"].Value;
            var value = match.Groups["double"].Success
                ? match.Groups["double"].Value
                : match.Groups["single"].Value;
            if (!TryParseHttpUri(value, out var uri) ||
                SameOrigin(uri, siteRoot))
            {
                continue;
            }

            var score = ScoreApiCandidate(script, name, uri);
            candidates.Add(new ApiRootCandidate(
                NormalizeRootUri(uri),
                score));
        }

        foreach (Match match in AbsoluteUrlRegex.Matches(script))
        {
            if (!TryParseHttpUri(match.Value, out var uri) ||
                SameOrigin(uri, siteRoot))
            {
                continue;
            }

            var score = ScoreApiCandidate(script, string.Empty, uri);
            candidates.Add(new ApiRootCandidate(
                NormalizeRootUri(uri),
                score));
        }

        var best = candidates
            .OrderByDescending(static candidate => candidate.Score)
            .ThenBy(
                static candidate => candidate.Uri.AbsoluteUri,
                StringComparer.Ordinal)
            .FirstOrDefault();
        if (best is null || best.Score < 10)
        {
            throw new InvalidDataException(
                "The current ManhwaWeb API endpoint could not be found in its application script.");
        }

        return best.Uri;
    }

    /// <summary>
    /// Reads compact object literals such as
    /// <c>{id:3,nombre:"Acción"}</c> directly from the application bundle.
    /// The scanner works on spans and allocates only recognized genre labels;
    /// scanning a multi-megabyte minified script therefore does not create a
    /// substring for every object or property.
    /// </summary>
    private static ManhwaGenreCatalog ParseGenreCatalog(string script)
    {
        var source = script.AsSpan();
        var labelsById = new Dictionary<int, string>();
        var entries = new List<(string Label, string Token)>();
        const string propertyName = "nombre";
        var searchAt = 0;
        while (searchAt < source.Length)
        {
            var relative = source[searchAt..].IndexOf(
                propertyName,
                StringComparison.Ordinal);
            if (relative < 0)
            {
                break;
            }

            var nameIndex = searchAt + relative;
            var windowStart = Math.Max(0, nameIndex - 512);
            var objectStartRelative = source[windowStart..nameIndex]
                .LastIndexOf('{');
            var objectEndRelative = source[nameIndex..].IndexOf('}');
            searchAt = nameIndex + propertyName.Length;
            if (objectStartRelative < 0 ||
                objectEndRelative < 0 ||
                objectEndRelative > 1_024)
            {
                continue;
            }

            var objectStart = windowStart + objectStartRelative;
            var objectEnd = nameIndex + objectEndRelative + 1;
            var objectLiteral = source[objectStart..objectEnd];
            if (!TryReadJavaScriptIntProperty(
                    objectLiteral,
                    "id",
                    out var id) ||
                id <= 0 ||
                !TryReadJavaScriptStringProperty(
                    objectLiteral,
                    propertyName,
                    out var rawLabel))
            {
                continue;
            }

            var label = NormalizeText(rawLabel);
            if (label.Length == 0 ||
                !ApiContentGenre.All.Any(genre =>
                    genre.MatchesLabel(label)) ||
                !labelsById.TryAdd(id, label))
            {
                continue;
            }

            entries.Add((
                label,
                id.ToString(CultureInfo.InvariantCulture)));
        }

        return new ManhwaGenreCatalog(
            ApiContentGenreMap.FromUpstreamLabels(entries),
            labelsById);
    }

    private static bool TryReadJavaScriptIntProperty(
        ReadOnlySpan<char> objectLiteral,
        ReadOnlySpan<char> propertyName,
        out int value)
    {
        value = 0;
        return TryReadJavaScriptProperty(
                   objectLiteral,
                   propertyName,
                   out var rawValue,
                   out _) &&
               int.TryParse(
                   rawValue,
                   NumberStyles.Integer,
                   CultureInfo.InvariantCulture,
                   out value);
    }

    private static bool TryReadJavaScriptStringProperty(
        ReadOnlySpan<char> objectLiteral,
        ReadOnlySpan<char> propertyName,
        out string value)
    {
        value = string.Empty;
        if (!TryReadJavaScriptProperty(
                objectLiteral,
                propertyName,
                out var rawValue,
                out var quote) ||
            quote == '\0')
        {
            return false;
        }

        value = DecodeJavaScriptString(rawValue);
        return value.Length > 0;
    }

    private static bool TryReadJavaScriptProperty(
        ReadOnlySpan<char> objectLiteral,
        ReadOnlySpan<char> propertyName,
        out ReadOnlySpan<char> value,
        out char quote)
    {
        value = default;
        quote = '\0';
        var searchAt = 0;
        while (searchAt < objectLiteral.Length)
        {
            var relative = objectLiteral[searchAt..].IndexOf(
                propertyName,
                StringComparison.Ordinal);
            if (relative < 0)
            {
                return false;
            }

            var nameStart = searchAt + relative;
            var cursor = nameStart + propertyName.Length;
            if (nameStart > 0 &&
                IsJavaScriptIdentifierCharacter(
                    objectLiteral[nameStart - 1]) ||
                cursor < objectLiteral.Length &&
                IsJavaScriptIdentifierCharacter(objectLiteral[cursor]))
            {
                searchAt = cursor;
                continue;
            }

            if (cursor < objectLiteral.Length &&
                objectLiteral[cursor] is '"' or '\'' &&
                nameStart > 0 &&
                objectLiteral[nameStart - 1] == objectLiteral[cursor])
            {
                cursor++;
            }

            while (cursor < objectLiteral.Length &&
                   char.IsWhiteSpace(objectLiteral[cursor]))
            {
                cursor++;
            }

            if (cursor >= objectLiteral.Length ||
                objectLiteral[cursor] != ':')
            {
                searchAt = cursor;
                continue;
            }

            cursor++;
            while (cursor < objectLiteral.Length &&
                   char.IsWhiteSpace(objectLiteral[cursor]))
            {
                cursor++;
            }

            if (cursor >= objectLiteral.Length)
            {
                return false;
            }

            if (objectLiteral[cursor] is '"' or '\'')
            {
                quote = objectLiteral[cursor++];
                var valueStart = cursor;
                var escaped = false;
                while (cursor < objectLiteral.Length)
                {
                    var character = objectLiteral[cursor];
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
                        value = objectLiteral[valueStart..cursor];
                        return true;
                    }

                    cursor++;
                }

                return false;
            }

            var scalarStart = cursor;
            while (cursor < objectLiteral.Length &&
                   objectLiteral[cursor] is not (',' or '}') &&
                   !char.IsWhiteSpace(objectLiteral[cursor]))
            {
                cursor++;
            }

            value = objectLiteral[scalarStart..cursor];
            return !value.IsEmpty;
        }

        return false;
    }

    private static bool IsJavaScriptIdentifierCharacter(char character) =>
        char.IsAsciiLetterOrDigit(character) ||
        character is '_' or '$';

    private static string DecodeJavaScriptString(ReadOnlySpan<char> value)
    {
        if (value.IndexOf('\\') < 0)
        {
            return value.ToString();
        }

        var builder = new StringBuilder(value.Length);
        for (var index = 0; index < value.Length; index++)
        {
            var character = value[index];
            if (character != '\\' || index + 1 >= value.Length)
            {
                builder.Append(character);
                continue;
            }

            var escaped = value[++index];
            if (escaped is 'u' or 'x')
            {
                var digitCount = escaped == 'u' ? 4 : 2;
                if (index + digitCount < value.Length &&
                    int.TryParse(
                        value.Slice(index + 1, digitCount),
                        NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture,
                        out var codePoint))
                {
                    builder.Append((char)codePoint);
                    index += digitCount;
                    continue;
                }
            }

            builder.Append(escaped switch
            {
                'n' => '\n',
                'r' => '\r',
                't' => '\t',
                'b' => '\b',
                'f' => '\f',
                _ => escaped
            });
        }

        return builder.ToString();
    }

    private static int ScoreApiCandidate(
        string script,
        string variableName,
        Uri uri)
    {
        var score = 0;
        if (uri.IdnHost.Contains(
                "manhwawebbackend",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 100;
        }

        if (uri.IdnHost.EndsWith(
                ".railway.app",
                StringComparison.OrdinalIgnoreCase))
        {
            score += 40;
        }

        if (!string.IsNullOrWhiteSpace(variableName))
        {
            foreach (var endpoint in new[]
                     {
                         "manhwa/library",
                         "manhwa/see",
                         "chapters/see"
                     })
            {
                if (script.Contains(
                        $"${{{variableName}}}/{endpoint}",
                        StringComparison.Ordinal) ||
                    script.Contains(
                        $"{variableName}+\"/{endpoint}",
                        StringComparison.Ordinal) ||
                    script.Contains(
                        $"{variableName}+'/{endpoint}",
                        StringComparison.Ordinal))
                {
                    score += 20;
                }
            }
        }

        return score;
    }

    private static IReadOnlyList<string> GetTitleNames(
        JsonElement element) =>
        new[]
        {
            GetOptionalString(element, "name_esp"),
            GetOptionalString(element, "the_real_name"),
            GetOptionalString(element, "name_raw")
        }
        .Where(static value =>
            !string.IsNullOrWhiteSpace(value) &&
            !string.Equals(
                value,
                "null",
                StringComparison.OrdinalIgnoreCase) &&
            !string.Equals(
                value,
                "no",
                StringComparison.OrdinalIgnoreCase))
        .Select(static value => NormalizeText(value))
        .Where(CatalogTransferIdentity.IsUsableName)
        .Distinct(StringComparer.CurrentCultureIgnoreCase)
        .ToArray();

    private static string ChooseCanonicalName(
        JsonElement element,
        IReadOnlyList<string> names)
    {
        var spanish = GetOptionalString(element, "name_esp");
        if (CatalogTransferIdentity.IsUsableName(spanish) &&
            !string.Equals(
                spanish,
                "no",
                StringComparison.OrdinalIgnoreCase))
        {
            return NormalizeText(spanish);
        }

        return names[0];
    }

    private static string? GetOriginalName(
        JsonElement element,
        string canonicalName)
    {
        var value = GetOptionalString(element, "the_real_name");
        return !CatalogTransferIdentity.IsUsableName(value) ||
               string.Equals(
                   NormalizeText(value),
                   canonicalName,
                   StringComparison.CurrentCultureIgnoreCase)
            ? null
            : NormalizeText(value);
    }

    private static IReadOnlyList<CatalogTransferAlias> CreateAliases(
        IEnumerable<string> names,
        string canonicalName) =>
        names
            .Where(name => !string.Equals(
                name,
                canonicalName,
                StringComparison.CurrentCultureIgnoreCase))
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .Select(static name => new CatalogTransferAlias
            {
                Name = name,
                AliasType = "provider"
            })
            .ToArray();

    private static IReadOnlyList<string> ExtractGenres(
        JsonElement element,
        ManhwaGenreCatalog? catalog = null)
    {
        if (!TryGetProperty(
                element,
                "_categoris",
                JsonValueKind.Array,
                out var categories))
        {
            return [];
        }

        var genres = new List<string>();
        var seen = new HashSet<string>(
            StringComparer.CurrentCultureIgnoreCase);
        foreach (var category in categories.EnumerateArray())
        {
            if (category.ValueKind is JsonValueKind.Number &&
                category.TryGetInt32(out var numericId) &&
                catalog?.LabelsById.TryGetValue(
                    numericId,
                    out var numericLabel) is true)
            {
                AddGenre(numericLabel);
                continue;
            }

            if (category.ValueKind is JsonValueKind.String)
            {
                var raw = category.GetString();
                if (int.TryParse(
                        raw,
                        NumberStyles.Integer,
                        CultureInfo.InvariantCulture,
                        out numericId) &&
                    catalog?.LabelsById.TryGetValue(
                        numericId,
                        out var stringLabel) is true)
                {
                    AddGenre(stringLabel);
                }
                else
                {
                    AddGenre(raw);
                }

                continue;
            }

            if (category.ValueKind is not JsonValueKind.Object)
            {
                continue;
            }

            foreach (var property in category.EnumerateObject())
            {
                if (property.Value.ValueKind is not JsonValueKind.String)
                {
                    continue;
                }

                AddGenre(property.Value.GetString());
            }
        }

        return genres;

        void AddGenre(string? value)
        {
            var genre = NormalizeText(value);
            if (genre.Length > 0 && seen.Add(genre))
            {
                genres.Add(genre);
            }
        }
    }

    private static string ResolveSubtype(JsonElement element, string fallback = "manga")
    {
        var type = RemoveDiacritics(GetOptionalString(element, "_tipo") ?? string.Empty)
            .Trim().ToLowerInvariant().Replace('_', ' ').Replace('-', ' ');
        return type switch
        {
            "novel" or "novela" => "novel",
            "light novel" or "novela ligera" => "light_novel",
            "web novel" or "novela web" => "web_novel",
            "comic" => "comic",
            "manga" or "manhwa" or "manhua" => "manga",
            _ => fallback
        };
    }

    private static IReadOnlyList<string> BuildCategories(
        JsonElement element, string fallback = "manga")
    {
        var categories = new List<string>();
        if (ResolveSubtype(element, fallback) == "manga") categories.Add("Manga");
        AddCategory(GetOptionalString(element, "_tipo"));
        AddCategory(GetOptionalString(element, "_demografi"));
        return categories;

        void AddCategory(string? value)
        {
            var normalized = NormalizeText(value);
            if (normalized.Length == 0 ||
                string.Equals(
                    normalized,
                    "no",
                    StringComparison.OrdinalIgnoreCase) ||
                categories.Contains(
                    normalized,
                    StringComparer.CurrentCultureIgnoreCase))
            {
                return;
            }

            categories.Add(
                CultureInfo.CurrentCulture.TextInfo.ToTitleCase(
                    normalized.ToLower(
                        CultureInfo.CurrentCulture)));
        }
    }

    private static string MapPublicationStatus(string value)
    {
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        if (normalized.Contains("public", StringComparison.Ordinal) ||
            normalized.Contains("emision", StringComparison.Ordinal) ||
            normalized.Contains("ongoing", StringComparison.Ordinal))
        {
            return "ongoing";
        }

        if (normalized.Contains("final", StringComparison.Ordinal) ||
            normalized.Contains("complet", StringComparison.Ordinal))
        {
            return "completed";
        }

        if (normalized.Contains("cancel", StringComparison.Ordinal))
        {
            return "cancelled";
        }

        if (normalized.Contains("hiatus", StringComparison.Ordinal) ||
            normalized.Contains("paus", StringComparison.Ordinal))
        {
            return "paused";
        }

        return "unknown";
    }

    private static bool TryParseChapterRoute(
        Uri siteRoot,
        string value,
        out string route)
    {
        route = string.Empty;
        if (!TryResolveHttpUri(siteRoot, value, out var uri) ||
            !SameOrigin(uri, siteRoot))
        {
            return false;
        }

        const string prefix = "/leer/";
        if (!uri.AbsolutePath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            route = Uri.UnescapeDataString(uri.AbsolutePath[prefix.Length..])
                .Trim('/');
        }
        catch (UriFormatException)
        {
            return false;
        }

        return SafeChapterRouteRegex.IsMatch(route);
    }

    private static string ValidateExternalId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var externalId = value.Trim();
        if (!SafeExternalIdRegex.IsMatch(externalId))
        {
            throw new ArgumentException(
                "The ManhwaWeb title identifier is invalid.",
                nameof(value));
        }

        return externalId;
    }

    private static string ValidateChapterRoute(string value)
    {
        if (!SafeChapterRouteRegex.IsMatch(value))
        {
            throw new InvalidDataException(
                "ManhwaWeb returned an invalid chapter identifier.");
        }

        return value;
    }

    private static string NormalizeChapterDisplayValue(string value)
    {
        var trimmed = NormalizeText(value);
        if (decimal.TryParse(
                trimmed.Replace(',', '.'),
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var direct))
        {
            return direct.ToString(CultureInfo.InvariantCulture);
        }

        var match = ChapterNumberRegex.Match(trimmed);
        return match.Success
            ? NormalizeChapterKey(match.Value)
            : trimmed;
    }

    private static string NormalizeChapterKey(string value) =>
        decimal.TryParse(
            value.Trim().Replace(',', '.'),
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : value.Trim().ToLowerInvariant();

    private static decimal ParseChapterNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    private static string BuildChapterKey(
        Uri root,
        string externalId,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            externalId,
            seasonNumber: null,
            NormalizeChapterKey(displayNumber));

    private static bool IsFreshEndpoint(
        ApiEndpointCacheEntry? entry,
        Uri siteRoot,
        DateTimeOffset now) =>
        entry is not null &&
        SameOrigin(entry.SiteRoot, siteRoot) &&
        now - entry.FetchedAt < ApiEndpointCacheLifetime;

    private static Uri NormalizeRootUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            baseUri.Scheme is not ("http" or "https") ||
            baseUri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "ManhwaWeb requires an absolute HTTP or HTTPS base URL without embedded credentials.",
                nameof(baseUri));
        }

        return new UriBuilder(
            baseUri.Scheme,
            baseUri.IdnHost,
            baseUri.IsDefaultPort ? -1 : baseUri.Port,
            "/").Uri;
    }

    private static Uri GetOrigin(Uri uri) =>
        new UriBuilder(
            uri.Scheme,
            uri.IdnHost,
            uri.IsDefaultPort ? -1 : uri.Port,
            "/").Uri;

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

    private static bool TryResolveHttpUri(
        Uri root,
        string? value,
        out Uri uri)
    {
        uri = null!;
        return !string.IsNullOrWhiteSpace(value) &&
               Uri.TryCreate(root, value.Trim(), out var candidate) &&
               TryParseHttpUri(candidate.AbsoluteUri, out uri);
    }

    private static bool TryParseHttpUri(
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(value, UriKind.Absolute, out var candidate) ||
            candidate.Scheme is not ("http" or "https") ||
            candidate.UserInfo.Length > 0)
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool TryGetProperty(
        JsonElement element,
        string propertyName,
        JsonValueKind expectedKind,
        out JsonElement value)
    {
        value = default;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out value) &&
               value.ValueKind == expectedKind;
    }

    private static string? GetOptionalString(
        JsonElement element,
        string propertyName) =>
        TryGetString(element, propertyName, out var value)
            ? value
            : null;

    private static bool TryGetString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.String &&
               !string.IsNullOrWhiteSpace(
                   value = property.GetString() ?? string.Empty);
    }

    private static bool TryGetScalarString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        value = property.ValueKind switch
        {
            JsonValueKind.String => property.GetString() ?? string.Empty,
            JsonValueKind.Number => property.GetRawText(),
            _ => string.Empty
        };
        value = value.Trim();
        return value.Length > 0;
    }

    private static bool TryGetInt64(
        JsonElement element,
        string propertyName,
        out long value)
    {
        value = default;
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(propertyName, out var property))
        {
            return false;
        }

        return property.ValueKind switch
        {
            JsonValueKind.Number => property.TryGetInt64(out value),
            JsonValueKind.String => long.TryParse(
                property.GetString(),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out value),
            _ => false
        };
    }

    private static string? FormatUnixDate(long timestamp)
    {
        if (timestamp <= 0)
        {
            return null;
        }

        // The site has used both second and millisecond timestamps.
        var seconds = timestamp > 99_999_999_999
            ? timestamp / 1_000
            : timestamp;
        try
        {
            return DateTimeOffset
                .FromUnixTimeSeconds(seconds)
                .ToString(
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture);
        }
        catch (ArgumentOutOfRangeException)
        {
            return null;
        }
    }

    private static bool Contains(string value, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            value,
            query.Trim(),
            CompareOptions.IgnoreCase |
            CompareOptions.IgnoreNonSpace) >= 0;

    private static string NormalizeText(string? value) =>
        string.Join(
            " ",
            (value ?? string.Empty)
                .Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries |
                    StringSplitOptions.TrimEntries));

    private static string RemoveDiacritics(string value)
    {
        var decomposed = value.Normalize(NormalizationForm.FormD);
        var result = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) is not
                UnicodeCategory.NonSpacingMark)
            {
                result.Append(character);
            }
        }

        return result.ToString().Normalize(NormalizationForm.FormC);
    }

    private sealed record ApiEndpointCacheEntry(
        Uri SiteRoot,
        Uri ScriptUri,
        DateTimeOffset FetchedAt,
        Uri ApiRoot,
        ManhwaGenreCatalog Genres);

    private sealed record ManhwaGenreCatalog(
        ApiContentGenreMap Map,
        IReadOnlyDictionary<int, string> LabelsById)
    {
        public static ManhwaGenreCatalog Empty { get; } = new(
            ApiContentGenreMap.Empty,
            new Dictionary<int, string>());
    }

    private sealed record LibrarySearchPage(
        IReadOnlyList<ApiContentSearchItem> Items,
        bool HasNext);

    private sealed record DailyUpdateEntry(
        string ExternalId,
        string Name,
        string? ThumbnailRef,
        IReadOnlyList<string> Categories,
        string Subtype,
        string DisplayNumber,
        long CreatedAt);

    private sealed record ApiRootCandidate(
        Uri Uri,
        int Score);

    private sealed record ChapterEntry(
        string DisplayNumber,
        string Route,
        string? Thumbnail,
        long CreatedAt);
}

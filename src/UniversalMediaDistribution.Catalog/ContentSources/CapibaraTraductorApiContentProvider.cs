using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads CapibaraTraductor's public manga catalog and chapter-page API.
/// Scan and manga slugs are kept in the provider identity because different
/// scanlations may publish separate editions of the same work.
/// </summary>
public sealed partial class CapibaraTraductorApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "capibaratraductor";

    private const int MaximumCatalogBytes = 8 * 1024 * 1024;
    private const int MaximumDetailBytes = 8 * 1024 * 1024;
    private const int MaximumReaderBytes = 16 * 1024 * 1024;
    private const int MaximumChapters = 20_000;
    private const int MaximumPages = 2_000;
    private const int MaximumDailyUpdates = 24;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex SafeSlugRegex = new(
        "^[a-z0-9][a-z0-9-]{0,199}$",
        RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
        RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Uri> _chapterRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);

    public CapibaraTraductorApiContentProvider(HttpClient httpClient)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.CapibaraTraductor;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = NormalizeRoot(baseUri);
        return _trustedArtworkOrigins.Values
            .Prepend(TmoProviderUri.GetOrigin(root))
            .DistinctBy(
                static uri => uri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri)
    {
        var root = NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(artworkUri);
        if (!TmoProviderUri.IsSafePublicHttpUri(artworkUri))
        {
            return null;
        }

        var origin = TmoProviderUri.GetOrigin(artworkUri);
        return TmoProviderUri.IsProviderHost(root, artworkUri) ||
               _trustedArtworkOrigins.ContainsKey(origin.AbsoluteUri)
            ? root
            : null;
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        LoadDailyUpdatesAsync(
            Uri baseUri,
            CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        using var document = await ReadJsonAsync(
            root,
            new Uri(
                root,
                "api/manga-custom?order=latest&limit=30&nsfw=false"),
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        return ParseCatalog(document.RootElement, root)
            .Take(MaximumDailyUpdates)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (mediaType is not null and not MediaType.Book)
        {
            return [];
        }

        var root = NormalizeRoot(baseUri);
        using var document = await ReadJsonAsync(
            root,
            BuildCatalogUri(root, query.Trim(), page: 1, limit: 50),
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        return ParseCatalog(document.RootElement, root);
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Query is null ||
            request.HasFilters ||
            request.MediaType is not null and not MediaType.Book)
        {
            return ApiContentSearchPage.Empty;
        }

        var root = NormalizeRoot(baseUri);
        var page = ParsePageToken(request.ContinuationToken);
        using var document = await ReadJsonAsync(
            root,
            BuildCatalogUri(root, request.Query, page, request.PageSize),
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        var items = ParseCatalog(document.RootElement, root)
            .Take(request.PageSize)
            .ToArray();
        var maximumPage = GetMaximumPage(document.RootElement);
        var next = page < maximumPage
            ? checked(page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        return new ApiContentSearchPage(items, next);
    }

    public async ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        var identity = ParseExternalId(searchItem.ExternalId);
        var detailUri = BuildDetailUri(root, identity);
        var jsonUri = new Uri(
            root,
            $"api/manga-custom?ids={identity.CustomId.ToString(CultureInfo.InvariantCulture)}&limit=1&nsfw=false");
        using var document = await ReadJsonAsync(
            root,
            jsonUri,
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        if (!TryFindCatalogItem(document.RootElement, identity, out var detail))
        {
            throw new InvalidDataException(
                "CapibaraTraductor no longer exposes the selected manga.");
        }

        if (IsWriting(detail))
        {
            throw new ApiCatalogItemUnsupportedException(
                "CapibaraTraductor identifies this work as prose, not an image-based manga.");
        }

        var html = await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            detailUri,
            root,
            MaximumDetailBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "CapibaraTraductor",
            cancellationToken).ConfigureAwait(false);
        using var work = await ReadWorkAsync(root, identity, cancellationToken)
            .ConfigureAwait(false);
        var chapterSource = work is not null &&
            TryGetData(work.RootElement, out var workData)
            ? workData
            : detail;
        var name = GetString(detail, "title");
        if (name.Length == 0)
        {
            if (searchItem.RequireProviderTitle)
            {
                throw new InvalidDataException(
                    "The CapibaraTraductor title did not include a name.");
            }

            name = searchItem.PreviewItem.Name;
        }

        var thumbnail = GetArtwork(detail, "imageUrl") ??
            searchItem.PreviewItem.ThumbnailRef;
        var backdrop = GetArtwork(detail, "bannerUrl") ?? thumbnail;
        var aliases = ExtractAliases(detail, name);
        // The list payload trims chapters to the two newest, and the detail
        // page only renders the newest block, so neither is a complete source.
        // The per-work route is, and the scrape stays as a fallback.
        var units = ParseApiChapters(root, identity, chapterSource);
        if (units.Count == 0)
        {
            units = ExtractChapters(root, identity, html);
        }
        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "manga",
            Name = name,
            Description = NullIfEmpty(
                GetString(detail, "description") is { Length: > 0 } description
                    ? description
                    : GetNestedString(detail, "manga", "description")),
            PublicationStatus = MapStatus(GetString(detail, "status")),
            ThumbnailRef = thumbnail,
            BackdropRef = backdrop,
            Genres = ExtractNamedValues(detail, "genres"),
            Categories = BuildCategories(detail),
            AlternativeNames = aliases,
            Units = units
        });
    }

    public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit)
    {
        var root = NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Chapter ||
            unit.SeasonNumber is not null)
        {
            return [];
        }

        var identity = ParseExternalId(searchItem.ExternalId);
        return _chapterRoutes.TryGetValue(
                   BuildChapterKey(root, identity, unit.DisplayNumber),
                   out var route)
            ?
            [
                new ApiProviderUnitRoute(
                    ContentUnitKind.Chapter,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    route.PathAndQuery)
            ]
            : [];
    }

    public async ValueTask<ApiContentImageSet?> LoadUnitImagesAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRoot(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Chapter ||
            unit.SeasonNumber is not null)
        {
            return null;
        }

        var identity = ParseExternalId(searchItem.ExternalId);
        var displayNumber = ValidateDisplayNumber(unit.DisplayNumber);
        var readerUri = ResolveKnownReaderUri(
            root,
            identity,
            searchItem,
            displayNumber);
        if (readerUri is null)
        {
            _ = await LoadItemAsync(root, searchItem, cancellationToken)
                .ConfigureAwait(false);
            _chapterRoutes.TryGetValue(
                BuildChapterKey(root, identity, displayNumber),
                out readerUri);
        }

        if (readerUri is null)
        {
            return null;
        }

        var pagesUri = new Uri(
            root,
            $"api/manga-custom/{Uri.EscapeDataString(identity.MangaSlug)}/chapter/{Uri.EscapeDataString(displayNumber)}/pages");
        using var document = await ReadJsonAsync(
            root,
            pagesUri,
            MaximumReaderBytes,
            cancellationToken,
            identity.ScanSlug).ConfigureAwait(false);
        // Early-access chapters answer 200 with a status:false envelope and no
        // data. That is a chapter which legitimately cannot be read yet, not a
        // schema change, so it reports as unavailable instead of an error that
        // would send the next reader hunting for drift.
        if (!IsSuccessEnvelope(document.RootElement))
        {
            return null;
        }

        if (!TryGetData(document.RootElement, out var pagesElement) ||
            pagesElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "CapibaraTraductor returned an invalid chapter-page response.");
        }

        var rawPages = new List<(int Number, Uri Uri)>();
        var seenNumbers = new HashSet<int>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var page in pagesElement.EnumerateArray())
        {
            if (rawPages.Count >= MaximumPages)
            {
                throw new InvalidDataException(
                    "The CapibaraTraductor chapter contains too many pages.");
            }

            if (!TryGetInt32(page, "number", out var number) ||
                number is < 1 or > MaximumPages ||
                !TryGetPublicUri(page, "imageUrl", out var imageUri) ||
                !seenNumbers.Add(number) ||
                !seenUris.Add(imageUri.AbsoluteUri))
            {
                continue;
            }

            RegisterArtworkOrigin(imageUri);
            rawPages.Add((number, imageUri));
        }

        if (rawPages.Count == 0)
        {
            throw new InvalidDataException(
                "The CapibaraTraductor chapter did not contain reader pages.");
        }

        return new ApiContentImageSet(
            ContentUnitKind.Chapter,
            unit.DisplayNumber,
            seasonNumber: null,
            rawPages
                .OrderBy(static page => page.Number)
                .Select((page, index) =>
                    new ApiContentImagePage(index, page.Uri, readerUri)));
    }

    private IReadOnlyList<ApiContentSearchItem> ParseCatalog(
        JsonElement rootElement,
        Uri root)
    {
        if (!TryGetData(rootElement, out var data) ||
            !TryGetProperty(data, "items", JsonValueKind.Array, out var items))
        {
            throw new InvalidDataException(
                "CapibaraTraductor returned an invalid catalog response.");
        }

        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in items.EnumerateArray())
        {
            if (!TryCreateIdentity(element, out var identity) ||
                !seen.Add(identity.ExternalId) ||
                IsWriting(element))
            {
                continue;
            }

            var name = GetString(element, "title");
            if (name.Length == 0)
            {
                continue;
            }

            var thumbnail = GetArtwork(element, "imageUrl");
            var backdrop = GetArtwork(element, "bannerUrl");
            var routes = ExtractRecentUnitRoutes(root, identity, element);
            results.Add(new ApiContentSearchItem(
                identity.ExternalId,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = name,
                    Description = NullIfEmpty(GetString(element, "description")),
                    PublicationStatus = MapStatus(GetString(element, "status")),
                    ThumbnailRef = thumbnail,
                    BackdropRef = backdrop,
                    Genres = ExtractNamedValues(element, "genres"),
                    Categories = BuildCategories(element),
                    AlternativeNames = ExtractAliases(element, name)
                },
                routes));
        }

        return results;
    }

    /// <summary>
    /// Fetches the per-work detail route, or null when it is unavailable.
    /// <para>
    /// A title still resolves from the list payload and the detail page when
    /// this route fails, so a failure here degrades the chapter list rather
    /// than the whole item.
    /// </para>
    /// </summary>
    private async ValueTask<JsonDocument?> ReadWorkAsync(
        Uri root,
        MangaIdentity identity,
        CancellationToken cancellationToken)
    {
        try
        {
            return await ReadJsonAsync(
                root,
                new Uri(
                    root,
                    $"api/manga-custom/{Uri.EscapeDataString(identity.MangaSlug)}"),
                MaximumCatalogBytes,
                cancellationToken,
                identity.ScanSlug).ConfigureAwait(false);
        }
        catch (HttpRequestException)
        {
            return null;
        }
        catch (InvalidDataException)
        {
            return null;
        }
    }

    /// <summary>
    /// Reads a work's complete chapter list from the per-work API route.
    /// <para>
    /// The detail page server-renders only the newest ten-chapter block and
    /// produces the rest in the browser, so scraping its anchors caps a
    /// 360-chapter series at about ten units and leaves the reader unable to
    /// resolve any older chapter. This route returns every chapter.
    /// </para>
    /// </summary>
    private IReadOnlyList<CatalogTransferUnit> ParseApiChapters(
        Uri root,
        MangaIdentity identity,
        JsonElement detail)
    {
        if (!TryGetProperty(
                detail,
                "chapters",
                JsonValueKind.Array,
                out var chapters))
        {
            return [];
        }

        var seen = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var chapter in chapters.EnumerateArray())
        {
            if (seen.Count >= MaximumChapters)
            {
                throw new InvalidDataException(
                    "The CapibaraTraductor manga contains too many chapters.");
            }

            if (!TryGetDecimalText(chapter, "number", out var number) ||
                !IsReleased(chapter))
            {
                continue;
            }

            var normalized =
                ApiContentProviderCacheKey.NormalizeDisplayNumber(number);
            if (seen.ContainsKey(normalized))
            {
                continue;
            }

            var title = GetString(chapter, "title");
            seen.Add(
                normalized,
                title.Length == 0 ? $"Capítulo {number}" : title);
            _chapterRoutes[BuildChapterKey(root, identity, number)] =
                BuildReaderUri(root, identity, number);
        }

        return seen
            .OrderBy(static pair => ParseDisplayNumber(pair.Key))
            .ThenBy(static pair => pair.Key, StringComparer.Ordinal)
            .Select(static (pair, index) => new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = pair.Key,
                SortOrder = index,
                Name = pair.Value
            })
            .ToArray();
    }

    /// <summary>
    /// Whether a chapter is published rather than scheduled or early access.
    /// <para>
    /// Offering an unreleased chapter is worse than omitting it: the pages
    /// route answers 200 with a refusal envelope, so the reader opens onto
    /// nothing.
    /// </para>
    /// </summary>
    private static bool IsReleased(JsonElement chapter)
    {
        if (chapter.TryGetProperty("isUnreleased", out var unreleased) &&
            unreleased.ValueKind is JsonValueKind.True)
        {
            return false;
        }

        return !chapter.TryGetProperty("releasedAt", out var releasedAt) ||
            releasedAt.ValueKind is not JsonValueKind.String ||
            !DateTimeOffset.TryParse(
                releasedAt.GetString(),
                CultureInfo.InvariantCulture,
                DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal,
                out var released) ||
            released <= DateTimeOffset.UtcNow;
    }

    private IReadOnlyList<CatalogTransferUnit> ExtractChapters(
        Uri root,
        MangaIdentity identity,
        string html)
    {
        var chapters = new Dictionary<string, (string Name, Uri Uri)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            if (chapters.Count >= MaximumChapters)
            {
                throw new InvalidDataException(
                    "The CapibaraTraductor manga contains too many chapters.");
            }

            if (!TryParseReaderUri(
                    root,
                    identity,
                    TmoProviderText.GetAttribute(anchor.Attributes, "href"),
                    out var number,
                    out var readerUri))
            {
                continue;
            }

            var normalized =
                ApiContentProviderCacheKey.NormalizeDisplayNumber(number);
            if (chapters.ContainsKey(normalized))
            {
                continue;
            }

            var title = TmoProviderText.HtmlToText(
                TmoProviderText.FindElementContentByClass(
                    anchor.Content,
                    "h4",
                    "text-white"));
            if (title.Length == 0)
            {
                title = $"Capítulo {number}";
            }

            chapters.Add(normalized, (title, readerUri));
            _chapterRoutes[BuildChapterKey(root, identity, number)] = readerUri;
        }

        return chapters
            .Select(static pair => new
            {
                Number = pair.Key,
                pair.Value.Name
            })
            .OrderBy(static chapter => ParseDisplayNumber(chapter.Number))
            .ThenBy(static chapter => chapter.Number, StringComparer.Ordinal)
            .Select(static (chapter, index) => new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = chapter.Number,
                SortOrder = index,
                Name = chapter.Name
            })
            .ToArray();
    }

    private IReadOnlyList<ApiProviderUnitRoute> ExtractRecentUnitRoutes(
        Uri root,
        MangaIdentity identity,
        JsonElement element)
    {
        if (!TryGetProperty(
                element,
                "chapters",
                JsonValueKind.Array,
                out var chapters))
        {
            return [];
        }

        var routes = new List<ApiProviderUnitRoute>();
        foreach (var chapter in chapters.EnumerateArray())
        {
            if (!TryGetDecimalText(chapter, "number", out var number) ||
                !IsReleased(chapter))
            {
                continue;
            }

            var readerUri = BuildReaderUri(root, identity, number);
            _chapterRoutes[BuildChapterKey(root, identity, number)] = readerUri;
            routes.Add(new ApiProviderUnitRoute(
                ContentUnitKind.Chapter,
                number,
                seasonNumber: null,
                readerUri.PathAndQuery));
        }

        return routes;
    }

    private Uri? ResolveKnownReaderUri(
        Uri root,
        MangaIdentity identity,
        ApiContentSearchItem searchItem,
        string displayNumber)
    {
        foreach (var route in searchItem.UnitRoutes)
        {
            if (route.UnitKind is ContentUnitKind.Chapter &&
                route.SeasonNumber is null &&
                ApiContentUnitRequest.DisplayNumbersMatch(
                    route.DisplayNumber,
                    displayNumber) &&
                TryParseReaderUri(
                    root,
                    identity,
                    route.Route,
                    out _,
                    out var routeUri))
            {
                _chapterRoutes[BuildChapterKey(
                    root,
                    identity,
                    displayNumber)] = routeUri;
                return routeUri;
            }
        }

        return _chapterRoutes.TryGetValue(
            BuildChapterKey(root, identity, displayNumber),
            out var cached)
            ? cached
            : null;
    }

    /// <param name="organizationSlug">
    /// Scanlation group owning the requested work, sent as
    /// <c>x-organization</c>.
    /// <para>
    /// Routes keyed only on a manga slug carry no organization in their path
    /// and resolve the tenant from this header, answering HTTP 500 without it.
    /// It must stay off the catalog and search calls: those are site-wide, and
    /// sending it there narrows the whole catalog to a single group.
    /// </para>
    /// </param>
    private async ValueTask<JsonDocument> ReadJsonAsync(
        Uri root,
        Uri endpoint,
        int maximumBytes,
        CancellationToken cancellationToken,
        string? organizationSlug = null)
    {
        var json = await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            endpoint,
            root,
            maximumBytes,
            "application/json,text/plain;q=0.9,*/*;q=0.5",
            "CapibaraTraductor",
            cancellationToken,
            string.IsNullOrEmpty(organizationSlug)
                ? null
                : [("x-organization", organizationSlug)]).ConfigureAwait(false);
        try
        {
            return JsonDocument.Parse(json);
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "CapibaraTraductor returned invalid JSON.",
                exception);
        }
    }

    private string? GetArtwork(JsonElement element, string propertyName)
    {
        if (!TryGetPublicUri(element, propertyName, out var uri))
        {
            return null;
        }

        RegisterArtworkOrigin(uri);
        return uri.AbsoluteUri;
    }

    private void RegisterArtworkOrigin(Uri uri)
    {
        if (!TmoProviderUri.IsSafePublicHttpUri(uri))
        {
            return;
        }

        var origin = TmoProviderUri.GetOrigin(uri);
        _trustedArtworkOrigins[origin.AbsoluteUri] = origin;
    }

    private static Uri BuildCatalogUri(
        Uri root,
        string query,
        int page,
        int limit) =>
        new(
            root,
            $"api/manga-custom?page={page.ToString(CultureInfo.InvariantCulture)}&limit={limit.ToString(CultureInfo.InvariantCulture)}&order=latest&nsfw=false&search={Uri.EscapeDataString(query)}");

    private static Uri BuildDetailUri(Uri root, MangaIdentity identity) =>
        new(
            root,
            $"{Uri.EscapeDataString(identity.ScanSlug)}/manga/{Uri.EscapeDataString(identity.MangaSlug)}");

    private static Uri BuildReaderUri(
        Uri root,
        MangaIdentity identity,
        string displayNumber) =>
        new(
            BuildDetailUri(root, identity),
            $"{BuildDetailUri(root, identity).AbsolutePath.TrimEnd('/')}/chapters/{Uri.EscapeDataString(displayNumber)}");

    private static bool TryParseReaderUri(
        Uri root,
        MangaIdentity identity,
        string? value,
        out string displayNumber,
        out Uri readerUri)
    {
        displayNumber = string.Empty;
        readerUri = null!;
        if (!TmoProviderUri.TryResolveSameOrigin(root, value, out var candidate))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(candidate);
        if (segments.Length != 5 ||
            !string.Equals(
                segments[0],
                identity.ScanSlug,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(segments[1], "manga", StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                segments[2],
                identity.MangaSlug,
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                segments[3],
                "chapters",
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            displayNumber = ValidateDisplayNumber(segments[4]);
        }
        catch (InvalidDataException)
        {
            return false;
        }

        readerUri = candidate;
        return true;
    }

    private static MangaIdentity ParseExternalId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var parts = value.Trim().Split('/');
        if (parts.Length != 3 ||
            !int.TryParse(
                parts[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var customId) ||
            customId <= 0 ||
            !SafeSlugRegex.IsMatch(parts[1]) ||
            !SafeSlugRegex.IsMatch(parts[2]))
        {
            throw new ArgumentException(
                "The CapibaraTraductor manga identifier is invalid.",
                nameof(value));
        }

        return new MangaIdentity(customId, parts[1], parts[2]);
    }

    private static bool TryCreateIdentity(
        JsonElement element,
        out MangaIdentity identity)
    {
        identity = default;
        if (!TryGetInt32(element, "id", out var customId) ||
            customId <= 0 ||
            !TryGetProperty(
                element,
                "organization",
                JsonValueKind.Object,
                out var organization) ||
            !TryGetProperty(
                element,
                "manga",
                JsonValueKind.Object,
                out var manga))
        {
            return false;
        }

        var scanSlug = GetString(organization, "slug");
        var mangaSlug = GetString(manga, "slug");
        if (!SafeSlugRegex.IsMatch(scanSlug) ||
            !SafeSlugRegex.IsMatch(mangaSlug))
        {
            return false;
        }

        identity = new MangaIdentity(customId, scanSlug, mangaSlug);
        return true;
    }

    private static bool TryFindCatalogItem(
        JsonElement root,
        MangaIdentity expected,
        out JsonElement result)
    {
        result = default;
        if (!TryGetData(root, out var data) ||
            !TryGetProperty(data, "items", JsonValueKind.Array, out var items))
        {
            return false;
        }

        foreach (var item in items.EnumerateArray())
        {
            if (TryCreateIdentity(item, out var identity) &&
                identity == expected)
            {
                result = item;
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<CatalogTransferAlias> ExtractAliases(
        JsonElement element,
        string canonicalName)
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var mangaTitle = GetNestedString(element, "manga", "title");
        if (mangaTitle.Length > 0 &&
            !string.Equals(mangaTitle, canonicalName, StringComparison.OrdinalIgnoreCase))
        {
            names.Add(mangaTitle);
        }

        return names.Select(static name => new CatalogTransferAlias
        {
            Name = name,
            AliasType = "provider"
        }).ToArray();
    }

    private static IReadOnlyList<string> ExtractNamedValues(
        JsonElement element,
        string propertyName)
    {
        if (!TryGetProperty(
                element,
                propertyName,
                JsonValueKind.Array,
                out var values))
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(static value => GetString(value, "name"))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private static IReadOnlyList<string> BuildCategories(JsonElement element)
    {
        var values = new List<string> { "Manga" };
        var demography = GetNestedString(element, "manga", "demography", "name");
        if (demography.Length > 0)
        {
            values.Add(demography);
        }

        var organization = GetNestedString(element, "organization", "name");
        if (organization.Length > 0)
        {
            values.Add(organization);
        }

        return values.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    }

    /// <summary>
    /// Whether an entry is prose rather than a comic.
    /// <para>
    /// Both fields are needed: older lists omitted <c>manga.bookType</c>, while
    /// current lists sometimes label prose as <c>workType=manga</c> and put its
    /// actual novel/light-novel type in <c>manga.bookType.code</c>.
    /// </para>
    /// </summary>
    private static bool IsWriting(JsonElement element)
    {
        return IsWritingType(GetString(element, "workType")) ||
               IsWritingType(GetNestedString(element, "manga", "bookType", "code"));
    }

    private static bool IsWritingType(string value)
    {
        var type = value.AsSpan().Trim();
        return type.Equals("novel", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("light-novel", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("book", StringComparison.OrdinalIgnoreCase) ||
               type.Equals("short-story", StringComparison.OrdinalIgnoreCase);
    }

    private static int GetMaximumPage(JsonElement root)
    {
        if (!TryGetData(root, out var data) ||
            !TryGetInt32(data, "maxPage", out var maximumPage))
        {
            return 1;
        }

        return Math.Max(1, maximumPage);
    }

    private static int ParsePageToken(string? token)
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
            page is < 1 or > 100_000)
        {
            throw new ArgumentException(
                "CapibaraTraductor returned an invalid catalog page token.",
                nameof(token));
        }

        return page;
    }

    private static string ValidateDisplayNumber(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!decimal.TryParse(
                value.Trim(),
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number) ||
            number < 0)
        {
            throw new InvalidDataException(
                "CapibaraTraductor returned an invalid chapter number.");
        }

        return number.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
    }

    private static decimal ParseDisplayNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    private static string BuildChapterKey(
        Uri root,
        MangaIdentity identity,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            identity.ExternalId,
            seasonNumber: null,
            ApiContentProviderCacheKey.NormalizeDisplayNumber(displayNumber));

    private static Uri NormalizeRoot(Uri baseUri) =>
        TmoProviderUri.NormalizeRoot(baseUri, "CapibaraTraductor");

    /// <summary>
    /// Whether a response envelope reports success.
    /// <para>
    /// The API signals a refusal in the body rather than the status line, so a
    /// missing <c>status</c> is treated as success to stay compatible with the
    /// routes that omit it.
    /// </para>
    /// </summary>
    private static bool IsSuccessEnvelope(JsonElement root) =>
        root.ValueKind is not JsonValueKind.Object ||
        !root.TryGetProperty("status", out var status) ||
        status.ValueKind is not JsonValueKind.False;

    private static bool TryGetData(JsonElement root, out JsonElement data) =>
        TryGetProperty(root, "data", JsonValueKind.Object, out data) ||
        TryGetProperty(root, "data", JsonValueKind.Array, out data);

    private static bool TryGetProperty(
        JsonElement element,
        string name,
        JsonValueKind kind,
        out JsonElement value)
    {
        value = default;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(name, out value) &&
               value.ValueKind == kind;
    }

    private static string GetString(JsonElement element, string name) =>
        element.ValueKind is JsonValueKind.Object &&
        element.TryGetProperty(name, out var value) &&
        value.ValueKind is JsonValueKind.String
            ? TmoProviderText.NormalizeText(value.GetString())
            : string.Empty;

    private static string GetNestedString(
        JsonElement element,
        string objectName,
        string propertyName) =>
        TryGetProperty(element, objectName, JsonValueKind.Object, out var nested)
            ? GetString(nested, propertyName)
            : string.Empty;

    private static string GetNestedString(
        JsonElement element,
        string firstObject,
        string secondObject,
        string propertyName) =>
        TryGetProperty(element, firstObject, JsonValueKind.Object, out var first) &&
        TryGetProperty(first, secondObject, JsonValueKind.Object, out var second)
            ? GetString(second, propertyName)
            : string.Empty;

    private static bool TryGetInt32(
        JsonElement element,
        string name,
        out int value)
    {
        value = default;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(name, out var property) &&
               (property.ValueKind is JsonValueKind.Number &&
                property.TryGetInt32(out value) ||
                property.ValueKind is JsonValueKind.String &&
                int.TryParse(
                    property.GetString(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out value));
    }

    private static bool TryGetDecimalText(
        JsonElement element,
        string name,
        out string value)
    {
        value = string.Empty;
        if (element.ValueKind is not JsonValueKind.Object ||
            !element.TryGetProperty(name, out var property))
        {
            return false;
        }

        var raw = property.ValueKind switch
        {
            JsonValueKind.Number => property.GetRawText(),
            JsonValueKind.String => property.GetString(),
            _ => null
        };
        if (raw is null)
        {
            return false;
        }

        try
        {
            value = ValidateDisplayNumber(raw);
            return true;
        }
        catch (InvalidDataException)
        {
            return false;
        }
    }

    private static bool TryGetPublicUri(
        JsonElement element,
        string name,
        out Uri uri) =>
        TmoProviderUri.TryCreatePublicHttpUri(GetString(element, name), out uri);

    private static string MapStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "ongoing" => "ongoing",
            "finished" or "completed" => "completed",
            "paused" or "pausa" or "hiatus" => "paused",
            "cancelled" => "cancelled",
            _ => "unknown"
        };

    private static string? NullIfEmpty(string value) =>
        value.Length == 0 ? null : value;

    private readonly record struct MangaIdentity(
        int CustomId,
        string ScanSlug,
        string MangaSlug)
    {
        public string ExternalId =>
            $"{CustomId.ToString(CultureInfo.InvariantCulture)}/{ScanSlug}/{MangaSlug}";
    }
}

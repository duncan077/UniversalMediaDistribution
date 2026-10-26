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
/// Reads the public JSON used by the Olympus Scanlation website.
/// </summary>
public sealed partial class OlympusXyzApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "olympusxyz";

    private const int MaximumSeriesListBytes = 16 * 1024 * 1024;
    private const int MaximumHomepageBytes = 4 * 1024 * 1024;
    private const int MaximumItemBytes = 4 * 1024 * 1024;
    private const int MaximumChapterListBytes = 4 * 1024 * 1024;
    private const int MaximumReaderBytes = 24 * 1024 * 1024;
    private const int MaximumSearchResults = 500;
    private const int MaximumDailyUpdateResults = 24;
    private const int MaximumChapterCount = 20_000;
    private const int MaximumPageCount = 2_000;
    private const int MaximumChapterListPages = 500;

    private static readonly TimeSpan SeriesListCacheLifetime =
        TimeSpan.FromDays(1);
    private static readonly Regex SafeSlugRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,239}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex SafeChapterIdRegex = new(
        "^[A-Za-z0-9][A-Za-z0-9_-]{0,127}$",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex ChapterNumberRegex = new(
        @"\d+(?:[.,]\d+)?",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex FigureRegex = new(
        @"<figure\b[^>]*>(?<body>.*?)</figure>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex SectionHeadingRegex = new(
        @"<h2\b[^>]*>(?<body>.*?)</h2\s*>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex AnchorRegex = new(
        @"<a\b(?<attributes>[^>]*)>(?<body>.*?)</a>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(2));
    private static readonly Regex ImageRegex = new(
        @"<img\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex TimeRegex = new(
        @"<time\b(?<attributes>[^>]*)>",
        RegexOptions.IgnoreCase | RegexOptions.Singleline |
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex HtmlAttributeRegex = new(
        """
        (?<name>[A-Za-z_:][-A-Za-z0-9_:.]*)\s*=\s*(?:"(?<double>[^"]*)"|'(?<single>[^']*)'|(?<bare>[^\s>]+))
        """,
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex HtmlTagRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));
    private static readonly Regex HtmlWhitespaceRegex = new(
        @"\s+",
        RegexOptions.CultureInvariant,
        TimeSpan.FromSeconds(1));

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _seriesListCacheGate = new(1, 1);
    private readonly ConcurrentDictionary<string, string> _chapterIds =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);
    private SeriesListCacheEntry? _seriesListCache;

    public OlympusXyzApiContentProvider(
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.OlympusXyz;

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
            MaximumHomepageBytes,
            cancellationToken,
            "text/html");
        var section = ExtractDailyUpdateSection(html);
        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (Match figure in FigureRegex.Matches(section))
        {
            if (results.Count >= MaximumDailyUpdateResults)
            {
                break;
            }

            var body = figure.Groups["body"].Value;
            string? slug = null;
            string? title = null;
            foreach (Match anchor in AnchorRegex.Matches(body))
            {
                var attributes = anchor.Groups["attributes"].Value;
                var href = ReadHtmlAttribute(attributes, "href");
                if (!TryGetSeriesSlug(root, href, out var candidate))
                {
                    continue;
                }

                slug = candidate;
                title = NormalizeHtmlText(
                    ReadHtmlAttribute(attributes, "title") ??
                    anchor.Groups["body"].Value);
                if (title.Length > 0)
                {
                    break;
                }
            }

            if (slug is null ||
                title is null ||
                title.Length == 0 ||
                !seen.Add(slug))
            {
                continue;
            }

            string? thumbnail = null;
            var image = ImageRegex.Match(body);
            if (image.Success &&
                TryResolveHttpUri(
                    root,
                    ReadHtmlAttribute(
                        image.Groups["attributes"].Value,
                        "src"),
                    out var artworkUri))
            {
                thumbnail = artworkUri.AbsoluteUri;
                AddTrustedArtworkOrigin(artworkUri);
            }

            results.Add(new ApiContentSearchItem(
                slug,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = title,
                    ThumbnailRef = thumbnail,
                    Categories = ["Manga"],
                    Units = ExtractDailyChapters(body, root, slug)
                }));
        }

        return results;
    }

    /// <summary>
    /// Genres exist on this source, but only on a title's own detail payload —
    /// the list endpoint does not carry them and does not filter on them.
    /// Verifying a genre would therefore cost one detail request per candidate,
    /// which is not a price a search should pay. No year is exposed at all.
    /// Both filters stay declined until the list API surfaces them.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities =>
        ApiContentSearchCapabilities.TextOnly;

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

        var json = await GetSeriesListJsonAsync(
            root,
            cancellationToken);
        using var document = JsonDocument.Parse(json);
        if (!TryGetProperty(
                document.RootElement,
                "data",
                JsonValueKind.Array,
                out var data))
        {
            throw new InvalidDataException(
                "Olympus returned an invalid series-list response.");
        }

        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in data.EnumerateArray())
        {
            if (results.Count >= MaximumSearchResults)
            {
                break;
            }

            if (element.ValueKind is not JsonValueKind.Object ||
                !TryGetString(element, "slug", out var slug) ||
                !SafeSlugRegex.IsMatch(slug) ||
                !TryGetString(element, "name", out var name) ||
                !Contains(name, query))
            {
                continue;
            }

            if (TryGetString(element, "type", out var type) &&
                !string.Equals(type, "comic", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            if (!seen.Add(slug))
            {
                continue;
            }

            var thumbnail = GetArtworkReference(root, element, "cover");
            results.Add(new ApiContentSearchItem(
                slug,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = NormalizeText(name),
                    ThumbnailRef = thumbnail,
                    Categories = ["Manga"]
                }));
        }

        return results;
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

        var detailJson = await ReadStringAsync(
            new Uri(
                root,
                $"api/series/{Uri.EscapeDataString(slug)}?type=comic"),
            root,
            MaximumItemBytes,
            cancellationToken);
        using var detailDocument = JsonDocument.Parse(detailJson);
        if (!TryGetProperty(
                detailDocument.RootElement,
                "data",
                JsonValueKind.Object,
                out var detail))
        {
            throw new InvalidDataException(
                "Olympus returned an invalid series response.");
        }

        var hasProviderName = TryGetString(
            detail,
            "name",
            out var detailName);
        if (searchItem.RequireProviderTitle && !hasProviderName)
        {
            throw new InvalidDataException(
                "The Olympus series did not include a title.");
        }

        var name = hasProviderName
            ? NormalizeText(detailName)
            : searchItem.PreviewItem.Name;
        if (name.Length == 0)
        {
            throw new InvalidDataException(
                "The Olympus series did not include a title.");
        }

        var description = TryGetString(detail, "summary", out var summary)
            ? NormalizeText(summary)
            : string.Empty;
        var thumbnail = GetArtworkReference(root, detail, "cover") ??
            searchItem.PreviewItem.ThumbnailRef;
        var status = "unknown";
        if (TryGetProperty(
                detail,
                "status",
                JsonValueKind.Object,
                out var statusObject) &&
            TryGetString(statusObject, "name", out var statusName))
        {
            status = MapPublicationStatus(statusName);
        }

        var genres = ReadNamedArray(detail, "genres");
        var units = TryUseKnownChapterId(
            root,
            slug,
            searchItem.UnitRoutes,
            out var knownUnits)
            ? knownUnits
            : await LoadChaptersAsync(
                root,
                slug,
                targetUnit?.DisplayNumber,
                cancellationToken);
        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "manga",
            Name = name,
            Description = description.Length == 0 ? null : description,
            PublicationStatus = status,
            ThumbnailRef = thumbnail,
            Genres = genres,
            Categories = ["Manga"],
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
        var slug = ValidateSlug(searchItem.ExternalId);
        return _chapterIds.TryGetValue(
                   BuildChapterKey(root, slug, unit.DisplayNumber),
                   out var chapterId) &&
               SafeChapterIdRegex.IsMatch(chapterId)
            ?
            [
                new ApiProviderUnitRoute(
                    ContentUnitKind.Chapter,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    chapterId)
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

        var slug = ValidateSlug(searchItem.ExternalId);
        var key = BuildChapterKey(root, slug, unit.DisplayNumber);
        if (!_chapterIds.TryGetValue(key, out var chapterId))
        {
            _ = await LoadItemForUnitAsync(
                root,
                searchItem,
                unit,
                cancellationToken);
            if (!_chapterIds.TryGetValue(key, out chapterId))
            {
                return null;
            }
        }

        if (!SafeChapterIdRegex.IsMatch(chapterId))
        {
            throw new InvalidDataException(
                "Olympus returned an invalid chapter identifier.");
        }

        var displayNumber = NormalizeChapterDisplayValue(unit.DisplayNumber);
        var readerRoute = decimal.TryParse(
            displayNumber.AsSpan(),
            NumberStyles.AllowDecimalPoint,
            CultureInfo.InvariantCulture,
            out var number) && number >= 0
            ? number.ToString(CultureInfo.InvariantCulture)
            : chapterId;
        string json;
        try
        {
            json = await ReadChapterJsonAsync(root, slug, readerRoute, cancellationToken);
        }
        catch (HttpRequestException exception) when (
            exception.StatusCode is HttpStatusCode.NotFound &&
            !string.Equals(readerRoute, chapterId, StringComparison.Ordinal))
        {
            // Older deployments still address the reader by its internal ID.
            json = await ReadChapterJsonAsync(root, slug, chapterId, cancellationToken);
        }

        using var document = JsonDocument.Parse(json);
        var chapter = document.RootElement;
        if (TryGetProperty(
                chapter,
                "chapter",
                JsonValueKind.Object,
                out var wrappedChapter))
        {
            chapter = wrappedChapter;
        }

        if (TryGetScalarString(chapter, "name", out var chapterName) &&
            !string.Equals(
                NormalizeChapterKey(NormalizeChapterDisplayValue(chapterName)),
                NormalizeChapterKey(displayNumber),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                "Olympus returned a different chapter than the one requested.");
        }

        if (!TryGetProperty(
                chapter,
                "pages",
                JsonValueKind.Array,
                out var pages))
        {
            throw new InvalidDataException(
                "The Olympus chapter did not contain reader pages.");
        }

        var imageUris = new List<Uri>();
        foreach (var page in pages.EnumerateArray())
        {
            if (imageUris.Count >= MaximumPageCount)
            {
                throw new InvalidDataException(
                    "The Olympus chapter contains too many reader pages.");
            }

            if (page.ValueKind is not JsonValueKind.String ||
                !TryParseHttpUri(page.GetString(), out var uri))
            {
                throw new InvalidDataException(
                    "The Olympus chapter contained an invalid reader-page URL.");
            }

            imageUris.Add(uri);
            AddTrustedArtworkOrigin(uri);
        }

        if (imageUris.Count == 0)
        {
            throw new InvalidDataException(
                "The Olympus chapter did not contain any reader pages.");
        }

        return new ApiContentImageSet(
            ContentUnitKind.Chapter,
            unit.DisplayNumber,
            seasonNumber: null,
            imageUris.Select(
                static (uri, index) =>
                    new ApiContentImagePage(index, uri)));
    }

    private ValueTask<string> ReadChapterJsonAsync(
        Uri root,
        string slug,
        string chapterRoute,
        CancellationToken cancellationToken) =>
        ReadStringAsync(
            new Uri(
                root,
                $"api/capitulo/{Uri.EscapeDataString(slug)}/" +
                $"{Uri.EscapeDataString(chapterRoute)}?type=comic"),
            root,
            MaximumReaderBytes,
            cancellationToken);

    private async ValueTask<IReadOnlyList<CatalogTransferUnit>>
        LoadChaptersAsync(
            Uri root,
            string slug,
            string? targetDisplayNumber,
            CancellationToken cancellationToken)
    {
        var targetKey = string.IsNullOrWhiteSpace(targetDisplayNumber)
            ? null
            : NormalizeChapterKey(targetDisplayNumber);
        var chapters = new Dictionary<string, ChapterEntry>(
            StringComparer.OrdinalIgnoreCase);
        var page = 1;
        var lastPage = 1;
        do
        {
            var endpoint = new Uri(
                root,
                $"api/series/{Uri.EscapeDataString(slug)}/chapters" +
                $"?page={page.ToString(CultureInfo.InvariantCulture)}" +
                "&direction=asc&type=comic");
            var json = await ReadStringAsync(
                endpoint,
                root,
                MaximumChapterListBytes,
                cancellationToken);
            using var document = JsonDocument.Parse(json);
            if (!TryGetProperty(
                    document.RootElement,
                    "data",
                    JsonValueKind.Array,
                    out var data))
            {
                throw new InvalidDataException(
                    "Olympus returned an invalid chapter-list response.");
            }

            foreach (var element in data.EnumerateArray())
            {
                if (chapters.Count >= MaximumChapterCount)
                {
                    throw new InvalidDataException(
                        "The Olympus title contains too many chapters.");
                }

                if (!TryGetScalarString(element, "id", out var id) ||
                    !SafeChapterIdRegex.IsMatch(id) ||
                    !TryGetScalarString(element, "name", out var chapterName))
                {
                    continue;
                }

                var displayNumber =
                    NormalizeChapterDisplayValue(chapterName);
                if (displayNumber.Length == 0)
                {
                    continue;
                }

                var normalized = NormalizeChapterKey(displayNumber);
                var releaseDate =
                    TryGetString(element, "published_at", out var publishedAt)
                        ? NormalizeDate(publishedAt)
                        : null;
                chapters.TryAdd(
                    normalized,
                    new ChapterEntry(
                        id,
                        displayNumber,
                        releaseDate));
            }

            if (targetKey is not null && chapters.ContainsKey(targetKey))
            {
                break;
            }

            if (TryGetProperty(
                    document.RootElement,
                    "meta",
                    JsonValueKind.Object,
                    out var meta) &&
                TryGetInt32(meta, "last_page", out var reportedLastPage))
            {
                lastPage = Math.Clamp(
                    reportedLastPage,
                    1,
                    MaximumChapterListPages);
            }

            page++;
        }
        while (page <= lastPage && page <= MaximumChapterListPages);

        var ordered = chapters.Values
            .Where(entry =>
                targetKey is null ||
                NormalizeChapterKey(entry.DisplayNumber) == targetKey)
            .OrderBy(static entry =>
                ParseChapterNumber(entry.DisplayNumber))
            .ThenBy(
                static entry => entry.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var chapter = ordered[index];
            _chapterIds[BuildChapterKey(
                root,
                slug,
                chapter.DisplayNumber)] = chapter.Id;
        }

        return ordered
            .Select(static (entry, index) =>
                new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = entry.DisplayNumber,
                    SortOrder = index,
                    ReleaseDate = entry.ReleaseDate
                })
            .ToArray();
    }

    private bool TryUseKnownChapterId(
        Uri root,
        string slug,
        IReadOnlyList<ApiProviderUnitRoute> routeHints,
        out IReadOnlyList<CatalogTransferUnit> units)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Chapter ||
                hint.SeasonNumber is not null ||
                hint.DisplayNumber is not { } displayNumber ||
                !SafeChapterIdRegex.IsMatch(hint.Route))
            {
                continue;
            }

            _chapterIds[
                BuildChapterKey(root, slug, displayNumber)] = hint.Route;
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

    private async ValueTask<string> GetSeriesListJsonAsync(
        Uri root,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var cached = Volatile.Read(ref _seriesListCache);
        if (IsFreshSeriesList(cached, root, now))
        {
            return cached!.Json;
        }

        await _seriesListCacheGate.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            cached = Volatile.Read(ref _seriesListCache);
            if (IsFreshSeriesList(cached, root, now))
            {
                return cached!.Json;
            }

            try
            {
                var json = await ReadStringAsync(
                    new Uri(root, "api/series/list"),
                    root,
                    MaximumSeriesListBytes,
                    cancellationToken);
                Volatile.Write(
                    ref _seriesListCache,
                    new SeriesListCacheEntry(root, now, json));
                return json;
            }
            catch (Exception exception)
                when (exception is not OperationCanceledException &&
                      cached is not null &&
                      SameOrigin(cached.Root, root))
            {
                return cached.Json;
            }
        }
        finally
        {
            _seriesListCacheGate.Release();
        }
    }

    private async ValueTask<string> ReadStringAsync(
        Uri endpoint,
        Uri siteRoot,
        int maximumBytes,
        CancellationToken cancellationToken,
        string accept = "application/json")
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
                $"Olympus response exceeds the {maximumBytes / 1024} KiB limit.");
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
                    $"Olympus response exceeds the {maximumBytes / 1024} KiB limit.");
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

    private void AddTrustedArtworkOrigin(Uri uri)
    {
        var origin = GetOrigin(uri);
        _trustedArtworkOrigins.TryAdd(origin.AbsoluteUri, origin);
    }

    private static IReadOnlyList<string> ReadNamedArray(
        JsonElement parent,
        string propertyName)
    {
        if (!TryGetProperty(
                parent,
                propertyName,
                JsonValueKind.Array,
                out var values))
        {
            return [];
        }

        return values.EnumerateArray()
            .Select(value =>
                value.ValueKind is JsonValueKind.Object &&
                TryGetString(value, "name", out var name)
                    ? NormalizeText(name)
                    : string.Empty)
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.CurrentCultureIgnoreCase)
            .ToArray();
    }

    private static string MapPublicationStatus(string value)
    {
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        if (normalized.Contains("activ", StringComparison.Ordinal) ||
            normalized.Contains("public", StringComparison.Ordinal) ||
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
        string slug,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            slug,
            seasonNumber: null,
            NormalizeChapterKey(displayNumber));

    private IReadOnlyList<CatalogTransferUnit> ExtractDailyChapters(
        string figureHtml,
        Uri root,
        string slug)
    {
        var chapters = new Dictionary<string, CatalogTransferUnit>(
            StringComparer.OrdinalIgnoreCase);
        foreach (Match anchor in AnchorRegex.Matches(figureHtml))
        {
            var attributes = anchor.Groups["attributes"].Value;
            var href = ReadHtmlAttribute(attributes, "href");
            if (!TryResolveHttpUri(root, href, out var chapterUri) ||
                !SameOrigin(root, chapterUri))
            {
                continue;
            }

            var segments = chapterUri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            string displayNumber;
            string? chapterId = null;
            if (segments.Length == 4 &&
                string.Equals(
                    segments[0],
                    "series",
                    StringComparison.OrdinalIgnoreCase) &&
                string.Equals(
                    segments[1],
                    $"comic-{slug}",
                    StringComparison.Ordinal) &&
                string.Equals(
                    segments[2],
                    "capitulo",
                    StringComparison.OrdinalIgnoreCase) &&
                decimal.TryParse(
                    segments[3].AsSpan(),
                    NumberStyles.AllowDecimalPoint,
                    CultureInfo.InvariantCulture,
                    out var number) &&
                number >= 0)
            {
                // The redesigned route carries the display number, not the API ID.
                // Keep the ID cache for IDs resolved from the chapter-list API.
                displayNumber = number.ToString(CultureInfo.InvariantCulture);
            }
            else if (segments.Length == 3 &&
                string.Equals(
                    segments[0],
                    "capitulo",
                    StringComparison.OrdinalIgnoreCase) &&
                SafeChapterIdRegex.IsMatch(segments[1]) &&
                string.Equals(
                    segments[2],
                    $"comic-{slug}",
                    StringComparison.Ordinal))
            {
                chapterId = segments[1];
                displayNumber = NormalizeChapterDisplayValue(
                    NormalizeHtmlText(anchor.Groups["body"].Value));
            }
            else
            {
                continue;
            }

            if (displayNumber.Length == 0)
            {
                continue;
            }

            var time = TimeRegex.Match(anchor.Groups["body"].Value);
            var releaseDate = time.Success
                ? NormalizeDate(
                    ReadHtmlAttribute(
                        time.Groups["attributes"].Value,
                        "datetime") ?? string.Empty)
                : null;
            var key = NormalizeChapterKey(displayNumber);
            chapters.TryAdd(
                key,
                new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = displayNumber,
                    ReleaseDate = releaseDate
                });
            if (chapterId is not null)
            {
                _chapterIds[BuildChapterKey(root, slug, displayNumber)] = chapterId;
            }
        }

        return chapters.Values
            .OrderBy(static chapter =>
                ParseChapterNumber(chapter.DisplayNumber))
            .ThenBy(
                static chapter => chapter.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .Select(static (chapter, index) =>
                chapter with
                {
                    SortOrder = index
                })
            .ToArray();
    }

    private static string ExtractDailyUpdateSection(string html)
    {
        var marker = -1;
        foreach (Match heading in SectionHeadingRegex.Matches(html))
        {
            var text = RemoveDiacritics(
                NormalizeHtmlText(heading.Groups["body"].Value));
            if (string.Equals(text, "Nuevos capitulos", StringComparison.OrdinalIgnoreCase) ||
                string.Equals(text, "Nuevos Lanzamientos", StringComparison.OrdinalIgnoreCase))
            {
                marker = heading.Index;
                break;
            }
        }
        if (marker < 0)
        {
            throw new InvalidDataException(
                "Olympus's daily-update section could not be found.");
        }

        var start = html.LastIndexOf(
            "<section",
            marker,
            StringComparison.OrdinalIgnoreCase);
        var end = html.IndexOf(
            "</section>",
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (start < 0 || end < 0)
        {
            throw new InvalidDataException(
                "Olympus returned an invalid daily-update section.");
        }

        return html[start..(end + "</section>".Length)];
    }

    private static bool TryGetSeriesSlug(
        Uri root,
        string? value,
        out string slug)
    {
        slug = string.Empty;
        if (!TryResolveHttpUri(root, value, out var uri) ||
            !SameOrigin(root, uri))
        {
            return false;
        }

        const string prefix = "/series/comic-";
        if (!uri.AbsolutePath.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        try
        {
            slug = Uri.UnescapeDataString(
                    uri.AbsolutePath[prefix.Length..])
                .Trim('/');
        }
        catch (UriFormatException)
        {
            return false;
        }

        return SafeSlugRegex.IsMatch(slug);
    }

    private static string? ReadHtmlAttribute(
        string attributes,
        string name)
    {
        foreach (Match match in HtmlAttributeRegex.Matches(attributes))
        {
            if (!string.Equals(
                    match.Groups["name"].Value,
                    name,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return WebUtility.HtmlDecode(
                match.Groups["double"].Success
                    ? match.Groups["double"].Value
                    : match.Groups["single"].Success
                        ? match.Groups["single"].Value
                        : match.Groups["bare"].Value);
        }

        return null;
    }

    private static string NormalizeHtmlText(string? value) =>
        HtmlWhitespaceRegex.Replace(
                WebUtility.HtmlDecode(
                    HtmlTagRegex.Replace(
                        value ?? string.Empty,
                        " "))
                    .Replace('\u00a0', ' '),
                " ")
            .Trim();

    private static string ValidateSlug(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var slug = value.Trim();
        if (!SafeSlugRegex.IsMatch(slug))
        {
            throw new ArgumentException(
                "The Olympus series identifier is invalid.",
                nameof(value));
        }

        return slug;
    }

    private static Uri NormalizeRootUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            baseUri.Scheme is not ("http" or "https") ||
            baseUri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "Olympus requires an absolute HTTP or HTTPS base URL without embedded credentials.",
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

    private static bool IsFreshSeriesList(
        SeriesListCacheEntry? entry,
        Uri root,
        DateTimeOffset now) =>
        entry is not null &&
        SameOrigin(entry.Root, root) &&
        now - entry.FetchedAt < SeriesListCacheLifetime;

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

    private static bool TryGetInt32(
        JsonElement element,
        string propertyName,
        out int value)
    {
        value = default;
        return element.ValueKind is JsonValueKind.Object &&
               element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.Number &&
               property.TryGetInt32(out value);
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

    private static string? NormalizeDate(string value) =>
        DateTimeOffset.TryParse(
            value,
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal,
            out var date)
            ? date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
            : null;

    private sealed record ChapterEntry(
        string Id,
        string DisplayNumber,
        string? ReleaseDate);

    private sealed record SeriesListCacheEntry(
        Uri Root,
        DateTimeOffset FetchedAt,
        string Json);
}

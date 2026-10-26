using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads Uchuujin Mangas' public WordPress/MangaReader catalog, series pages,
/// and the JSON reader configuration embedded in chapter pages.
/// </summary>
public sealed partial class UchuujinMangasApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "uchuujinmangas";

    private const int MaximumCatalogBytes = 8 * 1024 * 1024;
    private const int MaximumDetailBytes = 8 * 1024 * 1024;
    private const int MaximumReaderBytes = 24 * 1024 * 1024;
    private const int MaximumCards = 1_000;
    private const int MaximumChapters = 20_000;
    private const int MaximumPages = 2_000;
    private const int MaximumDailyUpdates = 24;
    private static readonly TimeSpan RegexTimeout = TimeSpan.FromSeconds(2);
    private static readonly Regex SafeSlugRegex = new(
        "^[a-z0-9][a-z0-9-]{0,249}$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        RegexTimeout);
    private static readonly Regex ChapterNumberRegex = new(
        @"\d+(?:[.,]\d+)?",
        RegexOptions.CultureInvariant,
        RegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly ConcurrentDictionary<string, Uri> _chapterRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);

    public UchuujinMangasApiContentProvider(HttpClient httpClient)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.UchuujinMangas;

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
        var html = await ReadHtmlAsync(
            root,
            new Uri(root, "manga/?order=update"),
            root,
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        return ParseCards(root, html, query: null)
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
        var html = await ReadHtmlAsync(
            root,
            BuildSearchUri(root, query.Trim(), page: 1),
            root,
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        return ParseCards(root, html, query);
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
        var html = await ReadHtmlAsync(
            root,
            BuildSearchUri(root, request.Query, page),
            root,
            MaximumCatalogBytes,
            cancellationToken).ConfigureAwait(false);
        var items = ParseCards(root, html, request.Query)
            .Take(request.PageSize)
            .ToArray();
        var next = HasNextPage(html)
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
        var slug = ValidateSlug(searchItem.ExternalId);
        var detailUri = BuildDetailUri(root, slug);
        var html = await ReadHtmlAsync(
            root,
            detailUri,
            root,
            MaximumDetailBytes,
            cancellationToken).ConfigureAwait(false);
        var title = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "h1",
                "entry-title"));
        if (title.Length == 0)
        {
            if (searchItem.RequireProviderTitle)
            {
                throw new InvalidDataException(
                    "The Uchuujin Mangas detail page did not include a title.");
            }

            title = searchItem.PreviewItem.Name;
        }

        var description = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "div",
                "entry-content"));
        var thumbnail = ResolveArtwork(
                root,
                TmoProviderText.ExtractMetaContent(
                    html,
                    "property",
                    "og:image")) ??
            searchItem.PreviewItem.ThumbnailRef;
        var type = ExtractInfoValue(html, "Tipo");
        var year = ParseReleaseYear(ExtractInfoValue(html, "Lanzado"));
        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = MapSubtype(type),
            Name = title,
            Description = description.Length == 0 ? null : description,
            ReleaseYear = year,
            PublicationStatus = MapStatus(ExtractInfoValue(html, "Estado")),
            ThumbnailRef = thumbnail,
            BackdropRef = thumbnail,
            Genres = ExtractGenres(root, html),
            Categories = BuildCategories(type),
            AlternativeNames = ExtractAliases(html, title),
            Units = ExtractChapters(root, slug, html)
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

        var slug = ValidateSlug(searchItem.ExternalId);
        return _chapterRoutes.TryGetValue(
                   BuildChapterKey(root, slug, unit.DisplayNumber),
                   out var readerUri)
            ?
            [
                new ApiProviderUnitRoute(
                    ContentUnitKind.Chapter,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    readerUri.PathAndQuery)
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

        var slug = ValidateSlug(searchItem.ExternalId);
        var displayNumber = NormalizeChapterNumber(unit.DisplayNumber);
        var readerUri = ResolveKnownReaderUri(
            root,
            slug,
            searchItem,
            displayNumber);
        if (readerUri is null)
        {
            _ = await LoadItemAsync(root, searchItem, cancellationToken)
                .ConfigureAwait(false);
            _chapterRoutes.TryGetValue(
                BuildChapterKey(root, slug, displayNumber),
                out readerUri);
        }

        if (readerUri is null)
        {
            return null;
        }

        var html = await ReadHtmlAsync(
            root,
            readerUri,
            BuildDetailUri(root, slug),
            MaximumReaderBytes,
            cancellationToken).ConfigureAwait(false);
        var imageUris = ParseReaderImages(root, html);
        if (imageUris.Count == 0)
        {
            throw new InvalidDataException(
                "The Uchuujin Mangas chapter did not contain reader pages.");
        }

        foreach (var imageUri in imageUris)
        {
            RegisterArtworkOrigin(imageUri);
        }

        return new ApiContentImageSet(
            ContentUnitKind.Chapter,
            unit.DisplayNumber,
            seasonNumber: null,
            imageUris.Select((uri, index) =>
                new ApiContentImagePage(index, uri, readerUri)));
    }

    internal static IReadOnlyList<Uri> ParseReaderImages(Uri root, string html)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(html);
        if (!TryExtractReaderObject(html, out var readerJson))
        {
            return [];
        }

        try
        {
            using var document = JsonDocument.Parse(readerJson);
            if (document.RootElement.ValueKind is not JsonValueKind.Object ||
                !document.RootElement.TryGetProperty(
                    "sources",
                    out var sources) ||
                sources.ValueKind is not JsonValueKind.Array)
            {
                return [];
            }

            foreach (var source in sources.EnumerateArray())
            {
                if (source.ValueKind is not JsonValueKind.Object ||
                    !source.TryGetProperty("images", out var images) ||
                    images.ValueKind is not JsonValueKind.Array)
                {
                    continue;
                }

                var result = new List<Uri>();
                var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var image in images.EnumerateArray())
                {
                    if (result.Count >= MaximumPages)
                    {
                        throw new InvalidDataException(
                            "The Uchuujin Mangas chapter contains too many pages.");
                    }

                    if (image.ValueKind is JsonValueKind.String &&
                        TmoProviderUri.TryResolveProviderAsset(
                            root,
                            image.GetString(),
                            out var uri) &&
                        seen.Add(uri.AbsoluteUri))
                    {
                        result.Add(uri);
                    }
                }

                if (result.Count > 0)
                {
                    return result;
                }
            }

            return [];
        }
        catch (JsonException exception)
        {
            throw new InvalidDataException(
                "Uchuujin Mangas embedded an invalid reader configuration.",
                exception);
        }
    }

    private IReadOnlyList<ApiContentSearchItem> ParseCards(
        Uri root,
        string html,
        string? query)
    {
        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in TmoProviderText.EnumerateClassBlocks(
                     html,
                     "div",
                     "bs",
                     MaximumCards))
        {
            foreach (var anchor in TmoProviderText.EnumerateElements(block, "a"))
            {
                if (!TryParseDetailUri(
                        root,
                        TmoProviderText.GetAttribute(anchor.Attributes, "href"),
                        out var slug,
                        out _) ||
                    !seen.Add(slug))
                {
                    continue;
                }

                var title = TmoProviderText.NormalizeText(
                    TmoProviderText.GetAttribute(anchor.Attributes, "title"));
                if (title.Length == 0)
                {
                    title = TmoProviderText.HtmlToText(
                        TmoProviderText.FindElementContentByClass(
                            anchor.Content,
                            "div",
                            "tt"));
                }

                if (title.Length == 0 ||
                    query is not null &&
                    !TmoProviderText.Contains(title, query))
                {
                    continue;
                }

                var thumbnail = ResolveArtwork(
                    root,
                    TmoProviderText.FindImageSourceByClass(
                        anchor.Content,
                        "ts-post-image"));
                results.Add(new ApiContentSearchItem(
                    slug,
                    new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = title,
                        ThumbnailRef = thumbnail,
                        Categories = ["Manga"]
                    }));
                break;
            }
        }

        return results;
    }

    private IReadOnlyList<CatalogTransferUnit> ExtractChapters(
        Uri root,
        string slug,
        string html)
    {
        var chapters = new Dictionary<string, (string Name, Uri Uri)>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var block in TmoProviderText.EnumerateClassBlocks(
                     html,
                     "div",
                     "eph-num",
                     MaximumChapters))
        {
            foreach (var anchor in TmoProviderText.EnumerateElements(block, "a"))
            {
                if (!TryParseChapterUri(
                        root,
                        TmoProviderText.GetAttribute(anchor.Attributes, "href"),
                        out var readerUri))
                {
                    continue;
                }

                var label = TmoProviderText.HtmlToText(
                    TmoProviderText.FindElementContentByClass(
                        anchor.Content,
                        "span",
                        "chapternum"));
                if (label.Length == 0)
                {
                    label = TmoProviderText.HtmlToText(anchor.Content);
                }

                var match = ChapterNumberRegex.Match(label);
                if (!match.Success)
                {
                    continue;
                }

                var number = NormalizeChapterNumber(match.Value);
                if (chapters.ContainsKey(number))
                {
                    continue;
                }

                chapters.Add(number, (label, readerUri));
                _chapterRoutes[BuildChapterKey(root, slug, number)] = readerUri;
                break;
            }
        }

        return chapters
            .Select(static pair => new
            {
                Number = pair.Key,
                pair.Value.Name
            })
            .OrderBy(static chapter => ParseChapterNumber(chapter.Number))
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

    private Uri? ResolveKnownReaderUri(
        Uri root,
        string slug,
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
                TryParseChapterUri(root, route.Route, out var routeUri))
            {
                _chapterRoutes[BuildChapterKey(root, slug, displayNumber)] =
                    routeUri;
                return routeUri;
            }
        }

        return _chapterRoutes.TryGetValue(
            BuildChapterKey(root, slug, displayNumber),
            out var cached)
            ? cached
            : null;
    }

    private string? ResolveArtwork(Uri root, string? value)
    {
        if (!TmoProviderUri.TryResolveProviderAsset(root, value, out var uri))
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

    private async ValueTask<string> ReadHtmlAsync(
        Uri root,
        Uri endpoint,
        Uri referrer,
        int maximumBytes,
        CancellationToken cancellationToken) =>
        await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            endpoint,
            referrer,
            maximumBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "Uchuujin Mangas",
            cancellationToken).ConfigureAwait(false);

    private static Uri BuildSearchUri(Uri root, string query, int page) =>
        new(
            root,
            $"?s={Uri.EscapeDataString(query)}&paged={page.ToString(CultureInfo.InvariantCulture)}");

    private static Uri BuildDetailUri(Uri root, string slug) =>
        new(root, $"manga/{Uri.EscapeDataString(slug)}/");

    private static bool TryParseDetailUri(
        Uri root,
        string? value,
        out string slug,
        out Uri uri)
    {
        slug = string.Empty;
        uri = null!;
        if (!TmoProviderUri.TryResolveSameOrigin(root, value, out var candidate))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(candidate);
        if (segments.Length != 2 ||
            !string.Equals(segments[0], "manga", StringComparison.OrdinalIgnoreCase) ||
            !SafeSlugRegex.IsMatch(segments[1]))
        {
            return false;
        }

        slug = segments[1];
        uri = candidate;
        return true;
    }

    private static bool TryParseChapterUri(
        Uri root,
        string? value,
        out Uri uri)
    {
        uri = null!;
        if (!TmoProviderUri.TryResolveSameOrigin(root, value, out var candidate))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(candidate);
        if (segments.Length != 4 ||
            segments[0].Length != 4 ||
            segments[1].Length != 2 ||
            segments[2].Length != 2 ||
            !int.TryParse(segments[0], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(segments[1], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !int.TryParse(segments[2], NumberStyles.None, CultureInfo.InvariantCulture, out _) ||
            !SafeSlugRegex.IsMatch(segments[3]))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool TryExtractReaderObject(
        string html,
        out ReadOnlyMemory<char> json)
    {
        json = default;
        const string marker = "ts_reader.run(";
        var markerIndex = html.IndexOf(marker, StringComparison.Ordinal);
        if (markerIndex < 0)
        {
            return false;
        }

        var source = html.AsSpan(markerIndex + marker.Length);
        var start = 0;
        while (start < source.Length && char.IsWhiteSpace(source[start]))
        {
            start++;
        }

        if (start >= source.Length || source[start] != '{')
        {
            return false;
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

            if (character == '"')
            {
                inString = true;
            }
            else if (character == '{')
            {
                depth++;
            }
            else if (character == '}' && --depth == 0)
            {
                var absoluteStart = markerIndex + marker.Length + start;
                json = html.AsMemory(
                    absoluteStart,
                    index - start + 1);
                return true;
            }
        }

        return false;
    }

    private static string ExtractInfoValue(string html, string label)
    {
        foreach (var block in TmoProviderText.EnumerateClassBlocks(
                     html,
                     "div",
                     "imptdt",
                     100))
        {
            var text = TmoProviderText.HtmlToText(block);
            if (!text.StartsWith(label, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return text[label.Length..].Trim(' ', ':');
        }

        return string.Empty;
    }

    private static IReadOnlyList<string> ExtractGenres(Uri root, string html)
    {
        var genres = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            if (!TmoProviderUri.TryResolveSameOrigin(
                    root,
                    TmoProviderText.GetAttribute(anchor.Attributes, "href"),
                    out var uri))
            {
                continue;
            }

            var segments = TmoProviderUri.GetPathSegments(uri);
            if (segments.Length != 2 ||
                !string.Equals(segments[0], "genres", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var genre = TmoProviderText.HtmlToText(anchor.Content);
            if (genre.Length > 0 && seen.Add(genre))
            {
                genres.Add(genre);
            }
        }

        return genres;
    }

    private static IReadOnlyList<CatalogTransferAlias> ExtractAliases(
        string html,
        string canonicalName)
    {
        var text = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "span",
                "alternative"));
        return text
            .Split(';', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(name => !string.Equals(
                name,
                canonicalName,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static name => new CatalogTransferAlias
            {
                Name = name,
                AliasType = "provider"
            })
            .ToArray();
    }

    private static IReadOnlyList<string> BuildCategories(string type) =>
        string.IsNullOrWhiteSpace(type) ||
        string.Equals(type, "Manga", StringComparison.OrdinalIgnoreCase)
            ? ["Manga"]
            : ["Manga", type];

    /// <summary>
    /// Maps the site's "Tipo" label onto a registered book subtype.
    /// <para>
    /// Only book.book, book.novel, book.manga, book.comic, book.light_novel
    /// and book.web_novel exist. Manhwa and manhua are not subtypes of their
    /// own, so returning them verbatim made <c>CatalogSubtypeResolver</c>
    /// throw and every Korean or Chinese series fail to import. They belong to
    /// the manga family, which matches how ZonaTMO's mapper treats them.
    /// </para>
    /// </summary>
    private static string MapSubtype(string value) =>
        value.ToLowerInvariant() switch
        {
            "comic" => "comic",
            "novel" or "novela" => "novel",
            _ => "manga"
        };

    private static string MapStatus(string value) =>
        value.ToLowerInvariant() switch
        {
            "ongoing" => "ongoing",
            "completed" => "completed",
            "paused" or "pausa" or "hiatus" => "paused",
            "cancelled" => "cancelled",
            _ => "unknown"
        };

    private static int? ParseReleaseYear(string value) =>
        int.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var year) &&
        year is >= ApiContentSearchRequest.MinimumReleaseYear and
            <= ApiContentSearchRequest.MaximumReleaseYear
            ? year
            : null;

    private static bool HasNextPage(string html)
    {
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            if (string.Equals(
                    TmoProviderText.GetAttribute(anchor.Attributes, "rel"),
                    "next",
                    StringComparison.OrdinalIgnoreCase) ||
                TmoProviderText.HtmlToText(anchor.Content)
                    .Contains("Siguiente", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        return false;
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
                "Uchuujin Mangas returned an invalid search page token.",
                nameof(token));
        }

        return page;
    }

    private static string ValidateSlug(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var slug = value.Trim();
        if (!SafeSlugRegex.IsMatch(slug))
        {
            throw new ArgumentException(
                "The Uchuujin Mangas title identifier is invalid.",
                nameof(value));
        }

        return slug;
    }

    private static string NormalizeChapterNumber(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim().Replace(',', '.');
        if (!decimal.TryParse(
                normalized,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out var number) ||
            number < 0)
        {
            throw new InvalidDataException(
                "Uchuujin Mangas returned an invalid chapter number.");
        }

        return number.ToString(
            "0.############################",
            CultureInfo.InvariantCulture);
    }

    private static decimal ParseChapterNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.AllowDecimalPoint,
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
            ApiContentProviderCacheKey.NormalizeDisplayNumber(displayNumber));

    private static Uri NormalizeRoot(Uri baseUri) =>
        TmoProviderUri.NormalizeRoot(baseUri, "Uchuujin Mangas");
}

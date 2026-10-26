using System.Collections.Concurrent;
using System.Globalization;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads ZonaTMO's public library, manga detail pages, and image reader. The
/// provider never uses the account-only reaction or session endpoints present
/// on reader pages.
/// </summary>
public sealed partial class ZonaTmoApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "zonatmo";

    private const int MaximumLibraryBytes = 2 * 1024 * 1024;
    private const int MaximumItemBytes = 4 * 1024 * 1024;
    private const int MaximumReaderBytes = 2 * 1024 * 1024;
    private const int MaximumLibraryCards = 500;
    private const int MaximumChapterCount = 20_000;
    private const int MaximumPageCount = 2_000;
    private const int MaximumPageNumber = 99_999;
    private const int MaximumDailyUpdateResults = 24;

    private readonly HttpClient _httpClient;
    private readonly ApiContentGenreMapCache _genreMaps = new();
    private readonly ConcurrentDictionary<string, Uri> _chapterRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);

    public ZonaTmoApiContentProvider(HttpClient httpClient)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.ZonaTmo;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.Native,
        supportsFilterOnlySearch: true);

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        return _trustedArtworkOrigins.Values
            .Prepend(TmoProviderUri.GetOrigin(root))
            .DistinctBy(
                static uri => uri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
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
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        var endpoint = new Uri(
            root,
            "biblioteca?order_item=creation&order_dir=desc&page=1");
        var html = await ReadLibraryAsync(
            root,
            endpoint,
            cancellationToken).ConfigureAwait(false);
        return ParseLibraryCards(
                root,
                html,
                query: null,
                filteredGenre: null)
            .Take(MaximumDailyUpdateResults)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (mediaType is not null and not MediaType.Book)
        {
            return [];
        }

        var endpoint = BuildLibraryUri(
            root,
            query.Trim(),
            genreToken: null,
            page: 1);
        var html = await ReadLibraryAsync(
            root,
            endpoint,
            cancellationToken).ConfigureAwait(false);
        return ParseLibraryCards(
            root,
            html,
            query,
            filteredGenre: null);
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Book ||
            request.ReleaseYear is not null)
        {
            return ApiContentSearchPage.Empty;
        }

        string? genreToken = null;
        if (request.Genre is not null)
        {
            var map = await LoadGenreMapAsync(root, cancellationToken)
                .ConfigureAwait(false);
            if (!map.TryGetToken(request.Genre, out genreToken))
            {
                return ApiContentSearchPage.Empty;
            }
        }

        var page = ParsePageToken(request.ContinuationToken);
        var endpoint = BuildLibraryUri(
            root,
            request.Query,
            genreToken,
            page);
        var html = await ReadLibraryAsync(
            root,
            endpoint,
            cancellationToken).ConfigureAwait(false);
        var items = ParseLibraryCards(
                root,
                html,
                request.Query,
                request.Genre)
            .Take(request.PageSize)
            .ToArray();
        var next = HasNextPage(html)
            ? checked(page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        return new ApiContentSearchPage(items, next);
    }

    public ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default) =>
        LoadItemCoreAsync(baseUri, searchItem, cancellationToken);

    public ValueTask<ApiContentItem> LoadItemForUnitAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return LoadItemCoreAsync(baseUri, searchItem, cancellationToken);
    }

    public IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Chapter ||
            unit.SeasonNumber is not null)
        {
            return [];
        }

        var identity = ParseExternalId(searchItem.ExternalId);
        var key = BuildChapterKey(
            root,
            identity.ExternalId,
            unit.DisplayNumber);
        return _chapterRoutes.TryGetValue(key, out var readerUri) &&
               TryParseReaderUri(root, readerUri.AbsoluteUri, out _)
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
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Chapter ||
            unit.SeasonNumber is not null)
        {
            return null;
        }

        var identity = ParseExternalId(searchItem.ExternalId);
        var readerUri = await ResolveReaderUriAsync(
            root,
            identity,
            searchItem,
            unit,
            cancellationToken).ConfigureAwait(false);
        if (readerUri is null)
        {
            return null;
        }

        var html = await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            readerUri,
            BuildItemUri(root, identity),
            MaximumReaderBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "ZonaTMO",
            cancellationToken).ConfigureAwait(false);
        var rawPages = new List<(int PageNumber, Uri Uri)>();
        var seenPages = new HashSet<int>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in TmoProviderText.EnumerateClassBlocks(
                     html,
                     "div",
                     "reader-img-wrap",
                     MaximumPageCount))
        {
            var opening = TmoProviderText.GetOpeningTagAttributes(block, "div");
            if (!int.TryParse(
                    TmoProviderText.GetAttribute(opening, "data-page"),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var pageNumber) ||
                pageNumber is < 1 or > MaximumPageCount)
            {
                continue;
            }

            string? source = null;
            foreach (var attributes in
                     TmoProviderText.EnumerateTagAttributes(block, "img"))
            {
                if (!TmoProviderText.HasClass(
                        TmoProviderText.GetAttribute(attributes, "class"),
                        "reader-image"))
                {
                    continue;
                }

                source = TmoProviderText.GetAttribute(attributes, "data-src") ??
                    TmoProviderText.GetAttribute(attributes, "src");
                break;
            }

            if (!TmoProviderUri.TryResolveProviderAsset(
                    root,
                    source,
                    out var imageUri) ||
                !seenPages.Add(pageNumber) ||
                !seenUris.Add(imageUri.AbsoluteUri))
            {
                continue;
            }

            RegisterArtworkOrigin(imageUri);
            rawPages.Add((pageNumber, imageUri));
        }

        if (rawPages.Count == 0)
        {
            return null;
        }

        var pages = rawPages
            .OrderBy(static page => page.PageNumber)
            .Select((page, index) => new ApiContentImagePage(
                index,
                page.Uri,
                readerUri))
            .ToArray();
        return new ApiContentImageSet(
            ContentUnitKind.Chapter,
            unit.DisplayNumber,
            seasonNumber: null,
            pages);
    }

    internal static ApiContentGenreMap ParseGenreMap(string html)
    {
        var labelsById = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var label in TmoProviderText.EnumerateElements(html, "label"))
        {
            var id = TmoProviderText.GetAttribute(label.Attributes, "for");
            var text = TmoProviderText.HtmlToText(label.Content);
            if (!string.IsNullOrWhiteSpace(id) && text.Length > 0)
            {
                labelsById.TryAdd(id, text);
            }
        }

        var entries = new List<(string Label, int Token)>();
        foreach (var input in
                 TmoProviderText.EnumerateTagAttributes(html, "input"))
        {
            if (!string.Equals(
                    TmoProviderText.GetAttribute(input, "name"),
                    "genders[]",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var id = TmoProviderText.GetAttribute(input, "id");
            var token = TmoProviderText.GetAttribute(input, "value");
            if (id is null ||
                token is null ||
                !labelsById.TryGetValue(id, out var label) ||
                !int.TryParse(
                    token,
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var numericToken) ||
                numericToken <= 0)
            {
                continue;
            }

            entries.Add((label, numericToken));
        }

        // The checkbox list is alphabetical by label and carries a later
        // English block ("Adventure" = 77) alongside the original Spanish one
        // ("Aventura" = 2). Both match the same genre, and the map keeps the
        // first label it sees, so document order would pick the English ids —
        // which no title is actually tagged with. Lowest id wins instead.
        return ApiContentGenreMap.FromUpstreamLabels(
            entries
                .OrderBy(static entry => entry.Token)
                .Select(static entry => (
                    entry.Label,
                    entry.Token.ToString(CultureInfo.InvariantCulture)))
                .ToArray());
    }

    private async ValueTask<ApiContentItem> LoadItemCoreAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "ZonaTMO");
        ArgumentNullException.ThrowIfNull(searchItem);
        var identity = ParseExternalId(searchItem.ExternalId);
        var endpoint = BuildItemUri(root, identity);
        var html = await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            endpoint,
            root,
            MaximumItemBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "ZonaTMO",
            cancellationToken).ConfigureAwait(false);

        var title = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "h1",
                "element-title"));
        if (title.Length == 0)
        {
            if (searchItem.RequireProviderTitle)
            {
                throw new InvalidDataException(
                    "The ZonaTMO detail page did not include a title.");
            }

            title = searchItem.PreviewItem.Name;
        }

        var typeLabel = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "h1",
                "book-type"));
        var description = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentById(
                html,
                "p",
                "manga-synopsis"));
        if (description.Length == 0)
        {
            description = TmoProviderText.HtmlToText(
                TmoProviderText.FindElementContentByClass(
                    html,
                    "p",
                    "element-description"));
        }

        var publicationStatus = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "span",
                "book-status"));
        var genres = ExtractGenres(html);
        if (genres.Count == 0)
        {
            genres = searchItem.PreviewItem.Genres;
        }

        var thumbnail = ResolveArtwork(
            root,
            TmoProviderText.FindImageSourceByClass(html, "book-thumbnail")) ??
            searchItem.PreviewItem.ThumbnailRef;
        // The detail page renders the demography as a div, where the library
        // card uses a span. Reading it here keeps the two paths agreeing on a
        // title's categories instead of dropping the shelf on import.
        var demography = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "div",
                "demography"));
        var aliases = ExtractAliases(html, title);
        var chapters = ExtractChapters(root, identity, html);
        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = MapBookSubtype(
                typeLabel.Length == 0 ? identity.Type : typeLabel),
            Name = title,
            Description = description.Length == 0 ? null : description,
            PublicationStatus = publicationStatus.Length == 0
                ? searchItem.PreviewItem.PublicationStatus
                : publicationStatus,
            ThumbnailRef = thumbnail,
            BackdropRef = thumbnail,
            Genres = genres,
            // ZonaTMO renders the detail badge upper cased, so a manga-type
            // title yields "Manga" and "MANGA". The validator normalizes case
            // before checking for duplicates, so without this the whole item
            // is rejected on import.
            Categories = new[]
                {
                    "Manga",
                    typeLabel,
                    demography
                }
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray(),
            AlternativeNames = aliases,
            Units = chapters
        });
    }

    private IReadOnlyList<ApiContentSearchItem> ParseLibraryCards(
        Uri root,
        string html,
        string? query,
        ApiContentGenre? filteredGenre)
    {
        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in TmoProviderText.EnumerateClassBlocks(
                     html,
                     "div",
                     "element",
                     MaximumLibraryCards))
        {
            LibraryIdentity identity = default;
            string? title = null;
            foreach (var anchor in TmoProviderText.EnumerateElements(block, "a"))
            {
                if (!TryParseLibraryUri(
                        root,
                        TmoProviderText.GetAttribute(
                            anchor.Attributes,
                            "href"),
                        out identity))
                {
                    continue;
                }

                foreach (var heading in
                         TmoProviderText.EnumerateElements(anchor.Content, "h4"))
                {
                    if (!TmoProviderText.HasClass(
                            TmoProviderText.GetAttribute(
                                heading.Attributes,
                                "class"),
                            "text-truncate"))
                    {
                        continue;
                    }

                    title = TmoProviderText.NormalizeText(
                        TmoProviderText.GetAttribute(
                            heading.Attributes,
                            "title"));
                    if (title.Length == 0)
                    {
                        title = TmoProviderText.HtmlToText(heading.Content);
                    }

                    break;
                }

                if (!string.IsNullOrWhiteSpace(title))
                {
                    break;
                }
            }

            if (string.IsNullOrWhiteSpace(title) ||
                !seen.Add(identity.ExternalId) ||
                query is not null &&
                !TmoProviderText.Contains(title, query))
            {
                continue;
            }

            var thumbnail = ResolveArtwork(
                root,
                TmoProviderText.FindImageSourceByClass(
                    block,
                    "cover-bg-img"));
            var typeLabel = TmoProviderText.HtmlToText(
                TmoProviderText.FindElementContentByClass(
                    block,
                    "span",
                    "book-type"));
            var demography = TmoProviderText.HtmlToText(
                TmoProviderText.FindElementContentByClass(
                    block,
                    "span",
                    "demography"));
            var status = TmoProviderText.HtmlToText(
                TmoProviderText.FindElementContentByClass(
                    block,
                    "span",
                    "book-meta-status"));
            var categories = new[]
                {
                    "Manga",
                    typeLabel,
                    demography
                }
                .Where(static value => value.Length > 0)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            results.Add(new ApiContentSearchItem(
                identity.ExternalId,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = MapBookSubtype(
                        typeLabel.Length == 0
                            ? identity.Type
                            : typeLabel),
                    Name = title,
                    PublicationStatus = status.Length == 0
                        ? null
                        : status,
                    ThumbnailRef = thumbnail,
                    Genres = filteredGenre is null
                        ? []
                        :
                        [
                            filteredGenre.Aliases.FirstOrDefault() ??
                            filteredGenre.Key
                        ],
                    Categories = categories
                }));
        }

        return results;
    }

    private IReadOnlyList<CatalogTransferUnit> ExtractChapters(
        Uri root,
        LibraryIdentity identity,
        string html)
    {
        var chapters = new List<(string DisplayNumber, Uri ReaderUri)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var block in TmoProviderText.EnumerateClassBlocks(
                     html,
                     "li",
                     "upload-link",
                     MaximumChapterCount))
        {
            var opening = TmoProviderText.GetOpeningTagAttributes(block, "li");
            if (!TryValidateDisplayNumber(
                    TmoProviderText.GetAttribute(
                        opening,
                        "data-chapter-number"),
                    out var displayNumber))
            {
                continue;
            }

            Uri? readerUri = null;
            foreach (var anchor in TmoProviderText.EnumerateElements(block, "a"))
            {
                if (TryParseReaderUri(
                        root,
                        TmoProviderText.GetAttribute(
                            anchor.Attributes,
                            "href"),
                        out var candidate))
                {
                    readerUri = candidate;
                    break;
                }
            }

            var normalized =
                ApiContentProviderCacheKey.NormalizeDisplayNumber(
                    displayNumber);
            if (readerUri is null || !seen.Add(normalized))
            {
                continue;
            }

            _chapterRoutes[BuildChapterKey(
                root,
                identity.ExternalId,
                displayNumber)] = readerUri;
            chapters.Add((displayNumber, readerUri));
        }

        return chapters
            .OrderBy(
                static chapter => ParseDisplayNumber(chapter.DisplayNumber))
            .ThenBy(
                static chapter => chapter.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .Select(static (chapter, index) => new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = chapter.DisplayNumber,
                SortOrder = index,
                Name = $"Capítulo {chapter.DisplayNumber}"
            })
            .ToArray();
    }

    private async ValueTask<Uri?> ResolveReaderUriAsync(
        Uri root,
        LibraryIdentity identity,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken)
    {
        foreach (var route in searchItem.UnitRoutes)
        {
            if (route.UnitKind is not ContentUnitKind.Chapter ||
                route.SeasonNumber is not null ||
                !ApiContentUnitRequest.DisplayNumbersMatch(
                    route.DisplayNumber,
                    unit.DisplayNumber) ||
                !TryParseReaderUri(root, route.Route, out var routeUri))
            {
                continue;
            }

            _chapterRoutes[BuildChapterKey(
                root,
                identity.ExternalId,
                unit.DisplayNumber)] = routeUri;
            return routeUri;
        }

        var key = BuildChapterKey(
            root,
            identity.ExternalId,
            unit.DisplayNumber);
        if (_chapterRoutes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _ = await LoadItemCoreAsync(
            root,
            searchItem,
            cancellationToken).ConfigureAwait(false);
        return _chapterRoutes.TryGetValue(key, out cached)
            ? cached
            : null;
    }

    private ValueTask<ApiContentGenreMap> LoadGenreMapAsync(
        Uri root,
        CancellationToken cancellationToken) =>
        _genreMaps.GetAsync(
            root,
            async token => ParseGenreMap(await ReadLibraryAsync(
                root,
                new Uri(root, "biblioteca"),
                token).ConfigureAwait(false)),
            cancellationToken);

    private async ValueTask<string> ReadLibraryAsync(
        Uri root,
        Uri endpoint,
        CancellationToken cancellationToken) =>
        await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            endpoint,
            new Uri(root, "biblioteca"),
            MaximumLibraryBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "ZonaTMO",
            cancellationToken).ConfigureAwait(false);

    private static Uri BuildLibraryUri(
        Uri root,
        string? query,
        string? genreToken,
        int page)
    {
        var parameters = new List<string>(4);
        if (!string.IsNullOrWhiteSpace(query))
        {
            parameters.Add("filter_by=title");
            parameters.Add($"title={Uri.EscapeDataString(query.Trim())}");
        }

        if (!string.IsNullOrWhiteSpace(genreToken))
        {
            parameters.Add(
                $"genders%5B%5D={Uri.EscapeDataString(genreToken)}");
        }

        parameters.Add($"page={page.ToString(CultureInfo.InvariantCulture)}");
        return new Uri(root, $"biblioteca?{string.Join('&', parameters)}");
    }

    private static bool HasNextPage(string html)
    {
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            var relation = TmoProviderText.GetAttribute(
                anchor.Attributes,
                "rel");
            if (relation?.Split(
                    (char[]?)null,
                    StringSplitOptions.RemoveEmptyEntries)
                .Contains("next", StringComparer.OrdinalIgnoreCase) is true)
            {
                return true;
            }
        }

        return false;
    }

    private static IReadOnlyList<string> ExtractGenres(string html)
    {
        var genres = new List<string>();
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            var href = TmoProviderText.GetAttribute(anchor.Attributes, "href");
            if (string.IsNullOrWhiteSpace(href) ||
                !href.Contains(
                    "genders[]=",
                    StringComparison.OrdinalIgnoreCase) &&
                !href.Contains(
                    "genders%5B%5D=",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var label = TmoProviderText.HtmlToText(anchor.Content);
            if (label.Length > 0 &&
                !genres.Contains(label, StringComparer.OrdinalIgnoreCase))
            {
                genres.Add(label);
            }
        }

        return genres;
    }

    private static IReadOnlyList<CatalogTransferAlias> ExtractAliases(
        string html,
        string title)
    {
        var names = new HashSet<string>(StringComparer.CurrentCultureIgnoreCase);
        foreach (var span in TmoProviderText.EnumerateElements(html, "span"))
        {
            if (!TmoProviderText.HasClass(
                    TmoProviderText.GetAttribute(span.Attributes, "class"),
                    "syn-badge"))
            {
                continue;
            }

            foreach (var value in TmoProviderText.HtmlToText(span.Content)
                         .Split(
                             ';',
                             StringSplitOptions.RemoveEmptyEntries |
                             StringSplitOptions.TrimEntries))
            {
                if (!string.Equals(
                        value,
                        title,
                        StringComparison.CurrentCultureIgnoreCase))
                {
                    names.Add(value);
                }
            }
        }

        return names
            .Where(CatalogTransferIdentity.IsUsableName)
            .Select(static name => new CatalogTransferAlias { Name = name })
            .ToArray();
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
        var origin = TmoProviderUri.GetOrigin(uri);
        _trustedArtworkOrigins.TryAdd(origin.AbsoluteUri, origin);
    }

    private static bool TryParseLibraryUri(
        Uri root,
        string? rawUri,
        out LibraryIdentity identity)
    {
        identity = default;
        if (!TmoProviderUri.TryResolveSameOrigin(root, rawUri, out var uri))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(uri);
        if (segments.Length != 4 ||
            !string.Equals(
                segments[0],
                "library",
                StringComparison.OrdinalIgnoreCase) ||
            !TryValidateType(segments[1], out var type) ||
            !long.TryParse(
                segments[2],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id) ||
            id <= 0 ||
            !TryValidateSlug(segments[3], out var slug))
        {
            return false;
        }

        identity = new LibraryIdentity(type, id, slug);
        return true;
    }

    private static LibraryIdentity ParseExternalId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        var segments = externalId.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (segments.Length != 3 ||
            !TryValidateType(segments[0], out var type) ||
            !long.TryParse(
                segments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id) ||
            id <= 0 ||
            !TryValidateSlug(segments[2], out var slug))
        {
            throw new ArgumentException(
                "The ZonaTMO library ID is invalid.",
                nameof(externalId));
        }

        return new LibraryIdentity(type, id, slug);
    }

    private static bool TryParseReaderUri(
        Uri root,
        string? rawUri,
        out Uri readerUri)
    {
        readerUri = null!;
        if (!TmoProviderUri.TryResolveSameOrigin(root, rawUri, out var uri))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(uri);
        if (segments.Length != 2 ||
            !string.Equals(
                segments[0],
                "view_uploads",
                StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(
                segments[1],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var uploadId) ||
            uploadId <= 0)
        {
            return false;
        }

        readerUri = uri;
        return true;
    }

    private static bool TryValidateType(
        string? value,
        out string type) =>
        TmoProviderUri.TryValidateSegment(
            value,
            40,
            static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-' or '_',
            out type);

    private static bool TryValidateSlug(
        string? value,
        out string slug) =>
        TmoProviderUri.TryValidateSegment(
            value,
            200,
            static character =>
                char.IsAsciiLetterOrDigit(character) || character == '-',
            out slug);

    private static bool TryValidateDisplayNumber(
        string? value,
        out string displayNumber) =>
        TmoProviderUri.TryValidateSegment(
            value,
            80,
            static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '-' or '_',
            out displayNumber);

    private static Uri BuildItemUri(Uri root, LibraryIdentity identity) =>
        new(
            root,
            $"library/{Uri.EscapeDataString(identity.Type)}/" +
            $"{identity.Id.ToString(CultureInfo.InvariantCulture)}/" +
            $"{Uri.EscapeDataString(identity.Slug)}");

    private static string BuildChapterKey(
        Uri root,
        string externalId,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            externalId,
            seasonNumber: null,
            ApiContentProviderCacheKey.NormalizeDisplayNumber(displayNumber));

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
            page is < 1 or > MaximumPageNumber)
        {
            throw new ArgumentException(
                "ZonaTMO returned an invalid page token.",
                nameof(token));
        }

        return page;
    }

    private static decimal ParseDisplayNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    private static string MapBookSubtype(string value)
    {
        var normalized = TmoProviderText.NormalizeText(value);
        if (normalized.Contains("novel", StringComparison.OrdinalIgnoreCase) ||
            normalized.Contains("novela", StringComparison.OrdinalIgnoreCase))
        {
            return "novel";
        }

        return normalized.Contains(
            "comic",
            StringComparison.OrdinalIgnoreCase)
            ? "comic"
            : "manga";
    }

    private readonly record struct LibraryIdentity(
        string Type,
        long Id,
        string Slug)
    {
        internal string ExternalId =>
            $"{Type}/{Id.ToString(CultureInfo.InvariantCulture)}/{Slug}";
    }
}

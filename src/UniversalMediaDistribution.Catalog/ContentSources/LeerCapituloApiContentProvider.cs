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
/// Reads LeerCapitulo's public search and reader payloads. The reader's
/// substitution alphabets are derived from the script referenced by each
/// chapter page and are never hard-coded.
/// </summary>
public sealed partial class LeerCapituloApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "leercapitulo";

    private const int MaximumSearchBytes = 1 * 1024 * 1024;
    private const int MaximumHtmlBytes = 4 * 1024 * 1024;
    private const int MaximumScriptBytes = 4 * 1024 * 1024;
    private const int MaximumSearchResults = 500;
    private const int MaximumDailyUpdateResults = 24;
    private const int MaximumChapterCount = 20_000;
    private const int MaximumPageCount = 2_000;

    private static readonly TimeSpan DecoderCacheLifetime =
        TimeSpan.FromDays(1);
    private static readonly TimeSpan HtmlRegexTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly Regex StripTagsRegex = new(
        "<[^>]+>",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex BreakRegex = new(
        @"<br\b[^>]*>",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex HorizontalWhitespaceRegex = new(
        @"[^\S\r\n]+",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex LineWhitespaceRegex = new(
        @"[ \t]*\r?\n[ \t]*",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex ChapterLabelRegex = new(
        @"\bcap[ií]tulo\s+(?<number>\d+(?:[.,]\d+)?(?:[-A-Za-z])?)(?:\s*:\s*(?<title>.*))?$",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex NonDigitRunRegex = new(
        "[^0-9]+",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);
    private static readonly Regex GenrePageRegex = new(
        @"(?:[?&]|&amp;)page=(?<page>[0-9]{1,5})",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant,
        HtmlRegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly TimeProvider _timeProvider;
    private readonly ApiContentGenreMapCache _genreMaps = new();
    private readonly SemaphoreSlim _decoderCacheGate = new(1, 1);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, string> _chapterRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, MangaIdentity> _canonicalMangas = new(StringComparer.Ordinal);
    private DecoderCacheEntry? _decoderCache;

    public LeerCapituloApiContentProvider(
        HttpClient httpClient,
        TimeProvider? timeProvider = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.LeerCapitulo;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = NormalizeRootUri(baseUri);
        var rootOrigin = GetOrigin(root);
        return _trustedArtworkOrigins.Values
            .Prepend(rootOrigin)
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
            "text/html",
            MaximumHtmlBytes,
            referrer: root,
            cancellationToken);
        var section = ExtractDailyUpdateSection(html);
        var artworkByTitle = new Dictionary<string, string>(
            StringComparer.CurrentCultureIgnoreCase);
        foreach (var attributes in EnumerateTagAttributes(
                     section,
                     "img"))
        {
            var title = NormalizeText(
                GetAttribute(attributes, "alt"));
            var source = GetAttribute(attributes, "data-src") ??
                GetAttribute(attributes, "src");
            if (title.Length == 0 ||
                !TryResolveHttpUri(
                    root,
                    source,
                    requireSameOrigin: true,
                    out var artworkUri))
            {
                continue;
            }

            artworkByTitle.TryAdd(title, artworkUri.AbsoluteUri);
        }

        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        // Current release cards use decorative images with empty alt text.
        // Associate covers with the manga link, not the display name.
        var artworkById = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var anchor in EnumerateElements(section, "a"))
        {
            if (!TryParseMangaLink(root,
                    GetAttribute(anchor.Attributes, "href") ?? string.Empty,
                    out var manga))
            {
                continue;
            }

            foreach (var attributes in EnumerateTagAttributes(anchor.Content, "img"))
            {
                var source = GetAttribute(attributes, "data-src");
                if (string.IsNullOrWhiteSpace(source))
                {
                    source = GetAttribute(attributes, "src");
                }

                if (TryResolveHttpUri(root, source, requireSameOrigin: true, out var artworkUri))
                {
                    artworkById.TryAdd(manga.ExternalId, artworkUri.AbsoluteUri);
                    break;
                }
            }
        }

        foreach (var anchor in EnumerateElements(section, "a"))
        {
            if (results.Count >= MaximumDailyUpdateResults)
            {
                break;
            }

            var href = GetAttribute(anchor.Attributes, "href");
            if (!TryParseMangaLink(
                    root,
                    href ?? string.Empty,
                    out var manga) ||
                seen.Contains(manga.ExternalId))
            {
                continue;
            }

            var title = NormalizeText(
                GetAttribute(anchor.Attributes, "title"));
            if (title.Length == 0)
            {
                title = NormalizeText(HtmlToText(anchor.Content));
            }

            if (title.Length == 0)
            {
                continue;
            }

            seen.Add(manga.ExternalId);
            if (!artworkById.TryGetValue(manga.ExternalId, out var thumbnail))
            {
                artworkByTitle.TryGetValue(title, out thumbnail);
            }
            results.Add(new ApiContentSearchItem(
                manga.ExternalId,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = title,
                    ThumbnailRef = thumbnail,
                    Categories = ["Manga"],
                    Units = ExtractChapters(
                        section,
                        root,
                        manga)
                }));
        }

        return results;
    }

    /// <summary>
    /// Genre shelves are published as ordinary server-rendered routes and can
    /// be browsed without query text. The site does not expose an original
    /// release year in either its list cards or title metadata.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.Native,
        supportsFilterOnlySearch: true);

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default) =>
        (await SearchAsync(baseUri, new ApiContentSearchRequest(query, mediaType), cancellationToken)).Items;

    private async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchLegacyAsync(
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

        var endpoint = new Uri(
            root,
            $"search-autocomplete?term={Uri.EscapeDataString(query.Trim())}");
        var json = await ReadStringAsync(
            endpoint,
            "application/json",
            MaximumSearchBytes,
            referrer: root,
            cancellationToken);

        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "LeerCapitulo returned an invalid search response.");
        }

        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (results.Count >= MaximumSearchResults)
            {
                break;
            }

            if (element.ValueKind is not JsonValueKind.Object ||
                !TryGetJsonString(element, "link", out var link) ||
                !TryParseMangaLink(root, link, out var manga))
            {
                continue;
            }

            var externalId = manga.ExternalId;
            if (!seen.Add(externalId))
            {
                continue;
            }

            _ = TryGetJsonString(element, "label", out var label);
            if (string.IsNullOrWhiteSpace(label))
            {
                _ = TryGetJsonString(element, "value", out label);
            }

            label = NormalizeText(label);
            if (label.Length == 0)
            {
                continue;
            }

            string? thumbnail = null;
            if (TryGetJsonString(element, "thumbnail", out var thumbnailValue) &&
                TryResolveHttpUri(
                    root,
                    thumbnailValue,
                    requireSameOrigin: true,
                    out var thumbnailUri))
            {
                thumbnail = thumbnailUri.AbsoluteUri;
            }

            results.Add(new ApiContentSearchItem(
                externalId,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = label,
                    ThumbnailRef = thumbnail,
                    Categories = ["Manga"]
                }));
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

        string? genreToken = null;
        if (request.Genre is not null)
        {
            var genres = await LoadGenreMapAsync(root, cancellationToken);
            if (!genres.TryGetToken(request.Genre, out genreToken))
            {
                return ApiContentSearchPage.Empty;
            }
        }

        var page = ParseGenrePageToken(request.ContinuationToken);
        var route = $"manga/?page={page.ToString(CultureInfo.InvariantCulture)}";
        if (request.Query is not null) route += $"&q={Uri.EscapeDataString(request.Query)}";
        if (genreToken is not null) route += $"&genre={Uri.EscapeDataString(genreToken)}";
        var endpoint = new Uri(root, route);
        string html;
        var legacy = false;
        try
        {
            html = await ReadStringAsync(endpoint, "text/html", MaximumHtmlBytes, root, cancellationToken);
        }
        catch (HttpRequestException exception) when (exception.StatusCode == HttpStatusCode.NotFound)
        {
            // Older mirrors still expose autocomplete and /genre/ routes.
            if (genreToken is null)
                return page == 1
                    ? new ApiContentSearchPage(await SearchLegacyAsync(root, request.Query!, request.MediaType, cancellationToken))
                    : ApiContentSearchPage.Empty;
            legacy = true;
            endpoint = new Uri(root, $"genre/{Uri.EscapeDataString(genreToken)}/" + (page > 1 ? $"?page={page}" : ""));
            html = await ReadStringAsync(endpoint, "text/html", MaximumHtmlBytes, root, cancellationToken);
        }
        var items = ParseGenreCards(
            root,
            html,
            legacy ? request.Query : null,
            request.Genre);
        var nextPage = HasGenrePage(html, page + 1)
            ? (page + 1).ToString(CultureInfo.InvariantCulture)
            : null;
        return new ApiContentSearchPage(items, nextPage);
    }

    private ValueTask<ApiContentGenreMap> LoadGenreMapAsync(
        Uri root,
        CancellationToken cancellationToken) =>
        _genreMaps.GetAsync(
            root,
            async token => ParseGenreMap(
                root,
                await ReadStringAsync(
                    root,
                    "text/html",
                    MaximumHtmlBytes,
                    referrer: root,
                    token)),
            cancellationToken);

    internal static ApiContentGenreMap ParseGenreMap(
        Uri root,
        string html)
    {
        var entries = new List<(string Label, string Token)>();
        foreach (var anchor in EnumerateElements(html, "a"))
        {
            var href = GetAttribute(anchor.Attributes, "href");
            if (!TryReadGenreToken(root, href, out var token))
            {
                continue;
            }

            var label = NormalizeText(HtmlToText(anchor.Content));
            if (label.Length > 0)
            {
                entries.Add((label, token));
            }
        }

        return ApiContentGenreMap.FromUpstreamLabels(entries);
    }

    private IReadOnlyList<ApiContentSearchItem> ParseGenreCards(
        Uri root,
        string html,
        string? query,
        ApiContentGenre? filteredGenre)
    {
        var results = new List<ApiContentSearchItem>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var modernCards = EnumerateElements(html, "article")
            .Where(article => HasClass(GetAttribute(article.Attributes, "class"), "lc-card"))
            .Select(article => article.Content);
        foreach (var card in modernCards.Concat(EnumerateClassDivBlocks(html, "mainpage-manga")))
        {
            if (results.Count >= MaximumSearchResults)
            {
                break;
            }

            MangaIdentity manga = default;
            var title = string.Empty;
            foreach (var anchor in EnumerateElements(card, "a"))
            {
                // Decorative cover links contain a format badge, not the title.
                if (HasClass(GetAttribute(anchor.Attributes, "class"), "lc-card-cover")) continue;
                var href = GetAttribute(anchor.Attributes, "href");
                if (!TryParseMangaLink(
                        root,
                        href ?? string.Empty,
                        out manga))
                {
                    continue;
                }

                title = NormalizeText(
                    GetAttribute(anchor.Attributes, "title"));
                if (title.Length == 0)
                {
                    foreach (var imageAttributes in EnumerateTagAttributes(
                                 anchor.Content,
                                 "img"))
                    {
                        title = NormalizeText(
                            GetAttribute(imageAttributes, "alt"));
                        if (title.Length > 0)
                        {
                            break;
                        }
                    }
                }

                if (title.Length == 0)
                {
                    title = NormalizeText(HtmlToText(anchor.Content));
                }

                if (title.Length > 0)
                {
                    break;
                }
            }

            if (title.Length == 0 ||
                !seen.Add(manga.ExternalId) ||
                query is not null && !Contains(title, query))
            {
                continue;
            }

            string? thumbnail = null;
            foreach (var imageAttributes in EnumerateTagAttributes(card, "img"))
            {
                var source = GetAttribute(imageAttributes, "data-src") ??
                    GetAttribute(imageAttributes, "src");
                if (TryResolveHttpUri(
                        root,
                        source,
                        requireSameOrigin: true,
                        out var thumbnailUri))
                {
                    thumbnail = thumbnailUri.AbsoluteUri;
                    break;
                }
            }

            var genres = ExtractGenres(root, card);
            if (genres.Count == 0 && filteredGenre is not null)
            {
                genres =
                [
                    filteredGenre.Aliases.FirstOrDefault() ??
                    filteredGenre.Key
                ];
            }

            results.Add(new ApiContentSearchItem(
                manga.ExternalId,
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = title,
                    ThumbnailRef = thumbnail,
                    Genres = genres,
                    Categories = ["Manga"]
                }));
        }

        return results;
    }

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
                "LeerCapitulo returned an invalid genre page token.",
                nameof(token));
        }

        return page;
    }

    private static bool HasGenrePage(string html, int expectedPage)
    {
        foreach (Match match in GenrePageRegex.Matches(html))
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

    public async ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default)
    {
        var root = NormalizeRootUri(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        var manga = ResolveManga(root, searchItem.ExternalId);
        var endpoint = BuildMangaUri(root, manga);
        var document = await ReadDocumentAsync(
            endpoint,
            "text/html",
            MaximumHtmlBytes,
            referrer: root,
            cancellationToken);
        var html = document.Text;
        if (document.Uri != endpoint)
        {
            if (!TryParseMangaLink(root, document.Uri.AbsoluteUri, out var canonical))
                throw new InvalidDataException("LeerCapitulo redirected the title outside its manga detail routes.");
            if (_canonicalMangas.Count >= 1024) _canonicalMangas.Clear();
            _canonicalMangas[CanonicalMangaKey(root, searchItem.ExternalId)] = canonical;
            manga = canonical;
        }

        var title = NormalizeText(
            FindElementContentByClass(html, "h1", "title-manga"));
        var modernDetails = EnumerateElements(html, "article")
            .Where(article => HasClass(GetAttribute(article.Attributes, "class"), "lc-panel"))
            .Select(article => article.Content)
            .FirstOrDefault(content => EnumerateElements(content, "h1").Any());
        if (title.Length == 0 && modernDetails is not null)
            title = NormalizeText(HtmlToText(EnumerateElements(modernDetails, "h1").First().Content));
        if (title.Length == 0)
        {
            if (searchItem.RequireProviderTitle)
            {
                throw new InvalidDataException(
                    "The LeerCapitulo detail page did not include a title.");
            }

            title = searchItem.PreviewItem.Name;
        }

        var metadataHtml =
            FindElementContentByClass(html, "p", "description-update");
        var metadataLines = HtmlToText(metadataHtml)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries);
        var aliasesText = modernDetails is not null
            ? HtmlToText(FindElementContentByClass(modernDetails, "p", "lc-muted"))
            : ExtractMetadataValue(
                metadataLines,
                "Títulos Alternativos:");
        var alternativeNames = aliasesText
            .Split(modernDetails is null ? ',' : '·', StringSplitOptions.RemoveEmptyEntries |
                        StringSplitOptions.TrimEntries)
            .Where(name => !string.Equals(
                name,
                title,
                StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Select(static name => new CatalogTransferAlias
            {
                Name = name,
                AliasType = "alternative"
            })
            .ToArray();
        var format = modernDetails is null ? ExtractMetadataValue(metadataLines, "Escribe:")
            : FindCatalogFilterLabel(root, modernDetails, "type");
        var status = MapPublicationStatus(
            modernDetails is null ? ExtractMetadataValue(metadataLines, "Estado:")
                : FindCatalogFilterLabel(root, modernDetails, "status"));
        var genres = ExtractGenres(root, modernDetails ?? metadataHtml);
        var description = NormalizeText(
            HtmlToText(FindElementContentByClass(
                html,
                "p",
                "manga-collapse")));
        if (description.Length == 0 && modernDetails is not null)
        {
            var synopsis = FindElementContentById(html, "section", "sinopsis");
            description = synopsis is null ? string.Empty : NormalizeText(string.Join("\n",
                EnumerateElements(synopsis, "p").Select(paragraph => HtmlToText(paragraph.Content))));
            if (description == "Esta serie todavia no tiene sinopsis.") description = string.Empty;
        }
        var thumbnail = ExtractMetaContent(html, "property", "og:image");
        if (string.IsNullOrWhiteSpace(thumbnail) && modernDetails is not null)
        {
            var cover = EnumerateTagAttributes(modernDetails, "img").FirstOrDefault();
            if (cover is not null) thumbnail = GetAttribute(cover, "data-src") ?? GetAttribute(cover, "src");
        }
        string? thumbnailUri = null;
        if (TryResolveHttpUri(
                root,
                thumbnail,
                requireSameOrigin: true,
                out var resolvedThumbnail))
        {
            thumbnailUri = resolvedThumbnail.AbsoluteUri;
        }

        var units = TryUseKnownChapterRoute(
            root,
            manga,
            searchItem.UnitRoutes,
            out var knownUnits)
            ? knownUnits
            : ExtractChapters(html, root, manga);
        var categories = string.IsNullOrWhiteSpace(format) ||
                         string.Equals(
                             format,
                             "Manga",
                             StringComparison.OrdinalIgnoreCase)
            ? new[] { "Manga" }
            : new[] { "Manga", NormalizeText(format) };

        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "manga",
            Name = title,
            Description = description.Length == 0 ? null : description,
            PublicationStatus = status,
            ThumbnailRef = thumbnailUri,
            Genres = genres,
            Categories = categories,
            AlternativeNames = alternativeNames,
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
        var manga = ResolveManga(root, searchItem.ExternalId);
        return _chapterRoutes.TryGetValue(
                   BuildChapterRouteKey(
                       root,
                       manga.ExternalId,
                       unit.DisplayNumber),
                   out var chapterRoute) &&
               TryValidateChapterRoute(chapterRoute, out var normalizedRoute)
            ?
            [
                new ApiProviderUnitRoute(
                    ContentUnitKind.Chapter,
                    unit.DisplayNumber,
                    seasonNumber: null,
                    normalizedRoute)
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

        var manga = ResolveManga(root, searchItem.ExternalId);
        var routeKey = BuildChapterRouteKey(
            root,
            manga.ExternalId,
            unit.DisplayNumber);
        var chapterRoute = _chapterRoutes.GetValueOrDefault(routeKey);
        if (chapterRoute is null &&
            !TryValidateChapterRoute(unit.DisplayNumber, out chapterRoute))
        {
            return null;
        }

        var chapterUri = BuildChapterUri(root, manga, chapterRoute);
        var document = await ReadDocumentAsync(
            chapterUri,
            "text/html",
            MaximumHtmlBytes,
            referrer: root,
            cancellationToken);
        var html = document.Text;
        chapterUri = document.Uri;
        var reader = FindElementContentById(html, "main", "lcPages");
        if (reader is not null)
        {
            var pages = new SortedDictionary<int, Uri>();
            foreach (var attributes in EnumerateTagAttributes(reader, "img"))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var source = GetAttribute(attributes, "data-src");
                if (string.IsNullOrWhiteSpace(source)) source = GetAttribute(attributes, "src");
                if (!int.TryParse(GetAttribute(attributes, "data-index"), NumberStyles.None, CultureInfo.InvariantCulture, out var index) ||
                    index < 0 || index >= MaximumPageCount ||
                    !TryResolveHttpUri(chapterUri, source, requireSameOrigin: false, out var image) ||
                    !HosterExtractorBase.IsSafePublicHttpUri(image) || !pages.TryAdd(index, image))
                    throw new InvalidDataException("LeerCapitulo returned an invalid reader image.");
            }
            if (pages.Count == 0 || pages.Keys.Where((index, expected) => index != expected).Any())
                throw new InvalidDataException("LeerCapitulo returned an incomplete reader image list.");
            foreach (var image in pages.Values)
            {
                var origin = GetOrigin(image);
                _trustedArtworkOrigins.TryAdd(origin.AbsoluteUri, origin);
            }
            return new ApiContentImageSet(ContentUnitKind.Chapter, unit.DisplayNumber, null,
                pages.Select(page => new ApiContentImagePage(page.Key, page.Value, chapterUri)));
        }
        var encodedPayload = NormalizeText(
            FindElementContentById(html, "p", "array_data"));
        var orderKey = ExtractMetaContent(html, "property", "ad:check");
        var scriptUri = FindReaderScriptUri(html, root);
        if (encodedPayload.Length == 0 ||
            string.IsNullOrWhiteSpace(orderKey) ||
            scriptUri is null)
        {
            throw new InvalidDataException(
                "The LeerCapitulo chapter did not contain a reader payload.");
        }

        var decoder = await GetDecoderAsync(
            scriptUri,
            chapterUri,
            forceRefresh: false,
            cancellationToken);
        IReadOnlyList<Uri> imageUris;
        try
        {
            imageUris = DecodeImageUris(
                encodedPayload,
                orderKey,
                decoder);
        }
        catch (InvalidDataException)
        {
            decoder = await GetDecoderAsync(
                scriptUri,
                chapterUri,
                forceRefresh: true,
                cancellationToken);
            imageUris = DecodeImageUris(
                encodedPayload,
                orderKey,
                decoder);
        }

        foreach (var imageUri in imageUris)
        {
            var origin = GetOrigin(imageUri);
            _trustedArtworkOrigins.TryAdd(origin.AbsoluteUri, origin);
        }

        return new ApiContentImageSet(
            ContentUnitKind.Chapter,
            unit.DisplayNumber,
            seasonNumber: null,
            imageUris.Select(
                static (uri, index) =>
                    new ApiContentImagePage(index, uri)));
    }

    private IReadOnlyList<CatalogTransferUnit> ExtractChapters(
        string html,
        Uri root,
        MangaIdentity manga)
    {
        var chapters = new Dictionary<string, ChapterEntry>(
            StringComparer.OrdinalIgnoreCase);
        foreach (var attributesAndContent in EnumerateElements(html, "a"))
        {
            var href = GetAttribute(
                attributesAndContent.Attributes,
                "href");
            if (!TryResolveHttpUri(
                    root,
                    href,
                    requireSameOrigin: true,
                    out var uri) ||
                !TryParseChapterLink(uri, manga, out var route))
            {
                continue;
            }

            var label = NormalizeText(
                GetAttribute(attributesAndContent.Attributes, "title"));
            if (label.Length == 0)
            {
                label = NormalizeText(
                    HtmlToText(FindElementContentByClass(attributesAndContent.Content, "span", "n")
                        ?? attributesAndContent.Content));
            }

            var displayNumber = GetChapterDisplayNumber(label, route);
            var normalizedNumber =
                NormalizeChapterDisplayNumber(displayNumber);
            if (chapters.ContainsKey(normalizedNumber))
            {
                continue;
            }

            chapters.Add(
                normalizedNumber,
                new ChapterEntry(
                    displayNumber,
                    label.Length == 0 ? null : label,
                    route));
        }

        if (chapters.Count > MaximumChapterCount)
        {
            throw new InvalidDataException(
                "The LeerCapitulo title contains too many chapters.");
        }

        var ordered = chapters.Values
            .OrderBy(
                static chapter => ParseChapterNumber(chapter.DisplayNumber))
            .ThenBy(
                static chapter => chapter.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
        for (var index = 0; index < ordered.Length; index++)
        {
            var chapter = ordered[index];
            _chapterRoutes[BuildChapterRouteKey(
                root,
                manga.ExternalId,
                chapter.DisplayNumber)] = chapter.Route;
        }

        return ordered
            .Select(static (chapter, index) => new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = chapter.DisplayNumber,
                SortOrder = index,
                Name = chapter.Name
            })
            .ToArray();
    }

    private bool TryUseKnownChapterRoute(
        Uri root,
        MangaIdentity manga,
        IReadOnlyList<ApiProviderUnitRoute> routeHints,
        out IReadOnlyList<CatalogTransferUnit> units)
    {
        foreach (var hint in routeHints)
        {
            if (hint.UnitKind is not ContentUnitKind.Chapter ||
                hint.SeasonNumber is not null ||
                hint.DisplayNumber is not { } displayNumber ||
                !TryValidateChapterRoute(hint.Route, out var chapterRoute))
            {
                continue;
            }

            _chapterRoutes[BuildChapterRouteKey(
                root,
                manga.ExternalId,
                displayNumber)] = chapterRoute;
            units =
            [
                new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = displayNumber,
                    SortOrder = 0,
                    Name = $"Capítulo {displayNumber}"
                }
            ];
            return true;
        }

        units = [];
        return false;
    }

    private async ValueTask<LeerCapituloDecoderDefinition> GetDecoderAsync(
        Uri scriptUri,
        Uri referrer,
        bool forceRefresh,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        var cached = Volatile.Read(ref _decoderCache);
        if (!forceRefresh && IsFreshDecoder(cached, scriptUri, now))
        {
            return cached!.Definition;
        }

        await _decoderCacheGate.WaitAsync(cancellationToken);
        try
        {
            now = _timeProvider.GetUtcNow();
            cached = Volatile.Read(ref _decoderCache);
            if (!forceRefresh && IsFreshDecoder(cached, scriptUri, now))
            {
                return cached!.Definition;
            }

            try
            {
                var script = await ReadStringAsync(
                    scriptUri,
                    "application/javascript, text/javascript, */*;q=0.1",
                    MaximumScriptBytes,
                    referrer,
                    cancellationToken);
                var definition =
                    LeerCapituloReaderScriptParser.Parse(script);
                Volatile.Write(
                    ref _decoderCache,
                    new DecoderCacheEntry(scriptUri, now, definition));
                return definition;
            }
            catch (Exception exception)
                when (!forceRefresh &&
                      exception is not OperationCanceledException &&
                      IsSameScript(cached, scriptUri))
            {
                return cached!.Definition;
            }
        }
        finally
        {
            _decoderCacheGate.Release();
        }
    }

    private async ValueTask<string> ReadStringAsync(
        Uri endpoint,
        string accept,
        int maximumBytes,
        Uri? referrer,
        CancellationToken cancellationToken) =>
        (await ReadDocumentAsync(endpoint, accept, maximumBytes, referrer, cancellationToken)).Text;

    private async ValueTask<(string Text, Uri Uri)> ReadDocumentAsync(
        Uri endpoint, string accept, int maximumBytes, Uri? referrer, CancellationToken cancellationToken)
    {
        var current = endpoint;
        var visited = new HashSet<Uri>();
        for (var hop = 0; hop <= 3; hop++)
        {
            if (!HosterExtractorBase.IsSafePublicHttpUri(current) || !HaveSameOrigin(endpoint, current) || !visited.Add(current))
                throw new InvalidDataException("LeerCapitulo returned an unsafe or looping redirect.");
            using var request = new HttpRequestMessage(HttpMethod.Get, current);
            request.Headers.Accept.ParseAdd(accept);
            request.Headers.UserAgent.ParseAdd(
                "Mozilla/5.0 (compatible; UniversalMediaDistribution/1.0)");
            request.Headers.Referrer = referrer;
            using var response = await ApiProviderHttp.SendAsync(
                _httpClient,
                request,
                HttpCompletionOption.ResponseHeadersRead,
                cancellationToken);
            var finalUri = response.RequestMessage?.RequestUri ?? current;
            if (!HosterExtractorBase.IsSafePublicHttpUri(finalUri) || !HaveSameOrigin(endpoint, finalUri))
                throw new InvalidDataException("LeerCapitulo returned an unsafe redirect target.");
            if (response.StatusCode is HttpStatusCode.MovedPermanently or HttpStatusCode.Found or HttpStatusCode.SeeOther or
                HttpStatusCode.TemporaryRedirect or HttpStatusCode.PermanentRedirect)
            {
                if (hop == 3 || response.Headers.Location is not { } location || !Uri.TryCreate(finalUri, location, out var target) ||
                    !HosterExtractorBase.IsSafePublicHttpUri(target) || !HaveSameOrigin(endpoint, target))
                    throw new InvalidDataException("LeerCapitulo returned an unsafe or excessive redirect chain.");
                referrer = finalUri;
                current = target;
                continue;
            }
            response.EnsureSuccessStatusCode();
            if (response.Content.Headers.ContentLength > maximumBytes)
            {
                throw new InvalidDataException(
                    $"LeerCapitulo response exceeds the {maximumBytes / 1024} KiB limit.");
            }

            await using var input =
                await response.Content.ReadAsStreamAsync(cancellationToken);
            using var output = new MemoryStream();
            var buffer = new byte[16 * 1024];
            while (true)
            {
                var bytesRead = await input.ReadAsync(
                    buffer,
                    cancellationToken);
                if (bytesRead == 0)
                {
                    break;
                }

                if (output.Length + bytesRead > maximumBytes)
                {
                    throw new InvalidDataException(
                        $"LeerCapitulo response exceeds the {maximumBytes / 1024} KiB limit.");
                }

                await output.WriteAsync(
                    buffer.AsMemory(0, bytesRead),
                    cancellationToken);
            }

            var encoding = GetResponseEncoding(
                response.Content.Headers.ContentType?.CharSet);
            return (encoding.GetString(
                output.GetBuffer(),
                0,
                checked((int)output.Length)), finalUri);
        }
        throw new InvalidDataException("LeerCapitulo returned an excessive redirect chain.");
    }

    private static string CanonicalMangaKey(Uri root, string externalId) => root.AbsoluteUri + "\n" + externalId;
    private MangaIdentity ResolveManga(Uri root, string externalId) =>
        _canonicalMangas.GetValueOrDefault(CanonicalMangaKey(root, externalId), ParseExternalId(externalId));

    private static IReadOnlyList<Uri> DecodeImageUris(
        string encodedPayload,
        string orderKey,
        LeerCapituloDecoderDefinition decoder)
    {
        string decodedCsv;
        try
        {
            var translated = new StringBuilder(encodedPayload.Length);
            foreach (var character in encodedPayload)
            {
                var index = decoder.EncodedAlphabet.IndexOf(character);
                translated.Append(
                    index >= 0
                        ? decoder.DecodedAlphabet[index]
                        : character);
            }

            var bytes = Convert.FromBase64String(translated.ToString());
            decodedCsv = new UTF8Encoding(
                encoderShouldEmitUTF8Identifier: false,
                throwOnInvalidBytes: true).GetString(bytes);
        }
        catch (Exception exception)
            when (exception is FormatException or DecoderFallbackException)
        {
            throw new InvalidDataException(
                "The LeerCapitulo reader payload could not be decoded.",
                exception);
        }

        var shuffledUrls = decodedCsv.Split(
            ',',
            StringSplitOptions.TrimEntries);
        if (shuffledUrls.Length is 0 or > MaximumPageCount ||
            shuffledUrls.Any(static value => value.Length == 0))
        {
            throw new InvalidDataException(
                "The LeerCapitulo reader returned an invalid page list.");
        }

        var normalizedOrder = NonDigitRunRegex.Replace(orderKey, "-");
        var characters = normalizedOrder.ToCharArray();
        Array.Reverse(characters);
        var order = new string(characters).Split(
            '-',
            StringSplitOptions.None);
        if (order.Length != shuffledUrls.Length)
        {
            throw new InvalidDataException(
                "The LeerCapitulo page order does not match the page list.");
        }

        var seen = new bool[shuffledUrls.Length];
        var result = new Uri[shuffledUrls.Length];
        for (var position = 0; position < order.Length; position++)
        {
            if (!int.TryParse(
                    order[position],
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var sourceIndex) ||
                sourceIndex < 0 ||
                sourceIndex >= shuffledUrls.Length ||
                seen[sourceIndex] ||
                !TryCreateHttpUri(
                    shuffledUrls[sourceIndex],
                    out var imageUri))
            {
                throw new InvalidDataException(
                    "The LeerCapitulo reader returned an invalid page order or URL.");
            }

            seen[sourceIndex] = true;
            result[position] = imageUri;
        }

        return result;
    }

    private static bool IsFreshDecoder(
        DecoderCacheEntry? cached,
        Uri scriptUri,
        DateTimeOffset now)
    {
        if (!IsSameScript(cached, scriptUri))
        {
            return false;
        }

        var age = now - cached!.FetchedAt;
        return age >= TimeSpan.Zero && age < DecoderCacheLifetime;
    }

    private static bool IsSameScript(
        DecoderCacheEntry? cached,
        Uri scriptUri) =>
        cached is not null &&
        string.Equals(
            cached.ScriptUri.AbsoluteUri,
            scriptUri.AbsoluteUri,
            StringComparison.Ordinal);

    private static bool TryGetJsonString(
        JsonElement element,
        string propertyName,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(propertyName, out var property) &&
               property.ValueKind is JsonValueKind.String &&
               (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static MangaIdentity ParseExternalId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        var segments = externalId.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (segments.Length != 2 ||
            !TryValidateMangaId(segments[0], out var id) ||
            !TryValidateSlug(segments[1], out var slug))
        {
            throw new ArgumentException(
                "The LeerCapitulo manga ID is invalid.",
                nameof(externalId));
        }

        return new MangaIdentity(id, slug);
    }

    private static bool TryParseMangaLink(
        Uri root,
        string rawLink,
        out MangaIdentity manga)
    {
        manga = default;
        if (!TryResolveHttpUri(
                root,
                rawLink,
                requireSameOrigin: true,
                out var uri))
        {
            return false;
        }

        var segments = GetPathSegments(uri);
        if (segments.Length != 3 ||
            !string.Equals(
                segments[0],
                "manga",
                StringComparison.OrdinalIgnoreCase) ||
            !TryValidateMangaId(segments[1], out var id) ||
            !TryValidateSlug(segments[2], out var slug))
        {
            return false;
        }

        manga = new MangaIdentity(id, slug);
        return true;
    }

    private static bool TryParseChapterLink(
        Uri uri,
        MangaIdentity manga,
        out string route)
    {
        route = string.Empty;
        var segments = GetPathSegments(uri);
        if (segments.Length != 4 ||
            !string.Equals(
                segments[0],
                "leer",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                segments[1],
                manga.Id,
                StringComparison.Ordinal) ||
            !string.Equals(
                segments[2],
                manga.Slug,
                StringComparison.Ordinal) ||
            !TryValidateChapterRoute(segments[3], out route))
        {
            return false;
        }

        return true;
    }

    private static string[] GetPathSegments(Uri uri)
    {
        try
        {
            return uri.AbsolutePath
                .Split('/', StringSplitOptions.RemoveEmptyEntries)
                .Select(Uri.UnescapeDataString)
                .ToArray();
        }
        catch (UriFormatException)
        {
            return [];
        }
    }

    private static bool TryValidateMangaId(
        string value,
        out string normalized) =>
        TryValidatePathSegment(
            value,
            80,
            static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '_' or '-',
            out normalized);

    private static bool TryValidateSlug(
        string value,
        out string normalized) =>
        TryValidatePathSegment(
            value,
            200,
            static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '-',
            out normalized);

    private static bool TryValidateChapterRoute(
        string value,
        out string normalized) =>
        TryValidatePathSegment(
            value,
            80,
            static character =>
                char.IsAsciiLetterOrDigit(character) ||
                character is '.' or '_' or '-' or '~',
            out normalized);

    private static bool TryValidatePathSegment(
        string value,
        int maximumLength,
        Func<char, bool> isAllowed,
        out string normalized)
    {
        normalized = value.Trim();
        return normalized.Length is > 0 &&
               normalized.Length <= maximumLength &&
               normalized.All(isAllowed);
    }

    private static Uri BuildMangaUri(
        Uri root,
        MangaIdentity manga) =>
        new(
            root,
            $"manga/{Uri.EscapeDataString(manga.Id)}/" +
            $"{Uri.EscapeDataString(manga.Slug)}/");

    private static Uri BuildChapterUri(
        Uri root,
        MangaIdentity manga,
        string route) =>
        new(
            root,
            $"leer/{Uri.EscapeDataString(manga.Id)}/" +
            $"{Uri.EscapeDataString(manga.Slug)}/" +
            $"{Uri.EscapeDataString(route)}/");

    private static Uri NormalizeRootUri(Uri baseUri)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        if (!baseUri.IsAbsoluteUri ||
            baseUri.Scheme is not ("http" or "https") ||
            baseUri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "LeerCapitulo requires an absolute HTTP or HTTPS base URL without embedded credentials.",
                nameof(baseUri));
        }

        var builder = new UriBuilder(baseUri)
        {
            Query = string.Empty,
            Fragment = string.Empty
        };
        if (!builder.Path.EndsWith('/'))
        {
            builder.Path += "/";
        }

        return builder.Uri;
    }

    private static Uri GetOrigin(Uri uri) =>
        new UriBuilder(uri)
        {
            Path = "/",
            Query = string.Empty,
            Fragment = string.Empty
        }.Uri;

    private static bool TryResolveHttpUri(
        Uri root,
        string? value,
        bool requireSameOrigin,
        out Uri uri)
    {
        uri = null!;
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        var decoded = WebUtility.HtmlDecode(value.Trim());
        if (!Uri.TryCreate(root, decoded, out var candidate) ||
            !IsSafeHttpUri(candidate) ||
            requireSameOrigin && !HaveSameOrigin(root, candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool TryCreateHttpUri(
        string value,
        out Uri uri)
    {
        uri = null!;
        if (!Uri.TryCreate(
                WebUtility.HtmlDecode(value.Trim()),
                UriKind.Absolute,
                out var candidate) ||
            !IsSafeHttpUri(candidate))
        {
            return false;
        }

        uri = candidate;
        return true;
    }

    private static bool IsSafeHttpUri(Uri uri) =>
        uri.IsAbsoluteUri &&
        uri.Scheme is "http" or "https" &&
        uri.UserInfo.Length == 0;

    private static bool HaveSameOrigin(Uri left, Uri right) =>
        string.Equals(
            left.Scheme,
            right.Scheme,
            StringComparison.OrdinalIgnoreCase) &&
        string.Equals(
            left.IdnHost,
            right.IdnHost,
            StringComparison.OrdinalIgnoreCase) &&
        left.Port == right.Port;

    private static string? FindElementContentByClass(
        string html,
        string tagName,
        string className)
    {
        var pattern =
            $@"<{Regex.Escape(tagName)}\b(?<attributes>[^>]*)>" +
            $@"(?<content>.*?)</{Regex.Escape(tagName)}\s*>";
        foreach (Match match in Regex.Matches(
                     html,
                     pattern,
                     RegexOptions.IgnoreCase |
                     RegexOptions.Singleline |
                     RegexOptions.CultureInvariant,
                     HtmlRegexTimeout))
        {
            var classValue = GetAttribute(
                match.Groups["attributes"].Value,
                "class");
            if (HasClass(classValue, className))
            {
                return match.Groups["content"].Value;
            }
        }

        return null;
    }

    private static string ExtractDailyUpdateSection(string html)
    {
        // The redesigned homepage has release articles inside a div grid.
        // Select only those cards, excluding trending/sidebar manga links.
        var releases = new StringBuilder();
        foreach (var article in EnumerateElements(html, "article"))
        {
            if (HasClass(GetAttribute(article.Attributes, "class"), "lc-release"))
            {
                releases.Append(article.Content);
            }
        }

        if (releases.Length > 0)
        {
            return releases.ToString();
        }

        var marker = html.IndexOf(
            "ltimos Capitulos Agregados",
            StringComparison.OrdinalIgnoreCase);
        if (marker < 0)
        {
            throw new InvalidDataException(
                "LeerCapitulo's daily-update section could not be found.");
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
                "LeerCapitulo returned an invalid daily-update section.");
        }

        return html[start..(end + "</section>".Length)];
    }

    private static string? FindElementContentById(
        string html,
        string tagName,
        string id)
    {
        var pattern =
            $@"<{Regex.Escape(tagName)}\b(?<attributes>[^>]*)>" +
            $@"(?<content>.*?)</{Regex.Escape(tagName)}\s*>";
        foreach (Match match in Regex.Matches(
                     html,
                     pattern,
                     RegexOptions.IgnoreCase |
                     RegexOptions.Singleline |
                     RegexOptions.CultureInvariant,
                     HtmlRegexTimeout))
        {
            if (string.Equals(
                    GetAttribute(
                        match.Groups["attributes"].Value,
                        "id"),
                    id,
                    StringComparison.OrdinalIgnoreCase))
            {
                return match.Groups["content"].Value;
            }
        }

        return null;
    }

    private static IEnumerable<ElementMatch> EnumerateElements(
        string html,
        string tagName)
    {
        var pattern =
            $@"<{Regex.Escape(tagName)}\b(?<attributes>[^>]*)>" +
            $@"(?<content>.*?)</{Regex.Escape(tagName)}\s*>";
        foreach (Match match in Regex.Matches(
                     html,
                     pattern,
                     RegexOptions.IgnoreCase |
                     RegexOptions.Singleline |
                     RegexOptions.CultureInvariant,
                     HtmlRegexTimeout))
        {
            yield return new ElementMatch(
                match.Groups["attributes"].Value,
                match.Groups["content"].Value);
        }
    }

    private static IEnumerable<string> EnumerateTagAttributes(
        string html,
        string tagName)
    {
        var pattern =
            $@"<{Regex.Escape(tagName)}\b(?<attributes>[^>]*)>";
        foreach (Match match in Regex.Matches(
                     html,
                     pattern,
                     RegexOptions.IgnoreCase |
                     RegexOptions.Singleline |
                     RegexOptions.CultureInvariant,
                     HtmlRegexTimeout))
        {
            yield return match.Groups["attributes"].Value;
        }
    }

    private static string? ExtractMetaContent(
        string html,
        string identifyingAttribute,
        string identifyingValue)
    {
        foreach (var attributes in EnumerateTagAttributes(html, "meta"))
        {
            if (string.Equals(
                    GetAttribute(attributes, identifyingAttribute),
                    identifyingValue,
                    StringComparison.OrdinalIgnoreCase))
            {
                return GetAttribute(attributes, "content");
            }
        }

        return null;
    }

    private static Uri? FindReaderScriptUri(
        string html,
        Uri root)
    {
        foreach (var attributes in EnumerateTagAttributes(html, "script"))
        {
            var source = GetAttribute(attributes, "src");
            if (!TryResolveHttpUri(
                    root,
                    source,
                    requireSameOrigin: true,
                    out var scriptUri) ||
                !scriptUri.AbsolutePath.EndsWith(
                    "/pVaTVp.js",
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return scriptUri;
        }

        return null;
    }

    private static IReadOnlyList<string> ExtractGenres(Uri root, string? metadataHtml)
    {
        if (string.IsNullOrWhiteSpace(metadataHtml))
        {
            return [];
        }

        var genres = new List<string>();
        foreach (var anchor in EnumerateElements(metadataHtml, "a"))
        {
            var href = GetAttribute(anchor.Attributes, "href");
            if (!TryReadGenreToken(root, href, out _))
            {
                continue;
            }

            var genre = NormalizeText(HtmlToText(anchor.Content));
            if (genre.Length > 0 &&
                !genres.Contains(
                    genre,
                    StringComparer.OrdinalIgnoreCase))
            {
                genres.Add(genre);
            }
        }

        return genres;
    }

    private static bool TryReadGenreToken(
        Uri root,
        string? href,
        out string token)
    {
        token = string.Empty;
        if (!TryResolveHttpUri(
                root,
                href,
                requireSameOrigin: true,
                out var uri))
        {
            return false;
        }

        const string marker = "/genre/";
        var queryGenre = CatalogQueryValue(uri, "genre");
        if (queryGenre is not null) return TryValidateSlug(queryGenre, out token);
        var markerIndex = uri.AbsolutePath.IndexOf(
            marker,
            StringComparison.OrdinalIgnoreCase);
        if (markerIndex < 0)
        {
            return false;
        }

        var value = uri.AbsolutePath[(markerIndex + marker.Length)..]
            .Trim('/');
        var separator = value.IndexOf('/');
        if (separator >= 0)
        {
            value = value[..separator];
        }

        try
        {
            value = Uri.UnescapeDataString(value);
        }
        catch (UriFormatException)
        {
            return false;
        }

        return TryValidateSlug(value, out token);
    }

    private static string? CatalogQueryValue(Uri uri, string key)
    {
        if (!uri.AbsolutePath.TrimEnd('/').EndsWith("/manga", StringComparison.OrdinalIgnoreCase)) return null;
        var query = uri.Query.AsSpan().TrimStart('?');
        foreach (var range in query.Split('&'))
        {
            var field = query[range];
            var equals = field.IndexOf('=');
            if (equals > 0 && field[..equals].Equals(key, StringComparison.OrdinalIgnoreCase))
                return WebUtility.UrlDecode(field[(equals + 1)..].ToString());
        }
        return null;
    }

    private static string FindCatalogFilterLabel(Uri root, string html, string key)
    {
        foreach (var anchor in EnumerateElements(html, "a"))
            if (TryResolveHttpUri(root, GetAttribute(anchor.Attributes, "href"), true, out var uri) &&
                CatalogQueryValue(uri, key) is not null)
                return NormalizeText(HtmlToText(anchor.Content));
        return string.Empty;
    }

    /// <summary>
    /// Finds balanced card divs with a span scanner. Regex cannot balance nested
    /// divs, while splitting the page creates an array and a substring for every
    /// unrelated element. This scanner allocates only the card blocks that will
    /// actually be parsed.
    /// </summary>
    private static IReadOnlyList<string> EnumerateClassDivBlocks(
        string html,
        string className)
    {
        var blocks = new List<string>();
        var source = html.AsSpan();
        var searchAt = 0;
        while (true)
        {
            var start = FindHtmlTag(source, "<div", searchAt);
            if (start < 0)
            {
                break;
            }

            var openingEnd = source[start..].IndexOf('>');
            if (openingEnd < 0)
            {
                break;
            }

            openingEnd += start;
            if (!HtmlTagHasClass(
                    source[start..(openingEnd + 1)],
                    className))
            {
                searchAt = openingEnd + 1;
                continue;
            }

            var end = FindBalancedDivEnd(source, openingEnd + 1);
            if (end < 0)
            {
                break;
            }

            blocks.Add(html[start..end]);
            searchAt = end;
        }

        return blocks;
    }

    private static int FindBalancedDivEnd(
        ReadOnlySpan<char> source,
        int contentStart)
    {
        var depth = 1;
        var cursor = contentStart;
        while (cursor < source.Length)
        {
            var nextOpen = FindHtmlTag(source, "<div", cursor);
            var nextClose = FindHtmlTag(source, "</div", cursor);
            if (nextClose < 0)
            {
                return -1;
            }

            if (nextOpen >= 0 && nextOpen < nextClose)
            {
                var openingEnd = source[nextOpen..].IndexOf('>');
                if (openingEnd < 0)
                {
                    return -1;
                }

                depth++;
                cursor = nextOpen + openingEnd + 1;
                continue;
            }

            var closingEnd = source[nextClose..].IndexOf('>');
            if (closingEnd < 0)
            {
                return -1;
            }

            depth--;
            cursor = nextClose + closingEnd + 1;
            if (depth == 0)
            {
                return cursor;
            }
        }

        return -1;
    }

    private static int FindHtmlTag(
        ReadOnlySpan<char> source,
        ReadOnlySpan<char> marker,
        int start)
    {
        while (start < source.Length)
        {
            var relative = source[start..].IndexOf(
                marker,
                StringComparison.OrdinalIgnoreCase);
            if (relative < 0)
            {
                return -1;
            }

            var index = start + relative;
            var boundary = index + marker.Length;
            if (boundary >= source.Length ||
                char.IsWhiteSpace(source[boundary]) ||
                source[boundary] is '>' or '/')
            {
                return index;
            }

            start = boundary;
        }

        return -1;
    }

    private static bool HtmlTagHasClass(
        ReadOnlySpan<char> tag,
        ReadOnlySpan<char> expectedClass)
    {
        var searchAt = 0;
        while (searchAt < tag.Length)
        {
            var relative = tag[searchAt..].IndexOf(
                "class",
                StringComparison.OrdinalIgnoreCase);
            if (relative < 0)
            {
                return false;
            }

            var nameStart = searchAt + relative;
            var cursor = nameStart + "class".Length;
            if (nameStart > 0 &&
                (char.IsAsciiLetterOrDigit(tag[nameStart - 1]) ||
                 tag[nameStart - 1] is '-' or '_') ||
                cursor < tag.Length &&
                (char.IsAsciiLetterOrDigit(tag[cursor]) ||
                 tag[cursor] is '-' or '_'))
            {
                searchAt = cursor;
                continue;
            }

            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length || tag[cursor] != '=')
            {
                searchAt = cursor;
                continue;
            }

            cursor++;
            while (cursor < tag.Length && char.IsWhiteSpace(tag[cursor]))
            {
                cursor++;
            }

            if (cursor >= tag.Length)
            {
                return false;
            }

            var quote = tag[cursor] is '"' or '\''
                ? tag[cursor++]
                : '\0';
            var valueStart = cursor;
            while (cursor < tag.Length &&
                   (quote == '\0'
                       ? !char.IsWhiteSpace(tag[cursor]) &&
                         tag[cursor] != '>'
                       : tag[cursor] != quote))
            {
                cursor++;
            }

            var value = tag[valueStart..cursor];
            while (!value.IsEmpty)
            {
                var tokenStart = 0;
                while (tokenStart < value.Length &&
                       char.IsWhiteSpace(value[tokenStart]))
                {
                    tokenStart++;
                }

                value = value[tokenStart..];
                if (value.IsEmpty)
                {
                    break;
                }

                var tokenEnd = 0;
                while (tokenEnd < value.Length &&
                       !char.IsWhiteSpace(value[tokenEnd]))
                {
                    tokenEnd++;
                }

                if (value[..tokenEnd].Equals(
                        expectedClass,
                        StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }

                value = value[tokenEnd..];
            }

            searchAt = cursor + 1;
        }

        return false;
    }

    internal static string? GetAttribute(
        ReadOnlySpan<char> attributes,
        ReadOnlySpan<char> name)
    {
        // Advance only forwards: a long valueless attribute must not cause
        // a regex to retry the same suffix at every character. Do not treat
        // apparent attributes inside another attribute's value as markup.
        var cursor = 0;
        while (cursor < attributes.Length)
        {
            while (cursor < attributes.Length &&
                   (char.IsWhiteSpace(attributes[cursor]) ||
                    attributes[cursor] is '/' or '='))
            {
                cursor++;
            }

            if (cursor == attributes.Length || attributes[cursor] == '>')
            {
                break;
            }

            var nameStart = cursor;
            while (cursor < attributes.Length &&
                   !char.IsWhiteSpace(attributes[cursor]) &&
                   attributes[cursor] is not ('=' or '/' or '>'))
            {
                cursor++;
            }

            var attributeName = attributes[nameStart..cursor];
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
            {
                cursor++;
            }

            if (cursor == attributes.Length || attributes[cursor] != '=')
            {
                continue;
            }

            cursor++;
            while (cursor < attributes.Length && char.IsWhiteSpace(attributes[cursor]))
            {
                cursor++;
            }

            ReadOnlySpan<char> value;
            if (cursor < attributes.Length && attributes[cursor] is '\'' or '"')
            {
                var quote = attributes[cursor++];
                var valueStart = cursor;
                var valueLength = attributes[cursor..].IndexOf(quote);
                if (valueLength < 0)
                {
                    // The remaining input belongs to an unterminated value.
                    return null;
                }

                value = attributes.Slice(valueStart, valueLength);
                cursor += valueLength + 1;
            }
            else
            {
                var valueStart = cursor;
                while (cursor < attributes.Length &&
                       !char.IsWhiteSpace(attributes[cursor]) &&
                       attributes[cursor] != '>')
                {
                    cursor++;
                }

                value = attributes[valueStart..cursor];
            }

            if (attributeName.Equals(name, StringComparison.OrdinalIgnoreCase))
            {
                return WebUtility.HtmlDecode(value.ToString());
            }
        }

        return null;
    }

    private static bool HasClass(
        string? classValue,
        string className) =>
        classValue?.Split(
                (char[]?)null,
                StringSplitOptions.RemoveEmptyEntries)
            .Contains(className, StringComparer.OrdinalIgnoreCase) is true;

    private static string HtmlToText(string? html)
    {
        if (string.IsNullOrWhiteSpace(html))
        {
            return string.Empty;
        }

        var withLines = BreakRegex.Replace(html, "\n");
        var withoutTags = StripTagsRegex.Replace(withLines, " ");
        return NormalizeText(WebUtility.HtmlDecode(withoutTags));
    }

    private static string NormalizeText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = value.ReplaceLineEndings("\n");
        normalized = HorizontalWhitespaceRegex.Replace(normalized, " ");
        normalized = LineWhitespaceRegex.Replace(normalized, "\n");
        return normalized.Trim();
    }

    private static bool Contains(string value, string query) =>
        CultureInfo.InvariantCulture.CompareInfo.IndexOf(
            value,
            query.Trim(),
            CompareOptions.IgnoreCase |
            CompareOptions.IgnoreNonSpace) >= 0;

    private static string ExtractMetadataValue(
        IEnumerable<string> lines,
        string prefix)
    {
        var line = lines.FirstOrDefault(value =>
            value.StartsWith(
                prefix,
                StringComparison.OrdinalIgnoreCase));
        return line is null
            ? string.Empty
            : NormalizeText(line[prefix.Length..]);
    }

    private static string MapPublicationStatus(string value)
    {
        var normalized = RemoveDiacritics(value).ToLowerInvariant();
        if (normalized.Contains("ongoing", StringComparison.Ordinal) ||
            normalized.Contains("emision", StringComparison.Ordinal) ||
            normalized.Contains("publicacion", StringComparison.Ordinal))
        {
            return "ongoing";
        }

        if (normalized.Contains("completed", StringComparison.Ordinal) ||
            normalized.Contains("complete", StringComparison.Ordinal) ||
            normalized.Contains("finalizado", StringComparison.Ordinal))
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

    private static string GetChapterDisplayNumber(
        string label,
        string route)
    {
        var match = ChapterLabelRegex.Match(label);
        return match.Success
            ? match.Groups["number"].Value.Replace(',', '.')
            : route;
    }

    private static decimal ParseChapterNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    private static string NormalizeChapterDisplayNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number.ToString(CultureInfo.InvariantCulture)
            : value.Trim().ToLowerInvariant();

    private static string BuildChapterRouteKey(
        Uri root,
        string externalId,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            externalId,
            seasonNumber: null,
            NormalizeChapterDisplayNumber(displayNumber));

    private static Encoding GetResponseEncoding(string? charset)
    {
        if (string.IsNullOrWhiteSpace(charset))
        {
            return Encoding.UTF8;
        }

        try
        {
            return Encoding.GetEncoding(charset.Trim(' ', '"', '\''));
        }
        catch (ArgumentException)
        {
            return Encoding.UTF8;
        }
    }

    private sealed record DecoderCacheEntry(
        Uri ScriptUri,
        DateTimeOffset FetchedAt,
        LeerCapituloDecoderDefinition Definition);

    private readonly record struct MangaIdentity(
        string Id,
        string Slug)
    {
        public string ExternalId => $"{Id}/{Slug}";
    }

    private sealed record ChapterEntry(
        string DisplayNumber,
        string? Name,
        string Route);

    private readonly record struct ElementMatch(
        string Attributes,
        string Content);
}

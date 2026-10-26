using System.Collections.Concurrent;
using System.Globalization;
using System.Text.Json;
using System.Text.RegularExpressions;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Reads AnimeTMO's public catalog index and its server-rendered anime and
/// episode pages. Zilla player URLs are converted to the public HLS route the
/// player itself requests, so those episodes can use the native player.
/// </summary>
public sealed partial class AnimeTmoApiContentProvider : IApiContentProvider
{
    public const string ProviderId = "animetmo";

    private const int MaximumCatalogBytes = 4 * 1024 * 1024;
    private const int MaximumItemBytes = 2 * 1024 * 1024;
    private const int MaximumEpisodeBytes = 2 * 1024 * 1024;
    private const int MaximumCatalogItems = 20_000;
    private const int MaximumSearchResults = 500;
    private const int MaximumDailyUpdateResults = 24;
    private const int MaximumEpisodes = 20_000;
    private const int MaximumServers = 100;
    private const int MaximumContinuationOffset = 100_000;

    private static readonly TimeSpan CatalogCacheLifetime =
        TimeSpan.FromMinutes(15);
    private static readonly TimeSpan HtmlRegexTimeout =
        TimeSpan.FromSeconds(2);
    private static readonly Regex YearRegex = new(
        @"\b(?<year>(?:19|20)\d{2})\b",
        RegexOptions.CultureInvariant,
        HtmlRegexTimeout);

    private readonly HttpClient _httpClient;
    private readonly VideoHosterResolver? _hosterResolver;
    private readonly TimeProvider _timeProvider;
    private readonly SemaphoreSlim _catalogGate = new(1, 1);
    private readonly ConcurrentDictionary<string, CatalogCacheEntry>
        _catalogByRoot = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _episodeRoutes =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, Uri> _trustedArtworkOrigins =
        new(StringComparer.OrdinalIgnoreCase);

    public AnimeTmoApiContentProvider(
        HttpClient httpClient,
        VideoHosterResolver? hosterResolver = null,
        TimeProvider? timeProvider = null)
    {
        _httpClient =
            httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _hosterResolver = hosterResolver;
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.AnimeTmo;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    public bool SupportsDailyUpdates => true;

    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.VerifiedLocal,
        releaseYear: ApiContentFilterSupport.VerifiedLocal,
        supportsFilterOnlySearch: true);

    public IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        return _trustedArtworkOrigins.Values
            .Prepend(TmoProviderUri.GetOrigin(root))
            .DistinctBy(
                static uri => uri.AbsoluteUri,
                StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
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
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        var catalog = await LoadCatalogAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return catalog
            .Take(MaximumDailyUpdateResults)
            .Select(entry => CreateSearchItem(root, entry))
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        if (mediaType is not null and not MediaType.Movie)
        {
            return [];
        }

        var catalog = await LoadCatalogAsync(root, cancellationToken)
            .ConfigureAwait(false);
        return catalog
            .Where(entry => TmoProviderText.Contains(entry.Title, query))
            .Take(MaximumSearchResults)
            .Select(entry => CreateSearchItem(root, entry))
            .ToArray();
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        ArgumentNullException.ThrowIfNull(request);
        if (request.MediaType is not null and not MediaType.Movie)
        {
            return ApiContentSearchPage.Empty;
        }

        var offset = ParseContinuationOffset(request.ContinuationToken);
        var catalog = await LoadCatalogAsync(root, cancellationToken)
            .ConfigureAwait(false);
        var candidates = catalog.Where(entry =>
            (request.Query is null ||
             TmoProviderText.Contains(entry.Title, request.Query)) &&
            (request.Genre is null ||
             request.Genre.MatchesAny(entry.Genres)) &&
            (request.ReleaseYear is null ||
             entry.ReleaseYear == request.ReleaseYear));
        var page = candidates
            .Skip(offset)
            .Take(request.PageSize + 1)
            .ToArray();
        var hasNext = page.Length > request.PageSize;
        var items = page
            .Take(request.PageSize)
            .Select(entry => CreateSearchItem(root, entry))
            .ToArray();
        var next = hasNext
            ? checked(offset + request.PageSize)
                .ToString(CultureInfo.InvariantCulture)
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
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode ||
            unit.SeasonNumber is not null)
        {
            return [];
        }

        var identity = ParseExternalId(searchItem.ExternalId);
        var key = BuildEpisodeKey(
            root,
            identity.ExternalId,
            unit.DisplayNumber);
        return _episodeRoutes.TryGetValue(key, out var episodeUri) &&
               TryParseEpisodeUri(
                   root,
                   identity.Slug,
                   episodeUri.AbsoluteUri,
                   out var displayNumber,
                   out _) &&
               ApiContentUnitRequest.DisplayNumbersMatch(
                   displayNumber,
                   unit.DisplayNumber)
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
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        ArgumentNullException.ThrowIfNull(searchItem);
        ArgumentNullException.ThrowIfNull(unit);
        if (unit.Kind is not ContentUnitKind.Episode ||
            unit.SeasonNumber is not null)
        {
            return null;
        }

        var identity = ParseExternalId(searchItem.ExternalId);
        var episodeUri = await ResolveEpisodeUriAsync(
            root,
            identity,
            searchItem,
            unit,
            cancellationToken).ConfigureAwait(false);
        if (episodeUri is null)
        {
            return null;
        }

        var itemUri = BuildItemUri(root, identity);
        var html = await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            episodeUri,
            itemUri,
            MaximumEpisodeBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "AnimeTMO",
            cancellationToken).ConfigureAwait(false);
        var sources = ParseVideoSources(episodeUri, html);
        if (sources.Count == 0)
        {
            return null;
        }

        if (_hosterResolver is not null)
        {
            sources = (await _hosterResolver.ResolveAsync(
                sources,
                cancellationToken).ConfigureAwait(false)).ToArray();
        }

        // Probe after generic resolution so an inaccessible Zilla stream is
        // not immediately retried by the shared Zilla extractor.
        sources = await DowngradeUnplayableHlsSourcesAsync(
            sources,
            cancellationToken).ConfigureAwait(false);

        return sources.Count == 0
            ? null
            : new ApiContentVideoSet(
                ContentUnitKind.Episode,
                unit.DisplayNumber,
                seasonNumber: null,
                sources);
    }

    private async ValueTask<ApiContentItem> LoadItemCoreAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken)
    {
        var root = TmoProviderUri.NormalizeRoot(baseUri, "AnimeTMO");
        ArgumentNullException.ThrowIfNull(searchItem);
        var identity = ParseExternalId(searchItem.ExternalId);
        var endpoint = BuildItemUri(root, identity);
        var html = await TmoProviderHttp.ReadStringAsync(
            _httpClient,
            endpoint,
            root,
            MaximumItemBytes,
            "text/html,application/xhtml+xml;q=0.9,*/*;q=0.7",
            "AnimeTMO",
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
                    "The AnimeTMO detail page did not include a title.");
            }

            title = searchItem.PreviewItem.Name;
        }

        var typeLabel = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "h1",
                "book-type"));
        var subtype = IsMovieType(typeLabel)
            ? "anime_movie"
            : searchItem.PreviewItem.Subtype == "anime_movie"
                ? "anime_movie"
                : "anime_series";
        var description = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "p",
                "element-description"));
        if (description.Length == 0)
        {
            description = TmoProviderText.NormalizeText(
                TmoProviderText.ExtractMetaContent(
                    html,
                    "property",
                    "og:description"));
        }

        var year = ExtractReleaseYear(html) ??
            searchItem.PreviewItem.ReleaseYear;
        var genres = ExtractGenres(html);
        if (genres.Count == 0)
        {
            genres = searchItem.PreviewItem.Genres;
        }

        var publicationStatus = TmoProviderText.HtmlToText(
            TmoProviderText.FindElementContentByClass(
                html,
                "span",
                "book-status"));
        var thumbnail = ResolveArtwork(
            root,
            TmoProviderText.FindImageSourceByClass(html, "book-thumbnail")) ??
            searchItem.PreviewItem.ThumbnailRef;
        var backdrop = ResolveArtwork(
            root,
            TmoProviderText.ExtractMetaContent(
                html,
                "property",
                "og:image")) ?? thumbnail;
        var aliases = ExtractAliases(html, title);
        var episodes = ExtractEpisodes(root, identity, html);

        return new ApiContentItem(new CatalogTransferItem
        {
            MediaType = "movie",
            Subtype = subtype,
            Name = title,
            Description = description.Length == 0 ? null : description,
            ReleaseYear = year,
            PublicationStatus = publicationStatus.Length == 0
                ? searchItem.PreviewItem.PublicationStatus
                : publicationStatus,
            ThumbnailRef = thumbnail,
            BackdropRef = backdrop,
            Genres = genres,
            Categories = typeLabel.Length == 0
                ? ["Anime"]
                : ["Anime", typeLabel],
            AlternativeNames = aliases,
            Units = episodes
        });
    }

    private async ValueTask<Uri?> ResolveEpisodeUriAsync(
        Uri root,
        AnimeIdentity identity,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken)
    {
        foreach (var route in searchItem.UnitRoutes)
        {
            if (route.UnitKind is not ContentUnitKind.Episode ||
                route.SeasonNumber is not null ||
                !ApiContentUnitRequest.DisplayNumbersMatch(
                    route.DisplayNumber,
                    unit.DisplayNumber) ||
                !TmoProviderUri.TryResolveSameOrigin(
                    root,
                    route.Route,
                    out var routeUri) ||
                !TryParseEpisodeUri(
                    root,
                    identity.Slug,
                    routeUri.AbsoluteUri,
                    out var routeDisplayNumber,
                    out _) ||
                !ApiContentUnitRequest.DisplayNumbersMatch(
                    routeDisplayNumber,
                    unit.DisplayNumber))
            {
                continue;
            }

            _episodeRoutes[BuildEpisodeKey(
                root,
                identity.ExternalId,
                unit.DisplayNumber)] = routeUri;
            return routeUri;
        }

        var key = BuildEpisodeKey(
            root,
            identity.ExternalId,
            unit.DisplayNumber);
        if (_episodeRoutes.TryGetValue(key, out var cached))
        {
            return cached;
        }

        _ = await LoadItemCoreAsync(
            root,
            searchItem,
            cancellationToken).ConfigureAwait(false);
        return _episodeRoutes.TryGetValue(key, out cached)
            ? cached
            : null;
    }

    private IReadOnlyList<CatalogTransferUnit> ExtractEpisodes(
        Uri root,
        AnimeIdentity identity,
        string html)
    {
        var episodes = new List<(string DisplayNumber, Uri Uri)>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            if (episodes.Count >= MaximumEpisodes ||
                !TmoProviderText.HasClass(
                    TmoProviderText.GetAttribute(
                        anchor.Attributes,
                        "class"),
                    "az-ep-card") ||
                !TryParseEpisodeUri(
                    root,
                    identity.Slug,
                    TmoProviderText.GetAttribute(
                        anchor.Attributes,
                        "href"),
                    out var displayNumber,
                    out var episodeUri))
            {
                continue;
            }

            var normalized =
                ApiContentProviderCacheKey.NormalizeDisplayNumber(
                    displayNumber);
            if (!seen.Add(normalized))
            {
                continue;
            }

            _episodeRoutes[BuildEpisodeKey(
                root,
                identity.ExternalId,
                displayNumber)] = episodeUri;
            episodes.Add((displayNumber, episodeUri));
        }

        return episodes
            .OrderBy(
                static episode => ParseDisplayNumber(episode.DisplayNumber))
            .ThenBy(
                static episode => episode.DisplayNumber,
                StringComparer.OrdinalIgnoreCase)
            .Select(static (episode, index) => new CatalogTransferUnit
            {
                Kind = "episode",
                DisplayNumber = episode.DisplayNumber,
                SortOrder = index,
                Name = $"Episodio {episode.DisplayNumber}"
            })
            .ToArray();
    }

    private IReadOnlyList<ApiContentVideoSource> ParseVideoSources(
        Uri episodeUri,
        string html)
    {
        var sources = new List<ApiContentVideoSource>();
        var seenUris = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var labels = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var button in TmoProviderText.EnumerateElements(html, "button"))
        {
            if (sources.Count >= MaximumServers ||
                !TmoProviderText.HasClass(
                    TmoProviderText.GetAttribute(button.Attributes, "class"),
                    "az-srv-btn") ||
                !TmoProviderUri.TryCreatePublicHttpUri(
                    TmoProviderText.GetAttribute(
                        button.Attributes,
                        "data-url"),
                    out var playbackUri) ||
                !seenUris.Add(playbackUri.AbsoluteUri))
            {
                continue;
            }

            var label = TmoProviderText.HtmlToText(button.Content);
            if (label.Length == 0)
            {
                label = $"Servidor {sources.Count + 1}";
            }

            var type = TmoProviderText.NormalizeText(
                TmoProviderText.GetAttribute(button.Attributes, "data-type"));
            if (type.Length > 0 &&
                !label.Contains(type, StringComparison.OrdinalIgnoreCase))
            {
                label = $"{label} · {type}";
            }

            label = MakeUniqueLabel(label, labels);
            sources.Add(CreateVideoSource(label, playbackUri, episodeUri));
        }

        if (sources.Count == 0)
        {
            foreach (var attributes in
                     TmoProviderText.EnumerateTagAttributes(html, "iframe"))
            {
                if (!TmoProviderUri.TryCreatePublicHttpUri(
                        TmoProviderText.GetAttribute(attributes, "src"),
                        out var playbackUri) ||
                    !seenUris.Add(playbackUri.AbsoluteUri))
                {
                    continue;
                }

                sources.Add(CreateVideoSource(
                    MakeUniqueLabel("Servidor 1", labels),
                    playbackUri,
                    episodeUri));
                break;
            }
        }

        return sources;
    }

    /// <summary>
    /// Replaces a direct HLS source with its embedded player page when the
    /// media segments cannot actually be fetched.
    /// <para>
    /// The Zilla host serves the playlist to any client but puts its segments
    /// behind a Cloudflare block. A player therefore parses the playlist,
    /// shows the full duration, and then stalls forever because every segment
    /// answers 403 — which reads as a video that loads but never starts. The
    /// embedded page plays in a browser engine that Cloudflare admits, so it
    /// is the working route when the direct one is walled off.
    /// </para>
    /// </summary>
    private async ValueTask<IReadOnlyList<ApiContentVideoSource>>
        DowngradeUnplayableHlsSourcesAsync(
            IReadOnlyList<ApiContentVideoSource> sources,
            CancellationToken cancellationToken)
    {
        List<ApiContentVideoSource>? rewritten = null;
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            if (source.Kind is not ApiContentVideoSourceKind.DirectMedia ||
                source.Referrer is not { } playerUri ||
                !IsZillaHost(source.PlaybackUri) ||
                await ZillaHosterExtractor.AreSegmentsReachableAsync(
                    _httpClient,
                    source.PlaybackUri,
                    playerUri,
                    cancellationToken).ConfigureAwait(false))
            {
                continue;
            }

            rewritten ??= [.. sources];
            rewritten[index] = new ApiContentVideoSource(
                source.Label,
                playerUri,
                mimeType: null,
                referrer: source.Referrer,
                kind: ApiContentVideoSourceKind.EmbeddedWeb);
        }

        return rewritten ?? sources;
    }


    private static bool IsZillaHost(Uri uri) =>
        string.Equals(
            uri.IdnHost,
            "player.zilla-networks.com",
            StringComparison.OrdinalIgnoreCase);

    private static ApiContentVideoSource CreateVideoSource(
        string label,
        Uri playbackUri,
        Uri episodeUri)
    {
        if (ZillaHosterExtractor.TryBuildHlsUri(playbackUri, out var hlsUri))
        {
            return new ApiContentVideoSource(
                label,
                hlsUri,
                "application/vnd.apple.mpegurl",
                referrer: playbackUri,
                kind: ApiContentVideoSourceKind.DirectMedia,
                httpOrigin: TmoProviderUri.GetOrigin(playbackUri));
        }

        if (playbackUri.AbsolutePath.EndsWith(
                ".m3u8",
                StringComparison.OrdinalIgnoreCase))
        {
            return new ApiContentVideoSource(
                label,
                playbackUri,
                "application/vnd.apple.mpegurl",
                referrer: episodeUri);
        }

        return new ApiContentVideoSource(
            label,
            playbackUri,
            mimeType: null,
            referrer: episodeUri,
            kind: ApiContentVideoSourceKind.EmbeddedWeb);
    }


    private async ValueTask<IReadOnlyList<AnimeCatalogEntry>> LoadCatalogAsync(
        Uri root,
        CancellationToken cancellationToken)
    {
        var now = _timeProvider.GetUtcNow();
        if (_catalogByRoot.TryGetValue(root.AbsoluteUri, out var cached) &&
            IsFresh(cached, now))
        {
            return cached.Entries;
        }

        await _catalogGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            now = _timeProvider.GetUtcNow();
            if (_catalogByRoot.TryGetValue(root.AbsoluteUri, out cached) &&
                IsFresh(cached, now))
            {
                return cached.Entries;
            }

            var endpoint = new Uri(
                root,
                "wp-content/uploads/az-catalog.json");
            var json = await TmoProviderHttp.ReadStringAsync(
                _httpClient,
                endpoint,
                new Uri(root, "biblioteca"),
                MaximumCatalogBytes,
                "application/json,*/*;q=0.7",
                "AnimeTMO",
                cancellationToken).ConfigureAwait(false);
            var entries = ParseCatalog(root, json);
            _catalogByRoot[root.AbsoluteUri] = new CatalogCacheEntry(
                entries,
                now);
            return entries;
        }
        finally
        {
            _catalogGate.Release();
        }
    }

    private IReadOnlyList<AnimeCatalogEntry> ParseCatalog(
        Uri root,
        string json)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind is not JsonValueKind.Array)
        {
            throw new InvalidDataException(
                "AnimeTMO returned an invalid catalog index.");
        }

        var entries = new List<AnimeCatalogEntry>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in document.RootElement.EnumerateArray())
        {
            if (entries.Count >= MaximumCatalogItems)
            {
                throw new InvalidDataException(
                    "AnimeTMO returned too many catalog entries.");
            }

            if (element.ValueKind is not JsonValueKind.Object ||
                !element.TryGetProperty("id", out var idProperty) ||
                !idProperty.TryGetInt64(out var id) ||
                id <= 0 ||
                !TryGetString(element, "slug", out var rawSlug) ||
                !TryValidateSlug(rawSlug, out var slug) ||
                !TryGetString(element, "title", out var rawTitle))
            {
                continue;
            }

            var title = TmoProviderText.NormalizeText(rawTitle);
            var externalId = BuildExternalId(id, slug);
            if (title.Length == 0 || !seen.Add(externalId))
            {
                continue;
            }

            _ = TryGetString(element, "cover", out var coverValue);
            var cover = ResolveArtwork(root, coverValue);
            _ = TryGetString(element, "type_name", out var typeName);
            _ = TryGetString(element, "type_slug", out var typeSlug);
            _ = TryGetString(element, "stat_name", out var status);
            _ = TryGetString(element, "year", out var yearValue);
            int? year = int.TryParse(
                yearValue,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var parsedYear) &&
                parsedYear is >= ApiContentSearchRequest.MinimumReleaseYear and
                    <= ApiContentSearchRequest.MaximumReleaseYear
                ? parsedYear
                : null;
            var genres = ReadStringArray(element, "genres");
            entries.Add(new AnimeCatalogEntry(
                id,
                slug,
                title,
                cover,
                TmoProviderText.NormalizeText(typeName),
                TmoProviderText.NormalizeText(typeSlug),
                TmoProviderText.NormalizeText(status),
                year,
                genres));
        }

        return entries;
    }

    private ApiContentSearchItem CreateSearchItem(
        Uri root,
        AnimeCatalogEntry entry)
    {
        if (entry.Thumbnail is not null &&
            Uri.TryCreate(
                entry.Thumbnail,
                UriKind.Absolute,
                out var thumbnailUri))
        {
            RegisterArtworkOrigin(thumbnailUri);
        }

        var typeLabel = entry.TypeName.Length > 0
            ? entry.TypeName
            : entry.TypeSlug;
        return new ApiContentSearchItem(
            entry.ExternalId,
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = IsMovieType(entry.TypeSlug) ||
                          IsMovieType(entry.TypeName)
                    ? "anime_movie"
                    : "anime_series",
                Name = entry.Title,
                ReleaseYear = entry.ReleaseYear,
                PublicationStatus = entry.Status.Length == 0
                    ? null
                    : entry.Status,
                ThumbnailRef = entry.Thumbnail,
                Genres = entry.Genres,
                Categories = typeLabel.Length == 0
                    ? ["Anime"]
                    : ["Anime", typeLabel]
            });
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

    private static IReadOnlyList<string> ExtractGenres(string html)
    {
        var genres = new List<string>();
        foreach (var anchor in TmoProviderText.EnumerateElements(html, "a"))
        {
            var href = TmoProviderText.GetAttribute(anchor.Attributes, "href");
            if (string.IsNullOrWhiteSpace(href) ||
                !href.Contains("genre=", StringComparison.OrdinalIgnoreCase))
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
            .Select(static name => new CatalogTransferAlias { Name = name })
            .ToArray();
    }

    private static int? ExtractReleaseYear(string html)
    {
        var metadata = TmoProviderText.EnumerateClassBlocks(
                html,
                "div",
                "manga-meta-bar",
                maximumBlocks: 4)
            .FirstOrDefault();
        if (metadata is null)
        {
            return null;
        }

        var match = YearRegex.Match(TmoProviderText.HtmlToText(metadata));
        return match.Success &&
               int.TryParse(
                   match.Groups["year"].ValueSpan,
                   NumberStyles.None,
                   CultureInfo.InvariantCulture,
                   out var year)
            ? year
            : null;
    }

    private static bool TryParseEpisodeUri(
        Uri root,
        string expectedSlug,
        string? rawUri,
        out string displayNumber,
        out Uri episodeUri)
    {
        displayNumber = string.Empty;
        episodeUri = null!;
        if (!TmoProviderUri.TryResolveSameOrigin(
                root,
                rawUri,
                out var candidate))
        {
            return false;
        }

        var segments = TmoProviderUri.GetPathSegments(candidate);
        if (segments.Length != 3 ||
            !string.Equals(
                segments[0],
                "ver",
                StringComparison.OrdinalIgnoreCase) ||
            !string.Equals(
                segments[1],
                expectedSlug,
                StringComparison.OrdinalIgnoreCase) ||
            !TryValidateDisplayNumber(segments[2], out displayNumber))
        {
            return false;
        }

        episodeUri = candidate;
        return true;
    }

    private static AnimeIdentity ParseExternalId(string externalId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        var segments = externalId.Split(
            '/',
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries);
        if (segments.Length != 2 ||
            !long.TryParse(
                segments[0],
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var id) ||
            id <= 0 ||
            !TryValidateSlug(segments[1], out var slug))
        {
            throw new ArgumentException(
                "The AnimeTMO anime ID is invalid.",
                nameof(externalId));
        }

        return new AnimeIdentity(id, slug);
    }

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

    private static Uri BuildItemUri(Uri root, AnimeIdentity identity) =>
        new(root, $"anime/{Uri.EscapeDataString(identity.Slug)}/");

    private static string BuildExternalId(long id, string slug) =>
        $"{id.ToString(CultureInfo.InvariantCulture)}/{slug}";

    private static string BuildEpisodeKey(
        Uri root,
        string externalId,
        string displayNumber) =>
        ApiContentProviderCacheKey.Create(
            root,
            externalId,
            seasonNumber: null,
            ApiContentProviderCacheKey.NormalizeDisplayNumber(displayNumber));

    private static int ParseContinuationOffset(string? token)
    {
        if (token is null)
        {
            return 0;
        }

        if (!int.TryParse(
                token,
                NumberStyles.None,
                CultureInfo.InvariantCulture,
                out var offset) ||
            offset is < 0 or > MaximumContinuationOffset)
        {
            throw new ArgumentException(
                "AnimeTMO returned an invalid continuation token.",
                nameof(token));
        }

        return offset;
    }

    private static decimal ParseDisplayNumber(string value) =>
        decimal.TryParse(
            value,
            NumberStyles.Number,
            CultureInfo.InvariantCulture,
            out var number)
            ? number
            : decimal.MaxValue;

    private static bool IsMovieType(string? value)
    {
        var normalized = TmoProviderText.NormalizeText(value);
        return normalized.Contains(
                   "pelicula",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Contains(
                   "película",
                   StringComparison.OrdinalIgnoreCase) ||
               normalized.Equals("movie", StringComparison.OrdinalIgnoreCase);
    }

    private static string MakeUniqueLabel(
        string requested,
        HashSet<string> labels)
    {
        var normalized = TmoProviderText.NormalizeText(requested);
        if (labels.Add(normalized))
        {
            return normalized;
        }

        for (var suffix = 2; suffix <= MaximumServers; suffix++)
        {
            var candidate = $"{normalized} {suffix}";
            if (labels.Add(candidate))
            {
                return candidate;
            }
        }

        throw new InvalidDataException(
            "AnimeTMO returned too many duplicate server labels.");
    }

    private static bool TryGetString(
        JsonElement element,
        string name,
        out string value)
    {
        value = string.Empty;
        return element.TryGetProperty(name, out var property) &&
               property.ValueKind is JsonValueKind.String &&
               (value = property.GetString() ?? string.Empty).Length > 0;
    }

    private static IReadOnlyList<string> ReadStringArray(
        JsonElement element,
        string name)
    {
        if (!element.TryGetProperty(name, out var property) ||
            property.ValueKind is not JsonValueKind.Array)
        {
            return [];
        }

        return property.EnumerateArray()
            .Where(static value => value.ValueKind is JsonValueKind.String)
            .Select(static value =>
                TmoProviderText.NormalizeText(value.GetString()))
            .Where(static value => value.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Take(100)
            .ToArray();
    }

    private static bool IsFresh(
        CatalogCacheEntry entry,
        DateTimeOffset now)
    {
        var age = now - entry.FetchedAt;
        return age >= TimeSpan.Zero && age < CatalogCacheLifetime;
    }

    private sealed record CatalogCacheEntry(
        IReadOnlyList<AnimeCatalogEntry> Entries,
        DateTimeOffset FetchedAt);

    private sealed record AnimeCatalogEntry(
        long Id,
        string Slug,
        string Title,
        string? Thumbnail,
        string TypeName,
        string TypeSlug,
        string Status,
        int? ReleaseYear,
        IReadOnlyList<string> Genres)
    {
        internal string ExternalId => BuildExternalId(Id, Slug);
    }

    private readonly record struct AnimeIdentity(long Id, string Slug)
    {
        internal string ExternalId => BuildExternalId(Id, Slug);
    }
}

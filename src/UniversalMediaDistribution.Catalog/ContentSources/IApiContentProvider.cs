using System.Globalization;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

[Flags]
public enum ApiContentProviderScope
{
    Manga = 1,
    Anime = 2,
    LiveAction = 4,
    All = Manga | Anime | LiveAction
}

public sealed record ApiContentProviderDescriptor
{
    public ApiContentProviderDescriptor(
        string id,
        string displayName,
        string defaultBaseUrl = "",
        bool isEnabledByDefault = false,
        ApiContentProviderScope scope = ApiContentProviderScope.All,
        IReadOnlyList<string>? retiredHosts = null,
        bool providesUnitContent = true)
    {
        var normalized = new ContentSourceProviderConfiguration
        {
            Id = id,
            DisplayName = displayName,
            Kind = ContentSourceProviderKind.Api,
            BaseUrl = defaultBaseUrl,
            IsEnabled = isEnabledByDefault
        }.Normalize();
        Id = normalized.Id;
        DisplayName = normalized.DisplayName;
        DefaultBaseUrl = normalized.BaseUrl;
        IsEnabledByDefault = normalized.IsEnabled;
        if ((scope & ~ApiContentProviderScope.All) != 0 ||
            scope == 0)
        {
            throw new ArgumentOutOfRangeException(nameof(scope));
        }

        Scope = scope;
        ProvidesUnitContent = providesUnitContent;
        RetiredHosts = retiredHosts is null
            ? []
            : retiredHosts
                .Select(static host => host.Trim())
                .Where(static host => host.Length > 0)
                .ToArray();
    }

    public string Id { get; }

    public string DisplayName { get; }

    public string DefaultBaseUrl { get; }

    public bool IsEnabledByDefault { get; }

    public ApiContentProviderScope Scope { get; }

    /// <summary>False for metadata catalogs that cannot supply reader pages or playable feeds.</summary>
    public bool ProvidesUnitContent { get; }

    /// <summary>
    /// Hosts this source used to answer on and no longer does.
    /// <para>
    /// A saved base URL normally outranks <see cref="DefaultBaseUrl"/>, which is
    /// what lets someone point a provider somewhere else. But once a site
    /// abandons a domain that saved value is not a preference any more, just a
    /// dead address, and it would keep winning on every existing install while
    /// only new ones picked up the move.
    /// </para>
    /// </summary>
    public IReadOnlyList<string> RetiredHosts { get; }

    /// <summary>
    /// Whether a saved base URL points at a domain this source has left.
    /// </summary>
    public bool IsRetiredBaseUrl(string? baseUrl) =>
        RetiredHosts.Count > 0 &&
        Uri.TryCreate(baseUrl, UriKind.Absolute, out var uri) &&
        RetiredHosts.Any(host =>
            string.Equals(host, uri.Host, StringComparison.OrdinalIgnoreCase));
}

/// <summary>
/// Lightweight built-in provider metadata. Keeping descriptors outside the
/// implementation types lets the composition root enumerate settings without
/// running provider static initializers (notably their parser regex fields).
/// </summary>
public static class BuiltInApiContentProviderDescriptors
{
    public static ApiContentProviderDescriptor UmdJson { get; } = new(
        "umd-json-api",
        "UMD catalog API",
        providesUnitContent: false);

    public static ApiContentProviderDescriptor LeerCapitulo { get; } = new(
        "leercapitulo",
        "LeerCapitulo",
        "https://www.leercapitulo.co/",
        isEnabledByDefault: true,
        scope: ApiContentProviderScope.Manga);

    public static ApiContentProviderDescriptor OlympusXyz { get; } = new(
        "olympusxyz",
        "Olympus Scanlation",
        "https://olympusxyz.com/",
        isEnabledByDefault: true,
        scope: ApiContentProviderScope.Manga);

    public static ApiContentProviderDescriptor ManhwaWeb { get; } = new(
        "manhwaweb",
        "ManhwaWeb",
        "https://manhwaweb.com/",
        scope: ApiContentProviderScope.Manga);

    public static ApiContentProviderDescriptor ZonaTmo { get; } = new(
        "zonatmo",
        "ZonaTMO",
        "https://zonatmo.org/",
        scope: ApiContentProviderScope.Manga);

    public static ApiContentProviderDescriptor CapibaraTraductor { get; } = new(
        "capibaratraductor",
        "CapibaraTraductor",
        "https://capibaratraductor.com/",
        scope: ApiContentProviderScope.Manga);

    public static ApiContentProviderDescriptor UchuujinMangas { get; } = new(
        "uchuujinmangas",
        "Uchuujin Mangas",
        "https://uchuujinmangas.com/",
        scope: ApiContentProviderScope.Manga);

    public static ApiContentProviderDescriptor AnimeTmo { get; } = new(
        "animetmo",
        "AnimeTMO",
        "https://animetmo.com/",
        scope: ApiContentProviderScope.Anime);

    public static ApiContentProviderDescriptor AnimeAv1 { get; } = new(
        "animeav1", "AnimeAV1", "https://animeav1.com/",
        scope: ApiContentProviderScope.Anime);

    public static ApiContentProviderDescriptor FramexTv { get; } = new(
        "framextv", "FramexTV Movies & Series", "https://framextv.xyz/",
        scope: ApiContentProviderScope.LiveAction);

    public static ApiContentProviderDescriptor PelisPlus { get; } = new(
        "pelisplus",
        "PelisPlus Movies & Series",
        "https://pelisplus.autos/",
        isEnabledByDefault: true,
        scope: ApiContentProviderScope.LiveAction);

    public static ApiContentProviderDescriptor Doramasflix { get; } = new(
        "doramasflix",
        "Doramasflix Movies & Series",
        "https://doramasflix.in/",
        isEnabledByDefault: true,
        scope: ApiContentProviderScope.LiveAction);

    // pelisplus.to answered 301 to ww3.tioplus.net, which answered 301 again to
    // tioplus.app. Page fetches follow a single hop, so the two-leg chain threw
    // and the source failed its daily fetch outright; both legs are retired so
    // an install that saved either one moves to the address that answers.
    public static ApiContentProviderDescriptor PelisPlusTo { get; } = new(
        "pelisplus-to",
        "PelisPlus.to Movies & Series",
        "https://tioplus.app/",
        scope: ApiContentProviderScope.LiveAction,
        retiredHosts: ["pelisplus.to", "www.pelisplus.to", "ww3.tioplus.net"]);

    public static ApiContentProviderDescriptor PelisflixHd { get; } = new(
        "pelisflix-hd",
        "PelisflixHD Movies & Series",
        "https://pelisflixhd1.top/",
        scope: ApiContentProviderScope.LiveAction,
        retiredHosts: ["pelisflixhd.blog", "www.pelisflixhd.blog"]);

    public static ApiContentProviderDescriptor PoseidonHd2 { get; } = new(
        "poseidon-hd2",
        "PoseidonHD2 Movies & Series",
        "https://www.poseidonhd2.co/",
        scope: ApiContentProviderScope.LiveAction);

    public static ApiContentProviderDescriptor SeriesFlix { get; } = new(
        "seriesflix",
        "SeriesFlix",
        "https://seriesflixhd.pw/",
        isEnabledByDefault: true,
        scope: ApiContentProviderScope.LiveAction,
        retiredHosts: ["seriesflixhd.win", "www.seriesflixhd.win"]);

    public static ApiContentProviderDescriptor JkAnime { get; } = new(
        "jkanime",
        "JKAnime",
        "https://jkanime.net/",
        scope: ApiContentProviderScope.Anime);
}

/// <summary>
/// Describes an API provider without constructing its implementation. The
/// implementation is created once, on first use, so disabled providers do not
/// contribute their parser, regex, cache, or hoster-extractor cost at startup.
/// </summary>
public sealed class ApiContentProviderRegistration
{
    private readonly Lazy<IApiContentProvider> _implementation;

    public ApiContentProviderRegistration(
        ApiContentProviderDescriptor descriptor,
        Func<IApiContentProvider> factory,
        bool supportsDailyUpdates = false)
    {
        Descriptor = descriptor ??
            throw new ArgumentNullException(nameof(descriptor));
        ArgumentNullException.ThrowIfNull(factory);
        SupportsDailyUpdates = supportsDailyUpdates;
        _implementation = new Lazy<IApiContentProvider>(
            () =>
            {
                var implementation = factory() ??
                    throw new InvalidOperationException(
                        $"API provider factory '{Descriptor.Id}' returned null.");
                if (!string.Equals(
                        implementation.Descriptor.Id,
                        Descriptor.Id,
                        StringComparison.Ordinal))
                {
                    throw new InvalidOperationException(
                        $"API provider factory '{Descriptor.Id}' returned " +
                        $"'{implementation.Descriptor.Id}'.");
                }

                return implementation;
            },
            LazyThreadSafetyMode.ExecutionAndPublication);
    }

    public ApiContentProviderRegistration(IApiContentProvider implementation)
        : this(
            (implementation ??
             throw new ArgumentNullException(nameof(implementation))).Descriptor,
            () => implementation,
            implementation.SupportsDailyUpdates)
    {
    }

    public ApiContentProviderDescriptor Descriptor { get; }

    public bool SupportsDailyUpdates { get; }

    public bool IsValueCreated => _implementation.IsValueCreated;

    public IApiContentProvider GetProvider() => _implementation.Value;
}

public sealed record ApiContentSearchItem(
    string ExternalId,
    CatalogTransferItem PreviewItem,
    IReadOnlyList<ApiProviderUnitRoute>? UnitRoutes = null,
    bool RequireProviderTitle = false)
{
    public string ExternalId { get; } =
        string.IsNullOrWhiteSpace(ExternalId)
            ? throw new ArgumentException(
                "An API search result requires an external ID.",
                nameof(ExternalId))
            : ExternalId.Trim();

    public CatalogTransferItem PreviewItem { get; } =
        PreviewItem ?? throw new ArgumentNullException(nameof(PreviewItem));

    public IReadOnlyList<ApiProviderUnitRoute> UnitRoutes { get; } =
        UnitRoutes?.ToArray() ?? [];

    /// <summary>
    /// True for a persisted title binding. Providers must then reject a detail
    /// response whose title is missing instead of filling it from
    /// <see cref="PreviewItem"/>; otherwise a soft-404 page can look valid.
    /// </summary>
    public bool RequireProviderTitle { get; } = RequireProviderTitle;
}

/// <summary>
/// How a provider can answer one search filter.
/// </summary>
public enum ApiContentFilterSupport
{
    /// <summary>
    /// The provider cannot honour the filter, so it is skipped for any request
    /// that sets it. This is the default: a provider opts in to a filter, it is
    /// never assumed to support one.
    /// </summary>
    Unsupported = 0,

    /// <summary>
    /// Upstream accepts the filter directly through a route or query parameter,
    /// so the provider pays no extra requests to apply it.
    /// </summary>
    Native,

    /// <summary>
    /// Upstream cannot filter, but the provider can verify the criterion against
    /// metadata already carried on its search results. Verification must not
    /// cost a detail request per candidate; a provider that would need one
    /// reports <see cref="Unsupported"/> instead.
    /// </summary>
    VerifiedLocal
}

/// <summary>
/// What a provider can filter on. Search semantics are strict: criteria combine
/// with AND, and a provider that cannot honour every selected criterion is
/// skipped rather than allowed to silently answer a broader question.
/// </summary>
public sealed record ApiContentSearchCapabilities
{
    public ApiContentSearchCapabilities(
        ApiContentFilterSupport genre = ApiContentFilterSupport.Unsupported,
        ApiContentFilterSupport releaseYear = ApiContentFilterSupport.Unsupported,
        bool supportsFilterOnlySearch = false)
    {
        if (!Enum.IsDefined(genre))
        {
            throw new ArgumentOutOfRangeException(nameof(genre));
        }

        if (!Enum.IsDefined(releaseYear))
        {
            throw new ArgumentOutOfRangeException(nameof(releaseYear));
        }

        if (supportsFilterOnlySearch &&
            genre is ApiContentFilterSupport.Unsupported &&
            releaseYear is ApiContentFilterSupport.Unsupported)
        {
            throw new ArgumentException(
                "A provider cannot support filter-only search without " +
                "supporting at least one filter.",
                nameof(supportsFilterOnlySearch));
        }

        Genre = genre;
        ReleaseYear = releaseYear;
        SupportsFilterOnlySearch = supportsFilterOnlySearch;
    }

    /// <summary>
    /// The default for providers that only accept free text and a media type.
    /// </summary>
    public static ApiContentSearchCapabilities TextOnly { get; } = new();

    public ApiContentFilterSupport Genre { get; }

    public ApiContentFilterSupport ReleaseYear { get; }

    /// <summary>
    /// Whether the provider can run a search with no query text at all, driven
    /// by filters alone. Providers with a browsable genre route can; providers
    /// whose only entry point is a search box cannot.
    /// </summary>
    public bool SupportsFilterOnlySearch { get; }

    /// <summary>
    /// Whether every criterion the request sets can be honoured. Callers use
    /// this to skip a provider before issuing a request, which is what keeps an
    /// unsupported filter from quietly widening the result set.
    /// </summary>
    public bool CanSatisfy(ApiContentSearchRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Query is null && !SupportsFilterOnlySearch)
        {
            return false;
        }

        if (request.Genre is not null &&
            Genre is ApiContentFilterSupport.Unsupported)
        {
            return false;
        }

        return request.ReleaseYear is null ||
            ReleaseYear is not ApiContentFilterSupport.Unsupported;
    }
}

/// <summary>
/// A single search request. At least one of query text, genre, or release year
/// is required, and the criteria that are set combine with AND.
/// </summary>
public sealed record ApiContentSearchRequest
{
    public const int DefaultPageSize = 50;

    public const int MaximumPageSize = 200;

    /// <summary>
    /// Sanity bounds only. They reject obvious nonsense such as a zero or a
    /// five-digit year; they are not a statement about what any catalog carries.
    /// </summary>
    public const int MinimumReleaseYear = 1800;

    public const int MaximumReleaseYear = 2200;

    private const int MaximumContinuationTokenLength = 512;

    public ApiContentSearchRequest(
        string? query = null,
        MediaType? mediaType = null,
        ApiContentGenre? genre = null,
        int? releaseYear = null,
        int pageSize = DefaultPageSize,
        string? continuationToken = null)
    {
        var normalizedQuery = string.IsNullOrWhiteSpace(query)
            ? null
            : query.Trim();
        if (normalizedQuery is null && genre is null && releaseYear is null)
        {
            throw new ArgumentException(
                "A search requires at least one of query text, genre, or " +
                "release year.");
        }

        if (mediaType is not null && !Enum.IsDefined(mediaType.Value))
        {
            throw new ArgumentOutOfRangeException(nameof(mediaType));
        }

        if (releaseYear is < MinimumReleaseYear or > MaximumReleaseYear)
        {
            throw new ArgumentOutOfRangeException(
                nameof(releaseYear),
                releaseYear,
                $"A release year must be between {MinimumReleaseYear} and " +
                $"{MaximumReleaseYear}.");
        }

        if (pageSize is < 1 or > MaximumPageSize)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                $"A page size must be between 1 and {MaximumPageSize}.");
        }

        var normalizedToken = string.IsNullOrWhiteSpace(continuationToken)
            ? null
            : continuationToken.Trim();
        if (normalizedToken is { Length: > MaximumContinuationTokenLength })
        {
            throw new ArgumentException(
                "A continuation token is too long to have come from a " +
                "provider.",
                nameof(continuationToken));
        }

        Query = normalizedQuery;
        MediaType = mediaType;
        Genre = genre;
        ReleaseYear = releaseYear;
        PageSize = pageSize;
        ContinuationToken = normalizedToken;
    }

    /// <summary>Free text, or null for a filter-only search.</summary>
    public string? Query { get; }

    public MediaType? MediaType { get; }

    public ApiContentGenre? Genre { get; }

    /// <summary>
    /// The exact original release or first-air year, never a re-release or
    /// last-updated year.
    /// </summary>
    public int? ReleaseYear { get; }

    public int PageSize { get; }

    /// <summary>
    /// Opaque provider-issued cursor from a previous
    /// <see cref="ApiContentSearchPage"/>, or null for the first page.
    /// </summary>
    public string? ContinuationToken { get; }

    public bool HasFilters => Genre is not null || ReleaseYear is not null;
}

/// <summary>
/// One page of search results.
/// </summary>
public sealed record ApiContentSearchPage
{
    private const int MaximumItems = 5_000;

    private const int MaximumContinuationTokenLength = 512;

    public ApiContentSearchPage(
        IEnumerable<ApiContentSearchItem> items,
        string? continuationToken = null,
        bool isTruncated = false)
    {
        ArgumentNullException.ThrowIfNull(items);
        var materialized = items.ToArray();
        if (materialized.Length > MaximumItems)
        {
            throw new ArgumentException(
                $"A search page cannot carry more than {MaximumItems} items.",
                nameof(items));
        }

        if (Array.IndexOf(materialized, null) >= 0)
        {
            throw new ArgumentException(
                "A search page cannot contain null items.",
                nameof(items));
        }

        var normalizedToken = string.IsNullOrWhiteSpace(continuationToken)
            ? null
            : continuationToken.Trim();
        if (normalizedToken is { Length: > MaximumContinuationTokenLength })
        {
            throw new ArgumentException(
                "A continuation token is too long.",
                nameof(continuationToken));
        }

        if (isTruncated && normalizedToken is not null)
        {
            throw new ArgumentException(
                "A truncated page cannot also offer a continuation token: " +
                "truncation means candidates the caller has no way to reach " +
                "were never examined.",
                nameof(isTruncated));
        }

        Items = materialized;
        ContinuationToken = normalizedToken;
        IsTruncated = isTruncated;
    }

    public static ApiContentSearchPage Empty { get; } = new([]);

    public IReadOnlyList<ApiContentSearchItem> Items { get; }

    /// <summary>
    /// Cursor for the next page, or null when the scan reached the end of what
    /// upstream offers.
    /// </summary>
    public string? ContinuationToken { get; }

    /// <summary>
    /// Set when the provider abandoned the scan at its own safety bound rather
    /// than at the end of the results. Distinct from paging: there is no token
    /// that reaches the candidates it skipped, so an empty or short page is not
    /// evidence that nothing else matches. Callers must not present a truncated
    /// page as a complete answer.
    /// </summary>
    public bool IsTruncated { get; }
}

public sealed record ApiContentImagePage
{
    public ApiContentImagePage(
        int position,
        Uri uri,
        Uri? referrer = null)
    {
        if (position < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(position));
        }

        ArgumentNullException.ThrowIfNull(uri);
        if (!uri.IsAbsoluteUri ||
            uri.Scheme is not ("http" or "https") ||
            uri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "API image pages require absolute HTTP or HTTPS URLs without embedded credentials.",
                nameof(uri));
        }

        if (referrer is not null &&
            (!referrer.IsAbsoluteUri ||
             referrer.Scheme is not ("http" or "https") ||
             referrer.UserInfo.Length > 0))
        {
            throw new ArgumentException(
                "API image-page referrers require absolute HTTP or HTTPS URLs without embedded credentials.",
                nameof(referrer));
        }

        Position = position;
        Uri = uri;
        Referrer = referrer;
    }

    public int Position { get; }

    public Uri Uri { get; }

    /// <summary>
    /// Optional provider page that the image host expects in the Referer
    /// request header. Providers derive this from their configured base URI.
    /// </summary>
    public Uri? Referrer { get; }
}

public sealed record ApiContentArtworkPolicy
{
    public ApiContentArtworkPolicy(Uri origin, Uri? referrer = null)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (!origin.IsAbsoluteUri ||
            origin.Scheme is not ("http" or "https") ||
            origin.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "An artwork policy requires an absolute HTTP or HTTPS origin without embedded credentials.",
                nameof(origin));
        }

        if (referrer is not null &&
            (!referrer.IsAbsoluteUri ||
             referrer.Scheme is not ("http" or "https") ||
             referrer.UserInfo.Length > 0))
        {
            throw new ArgumentException(
                "An artwork referrer requires an absolute HTTP or HTTPS URL without embedded credentials.",
                nameof(referrer));
        }

        Origin = new UriBuilder(
            origin.Scheme,
            origin.IdnHost,
            origin.IsDefaultPort ? -1 : origin.Port,
            "/").Uri;
        Referrer = referrer;
    }

    public Uri Origin { get; }

    public Uri? Referrer { get; }

    public bool Matches(Uri uri)
    {
        ArgumentNullException.ThrowIfNull(uri);
        return uri.IsAbsoluteUri &&
               string.Equals(
                   uri.Scheme,
                   Origin.Scheme,
                   StringComparison.OrdinalIgnoreCase) &&
               string.Equals(
                   uri.IdnHost,
                   Origin.IdnHost,
                   StringComparison.OrdinalIgnoreCase) &&
               uri.Port == Origin.Port;
    }
}

public sealed record ApiContentImageSet
{
    public ApiContentImageSet(
        ContentUnitKind? unitKind,
        string? displayNumber,
        int? seasonNumber,
        IEnumerable<ApiContentImagePage> pages)
    {
        if ((unitKind is null) != string.IsNullOrWhiteSpace(displayNumber))
        {
            throw new ArgumentException(
                "An image set must target either the whole title or a unit kind and display number.");
        }

        if (seasonNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        }

        ArgumentNullException.ThrowIfNull(pages);
        var orderedPages = pages
            .OrderBy(static page => page.Position)
            .ToArray();
        if (orderedPages.Length == 0 ||
            orderedPages.Select(static page => page.Position)
                .Where((position, index) => position != index)
                .Any())
        {
            throw new ArgumentException(
                "API image pages must have contiguous zero-based positions.",
                nameof(pages));
        }

        UnitKind = unitKind;
        DisplayNumber = string.IsNullOrWhiteSpace(displayNumber)
            ? null
            : displayNumber.Trim();
        SeasonNumber = seasonNumber;
        Pages = orderedPages;
    }

    public ContentUnitKind? UnitKind { get; }

    public string? DisplayNumber { get; }

    public int? SeasonNumber { get; }

    public IReadOnlyList<ApiContentImagePage> Pages { get; }

    public bool IsItemLevel => UnitKind is null;

    public bool Matches(ContentUnitKind kind, string displayNumber, int? seasonNumber) =>
        new ApiContentUnitRequest(
            kind,
            displayNumber,
            seasonNumber).Matches(this);
}

public enum ApiContentVideoSourceKind
{
    DirectMedia,
    EmbeddedWeb
}

/// <summary>
/// Carries a ClearKey key pair (kid + key) extracted from a DASH manifest's
/// embedded license info. The native player applies these to a DASH stream
/// so we can play DRM-encrypted content without bouncing through the
/// browser's EME pipeline.
/// </summary>
public sealed record ApiContentVideoDecryptionKey
{
    public ApiContentVideoDecryptionKey(string keyId, string key)
    {
        if (string.IsNullOrWhiteSpace(keyId) ||
            !IsHexString(keyId) ||
            keyId.Length is < 16 or > 64)
        {
            throw new ArgumentException(
                "A decryption key ID must be a 16 to 64 character hexadecimal string.",
                nameof(keyId));
        }

        if (string.IsNullOrWhiteSpace(key) ||
            !IsHexString(key) ||
            key.Length is < 16 or > 64)
        {
            throw new ArgumentException(
                "A decryption key must be a 16 to 64 character hexadecimal string.",
                nameof(key));
        }

        KeyId = keyId.Trim().ToLowerInvariant();
        Key = key.Trim().ToLowerInvariant();
    }

    public string KeyId { get; }

    public string Key { get; }

    private static bool IsHexString(string value)
    {
        foreach (var character in value)
        {
            if (!char.IsAsciiHexDigit(character))
            {
                return false;
            }
        }

        return true;
    }
}

public sealed record ApiContentSubtitleTrack
{
    public ApiContentSubtitleTrack(Uri source, string label, string language = "und", bool isDefault = false)
    {
        ArgumentNullException.ThrowIfNull(source);
        if (!HosterExtractorBase.IsSafePublicHttpUri(source)) throw new ArgumentException("Subtitles require a public HTTP URL.", nameof(source));
        if (string.IsNullOrWhiteSpace(label) || label.Length > 200 || label.Contains('\r') || label.Contains('\n'))
            throw new ArgumentException("Invalid subtitle label.", nameof(label));
        if (string.IsNullOrWhiteSpace(language) || language.Length > 80 || language.Contains('\r') || language.Contains('\n'))
            throw new ArgumentException("Invalid subtitle language.", nameof(language));
        Source = source; Label = label; Language = language; IsDefault = isDefault;
    }
    public Uri Source { get; }
    public string Label { get; }
    public string Language { get; }
    public bool IsDefault { get; }
}

public sealed record ApiContentVideoSource
{
    public ApiContentVideoSource(
        string label,
        Uri playbackUri,
        string? mimeType = null,
        Uri? referrer = null,
        ApiContentVideoSourceKind kind =
            ApiContentVideoSourceKind.DirectMedia,
        IReadOnlyList<ApiContentVideoDecryptionKey>? decryptionKeys = null,
        Uri? httpOrigin = null,
        string? userAgent = null,
        IReadOnlyList<ApiContentSubtitleTrack>? subtitles = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(playbackUri);
        if (!playbackUri.IsAbsoluteUri ||
            playbackUri.Scheme is not ("http" or "https") ||
            playbackUri.UserInfo.Length > 0)
        {
            throw new ArgumentException(
                "API video sources require absolute HTTP or HTTPS URLs without embedded credentials.",
                nameof(playbackUri));
        }

        if (referrer is not null &&
            (!referrer.IsAbsoluteUri ||
             referrer.Scheme is not ("http" or "https") ||
             referrer.UserInfo.Length > 0))
        {
            throw new ArgumentException(
                "API video referrers require absolute HTTP or HTTPS URLs without embedded credentials.",
                nameof(referrer));
        }

        if (httpOrigin is not null &&
            (!httpOrigin.IsAbsoluteUri ||
             httpOrigin.Scheme is not ("http" or "https") ||
             httpOrigin.UserInfo.Length > 0 ||
             httpOrigin.AbsolutePath != "/" ||
             httpOrigin.Query.Length > 0 ||
             httpOrigin.Fragment.Length > 0))
        {
            throw new ArgumentException(
                "API video origins require an absolute HTTP or HTTPS origin without credentials, a path, query, or fragment.",
                nameof(httpOrigin));
        }

        var keys = decryptionKeys?.ToArray() ?? [];
        if (keys.Length > 32)
        {
            throw new ArgumentException(
                "An API video source cannot carry more than 32 ClearKey pairs.",
                nameof(decryptionKeys));
        }

        Label = label.Trim();
        if (userAgent is not null && (userAgent.Length > 1024 || userAgent.Contains('\r') || userAgent.Contains('\n')))
            throw new ArgumentException("Invalid media user agent.", nameof(userAgent));
        UserAgent = userAgent;
        PlaybackUri = playbackUri;
        MimeType = string.IsNullOrWhiteSpace(mimeType)
            ? null
            : mimeType.Trim();
        Referrer = referrer;
        HttpOrigin = httpOrigin;
        Kind = kind;
        DecryptionKeys = keys;
        if (subtitles?.Count > 100) throw new ArgumentException("Too many subtitle tracks.", nameof(subtitles));
        Subtitles = subtitles?.ToArray() ?? [];
    }

    public string Label { get; }

    public Uri PlaybackUri { get; }

    public string? MimeType { get; }

    public Uri? Referrer { get; }

    /// <summary>
    /// Optional value for the media request's HTTP <c>Origin</c> header.
    /// Hosters may require this independently of the <c>Referer</c> header.
    /// </summary>
    public Uri? HttpOrigin { get; }

    public string? UserAgent { get; }

    public IReadOnlyList<ApiContentSubtitleTrack> Subtitles { get; }

    public ApiContentVideoSourceKind Kind { get; }

    public IReadOnlyList<ApiContentVideoDecryptionKey> DecryptionKeys { get; }

    public bool IsEmbeddedWeb =>
        Kind is ApiContentVideoSourceKind.EmbeddedWeb;

    public bool RequiresClearKeyDecryption =>
        DecryptionKeys.Count > 0;
}

public sealed record ApiContentVideoSet
{
    public ApiContentVideoSet(
        ContentUnitKind unitKind,
        string displayNumber,
        int? seasonNumber,
        IEnumerable<ApiContentVideoSource> sources)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNumber);
        if (seasonNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        }

        ArgumentNullException.ThrowIfNull(sources);
        var availableSources = sources.ToArray();
        if (availableSources.Length == 0)
        {
            throw new ArgumentException(
                "An API video set requires at least one playback server.",
                nameof(sources));
        }

        if (availableSources.Length > 100 ||
            availableSources
                .GroupBy(
                    static source => source.Label,
                    StringComparer.OrdinalIgnoreCase)
                .Any(static group => group.Count() > 1))
        {
            throw new ArgumentException(
                "API video server labels must be unique and no more than 100 servers may be returned.",
                nameof(sources));
        }

        UnitKind = unitKind;
        DisplayNumber = displayNumber.Trim();
        SeasonNumber = seasonNumber;
        Sources = availableSources;
    }

    public ContentUnitKind UnitKind { get; }

    public string DisplayNumber { get; }

    public int? SeasonNumber { get; }

    public IReadOnlyList<ApiContentVideoSource> Sources { get; }

    public bool Matches(
        ContentUnitKind kind,
        string displayNumber,
        int? seasonNumber) =>
        new ApiContentUnitRequest(
            kind,
            displayNumber,
            seasonNumber).Matches(this);
}

public sealed record ApiContentUnitRequest
{
    public ApiContentUnitRequest(
        ContentUnitKind kind,
        string displayNumber,
        int? seasonNumber,
        string? name = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayNumber);
        if (seasonNumber is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber));
        }

        Kind = kind;
        DisplayNumber = displayNumber.Trim();
        SeasonNumber = seasonNumber;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
    }

    public ContentUnitKind Kind { get; }

    public string DisplayNumber { get; }

    public int? SeasonNumber { get; }

    public string? Name { get; }

    public bool Matches(ApiContentImageSet imageSet)
    {
        ArgumentNullException.ThrowIfNull(imageSet);
        return imageSet.UnitKind == Kind &&
               imageSet.SeasonNumber == SeasonNumber &&
               DisplayNumbersMatch(
                   imageSet.DisplayNumber,
                   DisplayNumber);
    }

    public bool Matches(ApiContentVideoSet videoSet)
    {
        ArgumentNullException.ThrowIfNull(videoSet);
        return videoSet.UnitKind == Kind &&
               videoSet.SeasonNumber == SeasonNumber &&
               DisplayNumbersMatch(
                   videoSet.DisplayNumber,
                   DisplayNumber);
    }

    public static bool DisplayNumbersMatch(
        string? left,
        string? right)
    {
        if (string.IsNullOrWhiteSpace(left) ||
            string.IsNullOrWhiteSpace(right))
        {
            return false;
        }

        if (string.Equals(left, right, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return decimal.TryParse(
                   left.AsSpan(),
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out var leftNumber) &&
               decimal.TryParse(
                   right.AsSpan(),
                   NumberStyles.Number,
                   CultureInfo.InvariantCulture,
                   out var rightNumber) &&
               leftNumber == rightNumber;
    }
}

internal static class ApiContentProviderCacheKey
{
    public static string Create(
        Uri root,
        string externalId,
        int? seasonNumber,
        string normalizedDisplayNumber)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentException.ThrowIfNullOrWhiteSpace(externalId);
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedDisplayNumber);

        var rootValue = root.AbsoluteUri;
        var seasonLength = seasonNumber is null
            ? 0
            : GetInvariantLength(seasonNumber.Value);
        return string.Create(
            rootValue.Length +
            externalId.Length +
            normalizedDisplayNumber.Length +
            seasonLength +
            3,
            (
                Root: rootValue,
                ExternalId: externalId,
                SeasonNumber: seasonNumber,
                DisplayNumber: normalizedDisplayNumber),
            static (destination, state) =>
            {
                var offset = 0;
                state.Root.AsSpan().CopyTo(destination);
                offset += state.Root.Length;
                destination[offset++] = '\n';
                state.ExternalId.AsSpan().CopyTo(destination[offset..]);
                offset += state.ExternalId.Length;
                destination[offset++] = '\n';
                if (state.SeasonNumber is int season)
                {
                    _ = season.TryFormat(
                        destination[offset..],
                        out var written,
                        provider: CultureInfo.InvariantCulture);
                    offset += written;
                }

                destination[offset++] = '\n';
                state.DisplayNumber.AsSpan().CopyTo(destination[offset..]);
            });
    }

    public static string NormalizeDisplayNumber(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var start = 0;
        var end = value.Length;
        while (start < end && char.IsWhiteSpace(value[start]))
        {
            start++;
        }

        while (end > start && char.IsWhiteSpace(value[end - 1]))
        {
            end--;
        }

        var trimmed = value.AsSpan(start, end - start);
        if (decimal.TryParse(
                trimmed,
                NumberStyles.Number,
                CultureInfo.InvariantCulture,
                out var number))
        {
            return number.ToString(
                "0.############################",
                CultureInfo.InvariantCulture);
        }

        return string.Create(
            trimmed.Length,
            (Value: value, Start: start),
            static (destination, state) =>
            {
                var source = state.Value.AsSpan(
                    state.Start,
                    destination.Length);
                for (var index = 0; index < source.Length; index++)
                {
                    destination[index] = char.ToUpperInvariant(source[index]);
                }
            });
    }

    private static int GetInvariantLength(int value)
    {
        Span<char> buffer = stackalloc char[11];
        _ = value.TryFormat(
            buffer,
            out var written,
            provider: CultureInfo.InvariantCulture);
        return written;
    }
}

public sealed record ApiContentItem
{
    public ApiContentItem(
        CatalogTransferItem catalogItem,
        IEnumerable<ApiContentImageSet>? imageSets = null,
        IEnumerable<ApiContentVideoSet>? videoSets = null)
    {
        CatalogItem = catalogItem ??
            throw new ArgumentNullException(nameof(catalogItem));
        ImageSets = imageSets?.ToArray() ?? [];
        if (ImageSets.Count > 20_000 ||
            ImageSets.Sum(static set => (long)set.Pages.Count) > 100_000)
        {
            throw new ArgumentException(
                "An API item contains too many image sets or pages.",
                nameof(imageSets));
        }

        VideoSets = videoSets?.ToArray() ?? [];
        if (VideoSets.Count > 20_000 ||
            VideoSets.Sum(static set => (long)set.Sources.Count) > 100_000)
        {
            throw new ArgumentException(
                "An API item contains too many video sets or playback servers.",
                nameof(videoSets));
        }
    }

    public CatalogTransferItem CatalogItem { get; }

    public IReadOnlyList<ApiContentImageSet> ImageSets { get; }

    public IReadOnlyList<ApiContentVideoSet> VideoSets { get; }
}

/// <summary>
/// Code-only integration point for an online content API. Implementations translate
/// provider-specific search and detail payloads into the ID-free UMD catalog model.
/// Users can enable an implementation and change its base URL, but they cannot create
/// an implementation from settings.
/// </summary>
public interface IApiContentProvider
{
    ApiContentProviderDescriptor Descriptor { get; }

    bool SupportsDailyUpdates => false;

    IReadOnlyList<Uri> GetTrustedArtworkOrigins(Uri baseUri) => [baseUri];

    /// <summary>
    /// Returns a referrer for an artwork request when the provider's image host
    /// requires one. The URI must come from the provider's configured base URI
    /// or current response data; callers do not substitute a built-in domain.
    /// </summary>
    Uri? GetArtworkReferrer(Uri baseUri, Uri artworkUri) => null;

    ValueTask<IReadOnlyList<ApiContentSearchItem>> LoadDailyUpdatesAsync(
        Uri baseUri,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<IReadOnlyList<ApiContentSearchItem>>([]);

    /// <summary>
    /// What this provider can filter on. The default claims nothing beyond free
    /// text, so an existing provider keeps working and is skipped for any
    /// request that sets a filter it has not implemented.
    /// </summary>
    ApiContentSearchCapabilities SearchCapabilities =>
        ApiContentSearchCapabilities.TextOnly;

    /// <summary>
    /// Text search. Kept as the primitive every provider implements, and as the
    /// path title resolution uses when it already knows the name it wants.
    /// </summary>
    ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Filter-aware search. Override this when declaring any capability beyond
    /// <see cref="ApiContentSearchCapabilities.TextOnly"/>; the default
    /// implementation forwards a text-only request to the text overload.
    /// </summary>
    async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!SearchCapabilities.CanSatisfy(request))
        {
            // Skipped rather than broadened. Callers should normally test
            // CanSatisfy first and avoid the round trip altogether.
            return ApiContentSearchPage.Empty;
        }

        if (request.Query is null || request.HasFilters)
        {
            throw new InvalidOperationException(
                $"API provider '{Descriptor.Id}' declares search capabilities " +
                "that its implementation does not provide. Override the " +
                "request-based SearchAsync overload when declaring genre, " +
                "release year, or filter-only search support.");
        }

        var items = await SearchAsync(
            baseUri,
            request.Query,
            request.MediaType,
            cancellationToken);
        return new ApiContentSearchPage(items);
    }

    ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Loads enough title metadata to verify one selected unit. Providers with
    /// season or chapter-list endpoints can override this to avoid enumerating
    /// the complete title during provider discovery.
    /// </summary>
    ValueTask<ApiContentItem> LoadItemForUnitAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        return LoadItemAsync(baseUri, searchItem, cancellationToken);
    }

    /// <summary>
    /// Returns the provider-owned route learned for a successfully resolved
    /// unit. The catalog persists this opaque value with the title binding and
    /// supplies it on the next lookup so the provider can skip rediscovering a
    /// complete chapter or season directory.
    /// </summary>
    IReadOnlyList<ApiProviderUnitRoute> GetKnownUnitRoutes(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit) =>
        [];

    async ValueTask<ApiContentImageSet?> LoadUnitImagesAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var item = await LoadItemAsync(
            baseUri,
            searchItem,
            cancellationToken);
        return item.ImageSets.FirstOrDefault(unit.Matches);
    }

    async ValueTask<ApiContentVideoSet?> LoadUnitVideosAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        ApiContentUnitRequest unit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(unit);
        var item = await LoadItemAsync(
            baseUri,
            searchItem,
            cancellationToken);
        return item.VideoSets.FirstOrDefault(unit.Matches);
    }
}

/// <summary>
/// Reference code provider for a UMD-compatible HTTP API.
/// GET {baseUrl}/search?q=...&amp;mediaType=... returns a CatalogTransferDocument.
/// The search response contains complete items, so selecting one does not require a
/// second provider-specific representation.
/// </summary>
public sealed partial class UmdJsonApiContentProvider(HttpClient httpClient) :
    IApiContentProvider
{
    public const string ProviderId = "umd-json-api";

    private readonly HttpClient _httpClient =
        httpClient ?? throw new ArgumentNullException(nameof(httpClient));

    public static ApiContentProviderDescriptor DefaultDescriptor =>
        BuiltInApiContentProviderDescriptors.UmdJson;

    public ApiContentProviderDescriptor Descriptor => DefaultDescriptor;

    /// <summary>
    /// The protocol carries optional genre and year parameters, but the
    /// guarantee comes from verification rather than from trust: a search
    /// response holds complete items, so every filter is re-checked against the
    /// returned metadata. A server that ignores a parameter therefore still
    /// produces a correctly filtered result instead of a quietly broadened one.
    /// </summary>
    public ApiContentSearchCapabilities SearchCapabilities { get; } = new(
        genre: ApiContentFilterSupport.VerifiedLocal,
        releaseYear: ApiContentFilterSupport.VerifiedLocal,
        supportsFilterOnlySearch: true);

    public async ValueTask<IReadOnlyList<ApiContentSearchItem>> SearchAsync(
        Uri baseUri,
        string query,
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentException.ThrowIfNullOrWhiteSpace(query);
        return await ReadSearchAsync(
            baseUri,
            new ApiContentSearchRequest(query, mediaType),
            cancellationToken);
    }

    public async ValueTask<ApiContentSearchPage> SearchAsync(
        Uri baseUri,
        ApiContentSearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(request);
        return new ApiContentSearchPage(
            await ReadSearchAsync(baseUri, request, cancellationToken));
    }

    private async ValueTask<IReadOnlyList<ApiContentSearchItem>>
        ReadSearchAsync(
            Uri baseUri,
            ApiContentSearchRequest request,
            CancellationToken cancellationToken)
    {
        var endpoint = BuildSearchUri(baseUri, request);
        var document = await CatalogDocumentHttpReader.ReadAsync(
            _httpClient,
            endpoint,
            cancellationToken);
        return document.Items
            .Where(item => Matches(item, request))
            .Select(static item => new ApiContentSearchItem(
                BuildExternalId(item),
                item))
            .ToArray();
    }

    private static bool Matches(
        CatalogTransferItem item,
        ApiContentSearchRequest request)
    {
        if (request.MediaType is { } mediaType &&
            CatalogSubtypeResolver.ParseMediaType(item.MediaType) != mediaType)
        {
            return false;
        }

        if (request.Genre is { } genre &&
            !genre.MatchesAny(item.Genres) &&
            !genre.MatchesAny(item.Categories))
        {
            return false;
        }

        return request.ReleaseYear is not { } year ||
            CatalogReleaseYear.Resolve(item.ReleaseYear, item.ReleaseDate) ==
                year;
    }

    public ValueTask<ApiContentItem> LoadItemAsync(
        Uri baseUri,
        ApiContentSearchItem searchItem,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(baseUri);
        ArgumentNullException.ThrowIfNull(searchItem);
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new ApiContentItem(searchItem.PreviewItem));
    }

    private static Uri BuildSearchUri(
        Uri baseUri,
        ApiContentSearchRequest request)
    {
        var root = baseUri.AbsoluteUri.EndsWith(
            "/",
            StringComparison.Ordinal)
            ? baseUri
            : new Uri(baseUri.AbsoluteUri + '/', UriKind.Absolute);
        var parameters = new List<string>(4);
        if (request.Query is { } query)
        {
            parameters.Add($"q={Uri.EscapeDataString(query)}");
        }

        if (request.MediaType is { } mediaType)
        {
            parameters.Add(
                "mediaType=" +
                Uri.EscapeDataString(
                    mediaType.ToString().ToLowerInvariant()));
        }

        // The stable key is the token: this protocol is ours, so there is no
        // foreign taxonomy to map onto.
        if (request.Genre is { } genre)
        {
            parameters.Add($"genre={Uri.EscapeDataString(genre.Key)}");
        }

        if (request.ReleaseYear is { } year)
        {
            parameters.Add(
                $"year={year.ToString(CultureInfo.InvariantCulture)}");
        }

        return new Uri(root, $"search?{string.Join('&', parameters)}");
    }

    internal static string BuildExternalId(CatalogTransferItem item) =>
        CatalogTransferIdentity.GetKey(item);
}

internal static class CatalogDocumentHttpReader
{
    public static async ValueTask<CatalogTransferDocument> ReadAsync(
        HttpClient httpClient,
        Uri endpoint,
        CancellationToken cancellationToken)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        request.Headers.Accept.ParseAdd("application/json");
        using var response = await ApiProviderHttp.SendAsync(
            httpClient,
            request,
            HttpCompletionOption.ResponseHeadersRead,
            cancellationToken);
        response.EnsureSuccessStatusCode();
        if (response.Content.Headers.ContentLength >
            CatalogJsonTransferService.MaximumDocumentBytes)
        {
            throw new CatalogTransferValidationException(
                [$"Catalog JSON exceeds the {CatalogJsonTransferService.MaximumDocumentBytes / 1024 / 1024} MiB limit."]);
        }

        await using var stream =
            await response.Content.ReadAsStreamAsync(cancellationToken);
        var bytes = await CatalogJsonTransferService.ReadDocumentBytesAsync(
            stream,
            cancellationToken);
        return CatalogJsonTransferService.DeserializeAndValidate(bytes);
    }
}

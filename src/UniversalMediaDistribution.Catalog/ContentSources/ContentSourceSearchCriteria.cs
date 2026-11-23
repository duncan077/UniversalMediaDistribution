using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// What the user asked for, in one place, so the local catalog, the torrent
/// catalog, and every API provider are held to the same standard.
/// <para>
/// Criteria combine with AND, and each one is exact: a year means that exact
/// original release year, and a genre means that genre and not a broader one.
/// A source that cannot honour every criterion is skipped rather than allowed
/// to answer a wider question than the one asked.
/// </para>
/// </summary>
public sealed record ContentSourceSearchCriteria
{
    public const int MaximumQueryLength = 200;

    public ContentSourceSearchCriteria(
        string? query = null,
        MediaType? mediaType = null,
        MediaSubtypeGroup? subtypeGroup = null,
        ApiContentGenre? genre = null,
        int? releaseYear = null,
        string? category = null,
        PublicationStatus? status = null,
        string? providerName = null,
        bool browseOnline = false,
        bool localOnly = false)
    {
        var normalizedQuery = string.IsNullOrWhiteSpace(query)
            ? null
            : query.Trim();
        category = CatalogSearchMetadataFilters.NormalizeLabel(category);
        providerName = CatalogSearchMetadataFilters.NormalizeLabel(providerName);
        if (status is { } value && !Enum.IsDefined(value)) throw new ArgumentOutOfRangeException(nameof(status));
        if (normalizedQuery is null && genre is null && releaseYear is null && category is null && status is null && providerName is null && !browseOnline)
        {
            throw new ArgumentException(
                "A search requires text, a public metadata filter, or explicit online browsing.");
        }

        if (normalizedQuery is { Length: > MaximumQueryLength })
        {
            throw new ArgumentException(
                $"Search queries cannot exceed {MaximumQueryLength} " +
                "characters.",
                nameof(query));
        }

        if (releaseYear is { } year && !CatalogReleaseYear.IsPlausible(year))
        {
            throw new ArgumentOutOfRangeException(
                nameof(releaseYear),
                releaseYear,
                $"A release year must be between " +
                $"{CatalogReleaseYear.MinimumYear} and " +
                $"{CatalogReleaseYear.MaximumYear}.");
        }

        Query = normalizedQuery;
        MediaType = mediaType;
        SubtypeGroup = subtypeGroup;
        Genre = genre;
        ReleaseYear = releaseYear;
        Category = category;
        Status = status;
        ProviderName = providerName;
        BrowseOnline = browseOnline;
        LocalOnly = localOnly;
    }

    public string? Query { get; }

    public MediaType? MediaType { get; }

    public MediaSubtypeGroup? SubtypeGroup { get; }

    public ApiContentGenre? Genre { get; }

    public int? ReleaseYear { get; }

    public string? Category { get; }
    public PublicationStatus? Status { get; }
    public string? ProviderName { get; }
    public bool BrowseOnline { get; }
    public bool LocalOnly { get; }
    public bool HasCatalogFilters => Category is not null || Status is not null || ProviderName is not null || BrowseOnline;
    public bool HasFilters => Genre is not null || ReleaseYear is not null || HasCatalogFilters;

    /// <summary>
    /// True when nothing but filters was given, so the search browses rather
    /// than looks something up by name.
    /// </summary>
    public bool IsFilterOnly => Query is null;

    /// <summary>
    /// Applies every criterion to a card the caller already holds. Used for the
    /// local catalog and for sources that cannot filter upstream, so a result
    /// that reaches the user has been checked the same way whatever produced
    /// it. Media type and subtype group are deliberately not checked here:
    /// callers scope those before a card is ever built.
    /// </summary>
    public bool Matches(CatalogCard card) => Matches(card, sourceName: null);

    public bool Matches(CatalogCard card, string? sourceName)
    {
        ArgumentNullException.ThrowIfNull(card);
        if (!CatalogSearchMetadataFilters.Matches(card, Category, Status,
                string.Equals(sourceName, ProviderName, StringComparison.OrdinalIgnoreCase) ? null : ProviderName)) return false;
        if (ReleaseYear is not null && card.ReleaseYear != ReleaseYear)
        {
            return false;
        }

        if (Genre is not null &&
            !Genre.MatchesAny(card.Genres) &&
            !Genre.MatchesAny(card.Categories))
        {
            return false;
        }

        return Query is null || MatchesText(card, Query);
    }

    /// <summary>
    /// Only the text criterion. Used to re-check a provider's own results,
    /// which have long been held to the query text even when the provider
    /// matched loosely, without also re-checking filters the provider already
    /// applied upstream.
    /// </summary>
    public bool MatchesQueryText(CatalogCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        return Query is null || MatchesText(card, Query);
    }

    /// <summary>
    /// The same criteria expressed for an API provider, so the provider's
    /// declared capabilities can be tested against the real request.
    /// </summary>
    public ApiContentSearchRequest ToApiRequest() =>
        new(Query, MediaType, Genre, ReleaseYear, category: Category, status: Status,
            providerName: ProviderName, browse: BrowseOnline, subtypeGroup: SubtypeGroup);

    private static bool MatchesText(CatalogCard card, string query)
    {
        static bool Contains(string? value, string queryValue) =>
            !string.IsNullOrWhiteSpace(value) &&
            CultureInfo.InvariantCulture.CompareInfo.IndexOf(
                value,
                queryValue,
                CompareOptions.IgnoreCase |
                CompareOptions.IgnoreNonSpace) >= 0;

        return Contains(card.Name, query) ||
               Contains(card.Description, query) ||
               card.AlternativeNames.Any(name => Contains(name.Name, query)) ||
               card.Genres.Any(value => Contains(value, query)) ||
               card.Categories.Any(value => Contains(value, query)) ||
               card.Distributors.Any(value => Contains(value.Name, query));
    }
}

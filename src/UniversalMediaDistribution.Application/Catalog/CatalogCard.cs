using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.Catalog;

public sealed record CatalogCard(
    Guid Id,
    MediaType MediaType,
    string SubtypeCode,
    string Name,
    string Description,
    string? ThumbnailRef,
    IReadOnlyList<string> Genres,
    IReadOnlyList<string> Categories,
    IReadOnlyList<CatalogAlternativeName> AlternativeNames,
    IReadOnlyList<CatalogContributor> Contributors,
    string? ReleaseDate = null,
    DateTimeOffset? UpdatedAt = null,
    DateTimeOffset? CreatedAt = null,
    PublicationStatus PublicationStatus = PublicationStatus.Unknown)
{
    private readonly int? _releaseYear;
    private CatalogContributor[]? _distributors;

    /// <summary>
    /// Filtered once and kept, because the catalog filter reads this for every
    /// card on every pass and it used to hand back a freshly allocated array
    /// each time.
    /// </summary>
    /// <remarks>
    /// The cache assumes <see cref="Contributors"/> never changes for a given
    /// card, which holds because the record is only ever built whole. A
    /// <c>with</c> expression that replaced <see cref="Contributors"/> would
    /// copy this field along with it and go stale; there are none today.
    /// </remarks>
    public IReadOnlyList<CatalogContributor> Distributors =>
        _distributors ??= Contributors
            .Where(static contributor => contributor.IsDistributor)
            .ToArray();

    /// <summary>
    /// The catalog's stored release year, falling back to the leading year of
    /// <see cref="ReleaseDate"/> for rows saved before the year was stored.
    /// </summary>
    public int? ReleaseYear
    {
        get => CatalogReleaseYear.Resolve(_releaseYear, ReleaseDate);
        init => _releaseYear = value;
    }
}

namespace UniversalMediaDistribution.Domain.Catalog;

public sealed class CatalogItem
{
    private readonly List<string> _genres;
    private readonly List<string> _categories;
    private readonly List<CatalogAlternativeName> _alternativeNames;
    private readonly List<CatalogContributor> _contributors;

    public CatalogItem(
        Guid id,
        MediaType mediaType,
        string subtypeCode,
        string name,
        string? description = null,
        string? thumbnailRef = null,
        string? backdropRef = null,
        PublicationStatus publicationStatus = PublicationStatus.Unknown,
        IEnumerable<string>? genres = null,
        IEnumerable<string>? categories = null,
        IEnumerable<CatalogAlternativeName>? alternativeNames = null,
        IEnumerable<CatalogContributor>? contributors = null,
        string? releaseDate = null,
        DateTimeOffset? updatedAt = null,
        int? releaseYear = null,
        DateTimeOffset? createdAt = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A catalog item requires a non-empty identifier.", nameof(id));
        }

        if (!MediaSubtypeDefinition.IsCompatible(mediaType, subtypeCode))
        {
            throw new ArgumentException($"Subtype '{subtypeCode}' is not valid for media type '{mediaType}'.", nameof(subtypeCode));
        }

        Id = id;
        MediaType = mediaType;
        SubtypeCode = subtypeCode;
        PublicationStatus = publicationStatus;
        Description = description?.Trim() ?? string.Empty;
        ThumbnailRef = NormalizeOptionalReference(thumbnailRef);
        BackdropRef = NormalizeOptionalReference(backdropRef);
        ReleaseDate = NormalizeOptionalReference(releaseDate);
        ReleaseYear = CatalogReleaseYear.Normalize(releaseYear);
        UpdatedAt = updatedAt ?? DateTimeOffset.MinValue;
        CreatedAt = createdAt ?? UpdatedAt;

        SetName(name);

        _genres = NormalizeTaxonomy(genres);
        _categories = NormalizeTaxonomy(categories);
        if (mediaType is MediaType.Audio && _categories.Count > 0)
        {
            throw new ArgumentException(
                "Categories currently apply only to movie and book catalog items.",
                nameof(categories));
        }

        _alternativeNames = alternativeNames?
            .Where(static alternativeName => alternativeName is not null)
            .DistinctBy(
                static alternativeName =>
                    $"{alternativeName.NameKey}:{alternativeName.Language}:{alternativeName.Kind}",
                StringComparer.Ordinal)
            .OrderBy(static alternativeName => alternativeName.Name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
        _contributors = contributors?
            .Where(static contributor => contributor is not null)
            .DistinctBy(
                static contributor => contributor.StableKey,
                StringComparer.Ordinal)
            .OrderBy(static contributor => contributor.Name, StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
    }

    public Guid Id { get; }

    public MediaType MediaType { get; }

    public string SubtypeCode { get; private set; }

    public string Name { get; private set; } = string.Empty;

    public string NameKey { get; private set; } = string.Empty;

    public string Description { get; private set; }

    public string? ThumbnailRef { get; private set; }

    public string? BackdropRef { get; private set; }

    public PublicationStatus PublicationStatus { get; private set; }

    public string? ReleaseDate { get; private set; }

    /// <summary>
    /// The stored original release year, or the year implied by
    /// <see cref="ReleaseDate"/> when nothing was stored. Out-of-range stored
    /// values are dropped rather than persisted.
    /// </summary>
    public int? ReleaseYear { get; private set; }

    /// <summary>
    /// The year to filter and display on, resolving the stored value against
    /// the release date.
    /// </summary>
    public int? EffectiveReleaseYear =>
        CatalogReleaseYear.Resolve(ReleaseYear, ReleaseDate);

    public DateTimeOffset UpdatedAt { get; private set; }

    /// <summary>
    /// When the item first entered this catalog — not when it was released.
    /// Falls back to <see cref="UpdatedAt"/> so a caller that does not carry
    /// the stored value reads as "added when last touched" rather than as
    /// having been added at the dawn of time.
    /// </summary>
    public DateTimeOffset CreatedAt { get; private set; }

    public IReadOnlyList<string> Genres => _genres;

    public IReadOnlyList<string> Categories => _categories;

    public IReadOnlyList<CatalogAlternativeName> AlternativeNames => _alternativeNames;

    public IReadOnlyList<CatalogContributor> Contributors => _contributors;

    public static CatalogItem Create(
        MediaType mediaType,
        string subtypeCode,
        string name,
        string? description = null,
        string? thumbnailRef = null,
        string? backdropRef = null,
        PublicationStatus publicationStatus = PublicationStatus.Unknown,
        IEnumerable<string>? genres = null,
        IEnumerable<string>? categories = null,
        IEnumerable<CatalogAlternativeName>? alternativeNames = null,
        IEnumerable<CatalogContributor>? contributors = null) =>
        new(
            Guid.CreateVersion7(),
            mediaType,
            subtypeCode,
            name,
            description,
            thumbnailRef,
            backdropRef,
            publicationStatus,
            genres,
            categories,
            alternativeNames,
            contributors);

    public void Rename(string name) => SetName(name);

    private void SetName(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        Name = name.Trim();
        NameKey = NameNormalizer.Normalize(Name);
    }

    private static string? NormalizeOptionalReference(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null : value.Trim();

    private static List<string> NormalizeTaxonomy(IEnumerable<string>? values) =>
        values?
            .Where(static value => !string.IsNullOrWhiteSpace(value))
            .Select(static value => value.Trim())
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Order(StringComparer.OrdinalIgnoreCase)
            .ToList() ?? [];
}

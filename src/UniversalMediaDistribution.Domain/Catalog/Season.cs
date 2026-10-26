namespace UniversalMediaDistribution.Domain.Catalog;

public sealed record Season
{
    public Season(
        Guid id,
        Guid catalogItemId,
        int seasonNumber,
        int sortOrder,
        string? displayName = null,
        string? thumbnailRef = null)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A season requires a non-empty identifier.", nameof(id));
        }

        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException("A season requires a parent catalog item.", nameof(catalogItemId));
        }

        if (seasonNumber < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(seasonNumber), "Season zero is reserved for specials; negative seasons are invalid.");
        }

        if (sortOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }

        Id = id;
        CatalogItemId = catalogItemId;
        SeasonNumber = seasonNumber;
        SortOrder = sortOrder;
        DisplayName = string.IsNullOrWhiteSpace(displayName) ? null : displayName.Trim();
        ThumbnailRef = string.IsNullOrWhiteSpace(thumbnailRef) ? null : thumbnailRef.Trim();
    }

    public Guid Id { get; }

    public Guid CatalogItemId { get; }

    public int SeasonNumber { get; }

    public int SortOrder { get; }

    public string? DisplayName { get; }

    public string? ThumbnailRef { get; }
}


namespace UniversalMediaDistribution.Domain.Catalog;

public sealed record ContentUnit
{
    public ContentUnit(
        Guid id,
        Guid catalogItemId,
        ContentUnitKind kind,
        string displayNumber,
        int sortOrder,
        Guid? seasonId = null,
        string? name = null,
        string? thumbnailRef = null,
        int? durationSeconds = null,
        int? pageCount = null,
        bool isSpecial = false)
    {
        if (id == Guid.Empty)
        {
            throw new ArgumentException("A content unit requires a non-empty identifier.", nameof(id));
        }

        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException("A content unit requires a parent catalog item.", nameof(catalogItemId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(displayNumber);

        if (sortOrder < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(sortOrder));
        }

        if (durationSeconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(durationSeconds));
        }

        if (pageCount is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(pageCount));
        }

        Id = id;
        CatalogItemId = catalogItemId;
        SeasonId = seasonId;
        Kind = kind;
        DisplayNumber = displayNumber.Trim();
        SortOrder = sortOrder;
        Name = string.IsNullOrWhiteSpace(name) ? null : name.Trim();
        ThumbnailRef = string.IsNullOrWhiteSpace(thumbnailRef) ? null : thumbnailRef.Trim();
        DurationSeconds = durationSeconds;
        PageCount = pageCount;
        IsSpecial = isSpecial;
    }

    public Guid Id { get; }

    public Guid CatalogItemId { get; }

    public Guid? SeasonId { get; }

    public ContentUnitKind Kind { get; }

    public string DisplayNumber { get; }

    public int SortOrder { get; }

    public string? Name { get; }

    public string? ThumbnailRef { get; }

    public int? DurationSeconds { get; }

    public int? PageCount { get; }

    public bool IsSpecial { get; }
}


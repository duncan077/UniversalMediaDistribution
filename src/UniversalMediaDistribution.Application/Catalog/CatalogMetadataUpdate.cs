using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.Catalog;

public sealed record CatalogMetadataUpdate
{
    public const int MaximumAlternativeNameCount = 256;
    public const int MaximumNameLength = 512;
    public const int MaximumDescriptionLength = 100_000;
    public const int MaximumThumbnailReferenceLength = 2_048;

    public CatalogMetadataUpdate(
        Guid catalogItemId,
        string name,
        string? description,
        string? thumbnailRef,
        IEnumerable<CatalogAlternativeName>? alternativeNames)
    {
        if (catalogItemId == Guid.Empty)
        {
            throw new ArgumentException(
                "A catalog item identifier is required.",
                nameof(catalogItemId));
        }

        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        var normalizedName = name.Trim();
        if (normalizedName.Length > MaximumNameLength)
        {
            throw new ArgumentException(
                $"A catalog title cannot exceed {MaximumNameLength} characters.",
                nameof(name));
        }

        var normalizedDescription = description?.Trim() ?? string.Empty;
        if (normalizedDescription.Length > MaximumDescriptionLength)
        {
            throw new ArgumentException(
                $"A catalog description cannot exceed {MaximumDescriptionLength:N0} characters.",
                nameof(description));
        }

        var normalizedThumbnailRef = string.IsNullOrWhiteSpace(thumbnailRef)
            ? null
            : thumbnailRef.Trim();
        if (normalizedThumbnailRef?.Length > MaximumThumbnailReferenceLength)
        {
            throw new ArgumentException(
                $"A thumbnail reference cannot exceed {MaximumThumbnailReferenceLength:N0} characters.",
                nameof(thumbnailRef));
        }

        var normalizedAlternativeNames = alternativeNames?
            .Where(static alternativeName => alternativeName is not null)
            .DistinctBy(
                static alternativeName =>
                    $"{alternativeName.NameKey}\u001f{alternativeName.Language}\u001f{alternativeName.Kind}",
                StringComparer.Ordinal)
            .ToArray() ?? [];
        if (normalizedAlternativeNames.Length > MaximumAlternativeNameCount)
        {
            throw new ArgumentException(
                $"A metadata update cannot contain more than {MaximumAlternativeNameCount} alternative names.",
                nameof(alternativeNames));
        }

        CatalogItemId = catalogItemId;
        Name = normalizedName;
        Description = normalizedDescription;
        ThumbnailRef = normalizedThumbnailRef;
        AlternativeNames = normalizedAlternativeNames;
    }

    public Guid CatalogItemId { get; }

    public string Name { get; }

    public string Description { get; }

    public string? ThumbnailRef { get; }

    public IReadOnlyList<CatalogAlternativeName> AlternativeNames { get; }
}

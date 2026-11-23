using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

public static class CatalogSearchMetadataFilters
{
    public static string? NormalizeLabel(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return null;
        value = value.Trim();
        if (value.Length > 200) throw new ArgumentException("A catalog filter cannot exceed 200 characters.");
        return value;
    }

    public static bool Matches(CatalogCard card, string? category, PublicationStatus? status, string? providerName) =>
        (category is null || card.Categories.Concat(card.Genres).Contains(category, StringComparer.OrdinalIgnoreCase)) &&
        (status is null || PublicationStatusApplicability.AppliesTo(card.MediaType, card.SubtypeCode) && card.PublicationStatus == status) &&
        (providerName is null || card.Distributors.Any(d => string.Equals(d.Name, providerName, StringComparison.OrdinalIgnoreCase)));

    public static bool Matches(CatalogTransferItem item, ApiContentSearchRequest request)
    {
        var media = CatalogSubtypeResolver.ParseMediaType(item.MediaType);
        var subtype = CatalogSubtypeResolver.ResolveCode(media, item.Subtype);
        return (request.MediaType is null || request.MediaType == media) &&
            MediaSubtypeGroups.Matches(request.SubtypeGroup, media, subtype) &&
            (request.Category is null || item.Categories.Concat(item.Genres).Contains(request.Category, StringComparer.OrdinalIgnoreCase)) &&
            (request.Status is null || PublicationStatusApplicability.AppliesTo(media, subtype) && CatalogTransferMapper.ParsePublicationStatus(item.PublicationStatus) == request.Status) &&
            (request.ProviderName is null || item.Contributors.Any(c => string.Equals(c.Name, request.ProviderName, StringComparison.OrdinalIgnoreCase) &&
                c.Credits.Any(credit => string.Equals(credit.Role, "distributor", StringComparison.OrdinalIgnoreCase))));
    }
}

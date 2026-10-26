using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata.Tests;

internal static class MetadataTestData
{
    public static CatalogItemDetails Details(
        MediaType mediaType,
        string subtypeCode,
        string name,
        string? releaseDate = null,
        string? originalName = null,
        IEnumerable<CatalogAlternativeName>? alternativeNames = null)
    {
        var item = CatalogItem.Create(
            mediaType,
            subtypeCode,
            name,
            description: "Local description",
            thumbnailRef: "local-cover.webp",
            alternativeNames: alternativeNames);
        return new CatalogItemDetails(
            item,
            new CatalogContentIdentity(item.Id, $"item:{item.Id:N}"),
            originalName,
            releaseDate,
            "en",
            null,
            null,
            null,
            null,
            [],
            [],
            [],
            []);
    }
}

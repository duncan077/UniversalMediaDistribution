using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>Recognizes the historic synthetic catalog credit, not real upstream distributors.</summary>
public static class CatalogMetadataSource
{
    public static bool IsSyntheticDistributor(CatalogContributor contributor) =>
        contributor.Kind.Equals("organization", StringComparison.OrdinalIgnoreCase) &&
        contributor.Credits.Any(credit => credit.Role.Equals("Distributor", StringComparison.OrdinalIgnoreCase)) &&
        (contributor.Name.Equals("UMD catalog API", StringComparison.OrdinalIgnoreCase) ||
         contributor.Url?.AbsolutePath.TrimEnd('/').Equals("/api/v1/catalog", StringComparison.OrdinalIgnoreCase) == true);
}

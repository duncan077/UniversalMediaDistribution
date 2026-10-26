namespace UniversalMediaDistribution.Application.Catalog;

public sealed record CatalogOverview(
    int TotalCount,
    int MovieCount,
    int BookCount,
    int AudioCount,
    IReadOnlyList<CatalogCard> Items);


using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.Catalog;

public interface ICatalogRepository
{
    ValueTask<IReadOnlyList<CatalogItem>> ListAsync(
        MediaType? mediaType = null,
        CancellationToken cancellationToken = default);

    ValueTask<CatalogItemDetails?> GetDetailsAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> DeleteAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default);

    ValueTask<CatalogItemDetails> UpdateMetadataAsync(
        CatalogMetadataUpdate update,
        CancellationToken cancellationToken = default);
}

public sealed record CatalogCounts(
    int Total,
    int Movies,
    int Books,
    int Audio);

/// <summary>Removes a chapter or episode from the local catalog, preserving downloaded files.</summary>
public interface ICatalogUnitDeletionRepository
{
    ValueTask<bool> DeleteUnitAsync(CatalogContentIdentity identity, CancellationToken cancellationToken = default);
}

public interface ICatalogStatisticsRepository
{
    ValueTask<CatalogCounts> GetCountsAsync(
        CancellationToken cancellationToken = default);
}

/// <summary>
/// The newest chapter or episode a title carries.
/// </summary>
public sealed record CatalogLatestUnit(
    ContentUnitKind Kind,
    string DisplayNumber,
    int SortOrder);

/// <summary>
/// Where a title's newest unit comes from, so a card can name the latest
/// chapter without loading every unit of every title to find it.
/// <para>
/// Optional in the same way statistics are: a store that cannot answer simply
/// does not implement it, and the card falls back to showing nothing.
/// </para>
/// </summary>
public interface ICatalogUnitSummaryRepository
{
    ValueTask<IReadOnlyDictionary<Guid, CatalogLatestUnit>>
        GetLatestUnitsAsync(CancellationToken cancellationToken = default);
}

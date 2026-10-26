using UniversalMediaDistribution.Application.Catalog;

namespace UniversalMediaDistribution.Catalog.Transfer;

/// <summary>Optional targeted export for stores that can avoid reading unrelated media.</summary>
public interface ICatalogItemTransferStore
{
    ValueTask<CatalogTransferDocument> ExportCatalogItemAsync(
        Guid catalogItemId, CancellationToken cancellationToken = default);

    ValueTask<CatalogTransferDocument> ExportMatchingCatalogItemsAsync(
        IReadOnlyList<CatalogTransferItem> candidates, CancellationToken cancellationToken = default);
}

public interface ICatalogTransferStore : ICatalogRepository
{
    ValueTask<CatalogTransferDocument> ExportCatalogAsync(
        CancellationToken cancellationToken = default);

    ValueTask<CatalogImportResult> MergeCatalogAsync(
        CatalogTransferDocument document,
        string? sourceName = null,
        string? documentHash = null,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Imports a title explicitly opened by the user. Stores that track
    /// deletions restore this title with a new local synchronization version.
    /// Automatic refreshes must continue to use MergeCatalogAsync.
    /// </summary>
    ValueTask<CatalogImportResult> ImportSelectedItemAsync(
        CatalogTransferItem item,
        string? sourceName = null,
        CancellationToken cancellationToken = default) =>
        MergeCatalogAsync(
            new CatalogTransferDocument { Items = [item with { Deleted = false }] },
            sourceName, documentHash: null, cancellationToken);
}

using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Application.Catalog;

public sealed class CatalogBrowseService(ICatalogRepository repository)
{
    public bool CanDeleteUnits => repository is ICatalogUnitDeletionRepository;

    public ValueTask<bool> DeleteUnitAsync(CatalogContentIdentity identity,
        CancellationToken cancellationToken = default) =>
        repository is ICatalogUnitDeletionRepository units
            ? units.DeleteUnitAsync(identity, cancellationToken)
            : ValueTask.FromException<bool>(new NotSupportedException("This catalog cannot delete individual entries."));

    public ValueTask<CatalogItemDetails?> GetDetailsAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default) =>
        repository.GetDetailsAsync(catalogItemId, cancellationToken);

    public ValueTask<bool> DeleteAsync(
        Guid catalogItemId,
        CancellationToken cancellationToken = default) =>
        repository.DeleteAsync(catalogItemId, cancellationToken);

    /// <summary>
    /// The newest unit of every title, for cards that name the latest chapter.
    /// Empty when the store cannot answer, which leaves the cards silent
    /// rather than wrong.
    /// </summary>
    public ValueTask<IReadOnlyDictionary<Guid, CatalogLatestUnit>>
        GetLatestUnitsAsync(CancellationToken cancellationToken = default) =>
        repository is ICatalogUnitSummaryRepository units
            ? units.GetLatestUnitsAsync(cancellationToken)
            : ValueTask.FromResult<
                IReadOnlyDictionary<Guid, CatalogLatestUnit>>(
                new Dictionary<Guid, CatalogLatestUnit>());

    public async ValueTask<CatalogOverview> GetOverviewAsync(
        MediaType? filter = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<CatalogItem> visibleItems;
        CatalogCounts counts;
        if (filter is not null &&
            repository is ICatalogStatisticsRepository statisticsRepository)
        {
            var itemsTask = repository.ListAsync(filter, cancellationToken).AsTask();
            var countsTask = statisticsRepository
                .GetCountsAsync(cancellationToken)
                .AsTask();
            await Task.WhenAll(itemsTask, countsTask);
            visibleItems = itemsTask.Result;
            counts = countsTask.Result;
        }
        else
        {
            var allItems = await repository.ListAsync(
                cancellationToken: cancellationToken);
            visibleItems = filter is null
                ? allItems
                : allItems.Where(item => item.MediaType == filter).ToArray();
            counts = new CatalogCounts(
                allItems.Count,
                allItems.Count(static item => item.MediaType == MediaType.Movie),
                allItems.Count(static item => item.MediaType == MediaType.Book),
                allItems.Count(static item => item.MediaType == MediaType.Audio));
        }

        var cards = visibleItems
            .OrderBy(static item => item.Name, StringComparer.CurrentCultureIgnoreCase)
            .Select(static item =>
            {
                return new CatalogCard(
                    item.Id,
                    item.MediaType,
                    item.SubtypeCode,
                    item.Name,
                    item.Description,
                    item.ThumbnailRef,
                    item.Genres,
                    item.Categories,
                    item.AlternativeNames,
                    item.Contributors,
                    item.ReleaseDate,
                    item.UpdatedAt,
                    item.CreatedAt,
                    item.PublicationStatus)
                {
                    ReleaseYear = item.ReleaseYear
                };
            })
            .ToArray();

        return new CatalogOverview(
            counts.Total,
            counts.Movies,
            counts.Books,
            counts.Audio,
            cards);
    }
}

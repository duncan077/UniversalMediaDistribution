using UniversalMediaDistribution.Application.UserState;

namespace UniversalMediaDistribution.UI.ViewModels;

internal static class CatalogProgressIndex
{
    internal static Dictionary<Guid, CatalogProgressSummary> CreateRecent(
        IEnumerable<CatalogProgressSummary> summaries) =>
        CreateLatest(
            summaries,
            static _ => true);

    internal static Dictionary<Guid, CatalogProgressSummary> CreateResumable(
        IEnumerable<CatalogProgressSummary> summaries) =>
        CreateLatest(
            summaries,
            static summary =>
                summary.Progress.IsCompleted ||
                summary.Progress.Position > 0);

    private static Dictionary<Guid, CatalogProgressSummary> CreateLatest(
        IEnumerable<CatalogProgressSummary> summaries,
        Func<CatalogProgressSummary, bool> include)
    {
        ArgumentNullException.ThrowIfNull(summaries);
        ArgumentNullException.ThrowIfNull(include);
        return summaries
            .Where(include)
            .GroupBy(static summary => summary.CatalogItemId)
            .ToDictionary(
                static group => group.Key,
                static group => CarryFurthestUnit(
                    group
                        .OrderByDescending(
                            static summary =>
                                summary.Progress.UpdatedAt)
                        .First(),
                    group));
    }

    /// <summary>
    /// Puts what is waiting past the furthest unit onto the row the card
    /// shows.
    /// <para>
    /// The two are not the same row. The card shows whichever was touched most
    /// recently, and that can be progress on the title itself — which has no
    /// unit, so nothing can be after it and the count is zero. Reading the
    /// count from there reports a title with a hundred unread chapters as
    /// having none. The furthest unit is the one with the fewest left after
    /// it, counting only rows that name a unit at all.
    /// </para>
    /// </summary>
    private static CatalogProgressSummary CarryFurthestUnit(
        CatalogProgressSummary latest,
        IEnumerable<CatalogProgressSummary> group)
    {
        CatalogProgressSummary? furthest = null;
        foreach (var summary in group)
        {
            if (summary.Content.UnitKind is null)
            {
                continue;
            }

            if (furthest is null ||
                summary.LaterUnitCount < furthest.LaterUnitCount)
            {
                furthest = summary;
            }
        }

        // Copy only when the answer actually changes, so the common case hands
        // back the very row it was given rather than an equal-but-different
        // one.
        if (furthest is null ||
            (furthest.LaterUnitCount == latest.LaterUnitCount &&
             string.Equals(
                 furthest.LatestUnitNumber,
                 latest.LatestUnitNumber,
                 StringComparison.Ordinal)))
        {
            return latest;
        }

        return latest with
        {
            LaterUnitCount = furthest.LaterUnitCount,
            LatestUnitNumber = furthest.LatestUnitNumber
        };
    }
}

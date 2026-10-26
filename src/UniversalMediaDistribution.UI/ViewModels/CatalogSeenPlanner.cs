using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// Works out which chapters or episodes a "seen" action covers. Marking one
/// implies the ones before it, whether the user pressed the button or simply
/// opened the chapter.
/// </summary>
internal static class CatalogSeenPlanner
{
    /// <summary>
    /// Units in reading/watching order: loose units first, then each season.
    /// </summary>
    public static IReadOnlyList<CatalogContentUnitDetails> OrderUnits(
        CatalogItemDetails details)
    {
        ArgumentNullException.ThrowIfNull(details);

        // The same rule CatalogDetailsMapper displays them by, because this
        // list decides what "everything before this one" means when marking
        // seen. Ordering the two differently would mark the wrong chapters.
        var ordered = new List<CatalogContentUnitDetails>(
            details.Units
                .OrderBy(static unit =>
                    CatalogDetailsMapper.UnitNumber(unit.DisplayNumber))
                .ThenBy(static unit => unit.SortOrder));
        foreach (var season in details.Seasons
            .OrderBy(static season => season.SortOrder)
            .ThenBy(static season => season.Number))
        {
            ordered.AddRange(season.Units
                .OrderBy(static unit =>
                    CatalogDetailsMapper.UnitNumber(unit.DisplayNumber))
                .ThenBy(static unit => unit.SortOrder));
        }

        return ordered;
    }

    /// <summary>
    /// The target unit plus every earlier unit that has not been seen yet.
    /// Specials are left alone unless the target is the special itself, so
    /// watching episode 5 never marks an OVA as seen. Returns an empty list
    /// when there is nothing left to mark.
    /// </summary>
    public static IReadOnlyList<CatalogContentIdentity> ResolveUnseenUpTo(
        CatalogItemDetails? details,
        CatalogContentIdentity target,
        Func<CatalogContentIdentity, bool> isSeen)
    {
        ArgumentNullException.ThrowIfNull(target);
        ArgumentNullException.ThrowIfNull(isSeen);
        if (!target.IsUnit)
        {
            return [];
        }

        if (details is null ||
            details.Item.Id != target.CatalogItemId)
        {
            // Without the item's unit list there is no order to walk back
            // through, so only the target itself can be marked.
            return isSeen(target) ? [] : [target];
        }

        var ordered = OrderUnits(details);
        var index = IndexOf(ordered, target);
        if (index < 0)
        {
            return isSeen(target) ? [] : [target];
        }

        return ordered
            .Take(index + 1)
            .Where(unit =>
                !isSeen(unit.Identity) &&
                (!unit.IsSpecial ||
                 IsSameUnit(unit.Identity, target)))
            .Select(static unit => unit.Identity)
            .ToArray();
    }

    private static int IndexOf(
        IReadOnlyList<CatalogContentUnitDetails> ordered,
        CatalogContentIdentity target)
    {
        for (var index = 0; index < ordered.Count; index++)
        {
            if (IsSameUnit(ordered[index].Identity, target))
            {
                return index;
            }
        }

        return -1;
    }

    private static bool IsSameUnit(
        CatalogContentIdentity left,
        CatalogContentIdentity right) =>
        string.Equals(
            left.StableKey,
            right.StableKey,
            StringComparison.Ordinal);
}

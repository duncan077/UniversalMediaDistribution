namespace UniversalMediaDistribution.UI.ViewModels;

public static class CatalogPagination
{
    public const int DefaultPageSize = 25;

    public static int GetPageCount(
        int itemCount,
        int pageSize = DefaultPageSize)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(itemCount);
        ArgumentOutOfRangeException.ThrowIfLessThan(pageSize, 1);
        return Math.Max(1, (itemCount + pageSize - 1) / pageSize);
    }

    public static int ClampPage(
        int page,
        int itemCount,
        int pageSize = DefaultPageSize) =>
        Math.Clamp(page, 1, GetPageCount(itemCount, pageSize));

    public static IReadOnlyList<T> GetPage<T>(
        IReadOnlyList<T> items,
        int page,
        int pageSize = DefaultPageSize)
    {
        ArgumentNullException.ThrowIfNull(items);
        var clampedPage = ClampPage(page, items.Count, pageSize);
        var offset = (clampedPage - 1) * pageSize;
        return items
            .Skip(offset)
            .Take(pageSize)
            .ToArray();
    }
}

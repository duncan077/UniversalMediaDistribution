namespace UniversalMediaDistribution.Application.Catalog;

/// <summary>
/// Taxonomy labels that identify adult content across the different API
/// providers. Some providers expose these labels as categories and others as
/// genres, so callers pass both collections. Matching is case-insensitive and
/// trims each value first.
/// </summary>
public static class AdultContentCategoryFilter
{
    private static readonly string[] BlockedCategories =
    [
        "adult",
        "adulto",
        "porn",
        "porno",
        "+18",
        "18+"
    ];

    public static bool Matches(IEnumerable<string>? categories)
    {
        if (categories is null)
        {
            return false;
        }

        return categories.Any(category =>
            BlockedCategories.Any(blocked =>
                string.Equals(
                    category?.Trim(),
                    blocked,
                    StringComparison.OrdinalIgnoreCase)));
    }

    public static bool Matches(
        IEnumerable<string>? categories,
        IEnumerable<string>? genres) =>
        Matches(categories) || Matches(genres);
}

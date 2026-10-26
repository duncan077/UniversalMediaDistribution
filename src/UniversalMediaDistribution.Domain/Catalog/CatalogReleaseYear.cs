namespace UniversalMediaDistribution.Domain.Catalog;

/// <summary>
/// Shared handling of a catalog item's original release year, so the stored
/// column, the transfer format, and the derived fallback all agree on what a
/// year is.
/// </summary>
public static class CatalogReleaseYear
{
    /// <summary>
    /// Sanity bounds for a year the catalog stores. Deliberately the same range
    /// the search contract accepts, so a year that can be saved can also be
    /// searched for.
    /// </summary>
    public const int MinimumYear = 1800;

    public const int MaximumYear = 2200;

    public static bool IsPlausible(int year) =>
        year is >= MinimumYear and <= MaximumYear;

    /// <summary>
    /// Drops a stored year that falls outside the plausible range instead of
    /// letting it reach the catalog.
    /// </summary>
    public static int? Normalize(int? year) =>
        year is { } value && IsPlausible(value) ? value : null;

    /// <summary>
    /// Reads the leading year of a release date. Used only as a fallback for
    /// rows written before the year was stored on its own, so the accepted
    /// range stays as permissive as it was then.
    /// </summary>
    public static int? FromReleaseDate(string? releaseDate)
    {
        if (string.IsNullOrWhiteSpace(releaseDate))
        {
            return null;
        }

        var span = releaseDate.AsSpan().Trim();
        return span.Length >= 4 &&
            int.TryParse(span[..4], out var year) &&
            year is >= 1000 and <= 9999
                ? year
                : null;
    }

    /// <summary>
    /// The stored year when there is one, otherwise the year implied by the
    /// release date. Always within the plausible range, so what is filtered on
    /// is exactly what can be stored.
    /// </summary>
    public static int? Resolve(int? storedYear, string? releaseDate) =>
        Normalize(storedYear) ?? Normalize(FromReleaseDate(releaseDate));
}

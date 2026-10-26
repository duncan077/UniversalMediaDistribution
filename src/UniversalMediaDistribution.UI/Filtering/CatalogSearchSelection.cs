using System.Globalization;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Filtering;

/// <summary>
/// What the user has actually asked for, lifted out of the view model so the
/// rules can be tested without one.
/// <para>
/// The distinction that matters: genre and year are <em>criteria</em> that send
/// a new search to the sources, while the category and provider pickers only
/// narrow what already came back. Anything here is the former.
/// </para>
/// </summary>
public sealed record CatalogSearchSelection(
    string? Query = null,
    MediaType? MediaType = null,
    MediaSubtypeGroup? SubtypeGroup = null,
    ApiContentGenre? Genre = null,
    int? ReleaseYear = null)
{
    /// <summary>
    /// Whether there is anything to search for. A genre or a year is enough on
    /// its own, which is what makes a filter-only search possible.
    /// </summary>
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Query) ||
        Genre is not null ||
        ReleaseYear is not null;

    /// <summary>
    /// True when filters alone drive the search, so it browses rather than
    /// looks a title up by name.
    /// </summary>
    public bool IsFilterOnly =>
        string.IsNullOrWhiteSpace(Query) && HasCriteria;

    public ContentSourceSearchCriteria ToCriteria() =>
        new(Query, MediaType, SubtypeGroup, Genre, ReleaseYear);

    /// <summary>
    /// Names the search for the results header. A filter-only search has no
    /// text to show, so it is described by the filters that produced it.
    /// </summary>
    public string Describe()
    {
        var parts = new List<string>(3);
        if (!string.IsNullOrWhiteSpace(Query))
        {
            parts.Add(Query.Trim());
        }

        if (Genre is { } genre)
        {
            parts.Add(new CatalogGenreFilterViewModel(genre).Label);
        }

        if (ReleaseYear is { } year)
        {
            parts.Add(year.ToString(CultureInfo.InvariantCulture));
        }

        return string.Join(" · ", parts);
    }
}

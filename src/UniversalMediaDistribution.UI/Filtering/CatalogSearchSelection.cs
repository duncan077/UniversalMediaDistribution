using System.Globalization;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Filtering;

/// <summary>
/// What the user has actually asked for, lifted out of the view model so the
/// rules can be tested without one.
/// <para>
/// Public metadata filters send a new search. LocalOnly (favorites or the
/// explicit Local provider) changes its scope, but cannot start one by itself.
/// </para>
/// </summary>
public sealed record CatalogSearchSelection(
    string? Query = null,
    MediaType? MediaType = null,
    MediaSubtypeGroup? SubtypeGroup = null,
    ApiContentGenre? Genre = null,
    int? ReleaseYear = null,
    string? Category = null,
    PublicationStatus? Status = null,
    string? ProviderName = null,
    bool BrowseOnline = false,
    bool LocalOnly = false)
{
    /// <summary>
    /// Whether text, a public metadata filter, or the Online choice describes
    /// a query. Favorites/local scope alone do not start an online request.
    /// </summary>
    public bool HasCriteria =>
        !string.IsNullOrWhiteSpace(Query) ||
        Genre is not null ||
        ReleaseYear is not null || Category is not null || Status is not null || ProviderName is not null || BrowseOnline;

    /// <summary>
    /// True when filters alone drive the search, so it browses rather than
    /// looks a title up by name.
    /// </summary>
    public bool IsFilterOnly =>
        string.IsNullOrWhiteSpace(Query) && HasCriteria;

    public ContentSourceSearchCriteria ToCriteria() =>
        new(Query, MediaType, SubtypeGroup, Genre, ReleaseYear, Category, Status, ProviderName, BrowseOnline, LocalOnly);

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
        if (Category is not null) parts.Add(Category);
        if (Status is { } status) parts.Add(new CatalogStatusFilterViewModel(status).Label);
        if (ProviderName is not null) parts.Add(ProviderName);
        else if (BrowseOnline) parts.Add(ProviderFilterViewModel.Online().Label);

        return string.Join(" · ", parts);
    }
}

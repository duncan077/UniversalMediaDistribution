using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Filtering;

/// <summary>
/// Builds the contents of the catalog filter pickers.
/// <para>
/// Genre and year are search criteria, so their choices are fixed and do not
/// depend on the current results — offering only what came back would make it
/// impossible to search for anything else. Category is the exception: it
/// narrows what is already on screen, so it is still derived from the cards.
/// </para>
/// </summary>
public static class CatalogFilterChoices
{
    /// <summary>
    /// The oldest year the picker offers outright. Anything earlier is still
    /// reachable when the local library actually holds it.
    /// </summary>
    public const int EarliestOfferedYear = 1950;

    public static IReadOnlyList<CatalogGenreFilterViewModel> BuildGenres()
    {
        var choices = new List<CatalogGenreFilterViewModel>(
            ApiContentGenre.All.Count + 1)
        {
            new(genre: null)
        };
        foreach (var genre in ApiContentGenre.All)
        {
            choices.Add(new CatalogGenreFilterViewModel(genre));
        }

        return choices;
    }

    /// <summary>
    /// A fixed descending range, widened by whatever the local library holds
    /// outside it and by any year already picked, so a selection never
    /// disappears from the list that contains it.
    /// </summary>
    public static IReadOnlyList<CatalogYearFilterViewModel> BuildYears(
        IEnumerable<int?> localYears,
        int newestOfferedYear,
        int? pinnedYear = null)
    {
        ArgumentNullException.ThrowIfNull(localYears);
        var years = new SortedSet<int>(
            Enumerable.Range(
                EarliestOfferedYear,
                Math.Max(0, newestOfferedYear - EarliestOfferedYear + 1)));
        foreach (var year in localYears)
        {
            if (year is { } value)
            {
                years.Add(value);
            }
        }

        if (pinnedYear is { } pinned)
        {
            years.Add(pinned);
        }

        var choices = new List<CatalogYearFilterViewModel>(years.Count + 1)
        {
            new(year: null)
        };
        foreach (var year in years.Reverse())
        {
            choices.Add(new CatalogYearFilterViewModel(year));
        }

        return choices;
    }

    public static IReadOnlyList<CatalogStatusFilterViewModel> BuildStatuses()
    {
        var choices = new List<CatalogStatusFilterViewModel>
        {
            new(status: null)
        };
        foreach (var status in new[]
        {
            PublicationStatus.Ongoing,
            PublicationStatus.Completed,
            PublicationStatus.Paused,
            PublicationStatus.Cancelled
        })
        {
            choices.Add(new CatalogStatusFilterViewModel(status));
        }

        return choices;
    }

    /// <summary>
    /// The distinct categories and genres carried by the cards on screen.
    /// Unlike the genre picker this is deliberately result-derived: it exists
    /// to narrow a result set, not to ask a new question.
    /// </summary>
    public static IReadOnlyList<CatalogCategoryFilterViewModel>
        BuildCategories(
            IEnumerable<CatalogCard> cards,
            CultureInfo culture)
    {
        ArgumentNullException.ThrowIfNull(cards);
        ArgumentNullException.ThrowIfNull(culture);
        var choices = new List<CatalogCategoryFilterViewModel>
        {
            new(category: null)
        };
        foreach (var category in cards
                     .SelectMany(static card =>
                         card.Categories.Concat(card.Genres))
                     .Where(static value => !string.IsNullOrWhiteSpace(value))
                     .Distinct(StringComparer.OrdinalIgnoreCase)
                     .Order(StringComparer.Create(culture, ignoreCase: true)))
        {
            choices.Add(new CatalogCategoryFilterViewModel(category));
        }

        return choices;
    }
}

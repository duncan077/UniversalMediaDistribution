using System.Globalization;
using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Localization;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// A genre the user can search for. Unlike the category filter, the choices do
/// not come from whatever happened to be on screen: they are the shared genre
/// keys, so a genre can be asked for before any result mentions it.
/// </summary>
public sealed class CatalogGenreFilterViewModel
{
    public CatalogGenreFilterViewModel(ApiContentGenre? genre)
    {
        Genre = genre;
        Label = genre is null
            ? UiTextCatalog.Get("AllGenres")
            : ResolveLabel(genre);
    }

    public ApiContentGenre? Genre { get; }

    public string Label { get; }

    private static string ResolveLabel(ApiContentGenre genre)
    {
        // UiTextCatalog echoes the key back when it has no entry, which is the
        // signal to fall back rather than show "Genre_slice-of-life" to a user.
        var lookupKey = $"Genre_{genre.Key}";
        var translated = UiTextCatalog.Get(lookupKey);
        return string.Equals(translated, lookupKey, StringComparison.Ordinal)
            ? Prettify(genre.Key)
            : translated;
    }

    private static string Prettify(string key)
    {
        var builder = new StringBuilder(key.Length);
        var startOfWord = true;
        foreach (var character in key)
        {
            if (character == '-')
            {
                builder.Append(' ');
                startOfWord = true;
                continue;
            }

            builder.Append(startOfWord
                ? char.ToUpper(character, CultureInfo.InvariantCulture)
                : character);
            startOfWord = false;
        }

        return builder.ToString();
    }
}

public sealed class CatalogCategoryFilterViewModel
{
    public CatalogCategoryFilterViewModel(string? category)
    {
        Category = string.IsNullOrWhiteSpace(category)
            ? null
            : category.Trim();
        Label = Category ?? UiTextCatalog.Get("AllCategories");
    }

    public string? Category { get; }

    public string Label { get; }
}

public sealed class CatalogYearFilterViewModel
{
    public CatalogYearFilterViewModel(int? year)
    {
        Year = year;
        Label = year?.ToString(
            System.Globalization.CultureInfo.InvariantCulture) ??
            UiTextCatalog.Get("AllYears");
    }

    public int? Year { get; }

    public string Label { get; }
}

public sealed class CatalogStatusFilterViewModel
{
    public CatalogStatusFilterViewModel(PublicationStatus? status)
    {
        Status = status;
        Label = status is null
            ? UiTextCatalog.Get("AllStatuses")
            : UiTextCatalog.PublicationStatusName(status.Value);
    }

    public PublicationStatus? Status { get; }

    public string Label { get; }
}

using System.Globalization;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogGenreFilterViewModelTests
{
    [Fact]
    public void EveryOfferedGenreIsTranslatedInBothLanguages()
    {
        // The picker offers all of ApiContentGenre.All, so adding a genre
        // without labels would surface a raw key to the user.
        var english = CultureInfo.GetCultureInfo("en-US");
        var spanish = CultureInfo.GetCultureInfo("es-AR");
        foreach (var genre in ApiContentGenre.All)
        {
            var key = $"Genre_{genre.Key}";
            Assert.Contains(key, UiTextCatalog.ResourceKeys);
            Assert.NotEqual(key, UiTextCatalog.Get(key, english));
            Assert.NotEqual(key, UiTextCatalog.Get(key, spanish));
        }
    }

    [Fact]
    public void SpanishLabelsUseTheWordingTheProvidersPublish()
    {
        var spanish = CultureInfo.GetCultureInfo("es-AR");

        Assert.Equal("Acción", UiTextCatalog.Get("Genre_action", spanish));
        Assert.Equal(
            "Ciencia ficción",
            UiTextCatalog.Get("Genre_science-fiction", spanish));
        Assert.Equal(
            "Cosas de la vida",
            UiTextCatalog.Get("Genre_slice-of-life", spanish));
    }

    [Fact]
    public void KnownGenreUsesTheTranslatedLabel()
    {
        var filter = new CatalogGenreFilterViewModel(ApiContentGenre.Action);

        Assert.Equal(UiTextCatalog.Get("Genre_action"), filter.Label);
        Assert.Same(ApiContentGenre.Action, filter.Genre);
    }

    [Fact]
    public void TheAllEntryCarriesNoGenre()
    {
        var filter = new CatalogGenreFilterViewModel(genre: null);

        Assert.Null(filter.Genre);
        Assert.Equal(UiTextCatalog.Get("AllGenres"), filter.Label);
    }

    [Fact]
    public void AnUntranslatedGenreFallsBackToAReadableLabel()
    {
        // A provider map may carry a key before the shared list and its labels
        // catch up. Showing "Genre_mahou-shoujo" to a user would be worse than
        // showing a prettified key.
        var filter = new CatalogGenreFilterViewModel(
            ApiContentGenre.Parse("mahou-shoujo"));

        Assert.Equal("Mahou Shoujo", filter.Label);
    }
}

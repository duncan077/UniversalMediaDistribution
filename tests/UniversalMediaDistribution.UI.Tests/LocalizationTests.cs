using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Composition;
using UniversalMediaDistribution.UI.Localization;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class LocalizationTests
{
    [Fact]
    public void EnglishAndSpanishCatalogsHaveTheSameKeys()
    {
        Assert.True(UiTextCatalog.HasCompleteTranslations);
        Assert.True(UiTextCatalog.ResourceKeys.Count > 200);
    }

    [Theory]
    [InlineData("en-US", "CatalogTitle", "Local catalog")]
    [InlineData("es-AR", "CatalogTitle", "Catálogo local")]
    [InlineData("es-ES", "PlayerClose", "Cerrar reproductor")]
    [InlineData("fr-FR", "CatalogTitle", "Local catalog")]
    public void TextUsesSpanishOrFallsBackToEnglish(
        string cultureName,
        string key,
        string expected)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        Assert.Equal(expected, UiTextCatalog.Get(key, culture));
    }

    [Fact]
    public void FormatUsesTheRequestedCultureAndTranslation()
    {
        var value = UiTextCatalog.Format(
            CultureInfo.GetCultureInfo("es-AR"),
            "CatalogImported",
            2,
            3);

        Assert.Equal("Catálogo importado: 2 creados, 3 actualizados.", value);
    }

    [Fact]
    public void LocalizedTitlePrefersAnExactRegionalLanguage()
    {
        CatalogAlternativeName[] alternativeNames =
        [
            new("Título neutro", "es", "translation"),
            new("Título argentino", "es-AR", "translation"),
            new("English title", "en", "translation")
        ];

        var actual = CatalogDisplayNameSelector.Select(
            "Original title",
            alternativeNames,
            CultureInfo.GetCultureInfo("es-AR"));

        Assert.Equal("Título argentino", actual);
    }

    [Fact]
    public void LocalizedTitleFallsBackToTheNeutralLanguage()
    {
        CatalogAlternativeName[] alternativeNames =
        [
            new("Título en español", "es", "translation"),
            new("English title", "en", "translation")
        ];

        var actual = CatalogDisplayNameSelector.Select(
            "Original title",
            alternativeNames,
            CultureInfo.GetCultureInfo("es-MX"));

        Assert.Equal("Título en español", actual);
    }

    [Fact]
    public void LocalizedTitlePrefersATranslationWithinTheSameLanguage()
    {
        CatalogAlternativeName[] alternativeNames =
        [
            new("Nombre original", "es", "original"),
            new("Nombre traducido", "es", "translation")
        ];

        var actual = CatalogDisplayNameSelector.Select(
            "Original title",
            alternativeNames,
            CultureInfo.GetCultureInfo("es"));

        Assert.Equal("Nombre traducido", actual);
    }

    [Fact]
    public void LocalizedTitleFallsBackToPrimaryName()
    {
        CatalogAlternativeName[] alternativeNames =
        [
            new("日本語", "ja", "original")
        ];

        var actual = CatalogDisplayNameSelector.Select(
            "Primary title",
            alternativeNames,
            CultureInfo.GetCultureInfo("es-AR"));

        Assert.Equal("Primary title", actual);
    }

    [Fact]
    public void AndroidTvProfileEnablesTelevisionLayout()
    {
        Assert.True(RuntimeProfile.AndroidTv.IsTelevision);
        Assert.False(RuntimeProfile.Android.IsTelevision);
    }

    [Fact]
    public void HandheldProfilesUseTheTouchShell()
    {
        Assert.True(RuntimeProfile.Android.IsTouchPrimary);
        Assert.True(RuntimeProfile.Ios.IsTouchPrimary);
        Assert.False(RuntimeProfile.AndroidTv.IsTouchPrimary);
        Assert.False(RuntimeProfile.Desktop.IsTouchPrimary);
    }

    [Theory]
    [InlineData("tv", true, false)]
    [InlineData("Television", true, false)]
    [InlineData("touch", false, true)]
    [InlineData("mobile", false, true)]
    [InlineData("desktop", false, false)]
    public void ShellOverrideSwitchesTheLayoutProfile(
        string shell,
        bool expectsTelevision,
        bool expectsTouch)
    {
        var profile = RuntimeProfile.Desktop.WithShellOverride(shell);

        Assert.Equal(expectsTelevision, profile.IsTelevision);
        Assert.Equal(expectsTouch, profile.IsTouchPrimary);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("nonsense")]
    public void UnknownShellOverrideKeepsThePlatformProfile(string? shell)
    {
        var profile = RuntimeProfile.Android.WithShellOverride(shell);

        Assert.False(profile.IsTelevision);
        Assert.True(profile.IsTouchPrimary);
    }

    [Fact]
    public void DistributorFilterShowsOnlyThatDistributorAndUsesLocalizedOrder()
    {
        var selected = CreateDistributor("Selected Studio");
        CatalogCard[] cards =
        [
            CreateCard("Zulu", "Zorro", selected),
            CreateCard("Alpha", "Abeja", selected),
            CreateCard("Beta", "Barco", CreateDistributor("Other Studio"))
        ];

        var ordered = CatalogCardOrdering.FilterAndOrder(
            cards,
            selected.StableKey,
            CultureInfo.GetCultureInfo("es-AR"));

        Assert.Equal(
            ["Alpha", "Zulu"],
            ordered.Select(card => card.Name));
    }

    [Fact]
    public void DistributorFilterDoesNotTreatGeneralContributorsAsDistributors()
    {
        var author = new CatalogContributor(
            "Example Author",
            null,
            null,
            null,
            [new CatalogContributorCredit("Writer")]);
        var card = CreateCard("Example", "Ejemplo", author);

        Assert.Empty(card.Distributors);
        Assert.Empty(CatalogCardOrdering.FilterAndOrder(
            [card],
            author.StableKey,
            CultureInfo.GetCultureInfo("es-AR")));
    }

    [Fact]
    public void CatalogOrdersMostRecentlyUpdatedThenFavoriteTitles()
    {
        var contributor = CreateDistributor("Studio");
        var favorite = CreateCard(
            "Favorite",
            "Favorito",
            contributor,
            null,
            null,
            new DateTimeOffset(2024, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var newest = CreateCard(
            "Newest",
            "Nuevo",
            contributor,
            null,
            null,
            new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero));
        var older = CreateCard(
            "Older",
            "Anterior",
            contributor,
            null,
            null,
            new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero));

        var ordered = CatalogCardOrdering.FilterAndOrder(
            [older, favorite, newest],
            null,
            null,
            new HashSet<Guid> { favorite.Id });

        Assert.Equal(
            ["Newest", "Older", "Favorite"],
            ordered.Select(card => card.Name));
    }

    [Fact]
    public void CatalogCanFilterByCategoryAndYear()
    {
        var studio = CreateDistributor("Studio");
        var match = CreateCard(
            "Match",
            "Coincidencia",
            studio,
            ["Anime"],
            "2026-04-12");
        var other = CreateCard(
            "Other",
            "Otro",
            CreateDistributor("Other Studio"),
            ["Anime"],
            "2026");

        var ordered = CatalogCardOrdering.FilterAndOrder(
            [other, match],
            studio.StableKey,
            null,
            null,
            "anime",
            2026);

        Assert.Equal([match.Id], ordered.Select(card => card.Id));
    }

    /// <summary>
    /// UTF-8 bytes that were once read back as Latin-1 leave a telltale Ã or Â
    /// in front of a continuation byte: "título" saved that way becomes
    /// "tÃ­tulo", whose second character is an invisible soft hyphen, so the
    /// damage survives review and only shows up on a screen. Three of the daily
    /// update strings shipped like that.
    /// </summary>
    [Theory]
    [InlineData("en-US")]
    [InlineData("es-AR")]
    public void NoTranslationCarriesMisdecodedUtf8(string cultureName)
    {
        var culture = CultureInfo.GetCultureInfo(cultureName);

        var damaged = UiTextCatalog.ResourceKeys
            .Select(key => (Key: key, Text: UiTextCatalog.Get(key, culture)))
            .Where(entry => ContainsMisdecodedUtf8(entry.Text))
            .Select(entry => $"{entry.Key}: {entry.Text}")
            .ToList();

        Assert.Empty(damaged);
    }

    private static bool ContainsMisdecodedUtf8(string text)
    {
        for (var index = 0; index < text.Length - 1; index++)
        {
            if (text[index] is not ('Ã' or 'Â'))
            {
                continue;
            }

            // A UTF-8 continuation byte, surfacing as a Latin-1 character.
            if (text[index + 1] is >= '' and <= '¿')
            {
                return true;
            }
        }

        return false;
    }

    private static CatalogCard CreateCard(
        string primaryName,
        string spanishName,
        CatalogContributor contributor,
        IReadOnlyList<string>? categories = null,
        string? releaseDate = null,
        DateTimeOffset? updatedAt = null) =>
        new(
            Guid.CreateVersion7(),
            MediaType.Movie,
            "movie.movie",
            primaryName,
            string.Empty,
            null,
            [],
            categories ?? [],
            [new CatalogAlternativeName(spanishName, "es", "translation")],
            [contributor],
            releaseDate,
            updatedAt);

    private static CatalogContributor CreateDistributor(string name) =>
        new(
            name,
            "organization",
            credits:
            [
                new CatalogContributorCredit("Distributor")
            ]);
}

using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ContentSourceSearchCriteriaTests
{
    [Fact]
    public void CriteriaRequireAtLeastOneCriterion()
    {
        Assert.Throws<ArgumentException>(() =>
            new ContentSourceSearchCriteria());
        Assert.Throws<ArgumentException>(() =>
            new ContentSourceSearchCriteria(
                mediaType: MediaType.Movie,
                subtypeGroup: MediaSubtypeGroup.Anime));
        Assert.Throws<ArgumentException>(() =>
            new ContentSourceSearchCriteria(new string('a', 201)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ContentSourceSearchCriteria(releaseYear: 1234));
    }

    [Fact]
    public void FilterOnlySearchIsRecognized()
    {
        var filterOnly = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.Mecha);
        Assert.True(filterOnly.IsFilterOnly);
        Assert.True(filterOnly.HasFilters);

        var textOnly = new ContentSourceSearchCriteria("Akira");
        Assert.False(textOnly.IsFilterOnly);
        Assert.False(textOnly.HasFilters);
    }

    [Theory]
    [InlineData("Acción")]
    [InlineData("accion")]
    [InlineData("ACTION")]
    [InlineData("Action")]
    public void GenreMatchesAcrossAccentsCaseAndLanguage(string label)
    {
        var criteria = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.Action);

        Assert.True(criteria.Matches(Card(genres: [label])));
    }

    [Theory]
    [InlineData("Ciencia Ficción")]
    [InlineData("Ciencia-Ficcion")]
    [InlineData("science fiction")]
    [InlineData("Sci-Fi")]
    public void GenreTreatsSeparatorsAsInterchangeable(string label)
    {
        var criteria = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.ScienceFiction);

        Assert.True(criteria.Matches(Card(genres: [label])));
    }

    [Fact]
    public void GenreMatchesTheNonObviousProviderWording()
    {
        var criteria = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.SliceOfLife);

        Assert.True(criteria.Matches(Card(genres: ["Cosas de la vida"])));
        Assert.True(criteria.Matches(Card(genres: ["Slice of Life"])));
    }

    [Fact]
    public void GenreDoesNotMatchACompoundLabel()
    {
        // "Acción y Aventura" is its own shelf, not proof of either genre.
        // Widening here is exactly the silent broadening the contract forbids.
        var criteria = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.Action);

        Assert.False(criteria.Matches(Card(genres: ["Acción y Aventura"])));
    }

    [Fact]
    public void GenreAlsoConsidersCategories()
    {
        var criteria = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.Horror);

        Assert.True(criteria.Matches(Card(categories: ["Terror"])));
    }

    [Fact]
    public void GenreRejectsAnItemWithNoGenres()
    {
        var criteria = new ContentSourceSearchCriteria(
            genre: ApiContentGenre.Horror);

        Assert.False(criteria.Matches(Card()));
    }

    [Fact]
    public void YearUsesTheStoredValue()
    {
        var criteria = new ContentSourceSearchCriteria(releaseYear: 1988);

        Assert.True(criteria.Matches(Card(releaseYear: 1988)));
        Assert.False(criteria.Matches(Card(releaseYear: 1989)));
        Assert.False(criteria.Matches(Card()));
    }

    [Fact]
    public void YearFallsBackToTheReleaseDateForOlderRows()
    {
        var criteria = new ContentSourceSearchCriteria(releaseYear: 1988);

        Assert.True(criteria.Matches(Card(releaseDate: "1988-07-16")));
        Assert.False(criteria.Matches(Card(releaseDate: "1989-07-16")));
    }

    [Fact]
    public void StoredYearWinsOverTheReleaseDate()
    {
        var card = Card(releaseDate: "2001-01-01", releaseYear: 1988);

        Assert.Equal(1988, card.ReleaseYear);
        Assert.True(
            new ContentSourceSearchCriteria(releaseYear: 1988).Matches(card));
        Assert.False(
            new ContentSourceSearchCriteria(releaseYear: 2001).Matches(card));
    }

    [Fact]
    public void YearIsNotConfusedByADateElsewhereInTheText()
    {
        // A description mentioning another year must not make the item match.
        var criteria = new ContentSourceSearchCriteria(releaseYear: 1995);
        var card = Card(
            description: "A 1995 remaster of the 1988 original.",
            releaseYear: 1988);

        Assert.False(criteria.Matches(card));
    }

    [Fact]
    public void CriteriaCombineWithAnd()
    {
        var criteria = new ContentSourceSearchCriteria(
            "Akira",
            genre: ApiContentGenre.Mecha,
            releaseYear: 1988);

        Assert.True(criteria.Matches(Card(
            name: "Akira",
            genres: ["Mecha"],
            releaseYear: 1988)));

        // Every one of the three has to hold.
        Assert.False(criteria.Matches(Card(
            name: "Akira",
            genres: ["Mecha"],
            releaseYear: 1991)));
        Assert.False(criteria.Matches(Card(
            name: "Akira",
            genres: ["Drama"],
            releaseYear: 1988)));
        Assert.False(criteria.Matches(Card(
            name: "Ghost in the Shell",
            genres: ["Mecha"],
            releaseYear: 1988)));
    }

    [Fact]
    public void QueryTextCheckIgnoresTheFilters()
    {
        var criteria = new ContentSourceSearchCriteria(
            "Akira",
            genre: ApiContentGenre.Mecha,
            releaseYear: 1988);
        var card = Card(name: "Akira");

        // A provider that filtered natively returns previews that need not
        // repeat the genre, so only the text is re-checked.
        Assert.True(criteria.MatchesQueryText(card));
        Assert.False(criteria.Matches(card));
    }

    [Fact]
    public void FilterOnlyCriteriaAcceptAnyTextWhenReChecked() =>
        Assert.True(
            new ContentSourceSearchCriteria(genre: ApiContentGenre.Mecha)
                .MatchesQueryText(Card(name: "Anything")));

    [Fact]
    public void ApiRequestCarriesEveryCriterion()
    {
        var request = new ContentSourceSearchCriteria(
                "Akira",
                MediaType.Movie,
                MediaSubtypeGroup.Anime,
                ApiContentGenre.Mecha,
                1988)
            .ToApiRequest();

        Assert.Equal("Akira", request.Query);
        Assert.Equal(MediaType.Movie, request.MediaType);
        Assert.Equal(ApiContentGenre.Mecha, request.Genre);
        Assert.Equal(1988, request.ReleaseYear);
    }

    [Fact]
    public void ApiRequestFromFilterOnlyCriteriaIsSkippedByATextOnlyProvider()
    {
        var request =
            new ContentSourceSearchCriteria(genre: ApiContentGenre.Mecha)
                .ToApiRequest();

        Assert.False(
            ApiContentSearchCapabilities.TextOnly.CanSatisfy(request));
    }

    private static CatalogCard Card(
        string name = "Untitled",
        string description = "",
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? categories = null,
        string? releaseDate = null,
        int? releaseYear = null) =>
        new(
            Guid.CreateVersion7(),
            MediaType.Movie,
            "movie:anime_movie",
            name,
            description,
            ThumbnailRef: null,
            genres ?? [],
            categories ?? [],
            [],
            [],
            releaseDate)
        {
            ReleaseYear = releaseYear
        };
}

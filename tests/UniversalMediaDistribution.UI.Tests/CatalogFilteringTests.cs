using System.Globalization;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.ContentSources;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Filtering;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogSearchSelectionTests
{
    [Fact]
    public void AnEmptySelectionHasNothingToSearchFor()
    {
        var selection = new CatalogSearchSelection();

        Assert.False(selection.HasCriteria);
        Assert.False(selection.IsFilterOnly);
    }

    [Fact]
    public void AMediaTypeAloneIsNotACriterion() =>
        // The shelf being browsed scopes a search; it cannot start one.
        Assert.False(
            new CatalogSearchSelection(
                MediaType: MediaType.Movie,
                SubtypeGroup: MediaSubtypeGroup.Anime).HasCriteria);

    [Theory]
    [InlineData("Akira", null, null, true, false)]
    [InlineData(null, "action", null, true, true)]
    [InlineData(null, null, 1988, true, true)]
    [InlineData("   ", null, 1988, true, true)]
    [InlineData("Akira", "action", 1988, true, false)]
    public void CriteriaAndFilterOnlyAreDetected(
        string? query,
        string? genreKey,
        int? year,
        bool expectedHasCriteria,
        bool expectedFilterOnly)
    {
        var selection = new CatalogSearchSelection(
            query,
            Genre: genreKey is null ? null : ApiContentGenre.Parse(genreKey),
            ReleaseYear: year);

        Assert.Equal(expectedHasCriteria, selection.HasCriteria);
        Assert.Equal(expectedFilterOnly, selection.IsFilterOnly);
    }

    [Fact]
    public void CriteriaCarryEverythingToTheSearch()
    {
        var criteria = new CatalogSearchSelection(
                "  Akira  ",
                MediaType.Movie,
                MediaSubtypeGroup.Anime,
                ApiContentGenre.Mecha,
                1988)
            .ToCriteria();

        Assert.Equal("Akira", criteria.Query);
        Assert.Equal(MediaType.Movie, criteria.MediaType);
        Assert.Equal(MediaSubtypeGroup.Anime, criteria.SubtypeGroup);
        Assert.Equal(ApiContentGenre.Mecha, criteria.Genre);
        Assert.Equal(1988, criteria.ReleaseYear);
    }

    [Fact]
    public void DescribeNamesAFilterOnlySearchByItsFilters()
    {
        var described = new CatalogSearchSelection(
            Genre: ApiContentGenre.Mecha,
            ReleaseYear: 1988).Describe();

        Assert.Equal(
            $"{UiTextCatalogGenreLabel(ApiContentGenre.Mecha)} · 1988",
            described);
    }

    [Fact]
    public void DescribeLeadsWithTheQueryAndTrimsIt() =>
        Assert.Equal(
            "Akira · 1988",
            new CatalogSearchSelection("  Akira  ", ReleaseYear: 1988)
                .Describe());

    [Fact]
    public void DescribeUsesAnInvariantYearSoItNeverPicksUpADigitGroup() =>
        Assert.Equal(
            "1988",
            new CatalogSearchSelection(ReleaseYear: 1988).Describe());

    private static string UiTextCatalogGenreLabel(ApiContentGenre genre) =>
        new CatalogGenreFilterViewModel(genre).Label;
}

public sealed class CatalogFilterChoicesTests
{
    [Fact]
    public void GenreChoicesLeadWithAllAndCoverTheSharedKeys()
    {
        var choices = CatalogFilterChoices.BuildGenres();

        Assert.Null(choices[0].Genre);
        Assert.Equal(ApiContentGenre.All.Count + 1, choices.Count);
        Assert.Equal(
            ApiContentGenre.All,
            choices.Skip(1).Select(static choice => choice.Genre!).ToArray());
    }

    [Fact]
    public void YearChoicesRunNewestFirstAndLeadWithAll()
    {
        var choices = CatalogFilterChoices.BuildYears([], 2026);

        Assert.Null(choices[0].Year);
        Assert.Equal(2026, choices[1].Year);
        Assert.Equal(
            CatalogFilterChoices.EarliestOfferedYear,
            choices[^1].Year);
        Assert.Equal(
            choices.Skip(1).Select(static choice => choice.Year).ToArray(),
            choices.Skip(1)
                .Select(static choice => choice.Year)
                .OrderByDescending(static year => year)
                .ToArray());
    }

    [Fact]
    public void ALocalYearOutsideTheOfferedRangeIsStillReachable()
    {
        var choices = CatalogFilterChoices.BuildYears([1927, null, 1990], 2026);

        Assert.Contains(choices, choice => choice.Year == 1927);
        Assert.Equal(1927, choices[^1].Year);
    }

    [Fact]
    public void APickedYearSurvivesEvenWhenNothingCarriesIt()
    {
        // Otherwise re-selecting the preserved value after a rebuild would
        // silently fall back to "all years" and drop the user's filter.
        var choices = CatalogFilterChoices.BuildYears([], 2026, pinnedYear: 1899);

        Assert.Contains(choices, choice => choice.Year == 1899);
    }

    [Fact]
    public void YearChoicesAreDistinct()
    {
        var choices = CatalogFilterChoices.BuildYears(
            [1990, 1990, 2000],
            2026,
            pinnedYear: 2000);

        var years = choices
            .Skip(1)
            .Select(static choice => choice.Year!.Value)
            .ToArray();
        Assert.Equal(years.Length, years.Distinct().Count());
    }

    [Fact]
    public void StatusChoicesOfferTheRequestedPublicationStates()
    {
        var choices = CatalogFilterChoices.BuildStatuses();

        Assert.Null(choices[0].Status);
        Assert.Equal(
            [
                PublicationStatus.Ongoing,
                PublicationStatus.Completed,
                PublicationStatus.Paused,
                PublicationStatus.Cancelled
            ],
            choices.Skip(1)
                .Select(static choice => choice.Status!.Value)
                .ToArray());
    }

    [Theory]
    [InlineData("ongoing", PublicationStatus.Ongoing)]
    [InlineData("En emisión", PublicationStatus.Ongoing)]
    [InlineData("Finalizado", PublicationStatus.Completed)]
    [InlineData("finished", PublicationStatus.Completed)]
    [InlineData("Finished Airing", PublicationStatus.Completed)]
    [InlineData("Ended", PublicationStatus.Completed)]
    [InlineData("hiatus", PublicationStatus.Paused)]
    [InlineData("En pausa", PublicationStatus.Paused)]
    [InlineData("on hold", PublicationStatus.Paused)]
    [InlineData("cancelled", PublicationStatus.Cancelled)]
    [InlineData("Returning Series", PublicationStatus.Ongoing)]
    [InlineData("In Production", PublicationStatus.Ongoing)]
    public void ProviderStatusLabelsAreNormalizedToTheSharedStates(
        string value,
        PublicationStatus expected)
    {
        Assert.Equal(expected, CatalogTransferMapper.ParsePublicationStatus(value));
    }

    [Theory]
    [InlineData(MediaType.Movie, "movie.movie", false)]
    [InlineData(MediaType.Movie, "movie.series", true)]
    [InlineData(MediaType.Movie, "movie.anime_movie", true)]
    [InlineData(MediaType.Movie, "movie.anime_series", true)]
    [InlineData(MediaType.Book, "book.manga", true)]
    [InlineData(MediaType.Book, "book.comic", false)]
    public void PublicationStatusOnlyAppliesToSupportedCatalogKinds(
        MediaType mediaType,
        string subtypeCode,
        bool expected)
    {
        Assert.Equal(
            expected,
            PublicationStatusApplicability.AppliesTo(
                mediaType,
                subtypeCode));
    }

    [Fact]
    public void AnInvertedRangeYieldsOnlyTheAllEntry() =>
        Assert.Single(CatalogFilterChoices.BuildYears([], 1900));

    [Fact]
    public void CategoryChoicesAreDerivedFromTheCardsOnScreen()
    {
        var choices = CatalogFilterChoices.BuildCategories(
            [
                Card(genres: ["Drama"], categories: ["Movies"]),
                Card(genres: ["drama"], categories: ["Comics"])
            ],
            CultureInfo.InvariantCulture);

        Assert.Null(choices[0].Category);
        Assert.Equal(
            ["Comics", "Drama", "Movies"],
            choices.Skip(1)
                .Select(static choice => choice.Category!)
                .ToArray());
    }

    [Fact]
    public void BlankCategoriesAreDropped()
    {
        var choices = CatalogFilterChoices.BuildCategories(
            [Card(genres: ["   "], categories: ["Movies"])],
            CultureInfo.InvariantCulture);

        Assert.Equal(2, choices.Count);
        Assert.Equal("Movies", choices[1].Category);
    }

    internal static CatalogCard Card(
        string name = "Untitled",
        IReadOnlyList<string>? genres = null,
        IReadOnlyList<string>? categories = null,
        IReadOnlyList<CatalogContributor>? contributors = null) =>
        new(
            Guid.CreateVersion7(),
            MediaType.Movie,
            "movie:anime_movie",
            name,
            string.Empty,
            ThumbnailRef: null,
            genres ?? [],
            categories ?? [],
            [],
            contributors ?? []);
}

public sealed class SearchResultDisplayFilterTests
{
    private static readonly ContentSourceProviderConfiguration Provider =
        new ContentSourceProviderConfiguration
        {
            Id = "pelisplus",
            DisplayName = "PelisPlus",
            Kind = ContentSourceProviderKind.Api,
            BaseUrl = "https://pelisplus.example/",
            IsEnabled = true
        }.Normalize();

    [Fact]
    public void EverythingPassesWhenNothingIsSelected()
    {
        var filter = new SearchResultDisplayFilter();

        Assert.True(filter.Matches(LocalResult()));
        Assert.True(filter.Matches(OnlineResult()));
    }

    [Fact]
    public void AnUnavailableProviderDropsItsOnlineResults()
    {
        // A provider disabled mid-search leaves results behind that should
        // stop showing.
        var filter = new SearchResultDisplayFilter(
            isProviderAvailable: static _ => false);

        Assert.False(filter.Matches(OnlineResult()));
        Assert.True(filter.Matches(LocalResult()));
    }

    [Fact]
    public void TheLocalAndOnlineChoicesSplitByOrigin()
    {
        var localOnly = new SearchResultDisplayFilter(
            ProviderFilterViewModel.LocalProviderKey);
        var onlineOnly = new SearchResultDisplayFilter(
            ProviderFilterViewModel.OnlineProviderKey);

        Assert.True(localOnly.Matches(LocalResult()));
        Assert.False(localOnly.Matches(OnlineResult()));
        Assert.False(onlineOnly.Matches(LocalResult()));
        Assert.True(onlineOnly.Matches(OnlineResult()));
    }

    [Fact]
    public void ANamedProviderChoiceMatchesTheProviderOrADistributor()
    {
        var filter = new SearchResultDisplayFilter(
            ProviderFilterViewModel.CreateNamedProviderKey("PelisPlus"));

        Assert.True(filter.Matches(OnlineResult()));
        Assert.False(filter.Matches(LocalResult()));

        // A local card credited to the same distributor also belongs under it.
        var credited = LocalResult(CatalogFilterChoicesTests.Card(
            contributors: [Distributor("PelisPlus")]));
        Assert.True(filter.Matches(credited));
    }

    [Fact]
    public void CategoryMatchesGenresAsWellAsCategories()
    {
        var filter = new SearchResultDisplayFilter(category: "Drama");

        Assert.True(filter.Matches(
            LocalResult(CatalogFilterChoicesTests.Card(genres: ["Drama"]))));
        Assert.True(filter.Matches(
            LocalResult(CatalogFilterChoicesTests.Card(
                categories: ["drama"]))));
        Assert.False(filter.Matches(
            LocalResult(CatalogFilterChoicesTests.Card(genres: ["Comedy"]))));
    }

    [Fact]
    public void SearchCriteriaAreNotReChecked()
    {
        // A provider that filtered by genre or year natively returns preview
        // cards that need not repeat either. Re-checking here would discard
        // exactly the results the filter was supposed to find.
        var bareCard = CatalogFilterChoicesTests.Card();
        var filter = new SearchResultDisplayFilter();

        Assert.Null(bareCard.ReleaseYear);
        Assert.Empty(bareCard.Genres);
        Assert.True(filter.Matches(OnlineResult(bareCard)));
    }

    [Fact]
    public void CriteriaCombineWithAnd()
    {
        var filter = new SearchResultDisplayFilter(
            ProviderFilterViewModel.OnlineProviderKey,
            "Drama");

        Assert.True(filter.Matches(
            OnlineResult(CatalogFilterChoicesTests.Card(genres: ["Drama"]))));
        Assert.False(filter.Matches(
            LocalResult(CatalogFilterChoicesTests.Card(genres: ["Drama"]))));
        Assert.False(filter.Matches(
            OnlineResult(CatalogFilterChoicesTests.Card(genres: ["Comedy"]))));
    }

    [Fact]
    public void ANullResultIsRejected() =>
        Assert.Throws<ArgumentNullException>(() =>
            new SearchResultDisplayFilter().Matches(null!));

    // A distributor is an organization credited with a distribution role, not
    // a contributor "kind" of its own.
    private static CatalogContributor Distributor(string name) =>
        new(
            name,
            "organization",
            url: null,
            thumbnailRef: null,
            [new CatalogContributorCredit("distributor")]);

    private static ContentSourceCatalogSearchResult LocalResult(
        CatalogCard? card = null) =>
        ContentSourceCatalogSearchResult.Local(
            card ?? CatalogFilterChoicesTests.Card());

    private static ContentSourceCatalogSearchResult OnlineResult(
        CatalogCard? card = null) =>
        ContentSourceCatalogSearchResult.Online(
            card ?? CatalogFilterChoicesTests.Card(),
            Provider,
            "movie/akira",
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "anime_movie",
                Name = "Akira"
            });
}

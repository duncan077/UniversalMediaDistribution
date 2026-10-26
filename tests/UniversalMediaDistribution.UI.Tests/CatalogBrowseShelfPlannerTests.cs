using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogBrowseShelfPlannerTests
{
    private static readonly DateTimeOffset Origin =
        new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    [Fact]
    public void RecentlyAddedLeadsWithTheNewestArrival()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("Oldest", addedAt: Origin),
            Card("Newest", addedAt: Origin.AddDays(9)),
            Card("Middle", addedAt: Origin.AddDays(4))
        ]);

        var added = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.RecentlyAdded);
        Assert.Equal(
            ["Newest", "Middle", "Oldest"],
            added.Cards.Select(static card => card.Name));
    }

    [Fact]
    public void AFreshImportHasNothingToShowAsUpdated()
    {
        // created_at and updated_at are stamped together on import. Without a
        // tolerance the two top rows would be the same twelve cards.
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("One", addedAt: Origin, updatedAt: Origin),
            Card("Two", addedAt: Origin, updatedAt: Origin.AddSeconds(3)),
            Card("Three", addedAt: Origin, updatedAt: Origin.AddSeconds(9))
        ]);

        Assert.DoesNotContain(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.RecentlyUpdated);
    }

    [Fact]
    public void OnlyTitlesThatChangedAfterArrivingCountAsUpdated()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("Untouched", addedAt: Origin, updatedAt: Origin),
            Card(
                "New chapter",
                addedAt: Origin,
                updatedAt: Origin.AddDays(2)),
            Card(
                "Newer chapter",
                addedAt: Origin,
                updatedAt: Origin.AddDays(5)),
            Card(
                "Newest chapter",
                addedAt: Origin,
                updatedAt: Origin.AddDays(7))
        ]);

        var updated = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.RecentlyUpdated);
        Assert.Equal(
            ["Newest chapter", "Newer chapter", "New chapter"],
            updated.Cards.Select(static card => card.Name));
    }

    [Fact]
    public void ABatchThatGenuinelyChangedManyTitlesStillFillsTheRow()
    {
        // The importer only moves updated_at when a payload actually differs,
        // so one shared timestamp means one import really did change them all.
        // The order among them is arbitrary; showing them is not.
        var refreshed = Origin.AddDays(30);
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", addedAt: Origin, updatedAt: refreshed),
            Card("B", addedAt: Origin, updatedAt: refreshed),
            Card("C", addedAt: Origin, updatedAt: refreshed),
            Card("D", addedAt: Origin, updatedAt: refreshed)
        ]);

        var updated = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.RecentlyUpdated);
        Assert.Equal(4, updated.Cards.Count);
    }

    [Fact]
    public void AnOngoingLibraryUpdatedAtManyDifferentTimesKeepsItsRow()
    {
        // No single timestamp dominates, so nothing is a sweep.
        var cards = Enumerable
            .Range(0, 8)
            .Select(index => Card(
                $"Title {index}",
                addedAt: Origin,
                updatedAt: Origin.AddDays(10 + index)))
            .ToArray();

        var updated = Assert.Single(
            CatalogBrowseShelfPlanner.Plan(cards),
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.RecentlyUpdated);
        Assert.Equal("Title 7", updated.Cards[0].Name);
        Assert.Equal(8, updated.Cards.Count);
    }

    [Fact]
    public void AGenreBecomesAShelfOnceItHoldsEnoughToFillOne()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Action"]),
            Card("B", genres: ["Action"]),
            Card("C", genres: ["Action"]),
            Card("D", genres: ["Western"])
        ]);

        var genres = shelves
            .Where(static shelf =>
                shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
            .ToArray();
        var action = Assert.Single(genres);
        Assert.Equal(ApiContentGenre.Action, action.Genre);
        Assert.Equal(3, action.TotalCount);
    }

    [Fact]
    public void SpanishLabelsLandOnTheSameShelfAsTheirEnglishNames()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Terror"]),
            Card("B", genres: ["Horror"]),
            Card("C", genres: ["horror"])
        ]);

        var horror = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
        Assert.Equal(ApiContentGenre.Horror, horror.Genre);
        Assert.Equal(3, horror.TotalCount);
    }

    [Fact]
    public void ACompoundLabelFilesTheTitleUnderBothGenres()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Acción y Aventura"]),
            Card("B", genres: ["Acción y Aventura"]),
            Card("C", genres: ["Acción y Aventura"])
        ]);

        Assert.Equal(
            [ApiContentGenre.Action.Key, ApiContentGenre.Adventure.Key],
            shelves
                .Where(static shelf =>
                    shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
                .Select(static shelf => shelf.Key.GenreKey)
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void AMultiWordAliasIsNotTornApartByTheCompoundSplit()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Cosas de la vida"]),
            Card("B", genres: ["Slice of Life"]),
            Card("C", genres: ["recuentos de la vida"])
        ]);

        var sliceOfLife = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
        Assert.Equal(ApiContentGenre.SliceOfLife, sliceOfLife.Genre);
    }

    [Fact]
    public void ASlashSeparatedLabelIsSplitToo()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Sci-Fi / Fantasy"]),
            Card("B", genres: ["Sci-Fi / Fantasy"]),
            Card("C", genres: ["Sci-Fi / Fantasy"])
        ]);

        Assert.Equal(
            [ApiContentGenre.Fantasy.Key, ApiContentGenre.ScienceFiction.Key],
            shelves
                .Where(static shelf =>
                    shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
                .Select(static shelf => shelf.Key.GenreKey)
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void EveryPartOfAThreeWayLabelIsFiled()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Acción, Comedia y Romance"]),
            Card("B", genres: ["Acción, Comedia y Romance"]),
            Card("C", genres: ["Acción, Comedia y Romance"])
        ]);

        Assert.Equal(
            [
                ApiContentGenre.Action.Key,
                ApiContentGenre.Comedy.Key,
                ApiContentGenre.Romance.Key
            ],
            shelves
                .Where(static shelf =>
                    shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
                .Select(static shelf => shelf.Key.GenreKey)
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void AnEverydayPhraseIsNotMinedForGenresItDoesNotMean()
    {
        // "Rhythm and Blues" splits, but neither half is a genre — the split
        // must widen what matches, never invent a match.
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Rhythm and Blues"]),
            Card("B", genres: ["Rhythm and Blues"]),
            Card("C", genres: ["Rhythm and Blues"])
        ]);

        Assert.DoesNotContain(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
    }

    [Fact]
    public void SurroundingWhitespaceDoesNotHideAGenre()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["  Drama  "]),
            Card("B", genres: ["Drama"]),
            Card("C", genres: ["\tDrama\n"])
        ]);

        var drama = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
        Assert.Equal(3, drama.TotalCount);
    }

    [Fact]
    public void ATitleIsNeverListedTwiceOnOneShelf()
    {
        // The same genre said two ways must not double the card.
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Terror", "Horror"]),
            Card("B", genres: ["Horror"]),
            Card("C", genres: ["Terror"])
        ]);

        var horror = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
        Assert.Equal(3, horror.TotalCount);
        Assert.Equal(3, horror.Cards.Distinct().Count());
    }

    [Fact]
    public void AChangeMomentsAfterImportStillCountsAsUpdated()
    {
        // The comparison is strict, with no tolerance window: the importer
        // moves updated_at only on a real content change, so a chapter landing
        // a second after the title was first stored is a genuine update.
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", addedAt: Origin, updatedAt: Origin.AddSeconds(1)),
            Card("B", addedAt: Origin, updatedAt: Origin.AddSeconds(2)),
            Card("C", addedAt: Origin, updatedAt: Origin.AddSeconds(3))
        ]);

        var updated = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.RecentlyUpdated);
        Assert.Equal(["C", "B", "A"], updated.Cards.Select(static c => c.Name));
    }

    [Fact]
    public void AHyphenJoinedLabelFilesTheTitleUnderBothGenres()
    {
        // ApiContentGenre folds a hyphen into a word break, so this collapses
        // to "drama romance" and matches nothing as a whole.
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Drama-Romance"]),
            Card("B", genres: ["Drama-Romance"]),
            Card("C", genres: ["Drama-Romance"])
        ]);

        Assert.Equal(
            [ApiContentGenre.Drama.Key, ApiContentGenre.Romance.Key],
            shelves
                .Where(static shelf =>
                    shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
                .Select(static shelf => shelf.Key.GenreKey)
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void AHyphenatedAliasSurvivesAlongsideASeparator()
    {
        // The regression guard for the hyphen pass: "Sci-Fi" must stay whole
        // long enough to match, even though the label also carries a slash.
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Sci-Fi / Fantasy"]),
            Card("B", genres: ["Sci-Fi / Fantasy"]),
            Card("C", genres: ["Sci-Fi / Fantasy"])
        ]);

        Assert.Equal(
            [ApiContentGenre.Fantasy.Key, ApiContentGenre.ScienceFiction.Key],
            shelves
                .Where(static shelf =>
                    shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
                .Select(static shelf => shelf.Key.GenreKey)
                .OrderBy(static key => key, StringComparer.Ordinal));
    }

    [Fact]
    public void TheBusiestGenresComeFirst()
    {
        var cards = new List<CatalogCard>();
        // Drama outnumbers comedy, which outnumbers horror.
        for (var index = 0; index < 7; index++)
        {
            cards.Add(Card($"Drama {index}", genres: ["Drama"]));
        }

        for (var index = 0; index < 5; index++)
        {
            cards.Add(Card($"Comedy {index}", genres: ["Comedy"]));
        }

        for (var index = 0; index < 3; index++)
        {
            cards.Add(Card($"Horror {index}", genres: ["Horror"]));
        }

        var genres = CatalogBrowseShelfPlanner.Plan(cards)
            .Where(static shelf =>
                shelf.Key.Kind is CatalogBrowseShelfKind.Genre)
            .Select(static shelf => shelf.Key.GenreKey)
            .ToArray();

        Assert.Equal(
            [
                ApiContentGenre.Drama.Key,
                ApiContentGenre.Comedy.Key,
                ApiContentGenre.Horror.Key
            ],
            genres);
    }

    [Fact]
    public void AGenreShelfIsPreviewedAndReportsItsFullLocalCount()
    {
        var cards = Enumerable
            .Range(0, CatalogBrowseShelfPlanner.ShelfLimit + 6)
            .Select(index => Card($"Title {index}", genres: ["Comedy"]))
            .ToArray();

        var comedy = Assert.Single(
            CatalogBrowseShelfPlanner.Plan(cards),
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);

        Assert.Equal(
            CatalogBrowseShelfPlanner.ShelfLimit,
            comedy.Cards.Count);
        Assert.Equal(cards.Length, comedy.TotalCount);
    }

    [Fact]
    public void TheNumberOfGenreRowsIsCappedSoThePageStaysReadable()
    {
        var cards = ApiContentGenre.All
            .SelectMany(genre => Enumerable
                .Range(0, CatalogBrowseShelfPlanner.MinimumShelfSize)
                .Select(index => Card(
                    $"{genre.Key} {index}",
                    genres: [genre.Key])))
            .ToArray();

        var genreShelves = CatalogBrowseShelfPlanner.Plan(cards)
            .Count(static shelf =>
                shelf.Key.Kind is CatalogBrowseShelfKind.Genre);

        Assert.Equal(
            CatalogBrowseShelfPlanner.MaximumGenreShelves,
            genreShelves);
    }

    [Fact]
    public void AGenreTheLibraryBarelyHoldsIsLeftToTheFilterPicker()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Western"]),
            Card("B", genres: ["Western"])
        ]);

        Assert.DoesNotContain(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
    }

    [Fact]
    public void AGenreShelfKeepsTheCallersOrdering()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("Third alphabetically", genres: ["Drama"]),
            Card("First alphabetically", genres: ["Drama"]),
            Card("Second alphabetically", genres: ["Drama"])
        ]);

        var drama = Assert.Single(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
        Assert.Equal(
            [
                "Third alphabetically",
                "First alphabetically",
                "Second alphabetically"
            ],
            drama.Cards.Select(static card => card.Name));
    }

    [Fact]
    public void AnEmptyLibraryPlansNothing() =>
        Assert.Empty(CatalogBrowseShelfPlanner.Plan([]));

    [Fact]
    public void ALibraryTooSmallForRowsPlansNothing() =>
        Assert.Empty(CatalogBrowseShelfPlanner.Plan(
        [
            Card("Only one", genres: ["Drama"]),
            Card("Only two", genres: ["Drama"])
        ]));

    [Fact]
    public void ACallerCanUseShorterRowsOnConstrainedDevices()
    {
        const int constrainedLimit = 4;
        var cards = Enumerable
            .Range(0, 10)
            .Select(index => Card(
                $"Title {index}",
                addedAt: Origin.AddDays(index),
                genres: ["Drama"]))
            .ToArray();

        var shelves = CatalogBrowseShelfPlanner.Plan(
            cards,
            constrainedLimit);

        Assert.All(
            shelves,
            shelf => Assert.Equal(constrainedLimit, shelf.Cards.Count));
    }

    [Fact]
    public void AGenreCanBeTurnedOffEntirely()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
            [
                Card("A", genres: ["Drama"]),
                Card("B", genres: ["Drama"]),
                Card("C", genres: ["Drama"])
            ],
            CatalogBrowseShelfPlanner.ShelfLimit,
            maximumGenreShelves: 0);

        Assert.DoesNotContain(
            shelves,
            shelf => shelf.Key.Kind is CatalogBrowseShelfKind.Genre);
    }

    [Fact]
    public void RecencyRowsComeBeforeGenreRows()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", addedAt: Origin, genres: ["Drama"]),
            Card("B", addedAt: Origin.AddDays(1), genres: ["Drama"]),
            Card("C", addedAt: Origin.AddDays(2), genres: ["Drama"])
        ]);

        Assert.Equal(
            [CatalogBrowseShelfKind.RecentlyAdded, CatalogBrowseShelfKind.Genre],
            shelves.Select(static shelf => shelf.Key.Kind));
    }

    [Fact]
    public void ATitleWithNoRecognizedGenreStillReachesTheRecencyRows()
    {
        var shelves = CatalogBrowseShelfPlanner.Plan(
        [
            Card("A", genres: ["Unclassifiable nonsense"]),
            Card("B", genres: []),
            Card("C", genres: ["Comedia Romántica"])
        ]);

        var added = Assert.Single(shelves);
        Assert.Equal(CatalogBrowseShelfKind.RecentlyAdded, added.Key.Kind);
        Assert.Equal(3, added.Cards.Count);
    }

    private static CatalogCard Card(
        string name,
        DateTimeOffset? addedAt = null,
        DateTimeOffset? updatedAt = null,
        IReadOnlyList<string>? genres = null)
    {
        var created = addedAt ?? Origin;
        return new CatalogCard(
            Guid.NewGuid(),
            MediaType.Movie,
            "movie.movie",
            name,
            Description: string.Empty,
            ThumbnailRef: null,
            Genres: genres ?? [],
            Categories: [],
            AlternativeNames: [],
            Contributors: [],
            ReleaseDate: null,
            UpdatedAt: updatedAt ?? created,
            CreatedAt: created);
    }
}

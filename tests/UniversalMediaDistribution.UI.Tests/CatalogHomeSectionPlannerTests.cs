using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogHomeSectionPlannerTests
{
    [Fact]
    public void AnimeIsItsOwnShelfEvenThoughItIsFiledUnderMovies()
    {
        var sections = CatalogHomeSectionPlanner.Plan(
            [
                Card("Live action film", MediaType.Movie, "movie.movie"),
                Card("Anime film", MediaType.Movie, "movie.anime_movie"),
                Card("Anime show", MediaType.Movie, "movie.anime_series"),
                Card("A manga", MediaType.Book, "book.manga")
            ],
            new Dictionary<Guid, CatalogProgressSummary>());

        Assert.Equal(
            [
                CatalogHomeSectionKind.MoviesAndSeries,
                CatalogHomeSectionKind.Anime,
                CatalogHomeSectionKind.BooksAndManga
            ],
            sections.Select(static section => section.Kind));
        Assert.Equal(
            ["Anime film", "Anime show"],
            sections
                .Single(static section =>
                    section.Kind is CatalogHomeSectionKind.Anime)
                .Cards
                .Select(static card => card.Name));
    }

    [Fact]
    public void EmptyShelvesAreLeftOut()
    {
        var sections = CatalogHomeSectionPlanner.Plan(
            [Card("Only a book", MediaType.Book, "book.book")],
            new Dictionary<Guid, CatalogProgressSummary>());

        Assert.Equal(
            CatalogHomeSectionKind.BooksAndManga,
            Assert.Single(sections).Kind);
    }

    [Fact]
    public void LastSeenComesFirstAndIsOrderedByMostRecentlyOpened()
    {
        var oldest = Card("Oldest", MediaType.Movie, "movie.movie");
        var newest = Card("Newest", MediaType.Book, "book.manga");
        var untouched = Card("Untouched", MediaType.Movie, "movie.movie");
        var progress = new Dictionary<Guid, CatalogProgressSummary>
        {
            [oldest.Id] = Progress(oldest.Id, DateTimeOffset.UnixEpoch),
            [newest.Id] = Progress(
                newest.Id,
                DateTimeOffset.UnixEpoch.AddDays(3))
        };

        var sections = CatalogHomeSectionPlanner.Plan(
            [oldest, newest, untouched],
            progress);

        var lastSeen = sections[0];
        Assert.Equal(CatalogHomeSectionKind.LastSeen, lastSeen.Kind);
        Assert.Equal(
            ["Newest", "Oldest"],
            lastSeen.Cards.Select(static card => card.Name));
    }

    [Fact]
    public void ATitleOnTheLastSeenShelfStillAppearsUnderItsOwnKind()
    {
        var card = Card("Watched", MediaType.Movie, "movie.anime_series");
        var sections = CatalogHomeSectionPlanner.Plan(
            [card],
            new Dictionary<Guid, CatalogProgressSummary>
            {
                [card.Id] = Progress(card.Id, DateTimeOffset.UnixEpoch)
            });

        Assert.Equal(
            [CatalogHomeSectionKind.LastSeen, CatalogHomeSectionKind.Anime],
            sections.Select(static section => section.Kind));
    }

    [Fact]
    public void LastSeenIsCappedSoItStaysAShortcut()
    {
        var cards = Enumerable
            .Range(0, CatalogHomeSectionPlanner.LastSeenLimit + 5)
            .Select(index => Card(
                $"Title {index}",
                MediaType.Movie,
                "movie.movie"))
            .ToArray();
        var progress = cards
            .Select((card, index) => (card, index))
            .ToDictionary(
                static value => value.card.Id,
                value => Progress(
                    value.card.Id,
                    DateTimeOffset.UnixEpoch.AddDays(value.index)));

        var sections = CatalogHomeSectionPlanner.Plan(cards, progress);

        var lastSeen = sections[0];
        Assert.Equal(
            CatalogHomeSectionPlanner.LastSeenLimit,
            lastSeen.Cards.Count);
        // The newest opened title leads the shelf.
        Assert.Equal(
            $"Title {cards.Length - 1}",
            lastSeen.Cards[0].Name);
    }

    [Fact]
    public void AShelfIsPreviewedAndReportsHowManyItReallyHolds()
    {
        var cards = Enumerable
            .Range(0, CatalogHomeSectionPlanner.ShelfLimit + 4)
            .Select(index => Card(
                $"Book {index}",
                MediaType.Book,
                "book.manga"))
            .ToArray();

        var section = Assert.Single(CatalogHomeSectionPlanner.Plan(
            cards,
            new Dictionary<Guid, CatalogProgressSummary>()));

        Assert.Equal(CatalogHomeSectionPlanner.ShelfLimit, section.Cards.Count);
        Assert.Equal(cards.Length, section.TotalCount);
        Assert.True(section.HasMore);
    }

    [Fact]
    public void ACallerCanUseSmallerPreviewsOnConstrainedDevices()
    {
        const int constrainedLimit = 3;
        var cards = Enumerable
            .Range(0, 8)
            .Select(index => Card(
                $"Book {index}",
                MediaType.Book,
                "book.manga"))
            .ToArray();

        var section = Assert.Single(CatalogHomeSectionPlanner.Plan(
            cards,
            new Dictionary<Guid, CatalogProgressSummary>(),
            constrainedLimit));

        Assert.Equal(constrainedLimit, section.Cards.Count);
        Assert.Equal(cards.Length, section.TotalCount);
        Assert.True(section.HasMore);
    }

    [Fact]
    public void AShelfThatFitsIsNotMarkedAsHavingMore()
    {
        var section = Assert.Single(CatalogHomeSectionPlanner.Plan(
            [Card("Only one", MediaType.Book, "book.manga")],
            new Dictionary<Guid, CatalogProgressSummary>()));

        Assert.Equal(1, section.TotalCount);
        Assert.False(section.HasMore);
    }

    [Fact]
    public void TheIncomingOrderIsKeptWithinAShelf()
    {
        var sections = CatalogHomeSectionPlanner.Plan(
            [
                Card("Second alphabetically", MediaType.Movie, "movie.movie"),
                Card("First alphabetically", MediaType.Movie, "movie.series")
            ],
            new Dictionary<Guid, CatalogProgressSummary>());

        Assert.Equal(
            ["Second alphabetically", "First alphabetically"],
            Assert.Single(sections).Cards.Select(static card => card.Name));
    }

    private static CatalogCard Card(
        string name,
        MediaType mediaType,
        string subtypeCode) =>
        new(
            Guid.NewGuid(),
            mediaType,
            subtypeCode,
            name,
            Description: string.Empty,
            ThumbnailRef: null,
            Genres: [],
            Categories: [],
            AlternativeNames: [],
            Contributors: []);

    private static CatalogProgressSummary Progress(
        Guid catalogItemId,
        DateTimeOffset updatedAt) =>
        new(
            new CatalogContentIdentity(
                catalogItemId,
                stableKey: $"{catalogItemId:N}:episode:1",
                ContentUnitKind.Episode,
                displayNumber: "1",
                seasonNumber: 1),
            new MediaProgressState(
                position: 30,
                MediaPositionKind.Seconds,
                isCompleted: false,
                updatedAt));
}

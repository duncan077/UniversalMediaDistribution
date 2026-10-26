using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Seasons used to be one fragment of grey subheading, so "Episode 1" of one
/// season looked exactly like "Episode 1" of the next. The list marks the row
/// that opens each season instead.
/// </summary>
public sealed class SeasonHeaderTests
{
    [Fact]
    public void OnlyTheFirstRowOfASeasonCarriesTheHeader()
    {
        var entries = Build(
            ("Season 1", "1"),
            ("Season 1", "2"),
            ("Season 2", "1"),
            ("Season 2", "2"));

        CatalogDetailsViewModel.ApplySeasonHeaders(entries);

        Assert.Equal("Season 1", entries[0].SeasonHeader);
        Assert.Null(entries[1].SeasonHeader);
        Assert.Equal("Season 2", entries[2].SeasonHeader);
        Assert.Null(entries[3].SeasonHeader);
    }

    [Fact]
    public void ReversingTheOrderMovesTheHeaderToTheNewFirstRow()
    {
        var entries = Build(
            ("Season 1", "1"),
            ("Season 1", "2"),
            ("Season 2", "1"),
            ("Season 2", "2"));
        CatalogDetailsViewModel.ApplySeasonHeaders(entries);

        var reversed = entries.AsEnumerable().Reverse().ToArray();
        CatalogDetailsViewModel.ApplySeasonHeaders(reversed);

        // Newest first: season 2 opens the list, and the rows that used to
        // carry the headers must have given them up.
        Assert.Equal("Season 2", reversed[0].SeasonHeader);
        Assert.Null(reversed[1].SeasonHeader);
        Assert.Equal("Season 1", reversed[2].SeasonHeader);
        Assert.Null(reversed[3].SeasonHeader);
    }

    [Fact]
    public void ATitleWithoutSeasonsShowsNoHeaders()
    {
        var entries = Build(
            (null, "1"),
            (null, "2"));

        CatalogDetailsViewModel.ApplySeasonHeaders(entries);

        Assert.All(entries, entry => Assert.False(entry.ShowsSeasonHeader));
    }

    [Fact]
    public void SeasonlessRowsBeforeASeasonDoNotSuppressItsHeader()
    {
        // Specials and loose episodes are listed before the seasons, so the
        // first real season still has to announce itself.
        var entries = Build(
            (null, "1"),
            ("Season 1", "1"));

        CatalogDetailsViewModel.ApplySeasonHeaders(entries);

        Assert.Null(entries[0].SeasonHeader);
        Assert.Equal("Season 1", entries[1].SeasonHeader);
    }

    [Fact]
    public void ASeasonReturningLaterAnnouncesItselfAgain()
    {
        // Interleaved seasons are not the normal order, but a header that only
        // ever appeared once would leave the second run unlabelled.
        var entries = Build(
            ("Season 1", "1"),
            ("Season 2", "1"),
            ("Season 1", "2"));

        CatalogDetailsViewModel.ApplySeasonHeaders(entries);

        Assert.Equal("Season 1", entries[0].SeasonHeader);
        Assert.Equal("Season 2", entries[1].SeasonHeader);
        Assert.Equal("Season 1", entries[2].SeasonHeader);
    }

    [Fact]
    public void TheSelectorOffersEverySeasonPlusAllOfThem()
    {
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 1", "2"),
            ("Season 2", "1"));

        Assert.True(details.HasSeasonSelector);
        Assert.Equal(
            ["All seasons", "Season 1", "Season 2"],
            details.SeasonOptions.Select(option => option.Label));
        Assert.True(details.SelectedSeason?.IsAllSeasons);
    }

    [Fact]
    public void ChoosingASeasonLeavesOnlyItsEpisodes()
    {
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 1", "2"),
            ("Season 2", "1"));

        details.SelectedSeason = details.SeasonOptions
            .First(option => option.Label == "Season 2");

        Assert.Single(details.Entries);
        Assert.Equal("Episode 1", details.Entries[0].Heading);

        // The one row left opens its season, so it keeps the divider.
        Assert.Equal("Season 2", details.Entries[0].SeasonHeader);
    }

    [Fact]
    public void GoingBackToAllSeasonsRestoresEveryEpisode()
    {
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 2", "1"));
        details.SelectedSeason = details.SeasonOptions[1];

        details.SelectedSeason = details.SeasonOptions[0];

        Assert.Equal(2, details.Entries.Count);
    }

    [Fact]
    public void ReversingTheOrderKeepsTheChosenSeason()
    {
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 2", "1"),
            ("Season 2", "2"));
        details.SelectedSeason = details.SeasonOptions
            .First(option => option.Label == "Season 2");

        details.ApplyEntryOrder(isDescending: true);

        Assert.Equal(2, details.Entries.Count);
        Assert.All(
            details.Entries,
            entry => Assert.Equal("Season 2", entry.SeasonLabel));
        Assert.Equal("Episode 2", details.Entries[0].Heading);
    }

    [Fact]
    public void PressingASeasonButtonNarrowsTheList()
    {
        // The path the screen actually uses: each button carries its own
        // command, so nothing has to reach up out of the template.
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 2", "1"),
            ("Season 2", "2"));

        details.SeasonOptions
            .First(option => option.Label == "Season 2")
            .SelectCommand.Execute(null);

        Assert.Equal(2, details.Entries.Count);
        Assert.All(
            details.Entries,
            entry => Assert.Equal("Season 2", entry.SeasonLabel));
    }

    [Fact]
    public void ExactlyOneSeasonButtonIsEverMarkedChosen()
    {
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 2", "1"));
        Assert.Equal(
            [true, false, false],
            details.SeasonOptions.Select(option => option.IsSelected));

        details.SeasonOptions[2].SelectCommand.Execute(null);

        Assert.Equal(
            [false, false, true],
            details.SeasonOptions.Select(option => option.IsSelected));
    }

    [Fact]
    public void ATitleWithOneSeasonGetsNoSelector()
    {
        var details = BuildDetails(
            ("Season 1", "1"),
            ("Season 1", "2"));

        // One season and "all seasons" would be the same list twice.
        Assert.False(details.HasSeasonSelector);
    }

    [Fact]
    public void ATitleWithoutSeasonsGetsNoSelector()
    {
        var details = BuildDetails((null, "1"), (null, "2"));

        Assert.Empty(details.SeasonOptions);
        Assert.False(details.HasSeasonSelector);
    }

    /// <summary>
    /// A real details page over a made-up title: the selector reads the rows
    /// the page builds, so anything less would be testing the fixture.
    /// </summary>
    private static CatalogDetailsViewModel BuildDetails(
        params (string? Season, string Number)[] rows)
    {
        var catalogItemId = Guid.CreateVersion7();
        var seasonless = rows
            .Where(static row => row.Season is null)
            .Select((row, index) => Unit(catalogItemId, index, row.Number))
            .ToArray();
        var seasons = rows
            .Where(static row => row.Season is not null)
            .GroupBy(static row => row.Season!, StringComparer.Ordinal)
            .Select((group, groupIndex) => new CatalogSeasonDetails(
                groupIndex + 1,
                groupIndex,
                group.Key,
                ThumbnailRef: null,
                ReleaseDate: null,
                group
                    .Select((row, index) => Unit(
                        catalogItemId,
                        (groupIndex + 1) * 100 + index,
                        row.Number))
                    .ToArray()))
            .ToArray();

        return new CatalogDetailsViewModel(
            new CatalogItemDetails(
                new CatalogItem(
                    catalogItemId,
                    MediaType.Movie,
                    "movie.series",
                    "A series"),
                new CatalogContentIdentity(catalogItemId, "item"),
                OriginalName: null,
                ReleaseDate: null,
                OriginalLanguage: null,
                CountryCode: null,
                ContentRating: null,
                RuntimeSeconds: null,
                PageCount: null,
                seasons,
                seasonless,
                Sources: [],
                DistributionRights: []),
            new TorrentMediaSessionService(engine: null),
            openAsync: static _ => Task.CompletedTask);
    }

    private static CatalogContentUnitDetails Unit(
        Guid catalogItemId,
        int sortOrder,
        string number) =>
        new(
            new CatalogContentIdentity(
                catalogItemId,
                $"episode:{sortOrder}",
                ContentUnitKind.Episode,
                number),
            ContentUnitKind.Episode,
            number,
            sortOrder,
            Name: null,
            ThumbnailRef: null,
            DurationSeconds: null,
            PageCount: null,
            ReleaseDate: null,
            IsSpecial: false,
            Sources: []);

    private static CatalogContentEntryViewModel[] Build(
        params (string? Season, string Number)[] rows)
    {
        var catalogItemId = Guid.CreateVersion7();
        var itemIdentity = new CatalogContentIdentity(catalogItemId, "item");
        return rows
            .Select((row, index) => new CatalogContentEntryViewModel(
                $"Episode {row.Number}",
                subheading: row.Season,
                thumbnailRef: null,
                new CatalogContentIdentity(
                    catalogItemId,
                    $"episode:{index}",
                    ContentUnitKind.Episode,
                    row.Number),
                itemIdentity,
                MediaType.Movie,
                sources: [],
                new TorrentMediaSessionService(engine: null),
                openAsync: static _ => Task.CompletedTask,
                seasonLabel: row.Season))
            .ToArray();
    }
}

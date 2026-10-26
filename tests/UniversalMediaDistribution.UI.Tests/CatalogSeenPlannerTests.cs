using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogSeenPlannerTests
{
    private static readonly Guid ItemId = Guid.CreateVersion7();

    [Fact]
    public void MarkingAChapter_CoversEveryEarlierChapter()
    {
        var details = CreateChapters(1, 2, 3, 4, 5);
        var target = UnitOf(details, "3");

        var pending = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            target,
            _ => false);

        Assert.Equal(
            ["1", "2", "3"],
            pending.Select(static unit => unit.DisplayNumber));
    }

    [Fact]
    public void ChaptersAlreadySeen_AreNotWrittenAgain()
    {
        var details = CreateChapters(1, 2, 3, 4);
        var seen = new HashSet<string>(["chapter-1", "chapter-2"]);

        var pending = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            UnitOf(details, "4"),
            identity => seen.Contains(identity.StableKey));

        Assert.Equal(
            ["3", "4"],
            pending.Select(static unit => unit.DisplayNumber));
    }

    [Fact]
    public void AlreadySeenChapterWithNothingBefore_NeedsNoWrite()
    {
        var details = CreateChapters(1, 2);

        var pending = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            UnitOf(details, "1"),
            _ => true);

        Assert.Empty(pending);
    }

    [Fact]
    public void EpisodesCarryAcrossEarlierSeasons()
    {
        var details = CreateSeasons(
            (1, ["1", "2"]),
            (2, ["1", "2", "3"]));
        var target = details.Seasons
            .Single(static season => season.Number == 2)
            .Units
            .Single(static unit => unit.DisplayNumber == "2");

        var pending = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            target.Identity,
            _ => false);

        Assert.Equal(
            ["s1e1", "s1e2", "s2e1", "s2e2"],
            pending.Select(static unit => unit.StableKey));
    }

    [Fact]
    public void SpecialsAreLeftAloneUnlessTheyAreTheTarget()
    {
        var details = CreateChapters(1, 2, 3, specialNumbers: ["2.5"]);

        var throughThree = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            UnitOf(details, "3"),
            _ => false);
        var theSpecial = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            UnitOf(details, "2.5"),
            _ => false);

        Assert.Equal(
            ["1", "2", "3"],
            throughThree.Select(static unit => unit.DisplayNumber));
        Assert.Equal(
            ["1", "2", "2.5"],
            theSpecial.Select(static unit => unit.DisplayNumber));
    }

    [Fact]
    public void SortOrderDrivesTheSequence_NotTheListOrder()
    {
        // Stored out of order, as a provider import can leave them.
        var details = CreateDetails(
            ItemId,
            [
                CreateUnit(
                    ItemId,
                    "chapter-3",
                    "3",
                    sortOrder: 2,
                    ContentUnitKind.Chapter,
                    isSpecial: false),
                CreateUnit(
                    ItemId,
                    "chapter-1",
                    "1",
                    sortOrder: 0,
                    ContentUnitKind.Chapter,
                    isSpecial: false),
                CreateUnit(
                    ItemId,
                    "chapter-2",
                    "2",
                    sortOrder: 1,
                    ContentUnitKind.Chapter,
                    isSpecial: false)
            ],
            []);

        var pending = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            UnitOf(details, "2"),
            _ => false);

        Assert.Equal(
            ["1", "2", "3"],
            CatalogSeenPlanner.OrderUnits(details)
                .Select(static unit => unit.DisplayNumber));
        Assert.Equal(
            ["1", "2"],
            pending.Select(static unit => unit.DisplayNumber));
    }

    [Fact]
    public void WithoutTheItemDetails_OnlyTheTargetIsMarked()
    {
        var details = CreateChapters(1, 2, 3);
        var target = UnitOf(details, "3");

        var withoutDetails = CatalogSeenPlanner.ResolveUnseenUpTo(
            details: null,
            target,
            _ => false);
        var otherItem = CatalogSeenPlanner.ResolveUnseenUpTo(
            CreateChapters(Guid.CreateVersion7(), [1, 2, 3], []),
            target,
            _ => false);

        Assert.Equal([target], withoutDetails);
        Assert.Equal([target], otherItem);
    }

    [Fact]
    public void ItemLevelContent_IsNotTracked()
    {
        var details = CreateChapters(1, 2);

        var pending = CatalogSeenPlanner.ResolveUnseenUpTo(
            details,
            details.Identity,
            _ => false);

        Assert.Empty(pending);
    }

    private static CatalogContentIdentity UnitOf(
        CatalogItemDetails details,
        string displayNumber) =>
        CatalogSeenPlanner.OrderUnits(details)
            .Single(unit => unit.DisplayNumber == displayNumber)
            .Identity;

    private static CatalogItemDetails CreateChapters(
        params int[] numbers) =>
        CreateChapters(ItemId, numbers, []);

    private static CatalogItemDetails CreateChapters(
        int[] numbers,
        string[] specialNumbers) =>
        CreateChapters(ItemId, numbers, specialNumbers);

    private static CatalogItemDetails CreateChapters(
        int first,
        int second,
        int third,
        string[] specialNumbers) =>
        CreateChapters(ItemId, [first, second, third], specialNumbers);

    private static CatalogItemDetails CreateChapters(
        Guid itemId,
        int[] numbers,
        string[] specialNumbers)
    {
        var units = new List<CatalogContentUnitDetails>();
        var sortOrder = 0;
        foreach (var number in numbers)
        {
            units.Add(CreateUnit(
                itemId,
                $"chapter-{number}",
                number.ToString(),
                sortOrder++,
                ContentUnitKind.Chapter,
                isSpecial: false));
        }

        foreach (var special in specialNumbers)
        {
            units.Add(CreateUnit(
                itemId,
                $"chapter-{special}",
                special,
                sortOrder++,
                ContentUnitKind.Chapter,
                isSpecial: true));
        }

        // Sort order, not list order, defines the sequence.
        units = units
            .OrderBy(static unit => decimal.Parse(
                unit.DisplayNumber,
                System.Globalization.CultureInfo.InvariantCulture))
            .Select((unit, index) => unit with { SortOrder = index })
            .ToList();
        return CreateDetails(itemId, units, []);
    }

    private static CatalogItemDetails CreateSeasons(
        params (int Number, string[] Episodes)[] seasons)
    {
        var seasonDetails = seasons
            .Select((season, index) => new CatalogSeasonDetails(
                season.Number,
                index,
                $"Season {season.Number}",
                null,
                null,
                season.Episodes
                    .Select((episode, position) => CreateUnit(
                        ItemId,
                        $"s{season.Number}e{episode}",
                        episode,
                        position,
                        ContentUnitKind.Episode,
                        isSpecial: false))
                    .ToArray()))
            .ToArray();
        return CreateDetails(ItemId, [], seasonDetails);
    }

    private static CatalogContentUnitDetails CreateUnit(
        Guid itemId,
        string stableKey,
        string displayNumber,
        int sortOrder,
        ContentUnitKind kind,
        bool isSpecial) =>
        new(
            new CatalogContentIdentity(
                itemId,
                stableKey,
                kind,
                displayNumber),
            kind,
            displayNumber,
            sortOrder,
            null,
            null,
            null,
            null,
            null,
            isSpecial,
            []);

    private static CatalogItemDetails CreateDetails(
        Guid itemId,
        IReadOnlyList<CatalogContentUnitDetails> units,
        IReadOnlyList<CatalogSeasonDetails> seasons) =>
        new(
            new CatalogItem(
                itemId,
                MediaType.Book,
                "book.manga",
                "Test series"),
            new CatalogContentIdentity(itemId, "item"),
            null,
            null,
            null,
            null,
            null,
            null,
            null,
            seasons,
            units,
            [],
            []);
}

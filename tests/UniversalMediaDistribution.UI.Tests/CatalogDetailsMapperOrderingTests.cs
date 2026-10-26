using UniversalMediaDistribution.Catalog;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.UI.Tests;

public class CatalogDetailsMapperOrderingTests
{
    private static CatalogTransferItem Manga(
        params (string DisplayNumber, int SortOrder)[] chapters) =>
        new()
        {
            MediaType = "book",
            Subtype = "manga",
            Name = "Test Manga",
            Units = chapters
                .Select(chapter => new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = chapter.DisplayNumber,
                    SortOrder = chapter.SortOrder
                })
                .ToArray()
        };

    private static string[] Numbers(CatalogTransferItem item) =>
        CatalogDetailsMapper
            .ToDetails(item, Guid.NewGuid())
            .Units
            .Select(unit => unit.DisplayNumber ?? string.Empty)
            .ToArray();

    [Fact]
    public void OrdersByChapterNumberNotByBatchPosition()
    {
        // What a daily update actually produces: the newest chapters arrive in
        // their own batch numbered from zero, so 67 and 68 claim the first two
        // positions ahead of 65 and 66.
        var details = Manga(
            ("67", 0),
            ("68", 1),
            ("65", 2),
            ("66", 3));

        Assert.Equal(["65", "66", "67", "68"], Numbers(details));
    }

    [Fact]
    public void PlacesDecimalChaptersBetweenTheirNeighbours()
    {
        var details = Manga(
            ("56.5", 0),
            ("57", 1),
            ("54", 2),
            ("55", 3),
            ("56", 4));

        Assert.Equal(
            ["54", "55", "56", "56.5", "57"],
            Numbers(details));
    }

    [Fact]
    public void KeepsUnnumberedUnitsLastInProviderOrder()
    {
        // Specials carry no number, so there is nothing to sort them by; they
        // keep the order their provider gave and stay after the numbered runs.
        var details = Manga(
            ("Especial", 9),
            ("2", 1),
            ("Extra", 8),
            ("1", 0));

        Assert.Equal(["1", "2", "Extra", "Especial"], Numbers(details));
    }

    [Fact]
    public void FallsBackToSortOrderWhenNumbersTie()
    {
        var details = Manga(
            ("7", 3),
            ("7", 1));

        Assert.Equal(["7", "7"], Numbers(details));
    }
}

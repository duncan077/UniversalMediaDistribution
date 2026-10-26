using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApiCatalogUnitMergeTests
{
    [Theory]
    [InlineData("01", "1.00", true)]
    [InlineData(" 2.50 ", "+2.5", true)]
    [InlineData("1,000", "1000", true)]
    [InlineData("-0", "0.0", true)]
    [InlineData("Bonus", "BONUS", true)]
    [InlineData("Bonus ", "Bonus", false)]
    [InlineData("1e2", "100", false)]
    [InlineData("1.1", "1.2", false)]
    public void IndexedMergePreservesChapterMatchingRules(string localNumber, string remoteNumber, bool matches)
    {
        var local = Item(localNumber, "Local");
        var remote = Item(remoteNumber, "Remote");
        foreach (var preferLocal in new[] { false, true })
        {
            var merged = preferLocal
                ? ApiCatalogItemMerger.MergeMissing(local, remote)
                : ApiCatalogItemMerger.Merge(local, remote);
            Assert.Equal(matches ? 1 : 2, merged.Units.Count);
            if (matches)
            {
                Assert.Equal(localNumber, merged.Units[0].DisplayNumber);
                Assert.Equal(preferLocal ? "Local" : "Remote", merged.Units[0].Name);
            }
        }
    }

    [Fact]
    public void NewEquivalentChaptersCoalesceWithinEachSeasonWithoutCrossingSeasons()
    {
        var empty = Item("1", "Local") with { Units = [] };
        var chapters = new[] { Item("01", "First").Units[0], Item("1.0", "Last").Units[0] };
        var remote = empty with
        {
            Seasons = [new CatalogTransferSeason { Number = 1, SortOrder = 0, Units = chapters },
                new CatalogTransferSeason { Number = 2, SortOrder = 1, Units = chapters }]
        };
        // New seasons are copied whole; merge existing seasons exercises the
        // chapter index in each season independently.
        var local = empty with { Seasons = remote.Seasons.Select(season => season with { Units = [] }).ToArray() };
        foreach (var merged in new[] { ApiCatalogItemMerger.Merge(local, remote), ApiCatalogItemMerger.MergeMissing(local, remote) })
        {
            Assert.Equal(2, merged.Seasons.Count);
            Assert.All(merged.Seasons, season => Assert.Equal("01", Assert.Single(season.Units).DisplayNumber));
        }
    }

    private static CatalogTransferItem Item(string number, string name) => new()
    {
        MediaType = "book",
        Subtype = "manga",
        Name = "Same title",
        Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = number, SortOrder = 0, Name = name }]
    };
}

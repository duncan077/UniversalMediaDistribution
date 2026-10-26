using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Metadata.Tests;

public sealed class MetadataMatchSelectorTests
{
    [Fact]
    public void SelectBest_UsesExactAlternativeTitleAndReleaseYear()
    {
        var details = MetadataTestData.Details(
            MediaType.Movie,
            "movie.movie",
            "The Local Title",
            releaseDate: "2021-05-04",
            alternativeNames:
            [
                new CatalogAlternativeName("Provider Title", "en", "translation")
            ]);
        MetadataSearchCandidate[] candidates =
        [
            new(10, "Provider Title", null, "1999-01-01", []),
            new(20, "Provider Title", null, "2021-08-10", [])
        ];

        var result = MetadataMatchSelector.SelectBest(details, candidates, "Test");

        Assert.Equal(20, result.Id);
    }

    [Fact]
    public void SelectBest_RejectsFuzzyTitle()
    {
        var details = MetadataTestData.Details(
            MediaType.Movie,
            "movie.movie",
            "A Quiet Place");
        MetadataSearchCandidate[] candidates =
        [
            new(10, "A Quieter Place", null, "2024-01-01", [])
        ];

        var exception = Assert.Throws<InvalidOperationException>(
            () => MetadataMatchSelector.SelectBest(details, candidates, "Test"));

        Assert.Contains("no exact title", exception.Message);
    }

    [Fact]
    public void SelectBest_RejectsEquallyLikelyDuplicates()
    {
        var details = MetadataTestData.Details(
            MediaType.Movie,
            "movie.movie",
            "Shared Title");
        MetadataSearchCandidate[] candidates =
        [
            new(10, "Shared Title", null, "2020-01-01", []),
            new(20, "Shared Title", null, "2021-01-01", [])
        ];

        var exception = Assert.Throws<InvalidOperationException>(
            () => MetadataMatchSelector.SelectBest(details, candidates, "Test"));

        Assert.Contains("multiple equally likely", exception.Message);
    }
}

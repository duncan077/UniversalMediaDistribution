using UniversalMediaDistribution.UI.ViewModels;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public class SearchResultGroupingTests
{
    [Theory]
    [InlineData("book.novel", "book.manga", "Same title")]
    [InlineData("book.web_novel", "book.manga", "Same-title")]
    [InlineData("book.manga", "book.comic", "Same title")]
    public void SameNameInDifferentBookCategoriesProducesSeparateCards(string first, string second, string secondName)
    {
        var groups = SearchResultGrouping.Group([Card(first, "Same title"), Card(second, secondName)]);
        Assert.Equal(2, groups.Count);
        Assert.All(groups, group => Assert.Single(group.Members));
    }

    [Fact]
    public void SameBookCategoryStillGroupsSpellingVariants()
    {
        Assert.Single(SearchResultGrouping.Group([Card("book.novel", "Same title"), Card("book.novel", "Same-title")]));
    }

    private static ContentSourceCatalogSearchResult Card(string subtype, string name) =>
        ContentSourceCatalogSearchResult.Local(new CatalogCard(
            Guid.NewGuid(), MediaType.Book, subtype, name, "", null, [], [], [], []));

    [Theory]
    // Spelling, punctuation and spacing are what providers actually disagree
    // on, and collapsing those is the whole point of grouping.
    [InlineData("Spider-Man", "Spiderman")]
    [InlineData("Halo: Nightfall", "Halo Nightfall")]
    [InlineData("El Diablo Viste a la Moda", "El diablo viste a la moda")]
    [InlineData("Avatar - The Way of Water", "Avatar: The Way of Water")]
    [InlineData("Iron Man 2", "Iron-Man 2")]
    public void MergesTitlesThatOnlyDifferInSpelling(string left, string right)
    {
        Assert.True(SearchResultGrouping.IsSameTitle(left, right));
    }

    [Theory]
    // A sequel differs from its original by a number and almost nothing else,
    // so edit distance alone rates them near-identical on a long title. These
    // are the cases that must never collapse: hiding a distinct film behind
    // another is far worse than showing two cards.
    [InlineData("Dune", "Dune: Part Two")]
    [InlineData("El diablo viste a la moda", "El diablo viste a la moda 2")]
    [InlineData("Iron Man 2", "Iron Man 3")]
    [InlineData("The Matrix", "The Matrix Reloaded")]
    [InlineData("Avatar", "Avatar: The Way of Water")]
    [InlineData("Chesapeake Shores", "Chesapeake Shores 2016")]
    public void KeepsDistinctWorksApart(string left, string right)
    {
        Assert.False(SearchResultGrouping.IsSameTitle(left, right));
    }

    [Fact]
    public void ContainmentAloneDoesNotMerge()
    {
        // LevenshteinTitleMatcher.Similarity answers 1 whenever one title
        // contains the other, which is right for binding a provider title to a
        // catalogued one and wrong here. Grouping must not inherit it.
        Assert.False(SearchResultGrouping.IsSameTitle("Dune", "Dune Part Two"));
    }

    [Theory]
    [InlineData("", "Dune")]
    [InlineData("Dune", "")]
    [InlineData("   ", "Dune")]
    public void RefusesToMatchOnAnEmptyTitle(string left, string right)
    {
        Assert.False(SearchResultGrouping.IsSameTitle(left, right));
    }

    [Fact]
    public void IsCaseAndAccentInsensitive()
    {
        Assert.True(
            SearchResultGrouping.IsSameTitle(
                "EL DÍA DE LA REVELACIÓN",
                "El dia de la revelacion"));
    }
}

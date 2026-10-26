using UniversalMediaDistribution.Application.Catalog;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class AdultContentCategoryFilterTests
{
    [Theory]
    [InlineData("adult")]
    [InlineData(" ADULT ")]
    [InlineData("porn")]
    [InlineData("+18")]
    [InlineData("18+")]
    public void RecognizesBlockedAdultCategories(string category) =>
        Assert.True(AdultContentCategoryFilter.Matches([category]));

    [Fact]
    public void RecognizesBlockedAdultGenresWhenCategoriesAreSafe() =>
        Assert.True(AdultContentCategoryFilter.Matches(
            ["Anime"],
            ["Adult"]));

    [Theory]
    [InlineData("Manga")]
    [InlineData("Drama")]
    [InlineData("Adult Animation")]
    [InlineData("Erotic")]
    public void DoesNotBlockOtherCategories(string category) =>
        Assert.False(AdultContentCategoryFilter.Matches([category]));

    [Fact]
    public void EmptyOrMissingCategoriesAreAllowed()
    {
        Assert.False(AdultContentCategoryFilter.Matches([]));
        Assert.False(AdultContentCategoryFilter.Matches(null));
        Assert.False(AdultContentCategoryFilter.Matches(null, null));
    }
}

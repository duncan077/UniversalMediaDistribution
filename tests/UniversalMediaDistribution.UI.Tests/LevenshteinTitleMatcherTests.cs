using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class LevenshteinTitleMatcherTests
{
    [Theory]
    [InlineData(
        "Hidden Logistics Center",
        "Hidden Logistics Center in the Apocalypse")]
    [InlineData(
        "Hidden Logistics Center in the Apocalypse",
        "Hidden Logistics Center")]
    [InlineData(
        "hidden-logistics-center",
        "Hidden Logistics Center in the Apocalypse")]
    public void SimilarityAcceptsEitherTitleContainingTheOther(
        string left,
        string right)
    {
        Assert.Equal(1, LevenshteinTitleMatcher.Similarity(left, right));
    }

    [Theory]
    [InlineData("Avatar", "Avatar: La Leyenda de Korra")]
    [InlineData("Avatar: La Leyenda de Korra", "Avatar")]
    [InlineData("Avatar", "Avatar: The Legend of Korra")]
    [InlineData("Naruto", "Naruto Shippuden")]
    [InlineData("Bleach", "Bleach: Sennen Kessen-hen")]
    public void ATitleThatMerelyStartsTheSameIsNotTheSameWork(
        string left,
        string right)
    {
        // Containment used to score a flat 1, so a spin-off whose name begins
        // with the parent series' name was a perfect match for it.
        Assert.True(
            LevenshteinTitleMatcher.Similarity(left, right) <
            LevenshteinTitleMatcher.DefaultMinimumSimilarity,
            $"'{left}' and '{right}' should not match as the same title.");
    }

    [Theory]
    [InlineData("One Piece", "One Piece")]
    [InlineData("Tensei Shitara Slime Datta Ken", "Tensei Shitara Slime Datta Ken (2018)")]
    [InlineData("Mi Prometida es Muy Mala", "¡Mi Prometida es Muy Mala!")]
    public void TheSameWorkWrittenMoreFullyStillMatches(
        string left,
        string right)
    {
        Assert.True(
            LevenshteinTitleMatcher.Similarity(left, right) >=
            LevenshteinTitleMatcher.DefaultMinimumSimilarity,
            $"'{left}' and '{right}' should match as the same title.");
    }

    [Theory]
    [InlineData("kitten", "sitting", 3)]
    [InlineData("sitting", "kitten", 3)]
    [InlineData("", "three", 5)]
    public void DistanceUsesTheSameResultInEitherDirection(
        string left,
        string right,
        int expected)
    {
        Assert.Equal(
            expected,
            LevenshteinTitleMatcher.Distance(left, right));
    }

    [Fact]
    public void DistanceHandlesTitlesLargerThanTheStackBuffer()
    {
        var left = new string('a', 400);
        var right = left[..399] + "b";

        Assert.Equal(1, LevenshteinTitleMatcher.Distance(left, right));
    }
}

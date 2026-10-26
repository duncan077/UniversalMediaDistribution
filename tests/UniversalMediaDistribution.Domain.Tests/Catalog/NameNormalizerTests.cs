using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Domain.Tests.Catalog;

public sealed class NameNormalizerTests
{
    [Theory]
    [InlineData("Dune", "DUNE")]
    [InlineData("  Copper   Skies  ", "COPPER SKIES")]
    [InlineData("Manga\tTitle", "MANGA TITLE")]
    [InlineData("Cafe\u0301", "CAFÉ")]
    public void Normalize_ProducesStableExactMatchKey(string input, string expected)
    {
        var result = NameNormalizer.Normalize(input);

        Assert.Equal(expected, result);
    }

    [Fact]
    public void Normalize_RejectsWhitespaceOnlyName()
    {
        Assert.Throws<ArgumentException>(() => NameNormalizer.Normalize(" \t\r\n "));
    }
}

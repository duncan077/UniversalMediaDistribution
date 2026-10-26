using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A novel and its comic adaptation share a media type and usually a name, but
/// they are different works with different chapters. Treating them as one
/// entry merged their chapter lists together.
/// </summary>
public sealed class NovelAndComicAreSeparateWorksTests
{
    [Theory]
    [InlineData("novel", "manga")]
    [InlineData("novel", "comic")]
    [InlineData("web_novel", "manga")]
    [InlineData("light_novel", "comic")]
    [InlineData("manga", "comic")]
    public void ProseAndComicsWithTheSameNameDoNotConverge(
        string leftSubtype,
        string rightSubtype)
    {
        var left = Book("Shadow Slave", leftSubtype);
        var right = Book("Shadow Slave", rightSubtype);

        Assert.False(CatalogTransferIdentity.AreCompatible(left, right));
        Assert.Null(
            CatalogTransferIdentity.FindBestMatch([left], right));
    }

    [Theory]
    [InlineData("manga")]
    [InlineData("novel")]
    public void TheSameWorkStillConvergesWithItself(string subtype)
    {
        var existing = Book("Shadow Slave", subtype);
        var candidate = Book("Shadow Slave", subtype);

        Assert.True(CatalogTransferIdentity.AreCompatible(existing, candidate));
        Assert.NotNull(
            CatalogTransferIdentity.FindBestMatch([existing], candidate));
    }

    [Fact]
    public void AComicDoesNotAbsorbTheNovelWhenBothAreAlreadyStored()
    {
        // The catalog can legitimately hold both, so a lookup across them must
        // pick the matching subtype rather than whichever was stored first.
        var novel = Book("Shadow Slave", "novel");
        var manga = Book("Shadow Slave", "manga");

        var match = CatalogTransferIdentity.FindBestMatch(
            [novel, manga],
            Book("Shadow Slave", "manga"));

        Assert.NotNull(match);
        Assert.Equal("manga", match.Subtype);
    }

    private static CatalogTransferItem Book(string name, string subtype) =>
        new()
        {
            MediaType = "book",
            Subtype = subtype,
            Name = name
        };
}

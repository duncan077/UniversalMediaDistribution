using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ReaderPageRetentionTests
{
    private const int PerSide = 4;

    [Fact]
    public void TheCurrentPageAndItsNeighboursAreKept()
    {
        foreach (var index in Enumerable.Range(46, 9))
        {
            Assert.True(Retain(index, center: 50, pageCount: 300));
        }
    }

    [Fact]
    public void PagesOutsideTheWindowAreReleased()
    {
        Assert.False(Retain(45, center: 50, pageCount: 300));
        Assert.False(Retain(55, center: 50, pageCount: 300));
        Assert.False(Retain(0, center: 50, pageCount: 300));
        Assert.False(Retain(299, center: 50, pageCount: 300));
    }

    [Fact]
    public void AChapterThatFitsInTheWindowKeepsEveryPage()
    {
        var pageCount = (PerSide * 2) + 1;
        foreach (var index in Enumerable.Range(0, pageCount))
        {
            Assert.True(Retain(index, center: 0, pageCount: pageCount));
        }
    }

    [Fact]
    public void TheWindowIsNotShiftedAtTheStartOrEndOfAChapter()
    {
        // Near the first page the window simply runs off the front; it is not
        // slid forward, because the reader can go backwards too.
        Assert.True(Retain(0, center: 0, pageCount: 300));
        Assert.True(Retain(4, center: 0, pageCount: 300));
        Assert.False(Retain(5, center: 0, pageCount: 300));
        Assert.True(Retain(299, center: 299, pageCount: 300));
        Assert.True(Retain(295, center: 299, pageCount: 300));
        Assert.False(Retain(294, center: 299, pageCount: 300));
    }

    [Fact]
    public void AtMostNinePagesStayDecodedHoweverLongTheChapterIs()
    {
        var retained = Enumerable
            .Range(0, 1000)
            .Count(index => Retain(index, center: 500, pageCount: 1000));

        Assert.Equal((PerSide * 2) + 1, retained);
    }

    [Fact]
    public void ANegativeWindowIsRejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(
            () => ReaderPageRetention.ShouldRetain(0, 0, 10, -1));

    private static bool Retain(int index, int center, int pageCount) =>
        ReaderPageRetention.ShouldRetain(index, center, pageCount, PerSide);
}

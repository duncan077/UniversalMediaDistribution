using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogPaginationTests
{
    [Fact]
    public void DefaultPageSizeIsTwentyFive()
    {
        Assert.Equal(25, CatalogPagination.DefaultPageSize);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(25, 1)]
    [InlineData(26, 2)]
    [InlineData(300, 12)]
    [InlineData(301, 13)]
    public void PageCountCoversAllItems(int itemCount, int expected)
    {
        Assert.Equal(expected, CatalogPagination.GetPageCount(itemCount));
    }

    [Fact]
    public void GetPageReturnsOnlyTheRequestedSlice()
    {
        var items = Enumerable.Range(1, 60).ToArray();

        var page = CatalogPagination.GetPage(items, 2);

        Assert.Equal(Enumerable.Range(26, 25), page);
    }

    [Fact]
    public void GetPageClampsPastTheLastPage()
    {
        var items = Enumerable.Range(1, 52).ToArray();

        var page = CatalogPagination.GetPage(items, 99);

        Assert.Equal([51, 52], page);
    }
}

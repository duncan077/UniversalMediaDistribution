using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ImageReaderViewModelTests
{
    [Fact]
    public async Task PageNavigationStateTracksTheCurrentPage()
    {
        var itemId = Guid.NewGuid();
        var itemIdentity = new CatalogContentIdentity(
            itemId,
            $"book:{itemId:N}");
        var chapterIdentity = new CatalogContentIdentity(
            itemId,
            $"book:{itemId:N}:chapter:1",
            ContentUnitKind.Chapter,
            "1");
        using var reader = await ImageReaderViewModel.CreateAsync(
            "Chapter 1",
            chapterIdentity,
            itemIdentity,
            new ApiContentImageSet(
                ContentUnitKind.Chapter,
                "1",
                seasonNumber: null,
                [
                    new ApiContentImagePage(
                        0,
                        new Uri("https://images.example/1.webp")),
                    new ApiContentImagePage(
                        1,
                        new Uri("https://images.example/2.webp"))
                ]),
            new InMemoryUserMediaStateStore());

        Assert.False(reader.CanGoToPreviousPage);
        Assert.True(reader.CanGoToNextPage);
        Assert.False(reader.IsLastPage);

        await reader.NextPageCommand.ExecuteAsync(null);

        Assert.True(reader.CanGoToPreviousPage);
        Assert.False(reader.CanGoToNextPage);
        Assert.True(reader.IsLastPage);
    }

    [Fact]
    public async Task ZoomAppliesOneUniformWidthToEveryPage()
    {
        var itemId = Guid.NewGuid();
        var itemIdentity = new CatalogContentIdentity(
            itemId,
            $"book:{itemId:N}");
        var chapterIdentity = new CatalogContentIdentity(
            itemId,
            $"book:{itemId:N}:chapter:1",
            ContentUnitKind.Chapter,
            "1");
        using var reader = await ImageReaderViewModel.CreateAsync(
            "Chapter 1",
            chapterIdentity,
            itemIdentity,
            new ApiContentImageSet(
                ContentUnitKind.Chapter,
                "1",
                seasonNumber: null,
                [
                    new ApiContentImagePage(
                        0,
                        new Uri("https://images.example/narrow.webp")),
                    new ApiContentImagePage(
                        1,
                        new Uri("https://images.example/wide.webp"))
                ]),
            new InMemoryUserMediaStateStore());

        reader.SetViewportWidth(800);

        Assert.All(
            reader.Pages,
            page => Assert.Equal(800, page.DisplayWidth));

        reader.ZoomInCommand.Execute(null);

        Assert.Equal("110%", reader.ZoomPercentage);
        Assert.All(
            reader.Pages,
            page => Assert.Equal(880, page.DisplayWidth));

        reader.ResetZoomCommand.Execute(null);

        Assert.Equal("100%", reader.ZoomPercentage);
        Assert.All(
            reader.Pages,
            page => Assert.Equal(800, page.DisplayWidth));
    }
}

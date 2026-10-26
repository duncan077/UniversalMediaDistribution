using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogContentEntryProviderTests
{
    [Fact]
    public async Task ImageSelectionCarriesItsApiProviderIntoTheReader()
    {
        var catalogItemId = Guid.CreateVersion7();
        var itemIdentity = new CatalogContentIdentity(
            catalogItemId,
            "item");
        var chapterIdentity = new CatalogContentIdentity(
            catalogItemId,
            "chapter:1",
            ContentUnitKind.Chapter,
            "1");
        var imageSet = new ApiContentImageSet(
            ContentUnitKind.Chapter,
            "1",
            seasonNumber: null,
            [new ApiContentImagePage(
                0,
                new Uri("https://reader.example.test/page-1.webp"))]);
        ApiContentImageSelection? openedSelection = null;
        var entry = new CatalogContentEntryViewModel(
            "Chapter 1",
            subheading: null,
            thumbnailRef: null,
            chapterIdentity,
            itemIdentity,
            MediaType.Book,
            sources: [],
            new TorrentMediaSessionService(engine: null),
            openAsync: static _ => Task.CompletedTask,
            apiImageSet: imageSet,
            openApiImagesAsync: selection =>
            {
                openedSelection = selection;
                return Task.CompletedTask;
            },
            apiContentProviderId: "current-reader");

        await Assert.IsAssignableFrom<
                CommunityToolkit.Mvvm.Input.IAsyncRelayCommand>(
                entry.OpenApiImagesCommand)
            .ExecuteAsync(null);

        Assert.Equal(
            "current-reader",
            Assert.IsType<ApiContentImageSelection>(openedSelection).ProviderId);
    }
}

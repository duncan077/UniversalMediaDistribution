using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The in-row provider picker used to appear only once a provider had already
/// answered, so pressing "find on enabled APIs" looked like it did nothing
/// while the search ran.
/// </summary>
public sealed class ProviderSearchIndicatorTests
{
    [Fact]
    public void TheRowShowsProgressBeforeAnyProviderHasAnswered()
    {
        var entry = CreateEntry();

        Assert.False(entry.IsProviderPickerVisible);

        entry.BeginProviderSearch("Searching…", static () => { });

        Assert.True(entry.IsProviderPickerVisible);
        Assert.True(entry.IsProviderSearchRunning);
        Assert.False(entry.HasProviderChoices);
        Assert.Equal("Searching…", entry.ProviderChoiceMessage);
    }

    [Fact]
    public void ThePickerStaysUpWithResultsOnceTheSearchStops()
    {
        var entry = CreateEntry();
        entry.BeginProviderSearch("Searching…", static () => { });

        entry.ShowProviderChoices(
            "1 provider found this entry.",
            [
                new ApiContentProviderOptionViewModel(
                    "ZonaTMO",
                    0.92,
                    static () => Task.CompletedTask)
            ],
            static () => { });
        entry.EndProviderSearch();

        Assert.False(entry.IsProviderSearchRunning);
        Assert.True(entry.HasProviderChoices);
        Assert.True(entry.IsProviderPickerVisible);
    }

    [Fact]
    public void AFinishedSearchThatFoundNothingLeavesNoPickerBehind()
    {
        var entry = CreateEntry();
        entry.BeginProviderSearch("Searching…", static () => { });

        entry.EndProviderSearch();

        Assert.False(entry.IsProviderSearchRunning);
        Assert.False(entry.IsProviderPickerVisible);
    }

    [Fact]
    public void CancellingClearsTheRunningStateSoTheRowDoesNotSpinForever()
    {
        var entry = CreateEntry();
        entry.BeginProviderSearch("Searching…", static () => { });

        entry.ClearProviderChoices();

        Assert.False(entry.IsProviderSearchRunning);
        Assert.False(entry.IsProviderPickerVisible);
    }

    private static CatalogContentEntryViewModel CreateEntry()
    {
        var catalogItemId = Guid.CreateVersion7();
        return new CatalogContentEntryViewModel(
            "Episode 1",
            subheading: null,
            thumbnailRef: null,
            new CatalogContentIdentity(
                catalogItemId,
                "episode:1",
                ContentUnitKind.Episode,
                "1"),
            new CatalogContentIdentity(catalogItemId, "item"),
            MediaType.Movie,
            sources: [],
            new TorrentMediaSessionService(engine: null),
            openAsync: static _ => Task.CompletedTask);
    }
}

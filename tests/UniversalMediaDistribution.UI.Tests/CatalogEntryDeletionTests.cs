using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.Torrenting.Ingestion;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogEntryDeletionTests
{
    [Fact]
    public async Task RequestAndCancelNeverDeleteAndConfirmationPassesTheExactIdentity()
    {
        var entry = CreateEntry(ContentUnitKind.Episode);
        CatalogContentIdentity? deleted = null;
        entry.ConfigureDeletion(identity => { deleted = identity; return Task.FromResult(true); });
        Assert.True(entry.CanDelete);
        await entry.ConfirmDeleteCommand!.ExecuteAsync(null);
        Assert.Null(deleted);
        entry.RequestDeleteCommand!.Execute(null);
        Assert.True(entry.IsDeleteConfirmationVisible);
        Assert.Null(deleted);
        entry.CancelDeleteCommand!.Execute(null);
        await entry.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Null(deleted);
        entry.RequestDeleteCommand.Execute(null);
        await entry.ConfirmDeleteCommand.ExecuteAsync(null);
        Assert.Same(entry.Identity, deleted);
        Assert.False(entry.IsDeleteConfirmationVisible);
        Assert.False(entry.IsDeleting);
    }

    [Fact]
    public async Task FailedDeletionKeepsConfirmationAvailableForRetry()
    {
        var entry = CreateEntry(ContentUnitKind.Chapter);
        var result = new TaskCompletionSource<bool>();
        entry.ConfigureDeletion(_ => result.Task);
        entry.RequestDeleteCommand!.Execute(null);
        var pending = entry.ConfirmDeleteCommand!.ExecuteAsync(null);
        Assert.True(entry.IsDeleting);
        Assert.False(entry.ConfirmDeleteCommand.CanExecute(null));
        result.SetResult(false);
        await pending;
        Assert.True(entry.IsDeleteConfirmationVisible);
        Assert.False(entry.IsDeleting);
    }

    [Theory]
    [InlineData(null)]
    [InlineData(ContentUnitKind.Track)]
    [InlineData(ContentUnitKind.AudioChapter)]
    public void WholeReleasesAndOtherUnitKindsDoNotExposeChapterDeletion(ContentUnitKind? kind)
    {
        var entry = CreateEntry(kind);
        entry.ConfigureDeletion(_ => throw new InvalidOperationException());
        Assert.False(entry.CanDelete);
        Assert.Null(entry.ConfirmDeleteCommand);
    }

    private static CatalogContentEntryViewModel CreateEntry(ContentUnitKind? kind)
    {
        var itemId = Guid.NewGuid();
        return new CatalogContentEntryViewModel("Entry 1", null, null,
            new CatalogContentIdentity(itemId, "entry", kind, kind is null ? null : "1", kind == ContentUnitKind.Episode ? 2 : null),
            new CatalogContentIdentity(itemId, "item"), MediaType.Book, [],
            new TorrentMediaSessionService(null), _ => Task.CompletedTask);
    }
}

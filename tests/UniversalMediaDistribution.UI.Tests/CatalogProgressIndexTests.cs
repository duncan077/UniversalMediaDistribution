using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class CatalogProgressIndexTests
{
    [Fact]
    public void JustViewedTitleAppearsInRecentButNotResumeIndex()
    {
        var itemId = Guid.NewGuid();
        var viewed = Summary(
            itemId,
            "item",
            position: 0,
            isCompleted: false,
            DateTimeOffset.UtcNow);

        var recent = CatalogProgressIndex.CreateRecent([viewed]);
        var resumable = CatalogProgressIndex.CreateResumable([viewed]);

        Assert.Same(viewed, recent[itemId]);
        Assert.Empty(resumable);
    }

    [Fact]
    public void RecentViewDoesNotHideOlderResumableChapter()
    {
        var itemId = Guid.NewGuid();
        var chapter = Summary(
            itemId,
            "chapter:4",
            position: 12,
            isCompleted: false,
            DateTimeOffset.UtcNow.AddMinutes(-1));
        var viewed = Summary(
            itemId,
            "item",
            position: 0,
            isCompleted: false,
            DateTimeOffset.UtcNow);

        var recent = CatalogProgressIndex.CreateRecent([chapter, viewed]);
        var resumable =
            CatalogProgressIndex.CreateResumable([chapter, viewed]);

        Assert.Same(viewed, recent[itemId]);
        Assert.Same(chapter, resumable[itemId]);
    }

    private static CatalogProgressSummary Summary(
        Guid itemId,
        string suffix,
        double position,
        bool isCompleted,
        DateTimeOffset updatedAt) =>
        new(
            new CatalogContentIdentity(
                itemId,
                $"{itemId:N}:{suffix}",
                suffix == "item"
                    ? null
                    : ContentUnitKind.Chapter,
                suffix == "item"
                    ? null
                    : "4"),
            new MediaProgressState(
                position,
                MediaPositionKind.Page,
                isCompleted,
                updatedAt));
}

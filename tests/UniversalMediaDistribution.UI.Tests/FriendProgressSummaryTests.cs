using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The line the item page shows for a friend.
/// <para>
/// The trap here is <c>IsCompleted</c>. On an entry that names a unit it means
/// that unit was finished — the reader sets it on the last page of the chapter
/// in hand — so it must never be read as having finished the title.
/// </para>
/// </summary>
public sealed class FriendProgressSummaryTests
{
    /// <summary>
    /// Someone who has read one chapter of a long manga has finished that
    /// chapter, and saying they finished the manga is wrong.
    /// </summary>
    [Fact]
    public void FinishingAChapterIsNotFinishingTheTitle()
    {
        var summary = CreateSummary(
            unitKind: "chapter",
            displayNumber: "1",
            isCompleted: true);

        Assert.Equal("Pototo read up to chapter 1", summary);
    }

    [Fact]
    public void AChapterInProgressReadsTheSameWay()
    {
        var summary = CreateSummary(
            unitKind: "chapter",
            displayNumber: "75",
            isCompleted: false);

        Assert.Equal("Pototo read up to chapter 75", summary);
    }

    [Fact]
    public void AnEpisodeIsPhrasedAsWatching()
    {
        var summary = CreateSummary(
            unitKind: "episode",
            displayNumber: "4",
            isCompleted: false);

        Assert.Equal("Pototo watched up to episode 4", summary);
    }

    [Fact]
    public void AnEpisodeCarriesItsSeasonWhenThereIsOne()
    {
        var summary = CreateSummary(
            unitKind: "episode",
            displayNumber: "4",
            isCompleted: false,
            seasonNumber: 2);

        Assert.Equal("Pototo watched up to season 2, episode 4", summary);
    }

    /// <summary>
    /// Progress with no unit is the whole title — a film — and there the flag
    /// does mean finished.
    /// </summary>
    [Fact]
    public void AFinishedFilmIsReportedAsFinished()
    {
        var summary = CreateSummary(
            unitKind: null,
            displayNumber: null,
            isCompleted: true);

        Assert.Equal("Pototo finished this", summary);
    }

    [Fact]
    public void AnUnfinishedFilmIsReportedAsStarted()
    {
        var summary = CreateSummary(
            unitKind: null,
            displayNumber: null,
            isCompleted: false);

        Assert.Equal("Pototo has started this", summary);
    }

    private static string CreateSummary(
        string? unitKind,
        string? displayNumber,
        bool isCompleted,
        int? seasonNumber = null) =>
        new FriendProgressViewModel(
            new RemoteFriendProgress(
                Guid.CreateVersion7(),
                "Pototo",
                ContentItemKey.Create(MediaType.Book, "Gunnm"),
                unitKind,
                displayNumber,
                seasonNumber,
                isCompleted,
                DateTimeOffset.UtcNow)).Summary;
}

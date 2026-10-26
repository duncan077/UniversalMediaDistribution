using UniversalMediaDistribution.UI.Remote;
using UniversalMediaDistribution.UI.ViewModels;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A title a friend has and this device does not: the row can only be a name
/// and a way to go looking for it, and where it looks has to match what it is.
/// </summary>
public sealed class FriendTitleSearchTests
{
    [Fact]
    public void TheMediaTypeComesFromTheKeyAndTheSubtypeRidesAlong()
    {
        var manga = Create("book:LA VOLUNTAD", "La voluntad", "manga");
        var film = Create("movie:CAMP ROCK 3", "Camp Rock 3", "movie");

        Assert.Equal("book", manga.MediaType);
        Assert.Equal("manga", manga.Subtype);
        Assert.Equal("movie", film.MediaType);
        Assert.Equal("movie", film.Subtype);
    }

    [Fact]
    public void AKeyIsNeverShownRaw()
    {
        // A friend whose device has not published since titles began
        // travelling leaves only the key, which is the name in upper case
        // behind a prefix. The prefix is not part of the name.
        var title = Create("movie:A GREAT AWAKENING", "movie:A GREAT AWAKENING");

        Assert.Equal("A GREAT AWAKENING", title.Title);
    }

    [Fact]
    public async Task ClickingTheNameStartsTheSearchForIt()
    {
        FriendTitleViewModel? searched = null;
        var title = Create(
            "book:LA VOLUNTAD",
            "La voluntad",
            "manga",
            clicked =>
            {
                searched = clicked;
                return Task.CompletedTask;
            });

        Assert.True(title.CanSearch);
        await title.SearchCommand.ExecuteAsync(null);

        Assert.Same(title, searched);
    }

    [Fact]
    public void WithNowhereToSearchTheNameIsNotPressable()
    {
        var title = Create("book:LA VOLUNTAD", "La voluntad", "manga");

        Assert.False(title.CanSearch);
    }

    private static FriendTitleViewModel Create(
        string itemKey,
        string title,
        string? subtype = null,
        Func<FriendTitleViewModel, Task>? searchAsync = null) =>
        new(
            new RemoteFriendComparisonTitle(
                itemKey,
                title,
                ViewerFavorite: false,
                FriendFavorite: true,
                ViewerSeen: false,
                FriendSeen: true,
                UnitKind: "chapter",
                DisplayNumber: "12",
                SeasonNumber: null,
                IsCompleted: false,
                DateTimeOffset.UnixEpoch,
                subtype),
            searchAsync);
}

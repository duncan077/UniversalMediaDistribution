using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

/// <summary>
/// Two libraries side by side: what both accounts have, what only the friend
/// has, and what only the asking account has — with favourites counted, since
/// "we both like this" is the part worth reading first.
/// </summary>
public sealed class FriendComparisonTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "umd-friend-compare-" + Guid.CreateVersion7().ToString("N"));

    [Fact]
    public async Task ComparingBucketsSharedTheirsAndMine()
    {
        var (store, mine, theirs) = await CreateFriendsAsync();

        // They: favourite Both, progress in Both and TheirsOnly.
        await PublishAsync(
            store,
            theirs,
            favorites: ["Both"],
            progress: ["Both", "TheirsOnly"]);
        var viewer = CreateSnapshot(
            favorites: ["Both"],
            progress: ["Both", "MineOnly"]);

        var comparison = await store.CompareAsync(
            mine.AccountId,
            theirs.AccountId,
            viewer,
            CancellationToken.None);

        Assert.Equal(
            ["Both"],
            comparison.InCommon.Select(static t => t.Title));
        Assert.Equal(
            ["TheirsOnly"],
            comparison.FriendOnly.Select(static t => t.Title));
        Assert.Equal(
            ["MineOnly"],
            comparison.ViewerOnly.Select(static t => t.Title));
    }

    [Fact]
    public async Task AFavoriteNeitherHasWatchedIsNotSomethingInCommon()
    {
        var (store, mine, theirs) = await CreateFriendsAsync();

        // Both marked it and neither started it. Wanting the same thing is
        // not having seen the same thing, and only the latter counts.
        await PublishAsync(store, theirs, favorites: ["Both"], progress: []);
        var viewer = CreateSnapshot(favorites: ["Both"], progress: []);

        var comparison = await store.CompareAsync(
            mine.AccountId,
            theirs.AccountId,
            viewer,
            CancellationToken.None);

        Assert.Empty(comparison.InCommon);
        Assert.Empty(comparison.FriendOnly);
        Assert.Empty(comparison.ViewerOnly);
    }

    [Fact]
    public async Task AFavoriteBothHaveWatchedIsMarkedOnBothSides()
    {
        var (store, mine, theirs) = await CreateFriendsAsync();
        await PublishAsync(
            store,
            theirs,
            favorites: ["Both"],
            progress: ["Both"]);
        var viewer = CreateSnapshot(
            favorites: ["Both"],
            progress: ["Both"]);

        var comparison = await store.CompareAsync(
            mine.AccountId,
            theirs.AccountId,
            viewer,
            CancellationToken.None);

        var shared = Assert.Single(comparison.InCommon);
        Assert.True(shared.ViewerFavorite);
        Assert.True(shared.FriendFavorite);
        Assert.True(shared.ViewerSeen);
        Assert.True(shared.FriendSeen);
    }

    [Fact]
    public async Task ATitleOnlyOpenedDoesNotCountAsSeen()
    {
        // Opening a details page writes a position of zero so the title shows
        // on the recent shelf. Neither side has watched anything, so there is
        // nothing to compare.
        var (store, mine, theirs) = await CreateFriendsAsync();
        await PublishAsync(
            store,
            theirs,
            favorites: [],
            progress: [],
            opened: ["Peeked"]);
        var viewer = CreateSnapshot(
            favorites: [],
            progress: [],
            opened: ["Peeked"]);

        var comparison = await store.CompareAsync(
            mine.AccountId,
            theirs.AccountId,
            viewer,
            CancellationToken.None);

        Assert.Empty(comparison.InCommon);
        Assert.Empty(comparison.FriendOnly);
        Assert.Empty(comparison.ViewerOnly);
    }

    [Fact]
    public async Task AHiddenTitleStaysOutOfTheComparison()
    {
        var (store, mine, theirs) = await CreateFriendsAsync();
        await store.SetVisibilityAsync(
            theirs.AccountId,
            new FriendVisibilityUpdateRequest(true, true, ["book:SECRET"]),
            CancellationToken.None);
        await PublishAsync(
            store,
            theirs,
            favorites: ["Secret"],
            progress: ["Secret"]);
        var viewer = CreateSnapshot(favorites: [], progress: []);

        var comparison = await store.CompareAsync(
            mine.AccountId,
            theirs.AccountId,
            viewer,
            CancellationToken.None);

        Assert.Empty(comparison.InCommon);
        Assert.Empty(comparison.FriendOnly);
    }

    [Fact]
    public async Task AFriendWhoSharesNothingComparesToNothing()
    {
        var (store, mine, theirs) = await CreateFriendsAsync();
        await PublishAsync(
            store,
            theirs,
            favorites: ["Both"],
            progress: ["Both"]);
        await store.SetFriendSharingAsync(
            theirs.AccountId,
            mine.AccountId,
            shareProgress: false,
            CancellationToken.None);
        var viewer = CreateSnapshot(favorites: ["Both"], progress: ["Both"]);

        var comparison = await store.CompareAsync(
            mine.AccountId,
            theirs.AccountId,
            viewer,
            CancellationToken.None);

        Assert.Empty(comparison.InCommon);
        Assert.Empty(comparison.FriendOnly);
        Assert.Empty(comparison.ViewerOnly);
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private async Task<(SqliteFriendStore Store, AccountRecord Mine,
        AccountRecord Theirs)> CreateFriendsAsync()
    {
        var store = new SqliteFriendStore(_directory);
        var mine = CreateAccount("mine");
        var theirs = CreateAccount("theirs");
        await store.SendRequestAsync(mine, theirs, CancellationToken.None);
        await store.AcceptRequestAsync(theirs, mine, CancellationToken.None);
        await store.SetVisibilityAsync(
            theirs.AccountId,
            new FriendVisibilityUpdateRequest(true, true, []),
            CancellationToken.None);
        return (store, mine, theirs);
    }

    private static async Task PublishAsync(
        SqliteFriendStore store,
        AccountRecord account,
        string[] favorites,
        string[] progress,
        string[]? opened = null)
    {
        var snapshot = CreateSnapshot(favorites, progress, opened);
        await store.SynchronizeAsync(
            account,
            snapshot,
            CancellationToken.None);
    }

    private static UserSyncSnapshotState CreateSnapshot(
        string[] favorites,
        string[] progress,
        string[]? opened = null)
    {
        var peeked = opened ?? [];
        var titles = favorites
            .Concat(progress)
            .Concat(peeked)
            .Distinct()
            .ToArray();
        var document = new UserSyncDocument
        {
            DeviceId = new string('d', 32),
            DeviceName = "Device",
            Catalog = new CatalogTransferDocument
            {
                Items = titles
                    .Select(static title => new CatalogTransferItem
                    {
                        MediaType = "book",
                        Subtype = "manga",
                        Name = title,
                        Units =
                        [
                            new CatalogTransferUnit
                            {
                                Kind = "chapter",
                                DisplayNumber = "1",
                                SortOrder = 0
                            }
                        ]
                    })
                    .ToArray()
            },
            FavoriteItemStates = favorites
                .Select(static title => new UserSyncFavoriteItemState
                {
                    Item = new UserSyncCatalogItemIdentity
                    {
                        MediaType = "book",
                        Name = title
                    },
                    IsFavorite = true,
                    SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                        new string('d', 32),
                        DateTimeOffset.UnixEpoch.AddDays(1),
                        "favorite:" + title)
                })
                .ToArray(),
            Progress = progress
                .Select(static title => CreateProgress(title, position: 3))
                .Concat(peeked
                    .Select(static title =>
                        CreateProgress(title, position: 0)))
                .ToArray()
        };
        return new UserSyncSnapshotState(
            1,
            DateTimeOffset.UnixEpoch.AddDays(2),
            new string('d', 32),
            UserSyncDocumentMerger.Normalize(document));
    }

    private static UserSyncProgressEntry CreateProgress(
        string title,
        double position) =>
        new()
        {
            Content = new UserSyncContentIdentity
            {
                Item = new UserSyncCatalogItemIdentity
                {
                    MediaType = "book",
                    Name = title
                },
                UnitKind = "chapter",
                DisplayNumber = "1"
            },
            Position = position,
            PositionKind = "page",
            UpdatedAt = DateTimeOffset.UnixEpoch.AddDays(2),
            SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                new string('d', 32),
                DateTimeOffset.UnixEpoch.AddDays(2),
                "progress:" + title)
        };

    private static AccountRecord CreateAccount(string username) =>
        new(
            Guid.CreateVersion7(),
            username,
            username,
            "salt",
            "hash",
            1,
            DateTimeOffset.UtcNow);
}

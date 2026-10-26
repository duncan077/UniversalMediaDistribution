using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

/// <summary>
/// Friend progress is personal, so most of what matters here is what stays
/// invisible: before an accept, after a removal, and while sharing is off.
/// </summary>
public sealed class FriendStoreTests
{
    [Fact]
    public async Task AcceptRollsBackBothFriendEdgesIfTheSecondWriteFails()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await friends.SendRequestAsync(alice, bruno, CancellationToken.None);
        using var connection = new ServerDatabase(scope.Path).Open();
        using var command = connection.CreateCommand();
        command.CommandText = $"""
            CREATE TRIGGER fail_edge BEFORE INSERT ON server_documents
            WHEN NEW.key = 'friends/edges/{alice.AccountId:N}/{bruno.AccountId:N}.json'
            BEGIN SELECT RAISE(ABORT,'Simulated pair failure'); END;
            """;
        command.ExecuteNonQuery();
        await Assert.ThrowsAsync<Microsoft.Data.Sqlite.SqliteException>(() => friends.AcceptRequestAsync(bruno, alice, CancellationToken.None).AsTask());
        Assert.Empty(await friends.ListFriendsAsync(alice.AccountId, CancellationToken.None));
        Assert.Empty(await friends.ListFriendsAsync(bruno.AccountId, CancellationToken.None));
        Assert.Single(await friends.ListRequestsAsync(alice.AccountId, CancellationToken.None));
        command.CommandText = "DROP TRIGGER fail_edge;";
        command.ExecuteNonQuery();
        Assert.True(await friends.AcceptRequestAsync(bruno, alice, CancellationToken.None));
    }

    [Fact]
    public async Task APendingRequestIsNotAFriendship()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");

        var accepted = await friends.SendRequestAsync(
            alice,
            bruno,
            CancellationToken.None);

        Assert.False(accepted);
        Assert.Empty(await friends.ListFriendsAsync(
            alice.AccountId,
            CancellationToken.None));
        Assert.Empty(await friends.ListFriendsAsync(
            bruno.AccountId,
            CancellationToken.None));
    }

    [Fact]
    public async Task ARequestIsVisibleToBothSides()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");

        await friends.SendRequestAsync(alice, bruno, CancellationToken.None);

        Assert.Equal(
            "outgoing",
            Assert.Single(await friends.ListRequestsAsync(
                alice.AccountId,
                CancellationToken.None)).Direction);
        var incoming = Assert.Single(await friends.ListRequestsAsync(
            bruno.AccountId,
            CancellationToken.None));
        Assert.Equal("incoming", incoming.Direction);
        Assert.Equal(alice.AccountId, incoming.AccountId);
    }

    [Fact]
    public async Task AcceptingMakesBothSidesFriends()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await friends.SendRequestAsync(alice, bruno, CancellationToken.None);

        Assert.True(await friends.AcceptRequestAsync(
            bruno,
            alice,
            CancellationToken.None));

        Assert.Equal(
            bruno.AccountId,
            Assert.Single(await friends.ListFriendsAsync(
                alice.AccountId,
                CancellationToken.None)).AccountId);
        Assert.Equal(
            alice.AccountId,
            Assert.Single(await friends.ListFriendsAsync(
                bruno.AccountId,
                CancellationToken.None)).AccountId);

        // The request is spent once it is answered.
        Assert.Empty(await friends.ListRequestsAsync(
            alice.AccountId,
            CancellationToken.None));
        Assert.Empty(await friends.ListRequestsAsync(
            bruno.AccountId,
            CancellationToken.None));
    }

    /// <summary>
    /// Two people who each ask first would otherwise sit waiting on each other.
    /// </summary>
    [Fact]
    public async Task CrossedRequestsBecomeAFriendshipImmediately()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await friends.SendRequestAsync(alice, bruno, CancellationToken.None);

        var accepted = await friends.SendRequestAsync(
            bruno,
            alice,
            CancellationToken.None);

        Assert.True(accepted);
        Assert.Single(await friends.ListFriendsAsync(
            alice.AccountId,
            CancellationToken.None));
        Assert.Single(await friends.ListFriendsAsync(
            bruno.AccountId,
            CancellationToken.None));
    }

    [Fact]
    public async Task DecliningClearsTheRequestFromBothSides()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await friends.SendRequestAsync(alice, bruno, CancellationToken.None);

        Assert.True(await friends.DeclineRequestAsync(
            bruno.AccountId,
            alice.AccountId,
            CancellationToken.None));

        Assert.Empty(await friends.ListRequestsAsync(
            alice.AccountId,
            CancellationToken.None));
        Assert.Empty(await friends.ListRequestsAsync(
            bruno.AccountId,
            CancellationToken.None));
    }

    /// <summary>
    /// A friendship left standing on one side would keep serving that side's
    /// progress to someone who had been removed.
    /// </summary>
    [Fact]
    public async Task RemovingAFriendRemovesBothSides()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);

        Assert.True(await friends.RemoveFriendAsync(
            alice.AccountId,
            bruno.AccountId,
            CancellationToken.None));

        Assert.Empty(await friends.ListFriendsAsync(
            alice.AccountId,
            CancellationToken.None));
        Assert.Empty(await friends.ListFriendsAsync(
            bruno.AccountId,
            CancellationToken.None));
    }

    [Fact]
    public async Task AnAccountCannotAddItself()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");

        await Assert.ThrowsAsync<FriendConflictException>(
            () => friends.SendRequestAsync(
                alice,
                alice,
                CancellationToken.None).AsTask());
    }

    [Fact]
    public async Task SharingIsOffUntilItIsTurnedOn()
    {
        using var scope = TestDirectory.Create();
        var friends = new SqliteFriendStore(scope.Path);

        var visibility = await friends.GetVisibilityAsync(
            Guid.CreateVersion7(),
            CancellationToken.None);

        Assert.False(visibility.ShareProgress);
        Assert.True(visibility.DiscoverableByUsername);
    }

    [Fact]
    public async Task ProgressStaysHiddenWhileSharingIsOff()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: false,
            ("book", "Copper Skies", "75"));

        var matches = await friends.LookupActivityAsync(
            alice.AccountId,
            [SqliteFriendStore.CreateItemKey("book", "Copper Skies")],
            CancellationToken.None);

        Assert.Empty(matches);
    }

    /// <summary>
    /// Turning sharing off has to remove what is already published, not merely
    /// stop adding to it.
    /// </summary>
    [Fact]
    public async Task TurningSharingOffClearsWhatWasAlreadyPublished()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        var itemKey = SqliteFriendStore.CreateItemKey("book", "Copper Skies");
        var snapshot = await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));
        Assert.Single(await friends.LookupActivityAsync(
            alice.AccountId,
            [itemKey],
            CancellationToken.None));

        await friends.SetVisibilityAsync(
            bruno.AccountId,
            new FriendVisibilityUpdateRequest(false, true, []),
            CancellationToken.None);
        await friends.SynchronizeAsync(
            bruno,
            snapshot,
            CancellationToken.None);

        Assert.Empty(await friends.LookupActivityAsync(
            alice.AccountId,
            [itemKey],
            CancellationToken.None));
    }

    [Fact]
    public async Task AFriendSeesHowFarTheOtherHasRead()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));

        var match = Assert.Single(await friends.LookupActivityAsync(
            alice.AccountId,
            [SqliteFriendStore.CreateItemKey("book", "Copper Skies")],
            CancellationToken.None));

        Assert.Equal("75", match.DisplayNumber);
        Assert.Equal(bruno.AccountId, match.AccountId);
        Assert.Equal("Bruno", match.DisplayName);
    }

    /// <summary>
    /// Re-reading an early chapter must not walk the answer backwards.
    /// </summary>
    [Fact]
    public async Task TheFurthestChapterWinsOverTheMostRecentlyRead()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"),
            ("book", "Copper Skies", "10"));

        var match = Assert.Single(await friends.LookupActivityAsync(
            alice.AccountId,
            [SqliteFriendStore.CreateItemKey("book", "Copper Skies")],
            CancellationToken.None));

        Assert.Equal("75", match.DisplayNumber);
    }

    /// <summary>
    /// Side chapters carry decimals, so 10.5 has to sort between 10 and 11
    /// rather than fail to parse and fall back to whichever was touched last.
    /// </summary>
    [Fact]
    public async Task ADecimalChapterSortsBetweenItsNeighbours()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "11"),
            ("book", "Copper Skies", "10.5"));

        var match = Assert.Single(await friends.LookupActivityAsync(
            alice.AccountId,
            [SqliteFriendStore.CreateItemKey("book", "Copper Skies")],
            CancellationToken.None));

        Assert.Equal("11", match.DisplayNumber);
    }

    [Fact]
    public async Task AHiddenTitleIsNotPublished()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        var hidden = SqliteFriendStore.CreateItemKey("book", "Copper Skies");
        var shown = SqliteFriendStore.CreateItemKey("book", "Iron Tide");
        await friends.SetVisibilityAsync(
            bruno.AccountId,
            new FriendVisibilityUpdateRequest(true, true, [hidden]),
            CancellationToken.None);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: null,
            ("book", "Copper Skies", "75"),
            ("book", "Iron Tide", "3"));

        var matches = await friends.LookupActivityAsync(
            alice.AccountId,
            [hidden, shown],
            CancellationToken.None);

        Assert.Equal(shown, Assert.Single(matches).ItemKey);
    }

    /// <summary>
    /// The whole point of the mutual edge.
    /// </summary>
    [Fact]
    public async Task ProgressIsInvisibleToSomeoneWhoIsNotAFriend()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var stranger = await CreateAccountAsync(
            accounts,
            "stranger",
            "Stranger");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));

        var matches = await friends.LookupActivityAsync(
            stranger.AccountId,
            [SqliteFriendStore.CreateItemKey("book", "Copper Skies")],
            CancellationToken.None);

        Assert.Empty(matches);
    }

    /// <summary>
    /// The account-wide switch is the one that is off to begin with, so a new
    /// friend starts included rather than muted. Both defaulting to off would
    /// mean turning sharing on and still seeing nothing.
    /// </summary>
    [Fact]
    public async Task ANewFriendStartsIncluded()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, _) = await CreateFriendsAsync(accounts, friends);

        Assert.True(
            Assert.Single(await friends.ListFriendsAsync(
                alice.AccountId,
                CancellationToken.None)).ShareProgress);
    }

    [Fact]
    public async Task MutingOneFriendHidesProgressFromThatFriendOnly()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var carla = await CreateAccountAsync(accounts, "carla", "Carla");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await BefriendAsync(friends, bruno, alice);
        await BefriendAsync(friends, bruno, carla);
        var itemKey = SqliteFriendStore.CreateItemKey("book", "Copper Skies");
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));

        Assert.True(await friends.SetFriendSharingAsync(
            bruno.AccountId,
            alice.AccountId,
            shareProgress: false,
            CancellationToken.None));

        Assert.Empty(await friends.LookupActivityAsync(
            alice.AccountId,
            [itemKey],
            CancellationToken.None));
        Assert.Single(await friends.LookupActivityAsync(
            carla.AccountId,
            [itemKey],
            CancellationToken.None));
    }

    /// <summary>
    /// The owner of the progress decides who sees it. Muting someone must not
    /// be something the other side can undo from their own copy of the edge.
    /// </summary>
    [Fact]
    public async Task TheViewerCannotShareProgressWithThemselves()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        var itemKey = SqliteFriendStore.CreateItemKey("book", "Copper Skies");
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));
        await friends.SetFriendSharingAsync(
            bruno.AccountId,
            alice.AccountId,
            shareProgress: false,
            CancellationToken.None);

        // Alice turning her own edge on says who *she* shares with, not who
        // shares with her.
        await friends.SetFriendSharingAsync(
            alice.AccountId,
            bruno.AccountId,
            shareProgress: true,
            CancellationToken.None);

        Assert.Empty(await friends.LookupActivityAsync(
            alice.AccountId,
            [itemKey],
            CancellationToken.None));
    }

    [Fact]
    public async Task MutingAStrangerChangesNothing()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");

        Assert.False(await friends.SetFriendSharingAsync(
            alice.AccountId,
            Guid.CreateVersion7(),
            shareProgress: false,
            CancellationToken.None));
    }

    /// <summary>
    /// The point of the list: only what both have actually reached.
    /// </summary>
    [Fact]
    public async Task SharedTitlesAreTheOnesBothHaveProgressIn()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"),
            ("book", "Only Bruno Read This", "3"));
        var aliceSnapshot = await PublishProgressAsync(
            scope,
            friends,
            alice,
            shareProgress: false,
            ("book", "Copper Skies", "12"),
            ("book", "Only Alice Read This", "9"));

        var shared = await friends.ListSharedTitlesAsync(
            alice.AccountId,
            bruno.AccountId,
            aliceSnapshot,
            CancellationToken.None);

        var title = Assert.Single(shared);
        Assert.Equal("Copper Skies", title.Title);

        // Bruno's furthest, not Alice's.
        Assert.Equal("75", title.DisplayNumber);
    }

    /// <summary>
    /// The asking account's own half comes from its snapshot, so this works
    /// whether or not that account shares anything itself.
    /// </summary>
    [Fact]
    public async Task SharedTitlesDoNotRequireTheAskerToShare()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));
        var aliceSnapshot = await PublishProgressAsync(
            scope,
            friends,
            alice,
            shareProgress: false,
            ("book", "Copper Skies", "12"));

        Assert.False(
            (await friends.GetVisibilityAsync(
                alice.AccountId,
                CancellationToken.None)).ShareProgress);
        Assert.Single(await friends.ListSharedTitlesAsync(
            alice.AccountId,
            bruno.AccountId,
            aliceSnapshot,
            CancellationToken.None));
    }

    [Fact]
    public async Task SharedTitlesRespectAMutedFriend()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));
        var aliceSnapshot = await PublishProgressAsync(
            scope,
            friends,
            alice,
            shareProgress: false,
            ("book", "Copper Skies", "12"));
        await friends.SetFriendSharingAsync(
            bruno.AccountId,
            alice.AccountId,
            shareProgress: false,
            CancellationToken.None);

        Assert.Empty(await friends.ListSharedTitlesAsync(
            alice.AccountId,
            bruno.AccountId,
            aliceSnapshot,
            CancellationToken.None));
    }

    [Fact]
    public async Task SharedTitlesAreEmptyForSomeoneWhoIsNotAFriend()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var stranger = await CreateAccountAsync(
            accounts,
            "stranger",
            "Stranger");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await PublishProgressAsync(
            scope,
            friends,
            bruno,
            shareProgress: true,
            ("book", "Copper Skies", "75"));
        var strangerSnapshot = await PublishProgressAsync(
            scope,
            friends,
            stranger,
            shareProgress: false,
            ("book", "Copper Skies", "12"));

        Assert.Empty(await friends.ListSharedTitlesAsync(
            stranger.AccountId,
            bruno.AccountId,
            strangerSnapshot,
            CancellationToken.None));
    }

    /// <summary>
    /// A series carries a title-level progress row with no unit alongside its
    /// per-episode ones. That row must not win the rollup, or a viewer who has
    /// reached the second season is reported as having merely started.
    /// </summary>
    [Fact]
    public async Task ATitleLevelRowDoesNotOutrankEpisodeProgress()
    {
        using var scope = TestDirectory.Create();
        var accounts = CreateAccountStore(scope);
        var friends = new SqliteFriendStore(scope.Path);
        var (alice, bruno) = await CreateFriendsAsync(accounts, friends);
        await friends.SetVisibilityAsync(
            bruno.AccountId,
            new FriendVisibilityUpdateRequest(true, true, []),
            CancellationToken.None);

        var syncStore = new SqliteUserSyncStore(scope.Path);
        var snapshot = await syncStore.SaveAsync(
            bruno.AccountId,
            new UserSyncUploadRequest(0, CreateSeriesDocument()),
            CancellationToken.None);
        await friends.SynchronizeAsync(
            bruno,
            snapshot,
            CancellationToken.None);

        var match = Assert.Single(await friends.LookupActivityAsync(
            alice.AccountId,
            [SqliteFriendStore.CreateItemKey("movie", "Silo")],
            CancellationToken.None));

        Assert.Equal(2, match.SeasonNumber);
        Assert.Equal("3", match.DisplayNumber);
    }

    /// <summary>
    /// The shape a real series takes: several episodes across two seasons, plus
    /// a title-level row carrying no unit at all.
    /// </summary>
    private static UserSyncDocument CreateSeriesDocument()
    {
        var updatedAt = new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        UserSyncProgressEntry Episode(int season, string number, int minute) =>
            new()
            {
                Content = new UserSyncContentIdentity
                {
                    Item = new UserSyncCatalogItemIdentity
                    {
                        MediaType = "movie",
                        Name = "Silo"
                    },
                    UnitKind = "episode",
                    DisplayNumber = number,
                    SeasonNumber = season
                },
                Position = 60,
                PositionKind = "seconds",
                IsCompleted = season == 1,
                UpdatedAt = updatedAt.AddMinutes(minute)
            };

        return new UserSyncDocument
        {
            Version = 1,
            DeviceId = "device-one",
            DeviceName = "Test desktop",
            Catalog = new CatalogTransferDocument
            {
                SourceLabel = "Series tests",
                Items =
                [
                    new CatalogTransferItem
                    {
                        MediaType = "movie",
                        Subtype = "series",
                        Name = "Silo",
                        Description = "Test series",
                        Seasons =
                        [
                            CreateSeason(1, ["1", "2", "3"]),
                            CreateSeason(2, ["1", "2", "3"])
                        ]
                    }
                ]
            },
            Progress =
            [
                Episode(1, "1", 0),
                Episode(1, "2", 1),
                Episode(1, "3", 2),
                Episode(2, "2", 3),
                Episode(2, "3", 4),

                // Last, so recency alone would pick it.
                new UserSyncProgressEntry
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = new UserSyncCatalogItemIdentity
                        {
                            MediaType = "movie",
                            Name = "Silo"
                        }
                    },
                    Position = 0,
                    PositionKind = "seconds",
                    IsCompleted = false,
                    UpdatedAt = updatedAt.AddMinutes(9)
                }
            ]
        };
    }

    private static CatalogTransferSeason CreateSeason(
        int number,
        string[] episodes) =>
        new()
        {
            Number = number,
            SortOrder = number,
            Units = episodes
                .Select((episode, index) => new CatalogTransferUnit
                {
                    Kind = "episode",
                    DisplayNumber = episode,
                    SortOrder = index
                })
                .ToArray()
        };

    [Fact]
    public async Task ALookupCannotWalkAWholeLibrary()
    {
        using var scope = TestDirectory.Create();
        var friends = new SqliteFriendStore(scope.Path);
        var keys = Enumerable
            .Range(0, SqliteFriendStore.MaximumLookupKeys + 1)
            .Select(static index => $"book:title {index}")
            .ToArray();

        await Assert.ThrowsAsync<ArgumentException>(
            () => friends.LookupActivityAsync(
                Guid.CreateVersion7(),
                keys,
                CancellationToken.None).AsTask());
    }

    /// <summary>
    /// Two installs mint different catalog Guids for the same title, so the key
    /// friends match on has to come from the title itself. This mirrors the key
    /// the catalog import already dedupes on.
    /// </summary>
    [Fact]
    public void TheItemKeyIgnoresSpacingAndCase()
    {
        Assert.Equal(
            SqliteFriendStore.CreateItemKey("book", "Copper  Skies"),
            SqliteFriendStore.CreateItemKey("BOOK", " Copper Skies "));
        Assert.NotEqual(
            SqliteFriendStore.CreateItemKey("book", "Copper Skies"),
            SqliteFriendStore.CreateItemKey("movie", "Copper Skies"));
    }

    private static SqliteAccountStore CreateAccountStore(TestDirectory scope) =>
        new(scope.Path, passwordIterations: 100_000);

    private static async Task<AccountRecord> CreateAccountAsync(
        SqliteAccountStore accounts,
        string username,
        string displayName) =>
        await accounts.CreateAsync(
            new AdminAccountRegistrationRequest(
                username,
                "correct horse battery staple",
                displayName),
            CancellationToken.None);

    private static async Task<(AccountRecord Alice, AccountRecord Bruno)>
        CreateFriendsAsync(
            SqliteAccountStore accounts,
            SqliteFriendStore friends)
    {
        var alice = await CreateAccountAsync(accounts, "alice", "Alice");
        var bruno = await CreateAccountAsync(accounts, "bruno", "Bruno");
        await BefriendAsync(friends, alice, bruno);
        return (alice, bruno);
    }

    private static async Task BefriendAsync(
        SqliteFriendStore friends,
        AccountRecord first,
        AccountRecord second)
    {
        await friends.SendRequestAsync(first, second, CancellationToken.None);
        await friends.AcceptRequestAsync(second, first, CancellationToken.None);
    }

    /// <summary>
    /// Publishes progress the way the server does: through a real sync
    /// snapshot, since that is the only path the projection is built from.
    /// </summary>
    private static async Task<UserSyncSnapshotState> PublishProgressAsync(
        TestDirectory scope,
        SqliteFriendStore friends,
        AccountRecord account,
        bool? shareProgress,
        params (string MediaType, string Name, string Unit)[] entries)
    {
        if (shareProgress is not null)
        {
            await friends.SetVisibilityAsync(
                account.AccountId,
                new FriendVisibilityUpdateRequest(
                    shareProgress.Value,
                    true,
                    []),
                CancellationToken.None);
        }

        var syncStore = new SqliteUserSyncStore(scope.Path);
        var snapshot = await syncStore.SaveAsync(
            account.AccountId,
            new UserSyncUploadRequest(0, CreateDocument(entries)),
            CancellationToken.None);
        await friends.SynchronizeAsync(
            account,
            snapshot,
            CancellationToken.None);
        return snapshot;
    }

    private static UserSyncDocument CreateDocument(
        (string MediaType, string Name, string Unit)[] entries)
    {
        var updatedAt = new DateTimeOffset(
            2026,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero);
        return new UserSyncDocument
        {
            // Version 1 on purpose: the upgrade path fills in the per-record
            // sync versions that a current-version document is required to
            // carry, which keeps this fixture to the fields under test.
            Version = 1,
            DeviceId = "device-one",
            DeviceName = "Test desktop",

            // The validator refuses progress that points at a unit the
            // document's own catalog does not carry, so the catalog has to be
            // built from the same entries the progress refers to.
            Catalog = CreateCatalog(entries),
            Progress = entries
                .Select((entry, index) => new UserSyncProgressEntry
                {
                    Content = new UserSyncContentIdentity
                    {
                        Item = new UserSyncCatalogItemIdentity
                        {
                            MediaType = entry.MediaType,
                            Name = entry.Name
                        },
                        UnitKind = "chapter",
                        DisplayNumber = entry.Unit
                    },
                    Position = 1,
                    PositionKind = "page",
                    IsCompleted = false,

                    // Later entries are more recent, so a test that lists a
                    // lower chapter second is asking whether recency wins.
                    UpdatedAt = updatedAt.AddMinutes(index)
                })
                .ToArray()
        };
    }

    private static CatalogTransferDocument CreateCatalog(
        (string MediaType, string Name, string Unit)[] entries) =>
        new()
        {
            SourceLabel = "Friend tests",
            Items = entries
                .GroupBy(
                    static entry => (entry.MediaType, entry.Name))
                .Select(static group => new CatalogTransferItem
                {
                    MediaType = group.Key.MediaType,
                    Subtype = group.Key.MediaType == "book"
                        ? "manga"
                        : "movie",
                    Name = group.Key.Name,
                    Description = "Test title",
                    Units = group
                        .Select(static entry => entry.Unit)
                        .Distinct(StringComparer.Ordinal)
                        .Select(static (unit, index) =>
                            new CatalogTransferUnit
                            {
                                Kind = "chapter",
                                DisplayNumber = unit,
                                SortOrder = index
                            })
                        .ToArray()
                })
                .ToArray()
        };
}

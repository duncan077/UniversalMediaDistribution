namespace UniversalMediaDistribution.Server.Tests;

/// <summary>
/// The party room's rules: one party per account, codes that survive being
/// read out loud, a host that survives the host leaving, and a shared
/// timeline whose expected position moves only while it plays.
/// </summary>
public sealed class WatchPartyRegistryTests
{
    [Fact]
    public void CreatingMakesTheCreatorTheOnlyMemberAndHost()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");

        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));

        Assert.Equal(6, party.JoinCode.Length);
        Assert.Equal(alice.AccountId, party.HostAccountId);
        var member = Assert.Single(party.Members);
        Assert.Equal(alice.AccountId, member.AccountId);
        Assert.True(member.IsHost);
        Assert.False(party.Playback.IsPlaying);
        Assert.Null(party.Content);
    }

    [Fact]
    public void AnUnknownOrMalformedCodeJoinsNothing()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        registry.Create(alice, new WatchPartyCreateRequest(null, 0));
        var bruno = CreateAccount("bruno", "Bruno");

        Assert.Null(registry.Join(bruno, "ABC"));
        Assert.Null(registry.Join(bruno, "!!!!!!"));
        Assert.Null(registry.Join(bruno, string.Empty));
    }

    [Fact]
    public void JoiningIsCaseInsensitiveAndIdempotent()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        var bruno = CreateAccount("bruno", "Bruno");
        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));

        var joined = registry.Join(
            bruno,
            party.JoinCode.ToLowerInvariant());
        var joinedAgain = registry.Join(bruno, party.JoinCode);

        Assert.NotNull(joined);
        Assert.NotNull(joinedAgain);
        Assert.Equal(2, joinedAgain.Members.Length);
        Assert.Equal(alice.AccountId, joinedAgain.HostAccountId);
    }

    [Fact]
    public void CreatingASecondPartyLeavesTheFirst()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        var bruno = CreateAccount("bruno", "Bruno");
        var first = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));
        registry.Join(bruno, first.JoinCode);

        var second = registry.Create(
            bruno,
            new WatchPartyCreateRequest(null, 0));

        var remaining = registry.Get(alice.AccountId);
        Assert.NotNull(remaining);
        Assert.Equal(first.PartyId, remaining.PartyId);
        Assert.Single(remaining.Members);
        Assert.Equal(second.PartyId, registry.Get(bruno.AccountId)!.PartyId);
    }

    [Fact]
    public void TheHostLeavingPromotesTheLongestStandingMember()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        var bruno = CreateAccount("bruno", "Bruno");
        var carla = CreateAccount("carla", "Carla");
        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));
        registry.Join(bruno, party.JoinCode);
        registry.Join(carla, party.JoinCode);

        Assert.True(registry.Leave(alice.AccountId));

        Assert.Null(registry.Get(alice.AccountId));
        var remaining = registry.Get(bruno.AccountId);
        Assert.NotNull(remaining);
        Assert.Equal(bruno.AccountId, remaining.HostAccountId);
        Assert.Equal(2, remaining.Members.Length);
    }

    [Fact]
    public void ThePartyEndsWhenItsLastMemberLeaves()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        var bruno = CreateAccount("bruno", "Bruno");
        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));
        registry.Join(bruno, party.JoinCode);

        registry.Leave(alice.AccountId);
        registry.Leave(bruno.AccountId);

        var carla = CreateAccount("carla", "Carla");
        Assert.Null(registry.Join(carla, party.JoinCode));
        Assert.False(registry.Leave(alice.AccountId));
    }

    [Fact]
    public void AFullPartyRefusesTheNextJoin()
    {
        var registry = new WatchPartyRegistry();
        var party = registry.Create(
            CreateAccount("host", "Host"),
            new WatchPartyCreateRequest(null, 0));
        for (var index = 1; index < 16; index++)
        {
            Assert.NotNull(registry.Join(
                CreateAccount($"member{index}", $"Member {index}"),
                party.JoinCode));
        }

        Assert.Throws<WatchPartyConflictException>(() =>
            registry.Join(
                CreateAccount("onetoomany", "One Too Many"),
                party.JoinCode));
    }

    [Fact]
    public void ContentIsKeyedTheWayFriendProgressIs()
    {
        var registry = new WatchPartyRegistry();
        var party = registry.Create(
            CreateAccount("alice", "Alice"),
            new WatchPartyCreateRequest(
                new WatchPartyContentRequest(
                    "Movie",
                    "  Cowboy Bebop ",
                    Title: null,
                    UnitKind: "episode",
                    SeasonNumber: 1,
                    DisplayNumber: "5"),
                PositionSeconds: 120));

        Assert.NotNull(party.Content);
        Assert.Equal(
            UniversalMediaDistribution.Domain.Catalog.ContentItemKey.Create(
                "movie",
                "Cowboy Bebop"),
            party.Content.ItemKey);
        Assert.Equal("Cowboy Bebop", party.Content.Name);
        Assert.Equal("episode", party.Content.UnitKind);
        Assert.Equal(120, party.Playback.PositionSeconds);
    }

    [Fact]
    public void InvalidContentRefusesTheParty()
    {
        var registry = new WatchPartyRegistry();

        Assert.ThrowsAny<ArgumentException>(() =>
            registry.Create(
                CreateAccount("alice", "Alice"),
                new WatchPartyCreateRequest(
                    new WatchPartyContentRequest(
                        "movie",
                        Name: "   ",
                        Title: null,
                        UnitKind: null,
                        SeasonNumber: null,
                        DisplayNumber: null),
                    PositionSeconds: 0)));
    }

    /// <summary>
    /// The expected position is what a stall freezes and a recovery resumes,
    /// so the two have to be told apart from a pause somebody chose.
    /// </summary>
    [Fact]
    public void ADeliberatePauseIsNotUndoneByAStallRecovering()
    {
        var now = DateTimeOffset.UtcNow;
        var chosenPause = new WatchPartyRegistry.PlaybackState(
            IsPlaying: false,
            PositionSeconds: 100,
            now,
            "Alice");

        // A party paused on purpose sits still however long it waits, which
        // is what makes "resume only what buffering paused" meaningful.
        Assert.Equal(
            100,
            WatchPartyRegistry.ExpectedPosition(
                chosenPause,
                now.AddMinutes(5)),
            precision: 3);
    }

    [Fact]
    public void OnlyTheHostCanLockTransportControl()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        var bruno = CreateAccount("bruno", "Bruno");
        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));
        registry.Join(bruno, party.JoinCode);

        Assert.False(party.HostControlsOnly);
        var locked = registry.SetOptions(
            alice.AccountId,
            new WatchPartyOptionsRequest(HostControlsOnly: true));
        Assert.NotNull(locked);
        Assert.True(locked.HostControlsOnly);

        Assert.Throws<WatchPartyConflictException>(() =>
            registry.SetOptions(
                bruno.AccountId,
                new WatchPartyOptionsRequest(HostControlsOnly: false)));
        Assert.True(registry.Get(bruno.AccountId)!.HostControlsOnly);
    }

    [Fact]
    public void SettingOptionsWithoutAPartyAnswersNothing()
    {
        var registry = new WatchPartyRegistry();

        Assert.Null(registry.SetOptions(
            Guid.CreateVersion7(),
            new WatchPartyOptionsRequest(HostControlsOnly: true)));
    }

    /// <summary>
    /// Host succession has to carry the lock with it, or a locked party would
    /// become uncontrollable the moment its host stepped out.
    /// </summary>
    [Fact]
    public void ANewHostInheritsTheLockedParty()
    {
        var registry = new WatchPartyRegistry();
        var alice = CreateAccount("alice", "Alice");
        var bruno = CreateAccount("bruno", "Bruno");
        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));
        registry.Join(bruno, party.JoinCode);
        registry.SetOptions(
            alice.AccountId,
            new WatchPartyOptionsRequest(HostControlsOnly: true));

        registry.Leave(alice.AccountId);

        var remaining = registry.Get(bruno.AccountId);
        Assert.NotNull(remaining);
        Assert.Equal(bruno.AccountId, remaining.HostAccountId);
        Assert.True(remaining.HostControlsOnly);
        Assert.NotNull(registry.SetOptions(
            bruno.AccountId,
            new WatchPartyOptionsRequest(HostControlsOnly: false)));
    }

    [Fact]
    public void TheExpectedPositionMovesOnlyWhilePlaying()
    {
        var updatedAt = DateTimeOffset.UtcNow;
        var later = updatedAt.AddSeconds(30);

        var playing = new WatchPartyRegistry.PlaybackState(
            IsPlaying: true,
            PositionSeconds: 100,
            updatedAt,
            "Alice");
        var paused = playing with { IsPlaying = false };

        Assert.Equal(
            130,
            WatchPartyRegistry.ExpectedPosition(playing, later),
            precision: 3);
        Assert.Equal(
            100,
            WatchPartyRegistry.ExpectedPosition(paused, later),
            precision: 3);
    }

    [Fact]
    public void AnAbandonedPartyExpiresAndItsMembersAreFreed()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var registry = new WatchPartyRegistry(clock);
        var alice = CreateAccount("alice", "Alice");
        var party = registry.Create(
            alice,
            new WatchPartyCreateRequest(null, 0));

        clock.Advance(WatchPartyRegistry.IdlePartyLifetime +
            TimeSpan.FromMinutes(1));

        Assert.Null(registry.Get(alice.AccountId));
        var bruno = CreateAccount("bruno", "Bruno");
        Assert.Null(registry.Join(bruno, party.JoinCode));
    }

    private static AccountRecord CreateAccount(
        string username,
        string displayName) =>
        new(
            Guid.CreateVersion7(),
            username,
            displayName,
            PasswordSalt: string.Empty,
            PasswordHash: string.Empty,
            PasswordIterations: 100_000,
            DateTimeOffset.UtcNow);

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

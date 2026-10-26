using System.Threading.Channels;
using UniversalMediaDistribution.UI.Playback;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The sync loop's two directions, and the guard between them: a viewer's own
/// pause travels to the party, the party's commands reach the player, and an
/// applied command is never mistaken for a fresh viewer action and echoed
/// back.
/// </summary>
public sealed class WatchPartyServiceTests
{
    private static readonly Guid SelfAccountId = Guid.CreateVersion7();
    private static readonly Guid FriendAccountId = Guid.CreateVersion7();

    [Fact]
    public async Task ManualResyncMovesOnlyThisViewerWithoutSeekingTheRoom()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player, clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        clock.Advance(TimeSpan.FromSeconds(20));
        player.State = player.State with { PositionSeconds = 118, IsPlaying = false };

        await service.ResynchronizeAsync();

        // Even a small drift is corrected when the viewer explicitly asks.
        Assert.Equal(120, Assert.Single(player.Seeks));
        Assert.True(Assert.Single(player.PlayingChanges));
        await service.OnPlaybackTickAsync(player.State);
        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task ManualResyncDoesNotChangeAnotherTitle()
    {
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(DateTimeOffset.UtcNow);
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent() with { DisplayNumber = "6" }, 100);

        await service.ResynchronizeAsync();

        Assert.Empty(player.Seeks);
        Assert.Empty(player.PlayingChanges);
        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task NothingHappensWithoutTheCapabilityOrASession()
    {
        var client = new WatchPartyClient { Session = null };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player);

        Assert.False(service.IsAvailable);
        Assert.Null(await service.CreateAsync(null, 0));
        Assert.Null(await service.JoinAsync("ABCDEF"));
        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task ALocalPauseBecomesAPartyCommand()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());

        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.Sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));

        await service.OnPlaybackTickAsync(new PlatformMediaState(
            PositionSeconds: 110,
            DurationSeconds: 1_000,
            IsPlaying: false,
            IsEnded: false,
            IsClosed: false));

        var command = Assert.Single(client.Connection.Sent);
        Assert.Equal("pause", command.Type);
        Assert.Equal(110, command.PositionSeconds);
    }

    [Fact]
    public async Task ALocalJumpBecomesASeekCommand()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());

        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.Sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));

        // Expected position is 110; the viewer is at 300.
        await service.OnPlaybackTickAsync(new PlatformMediaState(
            PositionSeconds: 300,
            DurationSeconds: 1_000,
            IsPlaying: true,
            IsEnded: false,
            IsClosed: false));

        var command = Assert.Single(client.Connection.Sent);
        Assert.Equal("seek", command.Type);
        Assert.Equal(300, command.PositionSeconds);
    }

    [Fact]
    public async Task DriftInsideTheThresholdIsLeftAlone()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());

        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.Sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));

        // Two seconds off the expected 110 is sampling jitter, not a seek.
        await service.OnPlaybackTickAsync(new PlatformMediaState(
            PositionSeconds: 112,
            DurationSeconds: 1_000,
            IsPlaying: true,
            IsEnded: false,
            IsClosed: false));

        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task PlayingSomethingElseDetachesFromTheTimeline()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());

        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(
            CreateContent() with { DisplayNumber = "6" },
            0);
        client.Connection.Sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));

        await service.OnPlaybackTickAsync(new PlatformMediaState(
            PositionSeconds: 500,
            DurationSeconds: 1_000,
            IsPlaying: false,
            IsEnded: false,
            IsClosed: false));

        Assert.False(service.IsSynchronized);
        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task AFriendsPauseReachesThePlayer()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        player.PlayingChanges.Clear();

        var paused = CreatePlayingParty(clock.GetUtcNow()) with
        {
            Playback = new RemoteWatchPartyPlayback(
                IsPlaying: false,
                PositionSeconds: 150,
                clock.GetUtcNow(),
                "Bruno")
        };
        client.Connection.Push(new RemoteWatchPartyEvent(
            "pause",
            Sequence: 5,
            FriendAccountId,
            "Bruno",
            paused));

        await WaitForAsync(() => player.PlayingChanges.Count > 0);
        Assert.False(Assert.Single(player.PlayingChanges));
    }

    [Fact]
    public async Task ThisDevicesOwnEchoIsNotReApplied()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        player.PlayingChanges.Clear();
        player.Seeks.Clear();

        var ownEcho = client.PartyState with
        {
            Playback = new RemoteWatchPartyPlayback(
                IsPlaying: false,
                PositionSeconds: 100,
                clock.GetUtcNow(),
                "Alice")
        };
        client.Connection.Push(new RemoteWatchPartyEvent(
            "pause",
            Sequence: 5,
            SelfAccountId,
            "Alice",
            ownEcho));

        // A foreign seek afterwards proves the echo was processed and skipped
        // rather than still in flight. Its playing state matches the player's,
        // so the only apply it can cause is the seek itself.
        var foreignSeek = client.PartyState with
        {
            Playback = new RemoteWatchPartyPlayback(
                IsPlaying: true,
                PositionSeconds: 400,
                clock.GetUtcNow(),
                "Bruno")
        };
        client.Connection.Push(new RemoteWatchPartyEvent(
            "seek",
            Sequence: 6,
            FriendAccountId,
            "Bruno",
            foreignSeek));
        await WaitForAsync(() => player.Seeks.Count > 0);

        Assert.Equal(400, Assert.Single(player.Seeks));
        Assert.Empty(player.PlayingChanges);
    }

    /// <summary>
    /// The embedded web player reports a placeholder state — position zero,
    /// always playing — because a third-party page tells it nothing. Reading
    /// that as a viewer seek would drag the whole party back to the start.
    /// </summary>
    [Fact]
    public async Task APlayerWithoutTransportControlNeverCommandsTheParty()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer { SupportsTransport = false };
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());

        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 0);
        client.Connection.Sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(30));

        await service.OnPlaybackTickAsync(new PlatformMediaState(
            PositionSeconds: 0,
            DurationSeconds: null,
            IsPlaying: true,
            IsEnded: false,
            IsClosed: false));

        Assert.False(service.CanFollowParty);
        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task AStallIsReportedAndItsRecoveryToo()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.Sent.Clear();

        // Stalled: reported once, not on every tick that follows.
        var stalled = new PlatformMediaState(
            PositionSeconds: 110,
            DurationSeconds: 1_000,
            IsPlaying: false,
            IsEnded: false,
            IsClosed: false,
            ErrorMessage: null,
            IsBuffering: true);
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.OnPlaybackTickAsync(stalled);
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.OnPlaybackTickAsync(stalled);

        var stall = Assert.Single(client.Connection.Sent);
        Assert.Equal("buffering", stall.Type);
        Assert.True(stall.IsBuffering);

        // Recovered: reported once more, and never as a pause even though
        // the stalled sample said it was not playing.
        client.Connection.Sent.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.OnPlaybackTickAsync(stalled with
        {
            IsBuffering = false,
            IsPlaying = true
        });

        var recovered = Assert.Single(client.Connection.Sent);
        Assert.Equal("buffering", recovered.Type);
        Assert.False(recovered.IsBuffering);
    }

    /// <summary>
    /// A locked party ignores a member's transport, so the member is put back
    /// on the shared timeline instead of being left where they wandered.
    /// </summary>
    [Fact]
    public async Task ALockedPartyIgnoresAMembersPauseAndRestoresThem()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow()) with
        {
            HostControlsOnly = true
        };
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.Sent.Clear();
        player.PlayingChanges.Clear();
        clock.Advance(TimeSpan.FromSeconds(10));

        // The viewer paused: both the sample the monitor hands over and the
        // player's own state say so, as they would in a real player.
        var paused = new PlatformMediaState(
            PositionSeconds: 110,
            DurationSeconds: 1_000,
            IsPlaying: false,
            IsEnded: false,
            IsClosed: false);
        player.State = paused;
        await service.OnPlaybackTickAsync(paused);

        Assert.False(service.CanControlParty);
        Assert.Empty(client.Connection.Sent);
        Assert.True(Assert.Single(player.PlayingChanges));
    }

    /// <summary>
    /// A member opening the party's own title starts on the shared timeline,
    /// not at their private resume position — and a different title gets no
    /// position at all.
    /// </summary>
    [Fact]
    public async Task TheJoinPositionIsThePartysExpectedPosition()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        clock.Advance(TimeSpan.FromSeconds(10));

        var matching = service.GetJoinPositionFor(CreateContent());
        var other = service.GetJoinPositionFor(
            CreateContent() with { DisplayNumber = "6" });

        Assert.NotNull(matching);
        Assert.Equal(110, matching.Value, precision: 3);
        Assert.Null(other);
    }

    /// <summary>
    /// Only the party's own title is held at its first frame, and only while
    /// the party is actually paused — a member watching something else of
    /// their own is never held.
    /// </summary>
    [Fact]
    public async Task OnlyThePartysOwnPausedTitleOpensPaused()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        var playingParty = CreatePlayingParty(clock.GetUtcNow());
        client.PartyState = playingParty;
        await service.JoinAsync("ABCDEF");

        // The party is playing, so its title opens playing too.
        Assert.False(service.ShouldOpenPaused(CreateContent()));

        client.Connection.Push(new RemoteWatchPartyEvent(
            "pause",
            Sequence: 5,
            FriendAccountId,
            "Bruno",
            playingParty with
            {
                Playback = new RemoteWatchPartyPlayback(
                    IsPlaying: false,
                    PositionSeconds: 150,
                    clock.GetUtcNow(),
                    "Bruno")
            }));
        await WaitForAsync(() => service.ShouldOpenPaused(CreateContent()));

        Assert.True(service.ShouldOpenPaused(CreateContent()));
        Assert.False(service.ShouldOpenPaused(
            CreateContent() with { DisplayNumber = "6" }));
    }

    /// <summary>
    /// Closing playback changes what "synchronized" answers, so it must be
    /// announced even though nothing is sent to the party.
    /// </summary>
    [Fact]
    public async Task ClosingPlaybackRaisesAStateChange()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(
            client,
            player,
            clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        Assert.True(service.IsSynchronized);

        var raised = 0;
        service.StateChanged += () => Interlocked.Increment(ref raised);
        await service.NotifyPlaybackStartedAsync(null, 0);

        Assert.False(service.IsSynchronized);
        Assert.True(raised > 0);
    }

    [Fact]
    public async Task NumericEpisodeNumbersMatchAcrossFormats()
    {
        var left = CreateContent() with { DisplayNumber = "5" };
        var right = CreateContent() with { DisplayNumber = "05" };
        var different = CreateContent() with { DisplayNumber = "6" };

        Assert.True(WatchPartyService.ContentMatches(left, right));
        Assert.False(WatchPartyService.ContentMatches(left, different));
    }

    [Theory]
    [InlineData("buffering", true)]
    [InlineData("connected", true)]
    [InlineData("disconnected", false)]
    [InlineData("left", false)]
    [InlineData("content", false)]
    [InlineData("options", false)]
    public async Task CompleteSnapshotsRestorePlayback(string reason, bool ownAction)
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player, clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        player.State = player.State with { IsPlaying = false, PositionSeconds = 40 };

        client.Connection.Push(new RemoteWatchPartyEvent(reason, 5,
            ownAction ? SelfAccountId : FriendAccountId, "Alice", client.PartyState));

        await WaitForAsync(() => player.PlayingChanges.Count > 0);
        Assert.True(Assert.Single(player.PlayingChanges));
        Assert.Equal(100, Assert.Single(player.Seeks));
        clock.Advance(TimeSpan.FromSeconds(1));
        await service.OnPlaybackTickAsync(player.State);
        Assert.Empty(client.Connection.Sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ClosingOrChangingContentReleasesABufferingHold(bool changeContent)
    {
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(DateTimeOffset.UtcNow);
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        await service.OnPlaybackTickAsync(player.State with { IsBuffering = true });
        client.Connection.Sent.Clear();

        await service.NotifyPlaybackStartedAsync(
            changeContent ? CreateContent() with { DisplayNumber = "6" } : null, 0);

        var command = Assert.Single(client.Connection.Sent);
        Assert.Equal("buffering", command.Type);
        Assert.False(command.IsBuffering);
        Assert.False(service.IsSynchronized);
    }

    [Fact]
    public async Task AFailedBufferingReportIsRetried()
    {
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(DateTimeOffset.UtcNow);
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.FailNextSend = true;

        await service.OnPlaybackTickAsync(player.State with { IsBuffering = true });
        await service.OnPlaybackTickAsync(player.State with { IsBuffering = true });

        Assert.True(Assert.Single(client.Connection.Sent).IsBuffering);
    }

    [Fact]
    public async Task ASlowRemoteSeekDoesNotBecomeALocalCommand()
    {
        var clock = new FakeTimeProvider(DateTimeOffset.UtcNow);
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player, clock);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(clock.GetUtcNow());
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        player.SeekRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        client.Connection.Push(new RemoteWatchPartyEvent("seek", 5, FriendAccountId, "Bruno",
            client.PartyState with
            {
                Playback = client.PartyState.Playback with { PositionSeconds = 400 }
            }));
        await WaitForAsync(() => player.Seeks.Count > 0);
        clock.Advance(TimeSpan.FromSeconds(10));
        await service.OnPlaybackTickAsync(player.State);
        Assert.Empty(client.Connection.Sent);
        player.SeekRelease.TrySetResult();
    }

    [Fact]
    public async Task AnUnknownJoinCodePreservesTheCurrentConnection()
    {
        var client = new WatchPartyClient { Session = CreateSession() };
        await using var service = new WatchPartyService(client, new FakePlayer());
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(DateTimeOffset.UtcNow);
        await service.JoinAsync("ABCDEF");
        var originalParty = service.Current;
        client.PartyState = null;

        Assert.Null(await service.JoinAsync("ZZZZZZ"));

        Assert.Same(originalParty, service.Current);
        Assert.True(service.IsConnected);
        Assert.Equal(0, client.Connection.DisposeCount);
    }

    [Fact]
    public async Task OurOwnBufferingReportCanPauseThisPlayer()
    {
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(DateTimeOffset.UtcNow);
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        client.Connection.Push(new RemoteWatchPartyEvent("buffering", 5, SelfAccountId, "Alice",
            client.PartyState with
            {
                Playback = client.PartyState.Playback with { IsPlaying = false }
            }));

        await WaitForAsync(() => player.PlayingChanges.Count > 0);

        Assert.False(Assert.Single(player.PlayingChanges));
        Assert.Empty(client.Connection.Sent);
    }

    [Fact]
    public async Task ReconnectingReleasesTheOldSocketAndRestoresTheTimeline()
    {
        var client = new WatchPartyClient { Session = CreateSession() };
        var player = new FakePlayer();
        await using var service = new WatchPartyService(client, player);
        service.ApplyCapabilities(CreateCapabilities());
        client.PartyState = CreatePlayingParty(DateTimeOffset.UtcNow);
        await service.JoinAsync("ABCDEF");
        await service.NotifyPlaybackStartedAsync(CreateContent(), 100);
        var oldConnection = client.Connection;
        client.Connection = new FakeConnection();
        client.PartyState = client.PartyState with
        {
            Playback = client.PartyState.Playback with { IsPlaying = false, PositionSeconds = 250 }
        };
        oldConnection.CloseIncoming();

        await WaitForAsync(() => !service.IsConnected);
        Assert.True(service.IsInParty);
        Assert.Equal(1, oldConnection.DisposeCount);
        await WaitForAsync(() => player.PlayingChanges.Count > 0);
        Assert.True(service.IsConnected);
        Assert.Equal(250, Assert.Single(player.Seeks));
        Assert.False(Assert.Single(player.PlayingChanges));
        await service.ResetAsync();
        Assert.Equal(1, client.Connection.DisposeCount);
        Assert.False(service.IsInParty);
    }

    private static async Task WaitForAsync(Func<bool> condition)
    {
        for (var attempt = 0; attempt < 250; attempt++)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail("The expected background work never happened.");
    }

    private static RemoteAccountSessionInfo CreateSession() =>
        new(
            SelfAccountId,
            "alice",
            "Alice",
            DateTimeOffset.UtcNow.AddDays(1),
            new Uri("https://example.test/"));

    private static RemoteServerCapabilities CreateCapabilities() =>
        new(
            ApiVersion: 1,
            ReleaseNotifications: true,
            ReleaseUploads: false,
            Accounts: true,
            AccountRegistration: false,
            OnlineCatalog: true,
            BackupSync: true,
            SharedLists: true,
            Friends: true,
            WatchParties: true);

    private static RemoteWatchPartyContent CreateContent() =>
        new(
            "movie:cowboy bebop",
            "movie",
            "Cowboy Bebop",
            "Cowboy Bebop · S1 · 5",
            "episode",
            SeasonNumber: 1,
            DisplayNumber: "5");

    /// <summary>
    /// A party playing episode 5 from position 100 as of <paramref name="now"/>,
    /// hosted by the friend so this device is an ordinary member.
    /// </summary>
    private static RemoteWatchPartyState CreatePlayingParty(
        DateTimeOffset now) =>
        new(
            Guid.CreateVersion7(),
            "ABCDEF",
            FriendAccountId,
            [
                new RemoteWatchPartyMember(
                    FriendAccountId,
                    "Bruno",
                    IsHost: true,
                    IsConnected: true),
                new RemoteWatchPartyMember(
                    SelfAccountId,
                    "Alice",
                    IsHost: false,
                    IsConnected: true)
            ],
            CreateContent(),
            new RemoteWatchPartyPlayback(
                IsPlaying: true,
                PositionSeconds: 100,
                now,
                "Bruno"),
            now);

    private sealed class FakeConnection : IWatchPartyConnection
    {
        private readonly Channel<RemoteWatchPartyEvent> _incoming =
            Channel.CreateUnbounded<RemoteWatchPartyEvent>();

        public List<RemoteWatchPartyCommand> Sent { get; } = [];

        public bool FailNextSend { get; set; }
        public int DisposeCount { get; private set; }
        public void CloseIncoming() => _incoming.Writer.TryComplete();

        public void Push(RemoteWatchPartyEvent partyEvent) =>
            _incoming.Writer.TryWrite(partyEvent);

        public ValueTask SendAsync(
            RemoteWatchPartyCommand command,
            CancellationToken cancellationToken = default)
        {
            if (FailNextSend)
            {
                FailNextSend = false;
                throw new IOException("Connection interrupted.");
            }

            lock (Sent)
            {
                Sent.Add(command);
            }

            return ValueTask.CompletedTask;
        }

        public async ValueTask<RemoteWatchPartyEvent?> ReceiveAsync(
            CancellationToken cancellationToken = default)
        {
            try
            {
                return await _incoming.Reader.ReadAsync(cancellationToken);
            }
            catch (ChannelClosedException)
            {
                return null;
            }
        }

        public ValueTask DisposeAsync()
        {
            DisposeCount++;
            _incoming.Writer.TryComplete();
            return ValueTask.CompletedTask;
        }
    }

    /// <summary>
    /// Re-lists the interface so these members implement it: the stub base
    /// established its interface map without them, and a derived method alone
    /// would lose to the interface's own defaults.
    /// </summary>
    private sealed class WatchPartyClient :
        StubRemoteMediaServerClient,
        IRemoteMediaServerClient
    {
        public FakeConnection Connection { get; set; } = new();

        public RemoteWatchPartyState? PartyState { get; set; }

        public ValueTask<RemoteWatchPartyState> CreateWatchPartyAsync(
            RemoteWatchPartyContent? content,
            double positionSeconds,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PartyState ??
                throw new InvalidOperationException(
                    "The test did not stage a party."));

        public ValueTask<RemoteWatchPartyState?> JoinWatchPartyAsync(
            string joinCode,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PartyState);

        public ValueTask<RemoteWatchPartyState?> GetCurrentWatchPartyAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PartyState);

        public ValueTask<bool> LeaveWatchPartyAsync(
            CancellationToken cancellationToken = default)
        {
            PartyState = null;
            return ValueTask.FromResult(true);
        }

        public ValueTask<IWatchPartyConnection> ConnectWatchPartyAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IWatchPartyConnection>(Connection);
    }

    private sealed class FakePlayer : IPlatformMediaPlayer
    {
        public List<bool> PlayingChanges { get; } = [];

        public List<double> Seeks { get; } = [];

        public TaskCompletionSource? SeekRelease { get; set; }

        public PlatformMediaState State { get; set; } = new(
            PositionSeconds: 100,
            DurationSeconds: 1_000,
            IsPlaying: true,
            IsEnded: false,
            IsClosed: false);

        public bool IsAvailable => true;

        public Avalonia.Controls.Control? EmbeddedView => null;

        public bool SupportsTransport { get; init; } = true;

        public bool SupportsTransportControl => SupportsTransport;

        public ValueTask OpenAsync(
            PlatformMediaRequest request,
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;

        public ValueTask<PlatformMediaState> GetStateAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(State);

        public ValueTask SetPlayingAsync(
            bool playing,
            CancellationToken cancellationToken = default)
        {
            lock (PlayingChanges)
            {
                PlayingChanges.Add(playing);
            }

            State = State with { IsPlaying = playing };
            return ValueTask.CompletedTask;
        }

        public async ValueTask SeekAsync(
            double positionSeconds,
            CancellationToken cancellationToken = default)
        {
            lock (Seeks)
            {
                Seeks.Add(positionSeconds);
            }

            if (SeekRelease is not null)
            {
                await SeekRelease.Task.WaitAsync(cancellationToken);
            }
            State = State with { PositionSeconds = positionSeconds };
        }

        public ValueTask CloseAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.CompletedTask;
    }

    private sealed class FakeTimeProvider(DateTimeOffset start) : TimeProvider
    {
        private DateTimeOffset _now = start;

        public void Advance(TimeSpan delta) => _now += delta;

        public override DateTimeOffset GetUtcNow() => _now;
    }
}

using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// A details page asks about friends every time it opens, so what matters here
/// is that it costs one request per title and none at all when there is nobody
/// to ask.
/// </summary>
public sealed class FriendServiceTests
{
    [Fact]
    public async Task NothingIsAskedOfAServerWithoutTheFeature()
    {
        var client = new RecordingClient { Session = CreateSession() };
        var service = new FriendService(client);
        service.ApplyCapabilities(CreateCapabilities(friends: false));

        Assert.False(service.IsAvailable);
        Assert.Empty(await service.GetProgressAsync(
            MediaType.Book,
            "Copper Skies"));
        Assert.Empty(await service.GetFriendsAsync());
        Assert.Equal(0, client.LookupCount);
    }

    [Fact]
    public async Task NothingIsAskedWhileSignedOut()
    {
        var client = new RecordingClient { Session = null };
        var service = new FriendService(client);
        service.ApplyCapabilities(CreateCapabilities(friends: true));

        Assert.False(service.IsAvailable);
        Assert.Empty(await service.GetProgressAsync(
            MediaType.Book,
            "Copper Skies"));
        Assert.Equal(0, client.LookupCount);
    }

    [Fact]
    public async Task AFriendsProgressIsReturnedForTheTitle()
    {
        var service = CreateAvailableService(out var client);
        client.Progress = [CreateProgress("book", "Copper Skies", "75")];

        var progress = Assert.Single(
            await service.GetProgressAsync(MediaType.Book, "Copper Skies"));

        Assert.Equal("75", progress.DisplayNumber);
        Assert.Equal(
            ContentItemKey.Create(MediaType.Book, "Copper Skies"),
            Assert.Single(client.LastItemKeys));
    }

    [Fact]
    public async Task TheSameTitleIsOnlyAskedAboutOnce()
    {
        var service = CreateAvailableService(out var client);
        client.Progress = [CreateProgress("book", "Copper Skies", "75")];

        await service.GetProgressAsync(MediaType.Book, "Copper Skies");
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        Assert.Equal(1, client.LookupCount);
    }

    [Fact]
    public async Task ADifferentTitleIsAskedAboutSeparately()
    {
        var service = CreateAvailableService(out var client);

        await service.GetProgressAsync(MediaType.Book, "Copper Skies");
        await service.GetProgressAsync(MediaType.Book, "Iron Tide");

        Assert.Equal(2, client.LookupCount);
    }

    /// <summary>
    /// A new friend can have progress on a title already looked at, so the
    /// cached answer for it is wrong the moment they are accepted.
    /// </summary>
    [Fact]
    public async Task AcceptingARequestMakesTitlesAskAgain()
    {
        var service = CreateAvailableService(out var client);
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        await service.AcceptRequestAsync(Guid.CreateVersion7());
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        Assert.Equal(2, client.LookupCount);
    }

    [Fact]
    public async Task RemovingAFriendMakesTitlesAskAgain()
    {
        var service = CreateAvailableService(out var client);
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        await service.RemoveFriendAsync(Guid.CreateVersion7());
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        Assert.Equal(2, client.LookupCount);
    }

    [Fact]
    public async Task ChangingVisibilityMakesTitlesAskAgain()
    {
        var service = CreateAvailableService(out var client);
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        await service.SetVisibilityAsync(
            new RemoteFriendVisibility(true, true, []));
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        Assert.Equal(2, client.LookupCount);
    }

    /// <summary>
    /// A different server answers differently about the same titles.
    /// </summary>
    [Fact]
    public async Task SigningIntoAnotherServerDropsTheCache()
    {
        var service = CreateAvailableService(out var client);
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        service.ApplyCapabilities(CreateCapabilities(friends: true));
        await service.GetProgressAsync(MediaType.Book, "Copper Skies");

        Assert.Equal(2, client.LookupCount);
    }

    [Fact]
    public async Task ATitleWithoutANameIsNotAskedAbout()
    {
        var service = CreateAvailableService(out var client);

        Assert.Empty(await service.GetProgressAsync(MediaType.Book, "   "));
        Assert.Equal(0, client.LookupCount);
    }

    [Fact]
    public async Task SharingIsOffWhenThereIsNoServerToAsk()
    {
        var client = new RecordingClient { Session = null };
        var service = new FriendService(client);

        var visibility = await service.GetVisibilityAsync();

        Assert.False(visibility.ShareProgress);
    }

    private static FriendService CreateAvailableService(
        out RecordingClient client)
    {
        client = new RecordingClient { Session = CreateSession() };
        var service = new FriendService(client);
        service.ApplyCapabilities(CreateCapabilities(friends: true));
        return service;
    }

    private static RemoteServerCapabilities CreateCapabilities(bool friends) =>
        new(
            ApiVersion: 1,
            ReleaseNotifications: true,
            ReleaseUploads: false,
            Accounts: true,
            AccountRegistration: false,
            OnlineCatalog: true,
            BackupSync: true,
            SharedLists: true,
            Friends: friends);

    private static RemoteAccountSessionInfo CreateSession() =>
        new(
            Guid.CreateVersion7(),
            "alice",
            "Alice",
            DateTimeOffset.UtcNow.AddDays(1),
            new Uri("https://example.test/"));

    private static RemoteFriendProgress CreateProgress(
        string mediaType,
        string name,
        string displayNumber) =>
        new(
            Guid.CreateVersion7(),
            "Bruno",
            ContentItemKey.Create(mediaType, name),
            "chapter",
            displayNumber,
            null,
            false,
            DateTimeOffset.UtcNow);

    private sealed class RecordingClient : StubRemoteMediaServerClient
    {
        public int LookupCount { get; private set; }

        public IReadOnlyList<string> LastItemKeys { get; private set; } = [];

        public IReadOnlyList<RemoteFriendProgress> Progress { get; set; } = [];

        public override ValueTask<IReadOnlyList<RemoteFriendProgress>>
            LookupFriendProgressAsync(
                IReadOnlyList<string> itemKeys,
                CancellationToken cancellationToken = default)
        {
            LookupCount++;
            LastItemKeys = itemKeys;
            return ValueTask.FromResult(Progress);
        }

        public override ValueTask<IReadOnlyList<RemoteFriend>> GetFriendsAsync(
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<IReadOnlyList<RemoteFriend>>([]);

        public override ValueTask<bool> AcceptFriendRequestAsync(
            Guid accountId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public override ValueTask<bool> RemoveFriendAsync(
            Guid accountId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(true);

        public override ValueTask<RemoteFriendVisibility>
            SetFriendVisibilityAsync(
                RemoteFriendVisibility visibility,
                CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(visibility);
    }
}

/// <summary>
/// A remote client that refuses everything, so a test only has to say what it
/// actually exercises. The interface carries every server capability, and
/// without this each new one would mean editing every fake.
/// </summary>
internal abstract class StubRemoteMediaServerClient : IRemoteMediaServerClient
{
    public RemoteAccountSessionInfo? Session { get; set; }

    public string? AccessToken => null;

    public virtual ValueTask<RemoteAccountSessionInfo?> RestoreSessionAsync(
        Uri serviceBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteServerCapabilities> GetCapabilitiesAsync(
        Uri serviceBaseUri,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteAccountSessionInfo> RegisterAsync(
        Uri serviceBaseUri,
        string username,
        string password,
        string displayName,
        string deviceName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteAccountSessionInfo> LoginAsync(
        Uri serviceBaseUri,
        string username,
        string password,
        string deviceName,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask SignOutAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual void AbandonSession() => Session = null;

    public virtual ValueTask<RemoteSyncSnapshot?> GetSnapshotAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteSyncWriteResult> UploadSnapshotAsync(
        UserSyncDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteSharedList> PublishSharedListAsync(
        Guid listId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<IReadOnlyList<RemoteSharedList>>
        GetSharedListsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask RevokeSharedListAsync(
        string shareId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<IReadOnlyList<RemoteFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<IReadOnlyList<RemoteFriendRequest>>
        GetFriendRequestsAsync(CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteFriendRequestOutcome?>
        SendFriendRequestAsync(
            string username,
            CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<bool> AcceptFriendRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<bool> DeclineFriendRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<bool> RemoveFriendAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<bool> SetFriendSharingAsync(
        Guid accountId,
        bool shareProgress,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteFriendVisibility> GetFriendVisibilityAsync(
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteFriendVisibility> SetFriendVisibilityAsync(
        RemoteFriendVisibility visibility,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<IReadOnlyList<RemoteFriendProgress>>
        LookupFriendProgressAsync(
            IReadOnlyList<string> itemKeys,
            CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<IReadOnlyList<RemoteSharedTitle>>
        GetSharedTitlesAsync(
            Guid accountId,
            CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public virtual ValueTask<RemoteSyncWriteResult> PatchProgressAsync(
        UserSyncDocument patch,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();
}

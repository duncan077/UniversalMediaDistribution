using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.UI.Remote;

/// <summary>
/// What a server supports.
/// <para>
/// <see cref="Friends"/> defaults to false so a server that predates the
/// feature reports it absent rather than absent by omission, and the client
/// hides it instead of offering pages whose every request would answer 404.
/// </para>
/// </summary>
public sealed record RemoteServerCapabilities(
    int ApiVersion,
    bool ReleaseNotifications,
    bool ReleaseUploads,
    bool Accounts,
    bool AccountRegistration,
    bool OnlineCatalog,
    bool BackupSync,
    bool SharedLists,
    int SyncProtocolVersion = 1,
    long MaximumSyncBytes = 40L * 1024 * 1024,
    bool Friends = false,
    bool ProgressPatch = false,
    bool DeltaSync = false,
    bool WatchParties = false,
    bool GzipSyncRequests = false);

/// <summary>
/// The records that changed on the server after a known revision, as a
/// partial document — or the whole snapshot when the server cannot answer
/// incrementally and says so with <see cref="Full"/>.
/// </summary>
public sealed record RemoteSyncChanges(
    long FromRevision,
    long ToRevision,
    bool Full,
    UserSyncDocument Document);

public sealed record RemoteAccountSessionInfo(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset ExpiresAt,
    Uri ServiceBaseUri);

public sealed record RemoteSyncSnapshot(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId,
    UserSyncDocument Document);

public sealed record RemoteSyncWriteResult(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId);

public sealed record RemoteSharedList(
    string ShareId,
    Guid ListId,
    string Name,
    string OwnerDisplayName,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    Uri CatalogUri);

/// <summary>
/// An accepted friendship, seen from this account.
/// <see cref="SharesProgress"/> is this account's own choice about them, so it
/// is what a per-friend toggle reads and writes.
/// </summary>
public sealed record RemoteFriend(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset Since,
    bool SharesProgress,
    DateTimeOffset? ProgressUpdatedAt);

public sealed record RemoteFriendRequest(
    Guid AccountId,
    string Username,
    string DisplayName,
    bool IsIncoming,
    DateTimeOffset CreatedAt);

/// <summary>
/// One title as it stands between this account and a friend.
/// </summary>
public sealed record RemoteFriendComparisonTitle(
    string ItemKey,
    string Title,
    bool ViewerFavorite,
    bool FriendFavorite,
    bool ViewerSeen,
    bool FriendSeen,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt,
    string? Subtype = null);

/// <summary>
/// Two libraries side by side: shared, theirs alone, mine alone.
/// </summary>
public sealed record RemoteFriendComparison(
    IReadOnlyList<RemoteFriendComparisonTitle> InCommon,
    IReadOnlyList<RemoteFriendComparisonTitle> FriendOnly,
    IReadOnlyList<RemoteFriendComparisonTitle> ViewerOnly)
{
    public static RemoteFriendComparison Empty { get; } = new([], [], []);

    public bool IsEmpty =>
        InCommon.Count == 0 &&
        FriendOnly.Count == 0 &&
        ViewerOnly.Count == 0;
}

public sealed record RemoteFriendVisibility(
    bool ShareProgress,
    bool DiscoverableByUsername,
    IReadOnlyList<string> HiddenItemKeys);

/// <summary>
/// How far one friend has reached in one title.
/// </summary>
public sealed record RemoteFriendProgress(
    Guid AccountId,
    string DisplayName,
    string ItemKey,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt);

/// <summary>
/// A title both accounts have progress in, with how far the friend reached.
/// </summary>
public sealed record RemoteSharedTitle(
    string ItemKey,
    string Title,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt);

/// <summary>
/// The outcome of asking to add someone. A request can complete straight away
/// when the other person had already asked.
/// </summary>
public enum RemoteFriendRequestOutcome
{
    Sent = 1,
    Accepted = 2
}

public sealed class RemoteSyncConflictException(
    long currentRevision,
    string message) : Exception(message)
{
    public long CurrentRevision { get; } = currentRevision;
}

/// <summary>
/// What a watch party is watching. <see cref="ItemKey"/> is the cross-device
/// title identity friends already match progress on; the unit fields name one
/// episode/track inside it. The server derives the key itself, so it may be
/// left empty when creating or updating content.
/// </summary>
public sealed record RemoteWatchPartyContent(
    string ItemKey,
    string MediaType,
    string Name,
    string Title,
    string? UnitKind,
    int? SeasonNumber,
    string? DisplayNumber);

public sealed record RemoteWatchPartyMember(
    Guid AccountId,
    string DisplayName,
    bool IsHost,
    bool IsConnected,
    bool IsBuffering = false);

/// <summary>
/// The shared timeline: where it stood at <see cref="UpdatedAt"/> and whether
/// it has been running since. The current position is that stored position
/// plus the elapsed time when playing.
/// </summary>
public sealed record RemoteWatchPartyPlayback(
    bool IsPlaying,
    double PositionSeconds,
    DateTimeOffset UpdatedAt,
    string UpdatedByDisplayName)
{
    public double PositionAt(DateTimeOffset now) =>
        IsPlaying
            ? PositionSeconds +
              Math.Max(0, (now - UpdatedAt).TotalSeconds)
            : PositionSeconds;
}

public sealed record RemoteWatchPartyState(
    Guid PartyId,
    string JoinCode,
    Guid HostAccountId,
    IReadOnlyList<RemoteWatchPartyMember> Members,
    RemoteWatchPartyContent? Content,
    RemoteWatchPartyPlayback Playback,
    DateTimeOffset CreatedAt,
    bool HostControlsOnly = false);

/// <summary>
/// One realtime update from the party socket: the complete current state and
/// why it changed, so a missed message never leaves a member behind.
/// </summary>
public sealed record RemoteWatchPartyEvent(
    string Reason,
    long Sequence,
    Guid? InitiatedByAccountId,
    string? InitiatedByDisplayName,
    RemoteWatchPartyState? State);

/// <summary>
/// A realtime command from this member: <c>play</c>, <c>pause</c>,
/// <c>seek</c>, the host's <c>position</c> heartbeat, or the host's
/// <c>content</c> change.
/// </summary>
public sealed record RemoteWatchPartyCommand(
    string Type,
    double? PositionSeconds = null,
    RemoteWatchPartyContent? Content = null,
    bool? IsBuffering = null);

/// <summary>
/// One live connection to a watch party. Reading answers null when the server
/// closed the socket — the party ended, or the connection dropped.
/// </summary>
public interface IWatchPartyConnection : IAsyncDisposable
{
    ValueTask SendAsync(
        RemoteWatchPartyCommand command,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteWatchPartyEvent?> ReceiveAsync(
        CancellationToken cancellationToken = default);
}

public interface IRemoteMediaServerClient
{
    RemoteAccountSessionInfo? Session { get; }

    /// <summary>
    /// The bearer token of the current session, so the caller can keep it for
    /// the next application run. Null while signed out.
    /// </summary>
    string? AccessToken { get; }

    /// <summary>
    /// Adopts a token kept from an earlier run and confirms it with the server.
    /// Returns <see langword="null"/> when the server no longer honours it, in
    /// which case the client stays signed out and the caller should forget the
    /// stored token.
    /// </summary>
    ValueTask<RemoteAccountSessionInfo?> RestoreSessionAsync(
        Uri serviceBaseUri,
        string accessToken,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteServerCapabilities> GetCapabilitiesAsync(
        Uri serviceBaseUri,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteAccountSessionInfo> RegisterAsync(
        Uri serviceBaseUri,
        string username,
        string password,
        string displayName,
        string deviceName,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteAccountSessionInfo> LoginAsync(
        Uri serviceBaseUri,
        string username,
        string password,
        string deviceName,
        CancellationToken cancellationToken = default);

    ValueTask SignOutAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Forgets the current session without telling the server about it.
    /// <para>
    /// Signing out revokes the token, which is right when someone asks to sign
    /// out and wrong when the application changes profile: that token belongs
    /// to the profile being left, and it has to still work when that profile is
    /// chosen again.
    /// </para>
    /// </summary>
    void AbandonSession();

    ValueTask<RemoteSyncSnapshot?> GetSnapshotAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// What changed after <paramref name="sinceRevision"/>. Null when the
    /// account has no snapshot — or when this client does not implement the
    /// call, which reads the same way: sync the full snapshot instead.
    /// </summary>
    ValueTask<RemoteSyncChanges?> GetChangesAsync(
        long sinceRevision,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<RemoteSyncChanges?>(null);

    ValueTask<RemoteSyncWriteResult> UploadSnapshotAsync(
        UserSyncDocument document,
        long expectedRevision,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteSharedList> PublishSharedListAsync(
        Guid listId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RemoteSharedList>> GetSharedListsAsync(
        CancellationToken cancellationToken = default);

    ValueTask RevokeSharedListAsync(
        string shareId,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RemoteFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RemoteFriendRequest>> GetFriendRequestsAsync(
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Asks to add an account by its exact name.
    /// </summary>
    /// <returns>
    /// <see langword="null"/> when no account of that name accepts requests,
    /// which is also the answer for an account that exists but has turned
    /// discovery off.
    /// </returns>
    ValueTask<RemoteFriendRequestOutcome?> SendFriendRequestAsync(
        string username,
        CancellationToken cancellationToken = default);

    ValueTask<bool> AcceptFriendRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Declines an incoming request, or withdraws one already sent.
    /// </summary>
    ValueTask<bool> DeclineFriendRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> RemoveFriendAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);

    ValueTask<bool> SetFriendSharingAsync(
        Guid accountId,
        bool shareProgress,
        CancellationToken cancellationToken = default);

    ValueTask<RemoteFriendVisibility> GetFriendVisibilityAsync(
        CancellationToken cancellationToken = default);

    ValueTask<RemoteFriendVisibility> SetFriendVisibilityAsync(
        RemoteFriendVisibility visibility,
        CancellationToken cancellationToken = default);

    ValueTask<IReadOnlyList<RemoteFriendProgress>> LookupFriendProgressAsync(
        IReadOnlyList<string> itemKeys,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Titles this account and the named friend have both made progress in.
    /// </summary>
    ValueTask<IReadOnlyList<RemoteSharedTitle>> GetSharedTitlesAsync(
        Guid accountId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Both libraries compared. Empty when this client cannot ask — which
    /// reads the same way a server with nothing to compare does.
    /// </summary>
    ValueTask<RemoteFriendComparison> GetFriendComparisonAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(RemoteFriendComparison.Empty);

    /// <summary>
    /// Merges a few changed positions into the stored snapshot instead of
    /// replacing it. Carries no expected revision: a patch is merged record by
    /// record, so another device syncing in between cannot make it fail.
    /// </summary>
    ValueTask<RemoteSyncWriteResult> PatchProgressAsync(
        UserSyncDocument patch,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Opens a new watch party, leaving any party this account was already in.
    /// </summary>
    ValueTask<RemoteWatchPartyState> CreateWatchPartyAsync(
        RemoteWatchPartyContent? content,
        double positionSeconds,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<RemoteWatchPartyState>(
            new NotSupportedException(
                "This client does not support watch parties."));

    /// <summary>
    /// Joins the party using that code. Null when no party uses it — expired,
    /// mistyped, or ended.
    /// </summary>
    ValueTask<RemoteWatchPartyState?> JoinWatchPartyAsync(
        string joinCode,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<RemoteWatchPartyState?>(null);

    /// <summary>
    /// The party this account is currently in, or null.
    /// </summary>
    ValueTask<RemoteWatchPartyState?> GetCurrentWatchPartyAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<RemoteWatchPartyState?>(null);

    ValueTask<bool> LeaveWatchPartyAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(false);

    /// <summary>
    /// Locks transport control to the host, or unlocks it. Only the host may
    /// ask; null when this account is not in a party or the server refused.
    /// </summary>
    ValueTask<RemoteWatchPartyState?> SetWatchPartyOptionsAsync(
        bool hostControlsOnly,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult<RemoteWatchPartyState?>(null);

    /// <summary>
    /// Opens the realtime socket for the party this account is in. Join or
    /// create first.
    /// </summary>
    ValueTask<IWatchPartyConnection> ConnectWatchPartyAsync(
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<IWatchPartyConnection>(
            new NotSupportedException(
                "This client does not support watch parties."));
}

using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.Remote;

/// <summary>
/// The friend list and what friends have reached, over whatever remote session
/// is currently signed in.
/// <para>
/// Everything here answers empty rather than throwing when the feature is not
/// available — signed out, or a server too old to carry it. A details page asks
/// about friends as a matter of course, and a viewer with no account should not
/// see that turn into an error.
/// </para>
/// </summary>
public sealed class FriendService
{
    /// <summary>
    /// Matches the server's own cap, so a caller batching titles is told to
    /// split before the request is made rather than after it is refused.
    /// </summary>
    public const int MaximumLookupKeys = 200;

    private readonly IRemoteMediaServerClient _client;
    private readonly Lock _gate = new();
    private readonly Dictionary<string, IReadOnlyList<RemoteFriendProgress>>
        _progressByItemKey = new(StringComparer.Ordinal);

    private bool _supportsFriends;

    public FriendService(IRemoteMediaServerClient client) =>
        _client = client ?? throw new ArgumentNullException(nameof(client));

    /// <summary>
    /// Whether friends can be used at all: signed in, against a server that
    /// carries the feature.
    /// </summary>
    public bool IsAvailable
    {
        get
        {
            lock (_gate)
            {
                return _supportsFriends && _client.Session is not null;
            }
        }
    }

    /// <summary>
    /// Records what the signed-in server supports. Clears the cache too, since
    /// a different server answers differently about the same titles.
    /// </summary>
    public void ApplyCapabilities(RemoteServerCapabilities? capabilities)
    {
        lock (_gate)
        {
            _supportsFriends = capabilities?.Friends is true;
            _progressByItemKey.Clear();
        }
    }

    /// <summary>
    /// Forgets cached progress, so the next details page asks again. Called
    /// after anything that changes who can be seen.
    /// </summary>
    public void InvalidateProgress()
    {
        lock (_gate)
        {
            _progressByItemKey.Clear();
        }
    }

    public async ValueTask<IReadOnlyList<RemoteFriend>> GetFriendsAsync(
        CancellationToken cancellationToken = default) =>
        IsAvailable
            ? await _client.GetFriendsAsync(cancellationToken)
            : [];

    public async ValueTask<IReadOnlyList<RemoteFriendRequest>>
        GetRequestsAsync(CancellationToken cancellationToken = default) =>
        IsAvailable
            ? await _client.GetFriendRequestsAsync(cancellationToken)
            : [];

    public ValueTask<RemoteFriendRequestOutcome?> SendRequestAsync(
        string username,
        CancellationToken cancellationToken = default) =>
        IsAvailable
            ? _client.SendFriendRequestAsync(username, cancellationToken)
            : ValueTask.FromResult<RemoteFriendRequestOutcome?>(null);

    public async ValueTask<bool> AcceptRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var accepted = await _client.AcceptFriendRequestAsync(
            accountId,
            cancellationToken);
        if (accepted)
        {
            // A new friend can have progress on titles already looked at.
            InvalidateProgress();
        }

        return accepted;
    }

    public ValueTask<bool> DeclineRequestAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        IsAvailable
            ? _client.DeclineFriendRequestAsync(accountId, cancellationToken)
            : ValueTask.FromResult(false);

    public async ValueTask<bool> RemoveFriendAsync(
        Guid accountId,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return false;
        }

        var removed = await _client.RemoveFriendAsync(
            accountId,
            cancellationToken);
        if (removed)
        {
            InvalidateProgress();
        }

        return removed;
    }

    public async ValueTask<bool> SetFriendSharingAsync(
        Guid accountId,
        bool shareProgress,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return false;
        }

        return await _client.SetFriendSharingAsync(
            accountId,
            shareProgress,
            cancellationToken);
    }

    public async ValueTask<RemoteFriendVisibility> GetVisibilityAsync(
        CancellationToken cancellationToken = default) =>
        IsAvailable
            ? await _client.GetFriendVisibilityAsync(cancellationToken)
            : new RemoteFriendVisibility(false, true, []);

    public async ValueTask<RemoteFriendVisibility> SetVisibilityAsync(
        RemoteFriendVisibility visibility,
        CancellationToken cancellationToken = default)
    {
        if (!IsAvailable)
        {
            return visibility;
        }

        var saved = await _client.SetFriendVisibilityAsync(
            visibility,
            cancellationToken);

        // Hiding a title only takes effect on the next sync, but the cached
        // answer for it is wrong the moment the setting changes.
        InvalidateProgress();
        return saved;
    }

    /// <summary>
    /// Titles this account and one friend have both made progress in.
    /// </summary>
    public async ValueTask<IReadOnlyList<RemoteSharedTitle>>
        GetSharedTitlesAsync(
            Guid accountId,
            CancellationToken cancellationToken = default) =>
        IsAvailable
            ? await _client.GetSharedTitlesAsync(accountId, cancellationToken)
            : [];

    /// <summary>
    /// Both libraries compared: shared, theirs alone, mine alone.
    /// </summary>
    public async ValueTask<RemoteFriendComparison> GetComparisonAsync(
        Guid accountId,
        CancellationToken cancellationToken = default) =>
        IsAvailable
            ? await _client.GetFriendComparisonAsync(
                accountId,
                cancellationToken)
            : RemoteFriendComparison.Empty;

    /// <summary>
    /// What friends have reached in one title.
    /// <para>
    /// Cached by title: a details page asks every time it opens, and going back
    /// to a title just visited should not be another round trip. Anything that
    /// changes who is visible clears the cache rather than expiring it on a
    /// timer, so the answer is never quietly stale for a reason the viewer
    /// caused themselves.
    /// </para>
    /// </summary>
    public async ValueTask<IReadOnlyList<RemoteFriendProgress>>
        GetProgressAsync(
            MediaType mediaType,
            string name,
            CancellationToken cancellationToken = default)
    {
        if (!IsAvailable || string.IsNullOrWhiteSpace(name))
        {
            return [];
        }

        string itemKey;
        try
        {
            itemKey = ContentItemKey.Create(mediaType, name);
        }
        catch (ArgumentException)
        {
            return [];
        }

        lock (_gate)
        {
            if (_progressByItemKey.TryGetValue(itemKey, out var cached))
            {
                return cached;
            }
        }

        var matches = await _client.LookupFriendProgressAsync(
            [itemKey],
            cancellationToken);
        lock (_gate)
        {
            _progressByItemKey[itemKey] = matches;
        }

        return matches;
    }
}

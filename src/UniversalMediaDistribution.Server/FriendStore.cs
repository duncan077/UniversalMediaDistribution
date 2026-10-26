using System.Globalization;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Server;

internal sealed record FriendRequestCreateRequest(string Username);

internal sealed record FriendVisibilityUpdateRequest(
    bool ShareProgress,
    bool DiscoverableByUsername,
    string[] HiddenItemKeys);

internal sealed record FriendActivityLookupRequest(string[] ItemKeys);

/// <summary>
/// One direction of a pending friendship. Both directions are written so the
/// sender can list what they have asked for without a scan of every account.
/// </summary>
internal sealed record FriendRequestRecord(
    Guid FromAccountId,
    string FromUsername,
    string FromDisplayName,
    Guid ToAccountId,
    string ToUsername,
    string ToDisplayName,
    DateTimeOffset CreatedAt);

/// <summary>
/// One side of an accepted friendship. A friendship exists only when both
/// files are present, so a half-written accept can never expose progress.
/// <para>
/// <see cref="ShareProgress"/> is this side's answer about the other, so the
/// edge an account owns says who it shares with. A file written before this
/// existed reads as false, which errs towards showing nothing.
/// </para>
/// </summary>
internal sealed record FriendEdgeRecord(
    Guid AccountId,
    Guid FriendAccountId,
    string FriendUsername,
    string FriendDisplayName,
    DateTimeOffset Since,
    bool ShareProgress);

internal sealed record FriendSharingUpdateRequest(bool ShareProgress);

internal sealed record FriendVisibilityRecord(
    bool ShareProgress,
    bool DiscoverableByUsername,
    string[] HiddenItemKeys)
{
    public static FriendVisibilityRecord Default { get; } =
        new(ShareProgress: false, DiscoverableByUsername: true, []);
}

/// <summary>
/// The furthest point reached in one title, as offered to friends.
/// </summary>
internal sealed record FriendActivityEntry(
    string ItemKey,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt,
    bool IsFavorite = false,
    string? Title = null,
    bool HasProgress = true,
    string? Subtype = null);

internal sealed record FriendActivityRecord(
    Guid AccountId,
    DateTimeOffset UpdatedAt,
    FriendActivityEntry[] Entries);

/// <summary>
/// <paramref name="ProgressUpdatedAt"/> is when the friend last published
/// progress, which is only as recent as their last sync. Null means they have
/// published none.
/// </summary>
internal sealed record FriendSummaryResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    DateTimeOffset Since,
    bool ShareProgress,
    DateTimeOffset? ProgressUpdatedAt);

internal sealed record FriendRequestResponse(
    Guid AccountId,
    string Username,
    string DisplayName,
    string Direction,
    DateTimeOffset CreatedAt);

internal sealed record FriendActivityMatch(
    Guid AccountId,
    string Username,
    string DisplayName,
    string ItemKey,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt);

internal sealed record FriendActivityLookupResponse(
    FriendActivityMatch[] Matches);

/// <summary>
/// A title both accounts have progress in, with how far the friend reached.
/// </summary>
internal sealed record FriendSharedTitleResponse(
    string ItemKey,
    string Title,
    string? UnitKind,
    string? DisplayNumber,
    int? SeasonNumber,
    bool IsCompleted,
    DateTimeOffset UpdatedAt);

internal sealed record FriendSharedTitlesResponse(
    FriendSharedTitleResponse[] Titles);

/// <summary>
/// One title as it stands between two accounts: who marked it a favourite,
/// who has watched or read any of it, and how far the friend reached.
/// </summary>
internal sealed record FriendComparisonTitleResponse(
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
/// Two libraries side by side: what both accounts have, what only the friend
/// has, and what only this account has.
/// </summary>
internal sealed record FriendComparisonResponse(
    FriendComparisonTitleResponse[] InCommon,
    FriendComparisonTitleResponse[] FriendOnly,
    FriendComparisonTitleResponse[] ViewerOnly);

internal sealed class FriendConflictException(string message) :
    Exception(message);

internal sealed class SqliteFriendStore
{
    /// <summary>
    /// Enough to answer a details page many times over without letting one
    /// request walk a whole library.
    /// </summary>
    public const int MaximumLookupKeys = 200;

    /// <summary>
    /// Enough of each side of a comparison to browse, without answering one
    /// request with two whole libraries.
    /// </summary>
    public const int MaximumComparisonTitles = 300;

    public const int MaximumHiddenItemKeys = 5_000;
    public const int MaximumFriends = 500;

    private readonly string _root;
    private readonly ServerDatabase _storage;
    private SemaphoreSlim _writeGate => _storage.WriteGate;

    public SqliteFriendStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _storage = new ServerDatabase(dataDirectory);
        _root = Path.GetFullPath(Path.Combine(dataDirectory, "friends"));
    }

    /// <summary>
    /// Records a request in both directions, or completes the friendship when
    /// the other side has already asked.
    /// </summary>
    /// <returns>
    /// True when the request completed a friendship, so the caller can tell a
    /// viewer their request was answered immediately.
    /// </returns>
    public async ValueTask<bool> SendRequestAsync(
        AccountRecord from,
        AccountRecord to,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(from);
        ArgumentNullException.ThrowIfNull(to);
        if (from.AccountId == to.AccountId)
        {
            throw new FriendConflictException(
                "An account cannot add itself.");
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            if (_storage.Exists(GetEdgePath(from.AccountId, to.AccountId)))
            {
                throw new FriendConflictException(
                    "That account is already a friend.");
            }

            // The other side asking first turns this into an accept. Without
            // it two people who both send a request sit waiting on each other.
            var incoming = await ReadRequestAsync(
                to.AccountId,
                from.AccountId,
                cancellationToken);
            if (incoming is not null)
            {
                await CreateFriendshipAsync(from, to, cancellationToken);
                return write.CommitAndReturn(true);
            }

            var now = DateTimeOffset.UtcNow;
            var record = new FriendRequestRecord(
                from.AccountId,
                from.Username,
                from.DisplayName,
                to.AccountId,
                to.Username,
                to.DisplayName,
                now);
            await WriteRequestAsync(record, cancellationToken);
            return write.CommitAndReturn(false);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<bool> AcceptRequestAsync(
        AccountRecord account,
        AccountRecord requester,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(requester);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var incoming = await ReadRequestAsync(
                requester.AccountId,
                account.AccountId,
                cancellationToken);
            if (incoming is null)
            {
                return write.CommitAndReturn(false);
            }

            await CreateFriendshipAsync(account, requester, cancellationToken);
            return write.CommitAndReturn(true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<bool> DeclineRequestAsync(
        Guid accountId,
        Guid otherAccountId,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            cancellationToken.ThrowIfCancellationRequested();

            // Either direction may be the one that exists: declining an
            // incoming request and withdrawing an outgoing one are the same
            // operation from opposite ends.
            var removed = TryDeleteRequestPair(otherAccountId, accountId);
            return write.CommitAndReturn(TryDeleteRequestPair(accountId, otherAccountId) || removed);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<bool> RemoveFriendAsync(
        Guid accountId,
        Guid friendAccountId,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            cancellationToken.ThrowIfCancellationRequested();
            var removed = _storage.TryDeleteReporting(
                GetEdgePath(accountId, friendAccountId));

            // Both sides go. A friendship that survived on one side would keep
            // serving that side's progress to someone who had been removed.
            _storage.TryDelete(
                GetEdgePath(friendAccountId, accountId));
            return write.CommitAndReturn(removed);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<IReadOnlyList<FriendSummaryResponse>> ListFriendsAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var edges = new List<FriendSummaryResponse>();
        foreach (var path in EnumerateJson(GetEdgeDirectory(accountId)))
        {
            var edge = await _storage.ReadJsonAsync(
                path,
                ServerJsonSerializerContext.Default.FriendEdgeRecord,
                cancellationToken);
            if (edge is null)
            {
                continue;
            }

            // How current their half is. Read here so the viewer can tell an
            // empty answer from a stale one.
            var activity = await _storage.ReadJsonAsync(
                GetActivityPath(edge.FriendAccountId),
                ServerJsonSerializerContext.Default.FriendActivityRecord,
                cancellationToken);
            edges.Add(new FriendSummaryResponse(
                edge.FriendAccountId,
                edge.FriendUsername,
                edge.FriendDisplayName,
                edge.Since,
                edge.ShareProgress,
                activity?.UpdatedAt));
        }

        return edges
            .OrderBy(static friend => friend.DisplayName, StringComparer.Ordinal)
            .ToArray();
    }

    public async ValueTask<IReadOnlyList<FriendRequestResponse>>
        ListRequestsAsync(
            Guid accountId,
            CancellationToken cancellationToken)
    {
        var requests = new List<FriendRequestResponse>();
        foreach (var path in EnumerateJson(
                     GetRequestDirectory("in", accountId)))
        {
            var record = await _storage.ReadJsonAsync(
                path,
                ServerJsonSerializerContext.Default.FriendRequestRecord,
                cancellationToken);
            if (record is not null)
            {
                requests.Add(new FriendRequestResponse(
                    record.FromAccountId,
                    record.FromUsername,
                    record.FromDisplayName,
                    "incoming",
                    record.CreatedAt));
            }
        }

        foreach (var path in EnumerateJson(
                     GetRequestDirectory("out", accountId)))
        {
            var record = await _storage.ReadJsonAsync(
                path,
                ServerJsonSerializerContext.Default.FriendRequestRecord,
                cancellationToken);
            if (record is not null)
            {
                requests.Add(new FriendRequestResponse(
                    record.ToAccountId,
                    record.ToUsername,
                    record.ToDisplayName,
                    "outgoing",
                    record.CreatedAt));
            }
        }

        return requests
            .OrderByDescending(static request => request.CreatedAt)
            .ToArray();
    }

    /// <summary>
    /// Chooses whether one particular friend sees this account's progress.
    /// </summary>
    public async ValueTask<bool> SetFriendSharingAsync(
        Guid accountId,
        Guid friendAccountId,
        bool shareProgress,
        CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var path = GetEdgePath(accountId, friendAccountId);
            var edge = await _storage.ReadJsonAsync(
                path,
                ServerJsonSerializerContext.Default.FriendEdgeRecord,
                cancellationToken);
            if (edge is null)
            {
                return write.CommitAndReturn(false);
            }

            await _storage.WriteJsonAtomicAsync(
                path,
                edge with { ShareProgress = shareProgress },
                ServerJsonSerializerContext.Default.FriendEdgeRecord,
                cancellationToken);
            return write.CommitAndReturn(true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask<FriendVisibilityRecord> GetVisibilityAsync(
        Guid accountId,
        CancellationToken cancellationToken) =>
        await _storage.ReadJsonAsync(
            GetVisibilityPath(accountId),
            ServerJsonSerializerContext.Default.FriendVisibilityRecord,
            cancellationToken) ??
        FriendVisibilityRecord.Default;

    public async ValueTask<FriendVisibilityRecord> SetVisibilityAsync(
        Guid accountId,
        FriendVisibilityUpdateRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.HiddenItemKeys.Length > MaximumHiddenItemKeys)
        {
            throw new ArgumentException(
                $"At most {MaximumHiddenItemKeys} titles can be hidden.",
                nameof(request));
        }

        var record = new FriendVisibilityRecord(
            request.ShareProgress,
            request.DiscoverableByUsername,
            request.HiddenItemKeys
                .Where(static key => !string.IsNullOrWhiteSpace(key))
                .Select(static key => key.Trim())
                .Distinct(StringComparer.Ordinal)
                .ToArray());

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            await _storage.WriteJsonAtomicAsync(
                GetVisibilityPath(accountId),
                record,
                ServerJsonSerializerContext.Default.FriendVisibilityRecord,
                cancellationToken);
            return write.CommitAndReturn(record);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Rebuilds the account's shareable activity from its latest sync
    /// snapshot.
    /// <para>
    /// Driven from sync rather than a separate upload, the way shared lists
    /// are: the snapshot is already the authoritative copy of progress, and a
    /// second path would let the two disagree. Nothing is written while the
    /// account has sharing switched off, so turning it off and syncing clears
    /// what friends can see.
    /// </para>
    /// </summary>
    public async ValueTask SynchronizeAsync(
        AccountRecord account,
        UserSyncSnapshotState snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(snapshot);
        var visibility = await GetVisibilityAsync(
            account.AccountId,
            cancellationToken);
        var activityPath = GetActivityPath(account.AccountId);
        if (!visibility.ShareProgress)
        {
            await _writeGate.WaitAsync(cancellationToken);
            try
            {
                using var write = _storage.BeginWrite();
                _storage.TryDelete(activityPath);
                write.Commit();
            }
            finally
            {
                _writeGate.Release();
            }

            return;
        }

        var hidden = visibility.HiddenItemKeys.ToHashSet(StringComparer.Ordinal);
        var entries = BuildActivityEntries(snapshot.Document, hidden);
        var record = new FriendActivityRecord(
            account.AccountId,
            DateTimeOffset.UtcNow,
            entries);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            await _storage.WriteJsonAtomicAsync(
                activityPath,
                record,
                ServerJsonSerializerContext.Default.FriendActivityRecord,
                cancellationToken);
            write.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Titles both accounts have progress in.
    /// <para>
    /// Both halves are read here rather than sent up by the client: the asking
    /// account's half comes from its own sync snapshot, so it works whether or
    /// not that account shares anything, and the friend's half still passes
    /// every test that guards an ordinary lookup.
    /// </para>
    /// </summary>
    public async ValueTask<IReadOnlyList<FriendSharedTitleResponse>>
        ListSharedTitlesAsync(
            Guid accountId,
            Guid friendAccountId,
            UserSyncSnapshotState? viewerSnapshot,
            CancellationToken cancellationToken)
    {
        if (viewerSnapshot is null)
        {
            return [];
        }

        // The friendship, then their decision about this account, in that
        // order — the same two gates an ordinary lookup passes.
        var ownEdge = await _storage.ReadJsonAsync(
            GetEdgePath(accountId, friendAccountId),
            ServerJsonSerializerContext.Default.FriendEdgeRecord,
            cancellationToken);
        var theirEdge = await _storage.ReadJsonAsync(
            GetEdgePath(friendAccountId, accountId),
            ServerJsonSerializerContext.Default.FriendEdgeRecord,
            cancellationToken);
        if (ownEdge is null || theirEdge?.ShareProgress is not true)
        {
            return [];
        }

        var activity = await _storage.ReadJsonAsync(
            GetActivityPath(friendAccountId),
            ServerJsonSerializerContext.Default.FriendActivityRecord,
            cancellationToken);
        if (activity is null)
        {
            return [];
        }

        // The asking account's own titles are never filtered by its hidden
        // list: that list decides what others see of it, not what it sees.
        var mine = ReduceProgress(viewerSnapshot.Document, []);
        return activity.Entries
            .Where(entry => mine.ContainsKey(entry.ItemKey))
            .Select(entry => new FriendSharedTitleResponse(
                entry.ItemKey,
                mine[entry.ItemKey].Title,
                entry.UnitKind,
                entry.DisplayNumber,
                entry.SeasonNumber,
                entry.IsCompleted,
                entry.UpdatedAt))
            .OrderByDescending(static title => title.UpdatedAt)
            .ToArray();
    }

    /// <summary>
    /// The furthest point each friend has reached in the requested titles.
    /// </summary>
    public async ValueTask<IReadOnlyList<FriendActivityMatch>>
        LookupActivityAsync(
            Guid accountId,
            IReadOnlyList<string> itemKeys,
            CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(itemKeys);
        if (itemKeys.Count > MaximumLookupKeys)
        {
            throw new ArgumentException(
                $"At most {MaximumLookupKeys} titles can be looked up at once.",
                nameof(itemKeys));
        }

        var wanted = itemKeys
            .Where(static key => !string.IsNullOrWhiteSpace(key))
            .Select(static key => key.Trim())
            .ToHashSet(StringComparer.Ordinal);
        if (wanted.Count == 0)
        {
            return [];
        }

        var matches = new List<FriendActivityMatch>();
        foreach (var friend in await ListFriendsAsync(
                     accountId,
                     cancellationToken))
        {
            // Their edge towards this account, not this account's towards them:
            // whether progress is shared is the owner's decision, and the copy
            // read here has to be the one they control.
            var theirEdge = await _storage.ReadJsonAsync(
                GetEdgePath(friend.AccountId, accountId),
                ServerJsonSerializerContext.Default.FriendEdgeRecord,
                cancellationToken);
            if (theirEdge?.ShareProgress is not true)
            {
                continue;
            }

            var activity = await _storage.ReadJsonAsync(
                GetActivityPath(friend.AccountId),
                ServerJsonSerializerContext.Default.FriendActivityRecord,
                cancellationToken);
            if (activity is null)
            {
                continue;
            }

            matches.AddRange(activity.Entries
                .Where(entry => wanted.Contains(entry.ItemKey))
                .Select(entry => new FriendActivityMatch(
                    friend.AccountId,
                    friend.Username,
                    friend.DisplayName,
                    entry.ItemKey,
                    entry.UnitKind,
                    entry.DisplayNumber,
                    entry.SeasonNumber,
                    entry.IsCompleted,
                    entry.UpdatedAt)));
        }

        return matches;
    }

    /// <summary>
    /// Reduces every progress entry to one row per title.
    /// <para>
    /// Progress is stored per unit, but a details page asks a single question —
    /// how far has this person got. Furthest wins over most recent, because
    /// re-reading an early chapter should not walk the answer backwards; where
    /// the number will not parse, the most recently touched unit stands in.
    /// </para>
    /// </summary>
    private static FriendActivityEntry[] BuildActivityEntries(
        UserSyncDocument document,
        HashSet<string> hiddenItemKeys)
    {
        // Where each title sits in the catalog, so a friend's title the
        // viewer does not hold can still be searched for under manga, film or
        // anime rather than everywhere at once.
        var subtypes = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var item in document.Catalog.Items)
        {
            try
            {
                subtypes[CreateItemKey(item.MediaType, item.Name)] =
                    item.Subtype;
            }
            catch (ArgumentException)
            {
                // One unkeyable title costs itself, not the projection.
            }
        }

        var entries = new Dictionary<string, FriendActivityEntry>(
            StringComparer.Ordinal);
        foreach (var (itemKey, reduced) in
                 ReduceProgress(document, hiddenItemKeys))
        {
            entries[itemKey] = reduced.Entry with
            {
                Title = reduced.Title,
                Subtype = subtypes.GetValueOrDefault(itemKey)
            };
        }

        // A favourite marks what is already there rather than joining on its
        // own: a title nobody has watched is not something two people have in
        // common, however either of them feels about it. What survives here
        // has an episode, chapter or film behind it, and the star only says
        // how its owner feels about that.
        foreach (var favorite in document.FavoriteItemStates)
        {
            if (!favorite.IsFavorite)
            {
                continue;
            }

            string itemKey;
            try
            {
                itemKey = CreateItemKey(
                    favorite.Item.MediaType,
                    favorite.Item.Name);
            }
            catch (ArgumentException)
            {
                continue;
            }

            if (hiddenItemKeys.Contains(itemKey))
            {
                continue;
            }

            if (entries.TryGetValue(itemKey, out var existing))
            {
                entries[itemKey] = existing with { IsFavorite = true };
            }
        }

        return entries.Values
            .OrderByDescending(static entry => entry.UpdatedAt)
            .ToArray();
    }

    /// <summary>
    /// Both libraries side by side, bucketed into what they share and what
    /// each has alone.
    /// </summary>
    /// <remarks>
    /// Read here rather than sent up by the client for the same reason the
    /// shared-titles answer is: this account's half comes from its own
    /// snapshot, so it works whether or not it shares anything back, and the
    /// friend's half passes the same two gates every other lookup passes.
    /// </remarks>
    public async ValueTask<FriendComparisonResponse> CompareAsync(
        Guid accountId,
        Guid friendAccountId,
        UserSyncSnapshotState? viewerSnapshot,
        CancellationToken cancellationToken)
    {
        var empty = new FriendComparisonResponse([], [], []);
        if (viewerSnapshot is null)
        {
            return empty;
        }

        var ownEdge = await _storage.ReadJsonAsync(
            GetEdgePath(accountId, friendAccountId),
            ServerJsonSerializerContext.Default.FriendEdgeRecord,
            cancellationToken);
        var theirEdge = await _storage.ReadJsonAsync(
            GetEdgePath(friendAccountId, accountId),
            ServerJsonSerializerContext.Default.FriendEdgeRecord,
            cancellationToken);
        if (ownEdge is null || theirEdge?.ShareProgress is not true)
        {
            return empty;
        }

        var activity = await _storage.ReadJsonAsync(
            GetActivityPath(friendAccountId),
            ServerJsonSerializerContext.Default.FriendActivityRecord,
            cancellationToken);
        if (activity is null)
        {
            return empty;
        }

        // This account's hidden list decides what others see of it, never what
        // it sees, so its own half is read unfiltered.
        var mineProgress = ReduceProgress(viewerSnapshot.Document, []);
        var myFavorites = new Dictionary<string, string>(
            StringComparer.Ordinal);
        foreach (var favorite in viewerSnapshot.Document.FavoriteItemStates)
        {
            if (!favorite.IsFavorite)
            {
                continue;
            }

            try
            {
                myFavorites[CreateItemKey(
                    favorite.Item.MediaType,
                    favorite.Item.Name)] = favorite.Item.Name;
            }
            catch (ArgumentException)
            {
                // One unkeyable title costs itself, not the comparison.
            }
        }

        var inCommon = new List<FriendComparisonTitleResponse>();
        var friendOnly = new List<FriendComparisonTitleResponse>();
        var theirKeys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var entry in activity.Entries)
        {
            theirKeys.Add(entry.ItemKey);
            var mineSeen = mineProgress.TryGetValue(
                entry.ItemKey,
                out var mineEntry);
            var mineFavorite = myFavorites.TryGetValue(
                entry.ItemKey,
                out var mineFavoriteTitle);
            // Whichever side can name it best: this account's own spelling
            // when it has the title, the friend's otherwise, and the key
            // itself only if neither ever named it.
            var title = mineSeen
                ? mineEntry.Title
                : mineFavoriteTitle ?? entry.Title ?? entry.ItemKey;
            var comparison = new FriendComparisonTitleResponse(
                entry.ItemKey,
                title,
                mineFavorite,
                entry.IsFavorite,
                mineSeen,
                entry.HasProgress,
                entry.UnitKind,
                entry.DisplayNumber,
                entry.SeasonNumber,
                entry.IsCompleted,
                entry.UpdatedAt,
                entry.Subtype);
            if (mineSeen)
            {
                inCommon.Add(comparison);
            }
            else
            {
                friendOnly.Add(comparison);
            }
        }

        var viewerOnly = new List<FriendComparisonTitleResponse>();
        foreach (var (itemKey, reduced) in mineProgress)
        {
            if (theirKeys.Contains(itemKey))
            {
                continue;
            }

            viewerOnly.Add(new FriendComparisonTitleResponse(
                itemKey,
                reduced.Title,
                myFavorites.ContainsKey(itemKey),
                FriendFavorite: false,
                ViewerSeen: true,
                FriendSeen: false,
                reduced.Entry.UnitKind,
                reduced.Entry.DisplayNumber,
                reduced.Entry.SeasonNumber,
                reduced.Entry.IsCompleted,
                reduced.Entry.UpdatedAt));
        }

        return new FriendComparisonResponse(
            Bound(inCommon),
            Bound(friendOnly),
            Bound(viewerOnly));

        static FriendComparisonTitleResponse[] Bound(
            List<FriendComparisonTitleResponse> titles) =>
            titles
                .OrderByDescending(static title => title.UpdatedAt)
                .Take(MaximumComparisonTitles)
                .ToArray();
    }

    /// <summary>
    /// The furthest point per title, keeping the title's own name alongside.
    /// <para>
    /// The published projection drops the name — a key is all a details page
    /// needs, and a normalized key carries less about someone than a list of
    /// what they read. The shared-titles answer needs it back, and takes it
    /// from the asking account's own snapshot rather than the friend's.
    /// </para>
    /// </summary>
    private static Dictionary<string, ReducedProgress> ReduceProgress(
        UserSyncDocument document,
        HashSet<string> hiddenItemKeys)
    {
        var furthest = new Dictionary<string, ReducedProgress>(
            StringComparer.Ordinal);
        foreach (var progress in document.Progress)
        {
            // Opening a title writes a position of zero so it can appear on
            // the recent shelf. That is not having seen it — counting it as
            // such is what fills a comparison with titles neither person
            // ever watched — so a record only speaks for someone once it
            // shows an episode, chapter or film actually reached.
            if (!progress.IsCompleted && progress.Position <= 0)
            {
                continue;
            }

            string itemKey;
            try
            {
                itemKey = CreateItemKey(
                    progress.Content.Item.MediaType,
                    progress.Content.Item.Name);
            }
            catch (ArgumentException)
            {
                // A snapshot written by a newer or broken client should cost
                // one title, not the whole projection.
                continue;
            }

            if (hiddenItemKeys.Contains(itemKey))
            {
                continue;
            }

            var candidate = new ReducedProgress(
                new FriendActivityEntry(
                    itemKey,
                    progress.Content.UnitKind,
                    progress.Content.DisplayNumber,
                    progress.Content.SeasonNumber,
                    progress.IsCompleted,
                    progress.UpdatedAt),
                progress.Content.Item.Name);
            if (!furthest.TryGetValue(itemKey, out var existing) ||
                IsFurther(candidate.Entry, existing.Entry))
            {
                furthest[itemKey] = candidate;
            }
        }

        return furthest;
    }

    private readonly record struct ReducedProgress(
        FriendActivityEntry Entry,
        string Title);

    private static bool IsFurther(
        FriendActivityEntry candidate,
        FriendActivityEntry existing)
    {
        var candidateSeason = candidate.SeasonNumber ?? 0;
        var existingSeason = existing.SeasonNumber ?? 0;
        if (candidateSeason != existingSeason)
        {
            return candidateSeason > existingSeason;
        }

        if (TryParseUnitNumber(candidate.DisplayNumber, out var candidateUnit) &&
            TryParseUnitNumber(existing.DisplayNumber, out var existingUnit))
        {
            return candidateUnit > existingUnit;
        }

        return candidate.UpdatedAt > existing.UpdatedAt;
    }

    /// <summary>
    /// Chapter numbers are text and carry decimals for side chapters, so "10.5"
    /// has to sort between 10 and 11 rather than fail to parse.
    /// </summary>
    private static bool TryParseUnitNumber(string? value, out double number)
    {
        number = 0;
        return !string.IsNullOrWhiteSpace(value) &&
            double.TryParse(
                value,
                NumberStyles.AllowDecimalPoint,
                CultureInfo.InvariantCulture,
                out number);
    }

    /// <summary>
    /// The identity two accounts agree on for the same title. Shared with the
    /// client, which has to build the same string to ask about a title.
    /// </summary>
    public static string CreateItemKey(string mediaType, string name) =>
        ContentItemKey.Create(mediaType, name);

    private async ValueTask CreateFriendshipAsync(
        AccountRecord first,
        AccountRecord second,
        CancellationToken cancellationToken)
    {
        var now = DateTimeOffset.UtcNow;

        // New friends start included rather than muted. The account-wide switch
        // is the one that decides whether any of this is visible at all, and it
        // is off until deliberately turned on; making both default to off would
        // mean turning sharing on and still seeing nothing.
        await _storage.WriteJsonAtomicAsync(
            GetEdgePath(first.AccountId, second.AccountId),
            new FriendEdgeRecord(
                first.AccountId,
                second.AccountId,
                second.Username,
                second.DisplayName,
                now,
                ShareProgress: true),
            ServerJsonSerializerContext.Default.FriendEdgeRecord,
            cancellationToken);
        await _storage.WriteJsonAtomicAsync(
            GetEdgePath(second.AccountId, first.AccountId),
            new FriendEdgeRecord(
                second.AccountId,
                first.AccountId,
                first.Username,
                first.DisplayName,
                now,
                ShareProgress: true),
            ServerJsonSerializerContext.Default.FriendEdgeRecord,
            cancellationToken);
        TryDeleteRequestPair(first.AccountId, second.AccountId);
        TryDeleteRequestPair(second.AccountId, first.AccountId);
    }

    private ValueTask<FriendRequestRecord?> ReadRequestAsync(
        Guid fromAccountId,
        Guid toAccountId,
        CancellationToken cancellationToken) =>
        _storage.ReadJsonAsync(
            GetRequestPath("in", toAccountId, fromAccountId),
            ServerJsonSerializerContext.Default.FriendRequestRecord,
            cancellationToken);

    private async ValueTask WriteRequestAsync(
        FriendRequestRecord record,
        CancellationToken cancellationToken)
    {
        await _storage.WriteJsonAtomicAsync(
            GetRequestPath("in", record.ToAccountId, record.FromAccountId),
            record,
            ServerJsonSerializerContext.Default.FriendRequestRecord,
            cancellationToken);
        await _storage.WriteJsonAtomicAsync(
            GetRequestPath("out", record.FromAccountId, record.ToAccountId),
            record,
            ServerJsonSerializerContext.Default.FriendRequestRecord,
            cancellationToken);
    }

    private bool TryDeleteRequestPair(Guid fromAccountId, Guid toAccountId)
    {
        var removed = _storage.TryDeleteReporting(
            GetRequestPath("in", toAccountId, fromAccountId));
        _storage.TryDelete(
            GetRequestPath("out", fromAccountId, toAccountId));
        return removed;
    }

    private IEnumerable<string> EnumerateJson(string directory) =>
        _storage.EnumerateJson(directory);

    private string GetEdgeDirectory(Guid accountId) =>
        GetSafePath("edges", accountId.ToString("N"));

    private string GetEdgePath(Guid accountId, Guid friendAccountId) =>
        GetSafePath(
            "edges",
            accountId.ToString("N"),
            friendAccountId.ToString("N") + ".json");

    private string GetRequestDirectory(string direction, Guid accountId) =>
        GetSafePath("requests", direction, accountId.ToString("N"));

    private string GetRequestPath(
        string direction,
        Guid ownerAccountId,
        Guid otherAccountId) =>
        GetSafePath(
            "requests",
            direction,
            ownerAccountId.ToString("N"),
            otherAccountId.ToString("N") + ".json");

    private string GetVisibilityPath(Guid accountId) =>
        GetSafePath("visibility", accountId.ToString("N") + ".json");

    private string GetActivityPath(Guid accountId) =>
        GetSafePath("activity", accountId.ToString("N") + ".json");

    private string GetSafePath(params string[] segments)
    {
        var path = Path.GetFullPath(segments.Aggregate(_root, Path.Combine));
        if (!path.StartsWith(
                _root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The friend storage path is outside its data root.");
        }

        return path;
    }
}

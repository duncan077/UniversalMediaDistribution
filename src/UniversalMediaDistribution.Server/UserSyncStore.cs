using System.Security.Cryptography;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server;

internal sealed record UserSyncSnapshotState(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId,
    UserSyncDocument Document,
    IReadOnlyDictionary<string, long>? RecordRevisions = null);

internal sealed record UserSyncChangesResponse(
    long FromRevision,
    long ToRevision,
    bool Full,
    UserSyncDocument Document);

internal sealed record UserSyncSnapshotResponse(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId,
    UserSyncDocument Document);

internal sealed record UserSyncUploadRequest(
    long ExpectedRevision,
    UserSyncDocument Document);

/// <summary>
/// A few changed positions and the titles they point at, rather than the whole
/// state.
/// <para>
/// Carries no expected revision on purpose. A full upload replaces the
/// snapshot, so it must refuse to overwrite work it has not seen; a patch is
/// merged record by record with the versions already used to settle conflicts,
/// so it can be applied to whatever is current. Requiring a revision would only
/// make reading fail whenever another device happened to sync.
/// </para>
/// </summary>
internal sealed record UserSyncProgressPatchRequest(UserSyncDocument Patch);

internal sealed record UserSyncDeleteRequest(long ExpectedRevision);

internal sealed record UserSyncWriteResponse(
    long Revision,
    DateTimeOffset UpdatedAt,
    string UpdatedByDeviceId);

internal sealed record SyncConflictResponse(
    string Code,
    string Message,
    long CurrentRevision);

internal sealed class SyncConflictException(long currentRevision) :
    Exception("The remote sync snapshot changed on another device.")
{
    public long CurrentRevision { get; } = currentRevision;
}

/// <summary>
/// A patch arrived for an account that has never uploaded a snapshot.
/// <para>
/// A patch carries only the titles that moved, so taking it as the whole
/// snapshot would store a library of one or two titles and call it a backup.
/// Refusing it costs one upload — the client falls back to sending everything,
/// and every later patch has something real to merge into.
/// </para>
/// </summary>
internal sealed class SyncPatchWithoutSnapshotException() :
    Exception(
        "A progress patch needs a stored snapshot to merge into. Upload the " +
        "whole snapshot first.");

internal sealed class SqliteUserSyncStore
{
    private readonly string _root;
    private readonly ServerDatabase _storage;
    private SemaphoreSlim _writeGate => _storage.WriteGate;

    public SqliteUserSyncStore(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _storage = new ServerDatabase(dataDirectory);
        _root = Path.GetFullPath(
            Path.Combine(dataDirectory, "user-sync"));
    }

    public async ValueTask<UserSyncSnapshotState?> GetAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var state = await _storage.ReadJsonAsync(
            GetPath(accountId),
            ServerJsonSerializerContext.Default.UserSyncSnapshotState,
            cancellationToken);
        if (state is null)
        {
            return null;
        }

        var document = UserSyncDocumentMerger.Normalize(state.Document);
        // Normalization can qualify legacy book keys or follow a renamed
        // identity. An index stamped under the old keys cannot safely extract
        // records from this document: a successful but incomplete delta would
        // make the client advance past reading progress it never received.
        // Keep the full-snapshot repair path until the next write reindexes it.
        var revisions = state.RecordRevisions;
        if (revisions is not null &&
            (revisions.Any(pair => pair.Value <= 0 || pair.Value > state.Revision) ||
                !UserSyncDocumentDelta.EnumerateKeys(document)
                    .ToHashSet(StringComparer.Ordinal).SetEquals(revisions.Keys)))
        {
            revisions = null;
        }

        return state with { Document = document, RecordRevisions = revisions };
    }

    public async ValueTask<UserSyncSnapshotState> SaveAsync(
        Guid accountId,
        UserSyncUploadRequest request,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException(
                "A sync snapshot requires an account.",
                nameof(accountId));
        }

        ArgumentNullException.ThrowIfNull(request);
        if (request.ExpectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request.ExpectedRevision));
        }

        var document =
            UserSyncDocumentMerger.Normalize(request.Document);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var current = await GetAsync(accountId, cancellationToken);
            var currentRevision = current?.Revision ?? 0;
            if (request.ExpectedRevision != currentRevision)
            {
                // Another device wrote first. For documents that carry
                // identities the shared merge is convergent, so the server
                // merges instead of refusing — which ends the pull-merge-retry
                // loop clients used to run, and means an upload can never
                // overwrite work it has not seen. Older documents keep the
                // conflict answer: merging them here would restamp the account
                // to a format the rest of the fleet cannot read yet.
                if (current is null || document.Version < 4)
                {
                    throw new SyncConflictException(currentRevision);
                }

                var conflictCatalog =
                    CatalogTransferDocumentMerger.MergeWithRedirects(
                        current.Document.Catalog,
                        document.Catalog);
                document = UserSyncDocumentMerger.Merge(
                    current.Document,
                    document,
                    conflictCatalog.Document,
                    document.DeviceId,
                    document.DeviceName,
                    additionalRedirects: conflictCatalog.Redirects);
            }

            var state = CreateState(
                current,
                checked(currentRevision + 1),
                document.DeviceId,
                document);
            await _storage.WriteJsonAtomicAsync(
                GetPath(accountId),
                state,
                ServerJsonSerializerContext.Default.UserSyncSnapshotState,
                cancellationToken);
            return write.CommitAndReturn(state);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Builds the stored state, stamping every record with the revision that
    /// last changed it: changed records get the new revision, unchanged ones
    /// carry their old stamp forward, and a state from before the index
    /// existed stamps its unchanged records with its own revision — the most
    /// honest answer available.
    /// </summary>
    private static UserSyncSnapshotState CreateState(
        UserSyncSnapshotState? previous,
        long revision,
        string deviceId,
        UserSyncDocument document)
    {
        var changed = UserSyncDocumentDelta.ComputeChangedKeys(
            previous?.Document,
            document);
        var revisions = new Dictionary<string, long>(StringComparer.Ordinal);
        foreach (var key in UserSyncDocumentDelta.EnumerateKeys(document))
        {
            revisions[key] = changed.Contains(key)
                ? revision
                : previous?.RecordRevisions?.GetValueOrDefault(
                      key,
                      previous.Revision) ??
                  previous?.Revision ?? revision;
        }

        return new UserSyncSnapshotState(
            revision,
            DateTimeOffset.UtcNow,
            deviceId,
            document,
            revisions);
    }

    /// <summary>
    /// The records that changed after <paramref name="sinceRevision"/>, as a
    /// partial document, or the whole snapshot when the stored state predates
    /// the record index.
    /// </summary>
    public async ValueTask<UserSyncChangesResponse?> GetChangesAsync(
        Guid accountId,
        long sinceRevision,
        CancellationToken cancellationToken)
    {
        var state = await GetAsync(accountId, cancellationToken);
        if (state is null)
        {
            return null;
        }

        if (state.RecordRevisions is null)
        {
            return new UserSyncChangesResponse(
                sinceRevision,
                state.Revision,
                Full: true,
                state.Document);
        }

        var keys = state.RecordRevisions
            .Where(pair => pair.Value > sinceRevision)
            .Select(static pair => pair.Key)
            .ToHashSet(StringComparer.Ordinal);
        return new UserSyncChangesResponse(
            sinceRevision,
            state.Revision,
            Full: false,
            UserSyncDocumentDelta.Extract(state.Document, keys));
    }

    /// <summary>
    /// Merges a handful of changed positions into the stored snapshot.
    /// <para>
    /// Both mergers are unions keyed per record, so a patch adds and updates
    /// but never removes what it does not mention — which is what makes it safe
    /// to send a few rows instead of everything. Deletions still travel as
    /// tombstones, the same as they do in a full upload.
    /// </para>
    /// <para>
    /// The write is the expensive part and is unavoidable here: the snapshot is
    /// one document, so touching a row replaces the stored JSON. What the patch saves
    /// is the network and the client, which are the costly ends.
    /// </para>
    /// </summary>
    public async ValueTask<UserSyncSnapshotState> ApplyProgressPatchAsync(
        Guid accountId,
        UserSyncProgressPatchRequest request,
        CancellationToken cancellationToken)
    {
        if (accountId == Guid.Empty)
        {
            throw new ArgumentException(
                "A sync patch requires an account.",
                nameof(accountId));
        }

        ArgumentNullException.ThrowIfNull(request);
        var patch = UserSyncDocumentMerger.Normalize(request.Patch);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var current = await GetAsync(accountId, cancellationToken)
                ?? throw new SyncPatchWithoutSnapshotException();

            var patchCatalog =
                CatalogTransferDocumentMerger.MergeWithRedirects(
                    current.Document.Catalog,
                    patch.Catalog);
            var merged = UserSyncDocumentMerger.Merge(
                current.Document,
                patch,
                patchCatalog.Document,
                patch.DeviceId,
                patch.DeviceName,
                additionalRedirects: patchCatalog.Redirects);

            var state = CreateState(
                current,
                checked(current.Revision + 1),
                patch.DeviceId,
                merged);
            await _storage.WriteJsonAtomicAsync(
                GetPath(accountId),
                state,
                ServerJsonSerializerContext.Default.UserSyncSnapshotState,
                cancellationToken);
            return write.CommitAndReturn(state);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask DeleteAsync(
        Guid accountId,
        long expectedRevision,
        CancellationToken cancellationToken)
    {
        if (expectedRevision < 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(expectedRevision));
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var current = await GetAsync(accountId, cancellationToken);
            var currentRevision = current?.Revision ?? 0;
            if (expectedRevision != currentRevision)
            {
                throw new SyncConflictException(currentRevision);
            }

            _storage.TryDelete(GetPath(accountId));
            write.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private string GetPath(Guid accountId)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                _root,
                accountId.ToString("N"),
                "latest.json"));
        if (!path.StartsWith(
                _root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The user sync path is outside its data root.");
        }

        return path;
    }
}

internal sealed record SharedListPublishRequest(Guid ListId);

internal sealed record SharedListOwnerIndex(
    string ShareId,
    Guid ListId);

internal sealed record SharedListRecord(
    string ShareId,
    Guid OwnerAccountId,
    string OwnerDisplayName,
    Guid SourceListId,
    string Name,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    CatalogTransferDocument Catalog);

internal sealed record SharedListResponse(
    string ShareId,
    Guid ListId,
    string Name,
    string OwnerDisplayName,
    int ItemCount,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string CatalogPath);

internal sealed class SqliteSharedListStore
{
    private readonly string _root;
    private readonly SqliteUserSyncStore _syncStore;
    private readonly ServerDatabase _storage;
    private SemaphoreSlim _writeGate => _storage.WriteGate;

    public SqliteSharedListStore(
        string dataDirectory,
        SqliteUserSyncStore syncStore)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);
        _storage = new ServerDatabase(dataDirectory);
        _syncStore = syncStore ??
            throw new ArgumentNullException(nameof(syncStore));
        _root = Path.GetFullPath(
            Path.Combine(dataDirectory, "shared-lists"));
    }

    public async ValueTask<SharedListRecord> PublishAsync(
        AccountRecord account,
        Guid listId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        if (listId == Guid.Empty)
        {
            throw new ArgumentException(
                "A shared list requires a list ID.",
                nameof(listId));
        }

        var snapshot = await _syncStore.GetAsync(
            account.AccountId,
            cancellationToken) ??
            throw new InvalidOperationException(
                "Upload this device's sync snapshot before sharing a list.");
        var list = snapshot.Document.Lists.SingleOrDefault(
            candidate => candidate.Id == listId &&
                         !candidate.IsDeleted) ??
            throw new KeyNotFoundException(
                "The selected list is not in the latest sync snapshot.");

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var indexPath = GetOwnerIndexPath(account.AccountId, listId);
            var index = await _storage.ReadJsonAsync(
                indexPath,
                ServerJsonSerializerContext.Default.SharedListOwnerIndex,
                cancellationToken);
            var existing = index is null
                ? null
                : await GetAsync(index.ShareId, cancellationToken);
            var now = DateTimeOffset.UtcNow;
            var record = CreateRecord(
                account,
                list,
                snapshot.Document.Catalog,
                existing,
                now);
            await _storage.WriteJsonAtomicAsync(
                GetPublicPath(record.ShareId),
                record,
                ServerJsonSerializerContext.Default.SharedListRecord,
                cancellationToken);
            await _storage.WriteJsonAtomicAsync(
                indexPath,
                new SharedListOwnerIndex(record.ShareId, listId),
                ServerJsonSerializerContext.Default.SharedListOwnerIndex,
                cancellationToken);
            return write.CommitAndReturn(record);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public async ValueTask SynchronizeAsync(
        AccountRecord account,
        UserSyncSnapshotState snapshot,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(account);
        ArgumentNullException.ThrowIfNull(snapshot);
        var directory = GetOwnerDirectory(account.AccountId);
        if (_storage.EnumerateJson(directory).Count == 0)
        {
            return;
        }

        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            foreach (var indexPath in _storage.EnumerateJson(directory))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var index = await _storage.ReadJsonAsync(
                    indexPath,
                    ServerJsonSerializerContext.Default
                        .SharedListOwnerIndex,
                    cancellationToken);
                if (index is null)
                {
                    _storage.TryDelete(indexPath);
                    continue;
                }

                var shareId = NormalizeShareId(index.ShareId);
                var existing = await _storage.ReadJsonAsync(
                    GetPublicPath(shareId),
                    ServerJsonSerializerContext.Default.SharedListRecord,
                    cancellationToken);
                if (existing is null ||
                    existing.OwnerAccountId != account.AccountId ||
                    existing.SourceListId != index.ListId)
                {
                    _storage.TryDelete(indexPath);
                    continue;
                }

                var list = snapshot.Document.Lists.SingleOrDefault(
                    candidate => candidate.Id == index.ListId &&
                                 !candidate.IsDeleted);
                if (list is null)
                {
                    _storage.TryDelete(GetPublicPath(shareId));
                    _storage.TryDelete(indexPath);
                    continue;
                }

                var updated = CreateRecord(
                    account,
                    list,
                    snapshot.Document.Catalog,
                    existing,
                    DateTimeOffset.UtcNow);
                await _storage.WriteJsonAtomicAsync(
                    GetPublicPath(shareId),
                    updated,
                    ServerJsonSerializerContext.Default.SharedListRecord,
                    cancellationToken);
            }
            write.Commit();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public ValueTask<SharedListRecord?> GetAsync(
        string shareId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeShareId(shareId);
        return _storage.ReadJsonAsync(
            GetPublicPath(normalized),
            ServerJsonSerializerContext.Default.SharedListRecord,
            cancellationToken);
    }

    public async ValueTask<IReadOnlyList<SharedListRecord>> ListAsync(
        Guid accountId,
        CancellationToken cancellationToken)
    {
        var directory = GetOwnerDirectory(accountId);
        if (_storage.EnumerateJson(directory).Count == 0)
        {
            return [];
        }

        var result = new List<SharedListRecord>();
        foreach (var path in _storage.EnumerateJson(directory))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var index = await _storage.ReadJsonAsync(
                path,
                ServerJsonSerializerContext.Default.SharedListOwnerIndex,
                cancellationToken);
            if (index is null)
            {
                continue;
            }

            var record = await GetAsync(index.ShareId, cancellationToken);
            if (record?.OwnerAccountId == accountId)
            {
                result.Add(record);
            }
        }

        return result
            .OrderByDescending(static record => record.UpdatedAt)
            .ToArray();
    }

    public async ValueTask<bool> RevokeAsync(
        Guid accountId,
        string shareId,
        CancellationToken cancellationToken)
    {
        var normalized = NormalizeShareId(shareId);
        await _writeGate.WaitAsync(cancellationToken);
        try
        {
            using var write = _storage.BeginWrite();
            var record = await GetAsync(normalized, cancellationToken);
            if (record is null)
            {
                return write.CommitAndReturn(false);
            }

            if (record.OwnerAccountId != accountId)
            {
                throw new UnauthorizedAccessException(
                    "The shared list belongs to another account.");
            }

            _storage.TryDelete(GetPublicPath(normalized));
            _storage.TryDelete(
                GetOwnerIndexPath(accountId, record.SourceListId));
            return write.CommitAndReturn(true);
        }
        finally
        {
            _writeGate.Release();
        }
    }

    public static SharedListResponse ToResponse(SharedListRecord record) =>
        new(
            record.ShareId,
            record.SourceListId,
            record.Name,
            record.OwnerDisplayName,
            record.Catalog.Items.Count,
            record.CreatedAt,
            record.UpdatedAt,
            $"/api/v1/shared-lists/{record.ShareId}/catalog");

    private static SharedListRecord CreateRecord(
        AccountRecord account,
        UserSyncMediaList list,
        CatalogTransferDocument catalog,
        SharedListRecord? existing,
        DateTimeOffset now)
    {
        var itemKeys = list.Items
            .Select(UserSyncDocumentValidator.ValidateItemIdentity)
            .ToHashSet(StringComparer.Ordinal);
        var catalogItems = catalog.Items
            .Where(item => itemKeys.Contains(
                UserSyncDocumentValidator.CreateItemKey(
                    CatalogSubtypeResolver.ParseMediaType(item.MediaType),
                    item.Name, item.Subtype)))
            .ToArray();
        if (catalogItems.Length != itemKeys.Count)
        {
            throw new InvalidDataException(
                "The shared list references a catalog item that is unavailable.");
        }

        var record = new SharedListRecord(
            existing?.ShareId ?? CreateShareId(),
            account.AccountId,
            account.DisplayName,
            list.Id,
            list.Name,
            existing?.CreatedAt ?? now,
            now,
            new CatalogTransferDocument
            {
                ExportedAt = now,
                SourceLabel =
                    $"{list.Name} shared by {account.DisplayName}",
                Items = catalogItems
            });
        CatalogTransferValidator.Validate(record.Catalog);
        return record;
    }

    private string GetPublicPath(string shareId)
    {
        var path = Path.GetFullPath(
            Path.Combine(_root, "public", shareId + ".json"));
        EnsureUnderRoot(path);
        return path;
    }

    private string GetOwnerDirectory(Guid accountId)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                _root,
                "by-owner",
                accountId.ToString("N")));
        EnsureUnderRoot(path);
        return path;
    }

    private string GetOwnerIndexPath(Guid accountId, Guid listId)
    {
        var path = Path.GetFullPath(
            Path.Combine(
                GetOwnerDirectory(accountId),
                listId.ToString("N") + ".json"));
        EnsureUnderRoot(path);
        return path;
    }

    private void EnsureUnderRoot(string path)
    {
        if (!path.StartsWith(
                _root + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The shared-list path is outside its data root.");
        }
    }

    private static string NormalizeShareId(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        var normalized = value.Trim();
        if (normalized.Length != 24 ||
            normalized.Any(static character =>
                !char.IsAsciiLetterOrDigit(character) &&
                character is not ('-' or '_')))
        {
            throw new ArgumentException(
                "The shared-list ID is invalid.",
                nameof(value));
        }

        return normalized;
    }

    private static string CreateShareId() =>
        Convert.ToBase64String(RandomNumberGenerator.GetBytes(18))
            .TrimEnd('=')
            .Replace('+', '-')
            .Replace('/', '_');
}

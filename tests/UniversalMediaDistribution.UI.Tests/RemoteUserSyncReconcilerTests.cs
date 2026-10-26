using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class RemoteUserSyncReconcilerTests
{
    [Fact]
    public async Task ConcurrentWrite_PullsMergesAndRetries()
    {
        var local = CreateDocument("local", "organization:local", 1);
        var firstRemote = CreateDocument(
            "remote-a",
            "organization:remote-a",
            2);
        var secondRemote = UserSyncDocumentMerger.Merge(
            firstRemote,
            CreateDocument(
                "remote-b",
                "organization:remote-b",
                3),
            firstRemote.Catalog,
            "remote-b",
            "Remote B");
        var store = new ReconciliatingStore(local);
        var client = new ConflictingClient(firstRemote, secondRemote);

        var result = await RemoteUserSyncReconciler.SynchronizeAsync(
            client,
            store,
            "local",
            "Local");

        Assert.Equal(2, result.AttemptCount);
        Assert.Equal(2, client.UploadCount);
        Assert.Equal(3, result.BaselineRevision);
        Assert.Equal(
            [
                "organization:local",
                "organization:remote-a",
                "organization:remote-b"
            ],
            client.UploadedDocument!.FavoriteContributorKeys);
    }

    [Fact]
    public async Task AFullSnapshotWithNoLocalChangesIsNotUploadedAgain()
    {
        var remote = CreateDocument("remote", "organization:remote", 1);
        var store = new ReconciliatingStore(remote with { FavoriteStates = [], FavoriteContributorKeys = [] });

        var result = await RemoteUserSyncReconciler.SynchronizeAsync(
            new UnchangedSnapshotClient(remote), store, "local", "Local");

        Assert.Equal(5, result.WriteResult.Revision);
        Assert.Equal(5, result.BaselineRevision);
        Assert.Equal(1, result.AttemptCount);
        Assert.NotNull(result.RestoreResult);
        Assert.NotNull(result.NewBaseline);
        Assert.Equal("organization:remote", Assert.Single(store.Current.FavoriteStates).ContributorKey);
    }

    [Fact]
    public async Task TheReconciliationLeavesTheCallersThread()
    {
        // Restoring a snapshot is seconds of work on a real library. Awaited
        // from a command it would run wherever the command runs, and on that
        // thread the window is not drawing.
        var caller = new RecordingSynchronizationContext();
        var previous = SynchronizationContext.Current;
        SynchronizationContext.SetSynchronizationContext(caller);
        try
        {
            var store = new ContextRecordingStore(
                CreateDocument("local", "organization:local", 1));

            await RemoteUserSyncReconciler.SynchronizeAsync(
                new ConflictingClient(
                    CreateDocument("remote-a", "organization:remote-a", 2),
                    CreateDocument("remote-b", "organization:remote-b", 3)),
                store,
                "local",
                "Local");

            Assert.NotEmpty(store.ObservedContexts);
            Assert.DoesNotContain(caller, store.ObservedContexts);
        }
        finally
        {
            SynchronizationContext.SetSynchronizationContext(previous);
        }
    }

    private static UserSyncDocument CreateDocument(
        string deviceId,
        string favorite,
        int minute) =>
        UserSyncDocumentMerger.Normalize(new UserSyncDocument
        {
            DeviceId = deviceId,
            DeviceName = deviceId,
            Catalog = new CatalogTransferDocument(),
            FavoriteStates =
            [
                new UserSyncFavoriteState
                {
                    ContributorKey = favorite,
                    IsFavorite = true,
                    SyncVersion =
                        UserSyncDocumentMerger.CreateLegacyVersion(
                            deviceId,
                            DateTimeOffset.UnixEpoch.AddMinutes(minute),
                            favorite)
                }
            ]
        });

    private sealed class ReconciliatingStore(UserSyncDocument current) :
        IUserSyncStore
    {
        public UserSyncDocument Current { get; private set; } = current;

        public ValueTask<UserSyncDocument> ExportAsync(
            string deviceId,
            string deviceName,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(Current with
            {
                DeviceId = deviceId,
                DeviceName = deviceName,
                ExportedAt = DateTimeOffset.UtcNow
            });

        public ValueTask<UserSyncRestoreResult> RestoreAsync(
            UserSyncDocument document,
            CancellationToken cancellationToken = default)
        {
            Current = UserSyncDocumentMerger.Merge(
                Current,
                document,
                Current.Catalog,
                Current.DeviceId,
                Current.DeviceName);
            return ValueTask.FromResult(
                new UserSyncRestoreResult(0, 0, 0, 0, 0, 0));
        }
    }

    /// <summary>
    /// Stands in for the UI thread: work marshalled back to it is recorded as
    /// <see cref="SynchronizationContext.Current"/> for whoever runs there.
    /// </summary>
    private sealed class RecordingSynchronizationContext :
        SynchronizationContext
    {
        public override void Post(SendOrPostCallback callback, object? state) =>
            ThreadPool.QueueUserWorkItem(
                _ =>
                {
                    SetSynchronizationContext(this);
                    callback(state);
                },
                null);
    }

    private sealed class ContextRecordingStore(UserSyncDocument current) :
        IUserSyncStore
    {
        private readonly ReconciliatingStore _inner = new(current);

        public List<SynchronizationContext?> ObservedContexts { get; } = [];

        public ValueTask<UserSyncDocument> ExportAsync(
            string deviceId,
            string deviceName,
            CancellationToken cancellationToken = default)
        {
            ObservedContexts.Add(SynchronizationContext.Current);
            return _inner.ExportAsync(deviceId, deviceName, cancellationToken);
        }

        public ValueTask<UserSyncRestoreResult> RestoreAsync(
            UserSyncDocument document,
            CancellationToken cancellationToken = default)
        {
            ObservedContexts.Add(SynchronizationContext.Current);
            return _inner.RestoreAsync(document, cancellationToken);
        }
    }

    private sealed class UnchangedSnapshotClient(UserSyncDocument document) : StubRemoteMediaServerClient
    {
        public override ValueTask<RemoteSyncSnapshot?> GetSnapshotAsync(CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RemoteSyncSnapshot?>(new RemoteSyncSnapshot(5, document.ExportedAt, document.DeviceId, document));

        public override ValueTask<RemoteSyncWriteResult> UploadSnapshotAsync(
            UserSyncDocument snapshot, long expectedRevision, CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException("An unchanged snapshot must not be uploaded again.");
    }

    private sealed class ConflictingClient(
        UserSyncDocument first,
        UserSyncDocument second) : StubRemoteMediaServerClient
    {
        private int _readCount;

        public int UploadCount { get; private set; }

        public UserSyncDocument? UploadedDocument { get; private set; }

        public override ValueTask<RemoteSyncSnapshot?> GetSnapshotAsync(
            CancellationToken cancellationToken = default)
        {
            _readCount++;
            var document = _readCount == 1 ? first : second;
            return ValueTask.FromResult<RemoteSyncSnapshot?>(
                new RemoteSyncSnapshot(
                    _readCount,
                    DateTimeOffset.UtcNow,
                    document.DeviceId,
                    document));
        }

        public override ValueTask<RemoteSyncWriteResult> UploadSnapshotAsync(
            UserSyncDocument document,
            long expectedRevision,
            CancellationToken cancellationToken = default)
        {
            UploadCount++;
            if (UploadCount == 1)
            {
                throw new RemoteSyncConflictException(
                    2,
                    "Concurrent update");
            }

            Assert.Equal(2, expectedRevision);
            UploadedDocument = document;
            return ValueTask.FromResult(
                new RemoteSyncWriteResult(
                    3,
                    DateTimeOffset.UtcNow,
                    document.DeviceId));
        }
    }
}

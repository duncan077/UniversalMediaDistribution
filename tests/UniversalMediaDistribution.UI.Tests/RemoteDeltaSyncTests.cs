using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// The routine sync under delta: pull only what changed on the server, push
/// only what changed here, and never touch the full-snapshot endpoints.
/// </summary>
public sealed class RemoteDeltaSyncTests
{
    [Fact]
    public async Task ARoutineSyncMovesOnlyTheChangedRecords()
    {
        // Baseline: the device proved the server holds favorite A.
        // Local since then: favorite B. Server since then: favorite C.
        var baseline = CreateDocument(
            "device-local",
            ("organization:a", 1));
        var local = CreateDocument(
            "device-local",
            ("organization:a", 1),
            ("organization:b", 2));
        var serverChanges = CreateDocument(
            "device-remote",
            ("organization:c", 3));

        var store = new MergingStore(local);
        var client = new DeltaClient(
            new RemoteSyncChanges(5, 7, Full: false, serverChanges));

        var result = await RemoteUserSyncReconciler.SynchronizeAsync(
            client,
            store,
            "device-local",
            "Local",
            new RemoteSyncDeltaContext(5, baseline));

        // The server's change landed locally...
        Assert.NotNull(result.RestoreResult);
        Assert.Contains(
            store.Current.FavoriteStates,
            static state => state.ContributorKey == "organization:c");

        // ...and only the local change went up.
        var patched = Assert.IsType<UserSyncDocument>(client.PatchedDocument);
        var uploaded = Assert.Single(patched.FavoriteStates);
        Assert.Equal("organization:b", uploaded.ContributorKey);
        Assert.Empty(patched.Catalog.Items);

        // The next sync measures against what this one proved.
        Assert.NotNull(result.NewBaseline);
        Assert.Equal(9, result.WriteResult.Revision);
        // Revision 8 may belong to another device whose changes were not
        // downloaded here. The next pull must start at 7, not at our write.
        Assert.Equal(7, result.BaselineRevision);
    }

    [Fact]
    public async Task NothingChangedLocallyMeansNothingIsUploaded()
    {
        var baseline = CreateDocument(
            "device-local",
            ("organization:a", 1));
        var store = new MergingStore(baseline);
        var client = new DeltaClient(
            new RemoteSyncChanges(
                5,
                5,
                Full: false,
                CreateEmptyDocument("device-remote")));

        var result = await RemoteUserSyncReconciler.SynchronizeAsync(
            client,
            store,
            "device-local",
            "Local",
            new RemoteSyncDeltaContext(5, baseline));

        Assert.Null(client.PatchedDocument);
        Assert.Equal(5, result.WriteResult.Revision);
        Assert.Equal(5, result.BaselineRevision);
    }

    [Theory]
    [InlineData(8)]
    [InlineData(2)]
    public async Task AFullChangesResponseIsRestoredWithoutDownloadingItAgain(long revision)
    {
        var baseline = CreateDocument("device-local", ("organization:a", 1));
        var remote = CreateDocument("device-remote", ("organization:a", 1), ("organization:c", 3));
        // Preserve A's version: only C is new on the other device.
        remote = remote with { FavoriteStates = [baseline.FavoriteStates[0], remote.FavoriteStates[1]] };
        var store = new MergingStore(baseline);
        var client = new DeltaClient(new RemoteSyncChanges(5, revision, Full: true, remote));

        var result = await RemoteUserSyncReconciler.SynchronizeAsync(
            client, store, "device-local", "Local", new RemoteSyncDeltaContext(5, baseline));

        Assert.NotNull(result.RestoreResult);
        Assert.Equal(revision, result.BaselineRevision);
        Assert.Equal(revision, result.WriteResult.Revision);
        Assert.Contains(store.Current.FavoriteStates, state => state.ContributorKey == "organization:c");
        Assert.Null(client.PatchedDocument);
    }

    private static UserSyncDocument CreateEmptyDocument(string deviceId) =>
        UserSyncDocumentMerger.Normalize(new UserSyncDocument
        {
            DeviceId = deviceId,
            DeviceName = deviceId,
            Catalog = new CatalogTransferDocument()
        });

    private static UserSyncDocument CreateDocument(
        string deviceId,
        params (string Key, int Minute)[] favorites) =>
        UserSyncDocumentMerger.Normalize(new UserSyncDocument
        {
            DeviceId = deviceId,
            DeviceName = deviceId,
            Catalog = new CatalogTransferDocument(),
            FavoriteStates = favorites
                .Select(favorite => new UserSyncFavoriteState
                {
                    ContributorKey = favorite.Key,
                    IsFavorite = true,
                    SyncVersion = UserSyncDocumentMerger.CreateLegacyVersion(
                        deviceId,
                        DateTimeOffset.UnixEpoch.AddMinutes(favorite.Minute),
                        favorite.Key)
                })
                .ToArray()
        });

    private sealed class MergingStore(UserSyncDocument current) :
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

    // Re-lists the interface: a method on a derived class does not take an
    // interface default's slot unless the derived class re-declares the
    // interface and interface mapping runs over it again.
    private sealed class DeltaClient(RemoteSyncChanges changes) :
        StubRemoteMediaServerClient, IRemoteMediaServerClient
    {
        public UserSyncDocument? PatchedDocument { get; private set; }

        public ValueTask<RemoteSyncChanges?> GetChangesAsync(
            long sinceRevision,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<RemoteSyncChanges?>(changes);

        public override ValueTask<RemoteSyncSnapshot?> GetSnapshotAsync(
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "A delta sync must not pull the full snapshot.");

        public override ValueTask<RemoteSyncWriteResult> UploadSnapshotAsync(
            UserSyncDocument document,
            long expectedRevision,
            CancellationToken cancellationToken = default) =>
            throw new InvalidOperationException(
                "A delta sync must not upload the full snapshot.");

        public override ValueTask<RemoteSyncWriteResult> PatchProgressAsync(
            UserSyncDocument patch,
            CancellationToken cancellationToken = default)
        {
            PatchedDocument = patch;
            return ValueTask.FromResult(
                new RemoteSyncWriteResult(9, DateTimeOffset.UtcNow, "server"));
        }
    }
}

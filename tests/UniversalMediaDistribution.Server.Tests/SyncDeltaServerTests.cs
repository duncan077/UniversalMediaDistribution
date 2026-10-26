using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

/// <summary>
/// The server's half of delta sync: every record remembers the revision that
/// last changed it, "since N" answers with just those records, and an upload
/// that lost a race is merged instead of refused.
/// </summary>
public sealed class SyncDeltaServerTests : IDisposable
{
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "umd-delta-server-" + Guid.CreateVersion7().ToString("N"));

    [Fact]
    public async Task ChangesSinceARevisionCarryOnlyWhatChangedAfterIt()
    {
        var store = new SqliteUserSyncStore(_directory);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument("device-one", ("organization:a", 1))),
            CancellationToken.None);

        // The second upload repeats favorite A untouched — same version, so
        // the index must not restamp it — and adds favorite B.
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                1,
                CreateDocument(
                    "device-one",
                    ("organization:a", 1),
                    ("organization:b", 2))),
            CancellationToken.None);

        var changes = await store.GetChangesAsync(
            accountId,
            sinceRevision: 1,
            CancellationToken.None);

        Assert.NotNull(changes);
        Assert.False(changes.Full);
        Assert.Equal(2, changes.ToRevision);
        var changedFavorite = Assert.Single(changes.Document.FavoriteStates);
        Assert.Equal("organization:b", changedFavorite.ContributorKey);
    }

    [Fact]
    public async Task AnUploadThatLostARaceIsMergedInsteadOfRefused()
    {
        var store = new SqliteUserSyncStore(_directory);
        var accountId = Guid.CreateVersion7();
        await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument("device-one", ("organization:a", 1))),
            CancellationToken.None);

        // A second device uploads believing the account is empty. The old
        // answer was a conflict and a client-side retry loop; the shared
        // merge is convergent, so the server can simply apply it.
        var saved = await store.SaveAsync(
            accountId,
            new UserSyncUploadRequest(
                0,
                CreateDocument("device-two", ("organization:b", 2))),
            CancellationToken.None);

        Assert.Equal(2, saved.Revision);
        Assert.Equal(
            ["organization:a", "organization:b"],
            saved.Document.FavoriteStates
                .Select(static state => state.ContributorKey)
                .Order(StringComparer.Ordinal));
    }

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private static UserSyncDocument CreateDocument(
        string deviceId,
        params (string Key, int Minute)[] favorites) =>
        new()
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
        };
}

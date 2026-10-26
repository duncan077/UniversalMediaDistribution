using System.Text.Json;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;
using UniversalMediaDistribution.UI.Remote;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Drives a progress patch the whole way: a position saved into a real profile,
/// exported, sent over HTTP, merged by a real server, and read back by a friend.
/// <para>
/// Every part of this is covered somewhere already — the export against a
/// database, the merge against the store, the client against a stub — and none
/// of that would notice a wrong route, a field the server does not read, or a
/// merge that keeps the position but never refreshes what a friend can see.
/// </para>
/// <para>
/// Skipped unless <c>UMD_TEST_SERVER_URL</c> names a reachable server and
/// <c>UMD_TEST_SEED_PROFILE</c> names a seeded profile directory. The profile is
/// copied before anything is written, so the seeded one is only read.
/// </para>
/// </summary>
public sealed class ProgressPatchLiveTests
{
    private const string DeviceId = "patch-live-device";
    private const string DeviceName = "Patch live";
    private const string Password = "correct horse battery staple";

    [Fact]
    public async Task APatchReachesAFriendWithoutResendingTheLibrary()
    {
        var serverUrl = Environment.GetEnvironmentVariable(
            "UMD_TEST_SERVER_URL");
        var profileDirectory = Environment.GetEnvironmentVariable(
            "UMD_TEST_SEED_PROFILE");
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            string.IsNullOrWhiteSpace(profileDirectory) ||
            !File.Exists(Path.Combine(profileDirectory, "catalog.db")))
        {
            // Passes without testing anything, so an ordinary run is
            // unaffected on a machine with no server and no seeded profile.
            return;
        }

        var serverUri = new Uri(serverUrl);
        var suffix = Guid.CreateVersion7().ToString("N")[..12];
        var workingDirectory = CopyProfile(profileDirectory, suffix);
        try
        {
            await RunAsync(serverUri, suffix, workingDirectory);
        }
        finally
        {
            // A copy of a whole library per run, and reruns are the point.
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
                // Left behind in the temporary directory rather than failing a
                // test that has already finished.
            }
        }
    }

    private static async Task RunAsync(
        Uri serverUri,
        string suffix,
        string workingDirectory)
    {
        var database = new SqliteCatalogDatabase(
            Path.Combine(workingDirectory, "catalog.db"));
        var repository = new SqliteCatalogRepository(database);
        var syncStore = new SqliteUserSyncStore(database, repository);
        var stateStore = new SqliteUserMediaStateStore(database);

        // Fresh accounts: the seeded ones belong to the running application,
        // and a test that moved their progress would change what it shows.
        using var owner = new HttpRemoteMediaServerClient();
        using var viewer = new HttpRemoteMediaServerClient();
        var ownerSession = await RegisterAsync(
            owner,
            serverUri,
            $"patchowner{suffix}");
        var viewerSession = await RegisterAsync(
            viewer,
            serverUri,
            $"patchviewer{suffix}");

        var capabilities = await owner.GetCapabilitiesAsync(serverUri);
        Assert.True(
            capabilities.Friends,
            "The server under test does not report the friends capability.");
        Assert.True(
            capabilities.ProgressPatch,
            "The server under test does not report the progress-patch " +
            "capability, so the client would never try the endpoint.");

        await viewer.SendFriendRequestAsync($"patchowner{suffix}");
        Assert.True(
            await owner.AcceptFriendRequestAsync(viewerSession.AccountId));
        Assert.True(
            (await owner.SetFriendVisibilityAsync(
                new RemoteFriendVisibility(true, true, []))).ShareProgress);

        // The whole library once, the way a first sign-in sends it.
        var snapshot = await syncStore.ExportAsync(DeviceId, DeviceName);
        var uploaded = await owner.UploadSnapshotAsync(snapshot, 0);

        var (item, unit) = await ChooseUntouchedUnitAsync(repository, snapshot);
        var itemKey = ContentItemKey.Create(item.MediaType, item.Name);
        Assert.Empty(await viewer.LookupFriendProgressAsync([itemKey]));

        // Saved through the store a reader writes to, not assembled by hand:
        // the version stamped here is what decides the merge.
        await stateStore.SaveProgressAsync(
            unit.Identity,
            new MediaProgressState(
                1,
                MediaPositionKind.Page,
                isCompleted: false,
                DateTimeOffset.UtcNow));

        var patch = await syncStore.ExportProgressPatchAsync(
            DeviceId,
            DeviceName,
            [item.Id]);
        Assert.NotEmpty(patch.Progress);

        var patched = await owner.PatchProgressAsync(patch);
        Assert.True(
            patched.Revision > uploaded.Revision,
            $"A patch should move the revision past {uploaded.Revision}, " +
            $"but it stayed at {patched.Revision}.");

        // What the whole feature is for: a friend's view moved.
        var seen = Assert.Single(
            await viewer.LookupFriendProgressAsync([itemKey]));
        Assert.Equal(ownerSession.AccountId, seen.AccountId);
        Assert.Equal(unit.DisplayNumber, seen.DisplayNumber);
        Assert.Equal(unit.Identity.SeasonNumber, seen.SeasonNumber);

        await AssertNothingWasLostAsync(
            owner,
            snapshot,
            item,
            patched.Revision,
            expectedNewProgressRows: 1);

        // A second title, so a patch has to merge into what the last one left
        // rather than replace it. Sending one title and finding the other gone
        // would look like a working feature right up until someone read twice.
        var (second, secondUnit) = await ChooseUntouchedUnitAsync(
            repository,
            snapshot,
            except: item.Id);
        await stateStore.SaveProgressAsync(
            secondUnit.Identity,
            new MediaProgressState(
                1,
                MediaPositionKind.Page,
                isCompleted: false,
                DateTimeOffset.UtcNow));
        var secondPatched = await owner.PatchProgressAsync(
            await syncStore.ExportProgressPatchAsync(
                DeviceId,
                DeviceName,
                [second.Id]));
        Assert.True(secondPatched.Revision > patched.Revision);

        var secondKey = ContentItemKey.Create(second.MediaType, second.Name);
        var bothSeen = await viewer.LookupFriendProgressAsync(
            [itemKey, secondKey]);
        Assert.Equal(2, bothSeen.Count);
        Assert.Contains(bothSeen, entry => entry.ItemKey == itemKey);
        Assert.Contains(bothSeen, entry => entry.ItemKey == secondKey);

        // The fallback, on a revision a patch produced. It is what every
        // failure path lands on, and it has never run against a patched
        // snapshot — a conflict here would strand every later upload.
        var reconciled = await RemoteUserSyncReconciler.SynchronizeAsync(
            owner,
            syncStore,
            DeviceId,
            DeviceName);
        Assert.True(
            reconciled.WriteResult.Revision > secondPatched.Revision,
            "A full upload after a patch should still be accepted.");
        await AssertNothingWasLostAsync(
            owner,
            snapshot,
            item,
            reconciled.WriteResult.Revision,
            expectedNewProgressRows: 2);

        Report(snapshot, patch, uploaded.Revision, patched.Revision);
        await owner.SignOutAsync();
        await viewer.SignOutAsync();
    }

    /// <summary>
    /// The case that reached a running application: an account that has never
    /// synced, reading something, and sending a patch as its first upload. The
    /// server took it as the whole snapshot, so a library of 310 titles was
    /// stored as one — and the account then reported a backup it could restore.
    /// </summary>
    [Fact]
    public async Task AFreshAccountCannotBeGivenAPatchAsItsSnapshot()
    {
        var serverUrl = Environment.GetEnvironmentVariable(
            "UMD_TEST_SERVER_URL");
        var profileDirectory = Environment.GetEnvironmentVariable(
            "UMD_TEST_SEED_PROFILE");
        if (string.IsNullOrWhiteSpace(serverUrl) ||
            string.IsNullOrWhiteSpace(profileDirectory) ||
            !File.Exists(Path.Combine(profileDirectory, "catalog.db")))
        {
            return;
        }

        var serverUri = new Uri(serverUrl);
        var suffix = Guid.CreateVersion7().ToString("N")[..12];
        var workingDirectory = CopyProfile(profileDirectory, suffix);
        try
        {
            var database = new SqliteCatalogDatabase(
                Path.Combine(workingDirectory, "catalog.db"));
            var repository = new SqliteCatalogRepository(database);
            var syncStore = new SqliteUserSyncStore(database, repository);
            var stateStore = new SqliteUserMediaStateStore(database);

            using var client = new HttpRemoteMediaServerClient();
            await RegisterAsync(client, serverUri, $"patchfresh{suffix}");
            Assert.Null(await client.GetSnapshotAsync());

            var snapshot = await syncStore.ExportAsync(DeviceId, DeviceName);
            var (item, unit) = await ChooseUntouchedUnitAsync(
                repository,
                snapshot);
            await stateStore.SaveProgressAsync(
                unit.Identity,
                new MediaProgressState(
                    1,
                    MediaPositionKind.Page,
                    isCompleted: false,
                    DateTimeOffset.UtcNow));

            var patch = await syncStore.ExportProgressPatchAsync(
                DeviceId,
                DeviceName,
                [item.Id]);
            await Assert.ThrowsAsync<HttpRequestException>(
                () => client.PatchProgressAsync(patch).AsTask());

            // Refused, not half-applied: the account still has no snapshot, so
            // the client's fallback sends the library rather than one title.
            Assert.Null(await client.GetSnapshotAsync());
            await client.SignOutAsync();
        }
        finally
        {
            try
            {
                Directory.Delete(workingDirectory, recursive: true);
            }
            catch (IOException)
            {
            }
        }
    }

    /// <summary>
    /// The stored snapshot after a patch still has to be the whole library.
    /// A merge that took the patch's catalog as the new one would leave a
    /// friend's view correct and quietly cost every other title.
    /// </summary>
    private static async Task AssertNothingWasLostAsync(
        HttpRemoteMediaServerClient owner,
        UserSyncDocument snapshot,
        CatalogItem item,
        long expectedRevision,
        int expectedNewProgressRows)
    {
        var stored = await owner.GetSnapshotAsync();
        Assert.NotNull(stored);
        Assert.Equal(expectedRevision, stored.Revision);
        Assert.Equal(
            snapshot.Catalog.Items.Count,
            stored.Document.Catalog.Items.Count);
        Assert.Equal(
            snapshot.Progress.Count + expectedNewProgressRows,
            stored.Document.Progress.Count);

        var before = FindItem(snapshot, item.Name);
        var after = FindItem(stored.Document, item.Name);
        Assert.Equal(CountUnits(before), CountUnits(after));
        Assert.Equal(before.Seasons.Count, after.Seasons.Count);
    }

    private static CatalogTransferItem FindItem(
        UserSyncDocument document,
        string name) =>
        Assert.Single(
            document.Catalog.Items,
            candidate => string.Equals(
                candidate.Name,
                name,
                StringComparison.Ordinal));

    private static int CountUnits(CatalogTransferItem item) =>
        item.Units.Count +
        item.Seasons.Sum(static season => season.Units.Count);

    /// <summary>
    /// A title with units that nothing has been read in yet, so the friend's
    /// view of it can only be what this test just put there.
    /// </summary>
    private static async Task<(CatalogItem Item, CatalogContentUnitDetails Unit)>
        ChooseUntouchedUnitAsync(
            SqliteCatalogRepository repository,
            UserSyncDocument snapshot,
            Guid? except = null)
    {
        var read = snapshot.Progress
            .Select(static entry => ContentItemKey.Create(
                entry.Content.Item.MediaType,
                entry.Content.Item.Name))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var candidate in await repository.ListAsync())
        {
            if (candidate.Id == except ||
                read.Contains(
                    ContentItemKey.Create(
                        candidate.MediaType,
                        candidate.Name)))
            {
                continue;
            }

            var details = await repository.GetDetailsAsync(candidate.Id);
            var unit = details?.Units
                .Concat(details.Seasons.SelectMany(
                    static season => season.Units))
                .FirstOrDefault();
            if (unit is not null)
            {
                return (candidate, unit);
            }
        }

        throw new InvalidOperationException(
            "The seeded profile has no title with units that has not been " +
            "read, so there is nothing to patch unambiguously.");
    }

    /// <summary>
    /// Says what the exchange actually cost, because the reason for the
    /// endpoint is the difference between the two numbers.
    /// </summary>
    private static void Report(
        UserSyncDocument snapshot,
        UserSyncDocument patch,
        long uploadedRevision,
        long patchedRevision)
    {
        var whole = Measure(snapshot);
        var sent = Measure(patch);
        Console.WriteLine(
            $"PATCH LIVE snapshot={whole / 1024.0:F0}KiB " +
            $"patch={sent / 1024.0:F0}KiB " +
            $"ratio=1/{(sent == 0 ? 0 : whole / sent)} " +
            $"revision={uploadedRevision}->{patchedRevision}");
    }

    private static long Measure<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).LongLength;

    /// <summary>
    /// Copies the profile's database aside, journal files included. Writing
    /// progress into the seeded profile would change what the application shows
    /// next time it is started, and copying only the main file would drop
    /// anything still sitting in the write-ahead log.
    /// </summary>
    private static string CopyProfile(string profileDirectory, string suffix)
    {
        var workingDirectory = Path.Combine(
            Path.GetTempPath(),
            $"umd-patch-live-{suffix}");
        Directory.CreateDirectory(workingDirectory);
        foreach (var extension in new[] { "", "-wal", "-shm" })
        {
            var source = Path.Combine(
                profileDirectory,
                $"catalog.db{extension}");
            if (File.Exists(source))
            {
                File.Copy(
                    source,
                    Path.Combine(
                        workingDirectory,
                        $"catalog.db{extension}"),
                    overwrite: true);
            }
        }

        return workingDirectory;
    }

    private static async Task<RemoteAccountSessionInfo> RegisterAsync(
        HttpRemoteMediaServerClient client,
        Uri serverUri,
        string username) =>
        await client.RegisterAsync(
            serverUri,
            username,
            Password,
            username,
            "Patch live test");
}

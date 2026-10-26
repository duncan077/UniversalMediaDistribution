using UniversalMediaDistribution.Torrenting;
using UniversalMediaDistribution.Torrenting.Ingestion;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class TorrentDownloadPersistenceTests
{
    [Fact]
    public async Task DownloadRecord_RoundTripsWithManagedFiles()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteTorrentDownloadStore(scope.Database);
        var activityId = TorrentActivityId.Create();
        var sessionId = TorrentSessionId.Create();
        var now = DateTimeOffset.UtcNow;
        var expected = new TorrentDownloadRecord(
            Guid.CreateVersion7(),
            activityId,
            sessionId,
            "Chapter 73",
            "Example manga",
            TorrentDownloadState.Downloading,
            now.AddMinutes(-2),
            now,
            DownloadedBytes: 400,
            TotalBytes: 1_000,
            WantedDownloadedBytes: 400,
            WantedBytes: 1_000,
            Progress: 0.4,
            StorageKey: "ABC123",
            Storage: new TorrentSessionStorage(
            [
                new TorrentStoredFile(
                    Path.Combine(scope.DirectoryPath, "chapter.cbz"),
                    Path.Combine(scope.DirectoryPath, "chapter.cbz.!mt"),
                    1_000)
            ]));

        await store.UpsertAsync(expected);
        var actual = Assert.Single(
            await new SqliteTorrentDownloadStore(scope.Database).LoadAsync());

        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(activityId, actual.ActivityId);
        Assert.Equal(sessionId, actual.SessionId);
        Assert.Equal("Chapter 73", actual.Title);
        Assert.Equal(TorrentDownloadState.Downloading, actual.State);
        Assert.Equal(0.4, actual.Progress);
        Assert.Equal("ABC123", actual.StorageKey);
        Assert.NotNull(actual.Storage);
        var file = Assert.Single(actual.Storage.Files);
        Assert.Equal(1_000, file.Length);
        Assert.EndsWith("chapter.cbz", file.CompletePath);

        await store.DeleteAsync([expected.Id]);

        Assert.Empty(await store.LoadAsync());
    }

    [Fact]
    public async Task DownloadCleanupPreferences_Persist()
    {
        await using var scope = TestDatabaseScope.Create();
        var store = new SqliteTorrentDownloadPreferencesStore(scope.Database);

        await store.SaveAsync(
            new TorrentDownloadPreferences(
                TorrentSessionEndBehavior.DeleteDownloads));

        var actual = await new SqliteTorrentDownloadPreferencesStore(
            scope.Database).LoadAsync();
        Assert.Equal(
            TorrentSessionEndBehavior.DeleteDownloads,
            actual.SessionEndBehavior);
    }
}

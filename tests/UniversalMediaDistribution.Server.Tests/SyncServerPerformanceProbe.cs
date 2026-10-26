using System.Diagnostics;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sync;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class SyncServerPerformanceProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureSnapshotReadWriteAndEmptyDelta()
    {
        var source = Environment.GetEnvironmentVariable("UMD_TEST_SNAPSHOT_JSON");
        if (string.IsNullOrEmpty(source)) return;
        var directory = Path.Combine(Path.GetTempPath(), "umd-server-probe-" + Guid.NewGuid().ToString("N"));
        try
        {
            var document = JsonSerializer.Deserialize<UserSyncDocument>(await File.ReadAllBytesAsync(source), new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            var store = new SqliteUserSyncStore(directory);
            var account = Guid.NewGuid();
            var timer = Stopwatch.StartNew();
            await store.SaveAsync(account, new UserSyncUploadRequest(0, document), CancellationToken.None);
            Report("server-initial-write", timer);
            timer.Restart();
            await store.GetAsync(account, CancellationToken.None);
            Report("server-read", timer);
            timer.Restart();
            await store.GetChangesAsync(account, 1, CancellationToken.None);
            Report("server-empty-delta", timer);
            timer.Restart();
            await store.ApplyProgressPatchAsync(account, new UserSyncProgressPatchRequest(document with
            {
                Catalog = new(),
                Progress = [],
                ReaderPreferences = [],
                FavoriteStates = [],
                FavoriteItemStates = [],
                Lists = [],
                CatalogRedirects = []
            }), CancellationToken.None);
            Report("server-empty-patch", timer);
        }
        finally { if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true); }
    }
    private void Report(string stage, Stopwatch timer) => output.WriteLine($"{stage}={timer.Elapsed.TotalMilliseconds:F1}ms");
}

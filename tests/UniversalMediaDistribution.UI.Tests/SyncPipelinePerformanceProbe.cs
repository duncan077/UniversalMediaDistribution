using System.Diagnostics;
using System.IO.Compression;
using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.UI.Tests;

// Opt-in probe: use a copy, never the database of a running application.
public sealed class SyncPipelinePerformanceProbe(ITestOutputHelper output)
{
    [Fact]
    public async Task MeasureFullRestoreAndRepeatedSync()
    {
        var source = Environment.GetEnvironmentVariable("UMD_TEST_SNAPSHOT_DB");
        if (string.IsNullOrEmpty(source)) return;
        var path = Path.Combine(Path.GetTempPath(), "umd-sync-probe-" + Guid.NewGuid().ToString("N") + ".db");
        File.Copy(source, path);
        try
        {
            var database = new SqliteCatalogDatabase(path);
            var store = new SqliteUserSyncStore(database, new SqliteCatalogRepository(database));
            var timer = Stopwatch.StartNew();
            var document = await store.ExportAsync("probe-device", "Performance probe");
            Report("export", timer);
            var json = JsonSerializer.SerializeToUtf8Bytes(document, new JsonSerializerOptions(JsonSerializerDefaults.Web));
            using var compressed = new MemoryStream();
            using (var gzip = new GZipStream(compressed, CompressionLevel.Fastest, leaveOpen: true)) gzip.Write(json);
            output.WriteLine($"items={document.Catalog.Items.Count}; progress={document.Progress.Count}; bytes={json.Length}; gzipBytes={compressed.Length}");
            var exportPath = Environment.GetEnvironmentVariable("UMD_TEST_SNAPSHOT_JSON");
            if (!string.IsNullOrEmpty(exportPath)) await File.WriteAllBytesAsync(exportPath, json);
            var remote = JsonSerializer.Deserialize<UserSyncDocument>(json, new JsonSerializerOptions(JsonSerializerDefaults.Web))!;
            timer.Restart();
            await store.RestoreAsync(remote);
            Report("restore-identical-full", timer);
            timer.Restart();
            var after = await store.ExportAsync("probe-device", "Performance probe");
            Report("export-after-restore", timer);
            timer.Restart();
            var changes = UserSyncDocumentDelta.ComputeChangedKeys(document, after);
            Report("compare", timer);
            output.WriteLine($"changedRecords={changes.Count}");
        }
        finally { File.Delete(path); }
    }

    private void Report(string stage, Stopwatch timer) => output.WriteLine($"{stage}={timer.Elapsed.TotalMilliseconds:F1}ms");
}

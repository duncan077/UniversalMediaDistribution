using System.Text.Json;
using UniversalMediaDistribution.Catalog.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Reports how large a sync snapshot actually is for a given library.
/// <para>
/// A tool rather than a test. Every sync uploads the whole document and
/// downloads the remote one first, so how often that is worth doing depends on
/// a number nobody had measured. Point <c>UMD_TEST_SNAPSHOT_DB</c> at a
/// <em>copy</em> of a catalog database — exporting initializes the file, so it
/// must never be aimed at one in use.
/// </para>
/// </summary>
public sealed class SyncSnapshotSizeProbe
{
    [Fact]
    public async Task MeasureSnapshotSize()
    {
        var databasePath = Environment.GetEnvironmentVariable(
            "UMD_TEST_SNAPSHOT_DB");
        if (string.IsNullOrWhiteSpace(databasePath) ||
            !File.Exists(databasePath))
        {
            // Does nothing without a database to measure.
            return;
        }

        var database = new SqliteCatalogDatabase(databasePath);
        var repository = new SqliteCatalogRepository(database);
        var store = new SqliteUserSyncStore(database, repository);
        var document = await store.ExportAsync("probe-device", "Probe");

        var whole = Measure(document);
        var catalog = Measure(document.Catalog);
        var progress = Measure(document.Progress);

        Console.WriteLine(
            $"SNAPSHOT total={Kilobytes(whole)} " +
            $"catalog={Kilobytes(catalog)} ({Percent(catalog, whole)}) " +
            $"progress={Kilobytes(progress)} ({Percent(progress, whole)}) " +
            $"items={document.Catalog.Items.Count} " +
            $"progressRows={document.Progress.Count}");

        // The same library, patched for one title that has actually been
        // read: this is what an ordinary reading session would send.
        var readTitle = document.Progress
            .Select(static entry => entry.Content.Item.Name)
            .FirstOrDefault();
        var titles = await repository.ListAsync(cancellationToken: default);
        var busiest = titles.FirstOrDefault(title => string.Equals(
            title.Name,
            readTitle,
            StringComparison.Ordinal));
        if (busiest is null)
        {
            return;
        }

        var patch = await store.ExportProgressPatchAsync(
            "probe-device",
            "Probe",
            [busiest.Id]);

        // Proves it survives the same validation a real upload faces.
        UserSyncDocumentValidator.Validate(
            UserSyncDocumentMerger.Normalize(patch));

        var patchSize = Measure(patch);
        Console.WriteLine(
            $"PATCH total={Kilobytes(patchSize)} " +
            $"items={patch.Catalog.Items.Count} " +
            $"progressRows={patch.Progress.Count} " +
            $"ratio=1/{(patchSize == 0 ? 0 : whole / patchSize)} " +
            $"title='{busiest.Name}'");
    }

    private static long Measure<T>(T value) =>
        JsonSerializer.SerializeToUtf8Bytes(
            value,
            new JsonSerializerOptions(JsonSerializerDefaults.Web)).LongLength;

    private static string Kilobytes(long bytes) =>
        $"{bytes / 1024.0:F0}KiB";

    private static string Percent(long part, long whole) =>
        whole == 0 ? "0%" : $"{100.0 * part / whole:F0}%";
}

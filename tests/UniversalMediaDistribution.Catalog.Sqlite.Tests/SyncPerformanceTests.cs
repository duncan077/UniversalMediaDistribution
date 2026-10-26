using System.Diagnostics;
using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Sync;
using UniversalMediaDistribution.Catalog.Transfer;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class SyncPerformanceTests(ITestOutputHelper output)
{
    [Theory]
    [InlineData(true, false)]
    [InlineData(false, false)]
    [InlineData(true, true)]
    [InlineData(false, true)]
    public async Task PartialRestoreDoesNotRewriteUnrelatedCatalogRows(bool includeCatalogChange, bool fullSnapshot)
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = Enumerable.Range(0, 60).Select(index => Item($"Title {index / 2}", 15) with
            {
                Subtype = index % 2 == 0 ? "manga" : "novel"
            }).ToArray()
        });
        var store = new SqliteUserSyncStore(scope.Database, scope.Repository);
        var snapshot = await store.ExportAsync("performance-device", "Performance");
        var selected = snapshot.Catalog.Items[0];
        await using var connection = new SqliteConnection($"Data Source={scope.DatabasePath};Pooling=False");
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            CREATE TABLE sync_write_probe (item_id TEXT PRIMARY KEY);
            CREATE TRIGGER sync_write_probe_update AFTER UPDATE ON catalog_items
            BEGIN INSERT OR IGNORE INTO sync_write_probe VALUES (new.id); END;
            """;
        await command.ExecuteNonQueryAsync();
        var patch = snapshot with
        {
            Catalog = new CatalogTransferDocument
            {
                Items = !includeCatalogChange ? [] : [selected with
                {
                    Units = selected.Units.Concat([new CatalogTransferUnit
                    {
                        Kind = "chapter", DisplayNumber = "16", SortOrder = 15
                    }]).ToArray()
                }]
            }
        };
        if (fullSnapshot)
        {
            patch = patch with
            {
                Catalog = snapshot.Catalog with
                {
                    Items = snapshot.Catalog.Items.Select(item =>
                        includeCatalogChange && item.Id == selected.Id ? patch.Catalog.Items[0] : item).ToArray()
                }
            };
        }
        // Separate instances as on the wire: reference equality alone must
        // never be the reason repeated snapshots avoid writes.
        patch = System.Text.Json.JsonSerializer.Deserialize<UserSyncDocument>(
            System.Text.Json.JsonSerializer.Serialize(patch))!;
        var stopwatch = Stopwatch.StartNew();
        await store.RestoreAsync(patch);
        stopwatch.Stop();
        command.CommandText = "SELECT COUNT(*) FROM sync_write_probe;";
        var rewritten = (long)(await command.ExecuteScalarAsync())!;
        output.WriteLine($"Partial restore: {stopwatch.Elapsed.TotalMilliseconds:F1} ms; catalog rows rewritten: {rewritten}/60");
        var after = await store.ExportAsync("performance-device", "Performance");
        Assert.Equal(60, after.Catalog.Items.Count);
        Assert.Equal(includeCatalogChange ? 16 : 15, Assert.Single(after.Catalog.Items, item => item.Id == selected.Id).Units.Count);
        Assert.All(after.Catalog.Items.Where(item => item.Id != selected.Id), item => Assert.Equal(15, item.Units.Count));
        Assert.Equal(includeCatalogChange ? 1L : 0L, rewritten);
    }

    [Theory]
    [InlineData(1000)]
    [InlineData(4000)]
    public void LongSeriesMergeKeepsEquivalentChapterNumbersAndNewChapters(int chapterCount)
    {
        var left = Item("Long series", chapterCount);
        var right = left with
        {
            SyncVersion = left.SyncVersion! with { Clock = 2 },
            Units = Enumerable.Range(1, chapterCount + 1).Select(number => new CatalogTransferUnit
            {
                Kind = "chapter",
                DisplayNumber = number.ToString("D5"),
                SortOrder = number - 1,
                PageCount = 20
            }).ToArray()
        };
        var stopwatch = Stopwatch.StartNew();
        var merged = CatalogTransferDocumentMerger.Merge(
            new CatalogTransferDocument { Items = [left] }, new CatalogTransferDocument { Items = [right] });
        stopwatch.Stop();
        output.WriteLine($"Merge {chapterCount} chapters: {stopwatch.Elapsed.TotalMilliseconds:F1} ms");
        var result = Assert.Single(merged.Items);
        Assert.Equal(chapterCount + 1, result.Units.Count);
        Assert.All(result.Units, unit => Assert.Equal(20, unit.PageCount));
        Assert.Equal("00001", result.Units[0].DisplayNumber);
    }

    private static CatalogTransferItem Item(string name, int chapters) => new()
    {
        MediaType = "book",
        Subtype = "manga",
        Name = name,
        SyncVersion = new UserSyncVersion
        {
            Clock = 1,
            DeviceId = "performance-device",
            OperationId = Guid.Parse("22222222-2222-2222-2222-222222222222"),
            ModifiedAt = DateTimeOffset.UnixEpoch.AddDays(1)
        },
        Units = Enumerable.Range(1, chapters).Select(number => new CatalogTransferUnit
        {
            Kind = "chapter",
            DisplayNumber = number.ToString(),
            SortOrder = number - 1
        }).ToArray()
    };
}

using System.Diagnostics;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogPerformanceTests
{
    [Fact]
    public async Task ThousandItemCatalog_ImportsListsAndExportsWithinGuardrail()
    {
        const int itemCount = 1_000;
        var document = new CatalogTransferDocument
        {
            SourceLabel = "Performance regression catalog",
            Items = Enumerable.Range(0, itemCount)
                .Select(static index => new CatalogTransferItem
                {
                    MediaType = (index % 3) switch
                    {
                        0 => "movie",
                        1 => "book",
                        _ => "audio"
                    },
                    Subtype = (index % 3) switch
                    {
                        0 => "movie",
                        1 => "book",
                        _ => "album"
                    },
                    Name = $"Performance item {index:D4}",
                    Description = "A bounded catalog entry used by the startup performance guardrail.",
                    Genres = ["Independent"],
                    Categories = index % 3 == 2 ? [] : ["Performance"]
                })
                .ToArray()
        };

        await using var scope = TestDatabaseScope.Create();
        var transfer = new CatalogJsonTransferService(scope.Repository);
        var stopwatch = Stopwatch.StartNew();

        var import = await transfer.ImportAsync(document, "performance regression");
        var items = await scope.Repository.ListAsync();
        await using var exported = new MemoryStream();
        await transfer.ExportAsync(exported);

        stopwatch.Stop();

        Assert.Equal(itemCount, import.CreatedCount);
        Assert.Equal(itemCount, items.Count);
        Assert.True(exported.Length > 0);
        Assert.True(
            stopwatch.Elapsed < TimeSpan.FromSeconds(30),
            $"Catalog import, list, and export took {stopwatch.Elapsed}.");
    }
}

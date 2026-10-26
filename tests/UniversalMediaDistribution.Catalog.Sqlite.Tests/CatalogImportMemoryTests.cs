using System.Diagnostics;
using UniversalMediaDistribution.Catalog.Transfer;
using Xunit.Abstractions;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogImportMemoryTests(ITestOutputHelper output)
{
    [Fact]
    public async Task ImportPreservesExistingUnitsAndTargetedExportExcludesOtherSeries()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = Enumerable.Range(0, 25).Select(index => new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = $"Series {index:D2}",
                Description = new string('d', 4000),
                Units = Enumerable.Range(1, 100).Select(number => new CatalogTransferUnit
                {
                    Kind = "chapter",
                    DisplayNumber = number.ToString(),
                    SortOrder = number,
                    Name = $"Chapter {number}"
                }).ToArray()
            }).ToArray()
        });
        var id = (await scope.Repository.ListAsync()).Single(item => item.Name == "Series 00").Id;
        var update = new CatalogTransferDocument
        {
            Items = [new CatalogTransferItem
            {
                MediaType = "book", Subtype = "manga", Name = "Series 00",
                Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "101", SortOrder = 101 }]
            }]
        };

        // SQLite's completed async operations execute on this thread. These
        // diagnostics compare identical data before/after without flaky time
        // limits on a shared test host; correctness is asserted below.
        var before = GC.GetAllocatedBytesForCurrentThread();
        var thread = Environment.CurrentManagedThreadId;
        var watch = Stopwatch.StartNew();
        var import = await scope.Repository.MergeCatalogAsync(update);
        if (thread == Environment.CurrentManagedThreadId)
            output.WriteLine($"Import allocation={GC.GetAllocatedBytesForCurrentThread() - before:N0} bytes; elapsed={watch.Elapsed.TotalMilliseconds:F1}ms");
        Assert.Equal(1, import.UpdatedCount);
        var exported = await scope.Repository.ExportCatalogItemAsync(id);
        var item = Assert.Single(exported.Items);
        Assert.Equal("Series 00", item.Name);
        Assert.Equal(101, item.Units.Count);
        Assert.Equal(4000, item.Description!.Length);
        var matched = await scope.Repository.ExportMatchingCatalogItemsAsync(update.Items);
        Assert.Equal(101, Assert.Single(matched.Items).Units.Count);
        Assert.Empty((await scope.Repository.ExportMatchingCatalogItemsAsync(
            [new CatalogTransferItem { MediaType = "book", Subtype = "manga", Name = "Unknown" }])).Items);
        Assert.Equal(25, (await scope.Repository.ListAsync()).Count);
        Assert.Empty((await scope.Repository.ExportCatalogItemAsync(Guid.NewGuid())).Items);
        var all = await scope.Repository.ExportCatalogAsync();
        Assert.All(all.Items.Where(other => other.Name != item.Name), other => Assert.Equal(100, other.Units.Count));
    }

    [Fact]
    public async Task MatchingExportPreservesAliasesFormatsAndTombstones()
    {
        await using var scope = TestDatabaseScope.Create();
        var book = new CatalogTransferItem
        {
            MediaType = "book",
            Subtype = "manga",
            Name = "Saved title",
            AlternativeNames = [new CatalogTransferAlias { Name = "Provider title", Language = "en", AliasType = "provider" }],
            Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "1" }]
        };
        await scope.Repository.MergeCatalogAsync(new CatalogTransferDocument
        {
            Items = Enumerable.Range(0, 10).Select(index => new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "manga",
                Name = $"Unrelated {index}"
            }).Append(book).Append(new CatalogTransferItem
            {
                MediaType = "book",
                Subtype = "novel",
                Name = "Provider title"
            }).ToArray()
        });
        var candidate = new CatalogTransferItem { MediaType = "book", Subtype = "manga", Name = "Provider title" };
        var matched = Assert.Single((await scope.Repository.ExportMatchingCatalogItemsAsync([candidate])).Items);
        Assert.Equal("Saved title", matched.Name);
        Assert.Single(matched.Units);
        var id = (await scope.Repository.ListAsync()).Single(item => item.Name == "Saved title").Id;
        await scope.Repository.DeleteAsync(id);
        var deleted = Assert.Single((await scope.Repository.ExportMatchingCatalogItemsAsync([book])).Items);
        Assert.True(deleted.Deleted);
        Assert.NotNull(deleted.SyncVersion);
    }
}

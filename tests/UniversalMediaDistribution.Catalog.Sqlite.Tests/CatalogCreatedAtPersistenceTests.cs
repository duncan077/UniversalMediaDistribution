using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

/// <summary>
/// The "recently added" and "recently updated" rows on a browse page are only
/// as honest as these two timestamps, and they only tell different stories if
/// a re-import leaves the first one alone.
/// </summary>
public sealed class CatalogCreatedAtPersistenceTests
{
    [Fact]
    public async Task AnImportedItemComesBackWithWhenItWasAdded()
    {
        var before = DateTimeOffset.UtcNow.AddSeconds(-5);
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document("Added"));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));

        Assert.InRange(
            item.CreatedAt,
            before,
            DateTimeOffset.UtcNow.AddSeconds(5));
    }

    [Fact]
    public async Task ReimportingLeavesTheOriginalAddedDateAlone()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document("Serialized"));
        var added = Assert
            .Single(await scope.Repository.ListAsync(MediaType.Movie))
            .CreatedAt;

        await scope.Repository.MergeCatalogAsync(
            Document("Serialized", description: "Now with a new chapter."));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));
        Assert.Equal(added, item.CreatedAt);
        Assert.True(
            item.UpdatedAt >= item.CreatedAt,
            "An item cannot have been updated before it was added.");
    }

    [Fact]
    public async Task ReimportingTheSamePayloadIsNotAnUpdate()
    {
        // A remote sync or a re-imported export carries the whole library.
        // Rewriting updated_at for all of it would say everything changed.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document("Unchanged"));
        var first = Assert
            .Single(await scope.Repository.ListAsync(MediaType.Movie))
            .UpdatedAt;

        await Task.Delay(1100);
        var result = await scope.Repository.MergeCatalogAsync(
            Document("Unchanged"));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));
        Assert.Equal(first, item.UpdatedAt);
        Assert.Equal(0, result.UpdatedCount);
        Assert.Equal(1, result.UnchangedCount);
    }

    [Fact]
    public async Task AChangedFieldIsAnUpdate()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document("Revised"));
        var first = Assert
            .Single(await scope.Repository.ListAsync(MediaType.Movie))
            .UpdatedAt;

        await Task.Delay(1100);
        var result = await scope.Repository.MergeCatalogAsync(
            Document("Revised", description: "A better synopsis."));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));
        Assert.True(
            item.UpdatedAt > first,
            "A document that says something new must move updated_at.");
        Assert.Equal(1, result.UpdatedCount);
        Assert.Equal(0, result.UnchangedCount);
    }

    [Fact]
    public async Task ANewChapterIsAnUpdateEvenThoughTheItemItselfIsUnchanged()
    {
        // The whole point of the "recently updated" row: nothing about the
        // series changed, but it has a chapter it did not have before.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Manga("Serialized", 1));
        var first = Assert
            .Single(await scope.Repository.ListAsync(MediaType.Book))
            .UpdatedAt;

        await Task.Delay(1100);
        var result = await scope.Repository.MergeCatalogAsync(
            Manga("Serialized", 2));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Book));
        Assert.True(
            item.UpdatedAt > first,
            "A new chapter is exactly what 'recently updated' means.");
        Assert.Equal(1, result.UpdatedCount);
    }

    [Fact]
    public void AnItemBuiltWithoutOneReadsAsAddedWhenItWasLastTouched()
    {
        var touched = new DateTimeOffset(2026, 3, 4, 5, 6, 7, TimeSpan.Zero);
        var item = new CatalogItem(
            Guid.CreateVersion7(),
            MediaType.Movie,
            CatalogSubtypeResolver.ResolveCode(MediaType.Movie, "movie"),
            "No creation date",
            updatedAt: touched);

        Assert.Equal(touched, item.CreatedAt);
    }

    private static CatalogTransferDocument Document(
        string name,
        string? description = null) => new()
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "movie",
                    Subtype = "movie",
                    Name = name,
                    Description = description ?? $"Description for {name}."
                }
            ]
        };

    private static CatalogTransferDocument Manga(
        string name,
        int chapterCount) => new()
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = name,
                    Description = $"Description for {name}.",
                    Units = Enumerable
                        .Range(1, chapterCount)
                        .Select(number => new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = number.ToString(
                                System.Globalization.CultureInfo
                                    .InvariantCulture),
                            SortOrder = number - 1
                        })
                        .ToArray()
                }
            ]
        };
}

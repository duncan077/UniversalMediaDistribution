using Microsoft.Data.Sqlite;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class ChapterSortOrderPersistenceTests
{
    private static CatalogTransferDocument Manga(
        params (string DisplayNumber, int SortOrder)[] chapters) =>
        new()
        {
            Items =
            [
                new CatalogTransferItem
                {
                    MediaType = "book",
                    Subtype = "manga",
                    Name = "El Perro Loco de la Mansión del Duque",
                    Units = chapters
                        .Select(chapter => new CatalogTransferUnit
                        {
                            Kind = "chapter",
                            DisplayNumber = chapter.DisplayNumber,
                            SortOrder = chapter.SortOrder
                        })
                        .ToArray()
                }
            ]
        };

    private static async Task<(string Display, long Sort)[]> ReadStoredAsync(
        TestDatabaseScope scope)
    {
        await using var connection = new SqliteConnection(
            new SqliteConnectionStringBuilder
            {
                DataSource = scope.DatabasePath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            }.ToString());
        await connection.OpenAsync();
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT display_number, sort_order
            FROM content_units
            ORDER BY sort_order;
            """;
        var rows = new List<(string, long)>();
        await using var reader = await command.ExecuteReaderAsync();
        while (await reader.ReadAsync())
        {
            rows.Add((reader.GetString(0), reader.GetInt64(1)));
        }

        return rows.ToArray();
    }

    [Fact]
    public async Task StoredSortOrderFollowsTheChapterNumber()
    {
        // The shape a provider's daily update actually produces: the newest
        // chapters arrive in their own batch numbered from zero, so 67 and 68
        // claim the first two positions ahead of 65 and 66.
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Manga(("67", 0), ("68", 1), ("65", 2), ("66", 3)));

        var stored = await ReadStoredAsync(scope);

        Assert.Equal(
            ["65", "66", "67", "68"],
            stored.Select(static row => row.Display));
        Assert.Equal([0L, 1L, 2L, 3L], stored.Select(static row => row.Sort));
    }

    [Fact]
    public async Task ADailyUpdateDoesNotPushNewChaptersToTheFront()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Manga(("65", 0), ("66", 1)));

        // A later refresh publishes the next two, numbered from zero again.
        await scope.Repository.MergeCatalogAsync(
            Manga(("67", 0), ("68", 1)));

        var stored = await ReadStoredAsync(scope);

        Assert.Equal(
            ["65", "66", "67", "68"],
            stored.Select(static row => row.Display));
    }

    [Fact]
    public async Task DecimalChaptersSitBetweenTheirNeighbours()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Manga(("56.5", 0), ("57", 1), ("54", 2), ("55", 3), ("56", 4)));

        var stored = await ReadStoredAsync(scope);

        Assert.Equal(
            ["54", "55", "56", "56.5", "57"],
            stored.Select(static row => row.Display));
    }

    [Fact]
    public async Task UnnumberedChaptersKeepTheirOrderAtTheEnd()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Manga(("Especial", 9), ("2", 1), ("Extra", 8), ("1", 0)));

        var stored = await ReadStoredAsync(scope);

        Assert.Equal(
            ["1", "2", "Extra", "Especial"],
            stored.Select(static row => row.Display));
    }

    [Fact]
    public async Task DetailsReadBackInChapterOrder()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Manga(("67", 0), ("68", 1), ("65", 2), ("66", 3)));
        var item = Assert.Single(await scope.Repository.ListAsync(MediaType.Book));

        var details = await scope.Repository.GetDetailsAsync(item.Id);

        Assert.NotNull(details);
        Assert.Equal(
            ["65", "66", "67", "68"],
            details.Units.Select(static unit => unit.DisplayNumber));
    }
}

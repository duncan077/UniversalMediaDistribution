using System.Text.Json;
using UniversalMediaDistribution.Catalog.Transfer;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Catalog.Sqlite.Tests;

public sealed class CatalogReleaseYearTests
{
    [Fact]
    public async Task StoredYearSurvivesASaveWithoutAnyReleaseDate()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Document("Year only", releaseYear: 1988));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));

        Assert.Equal(1988, item.ReleaseYear);
        Assert.Null(item.ReleaseDate);
    }

    [Fact]
    public async Task SavingOnlyAReleaseDateStillPopulatesTheYear()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Document("Date only", releaseDate: "1995-11-29"));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));

        Assert.Equal(1995, item.ReleaseYear);
    }

    [Fact]
    public async Task StoredYearWinsOverTheReleaseDate()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(Document(
            "Rereleased",
            releaseDate: "2019-04-05",
            releaseYear: 1988));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));

        Assert.Equal(1988, item.ReleaseYear);
    }

    [Fact]
    public async Task YearRoundTripsThroughExport()
    {
        await using var scope = TestDatabaseScope.Create();
        var transfer = new CatalogJsonTransferService(scope.Repository);
        await transfer.ImportAsync(
            Document("Exported", releaseYear: 2004),
            "release year test");

        await using var json = new MemoryStream();
        await transfer.ExportAsync(json);
        json.Position = 0;

        using var parsed = await JsonDocument.ParseAsync(json);
        var exported = Assert.Single(
            parsed.RootElement.GetProperty("items").EnumerateArray().ToArray());
        Assert.Equal(2004, exported.GetProperty("releaseYear").GetInt32());
    }

    [Fact]
    public async Task AnImplausibleYearIsRejectedRatherThanStored()
    {
        await using var scope = TestDatabaseScope.Create();

        // Refusing the document beats accepting it and quietly blanking the
        // field: a five-digit year means the source is wrong about something.
        await Assert.ThrowsAsync<CatalogTransferValidationException>(
            async () => await scope.Repository.MergeCatalogAsync(
                Document("Nonsense", releaseYear: 12_025)));

        Assert.Empty(await scope.Repository.ListAsync(MediaType.Movie));
    }

    [Fact]
    public void ADirectlyConstructedItemDropsAnImplausibleYear()
    {
        // The domain object is the last line of defence, reached by paths that
        // never went through document validation.
        var item = new CatalogItem(
            Guid.CreateVersion7(),
            MediaType.Movie,
            CatalogSubtypeResolver.ResolveCode(MediaType.Movie, "movie"),
            "Nonsense",
            releaseYear: 12_025);

        Assert.Null(item.ReleaseYear);
    }

    [Fact]
    public async Task AnUpdateWithoutAYearKeepsTheStoredOne()
    {
        await using var scope = TestDatabaseScope.Create();
        await scope.Repository.MergeCatalogAsync(
            Document("Kept", releaseYear: 1988));
        await scope.Repository.MergeCatalogAsync(Document("Kept"));

        var item = Assert.Single(
            await scope.Repository.ListAsync(MediaType.Movie));

        Assert.Equal(1988, item.ReleaseYear);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData(1988, null, 1988)]
    [InlineData(null, "1995-11-29", 1995)]
    [InlineData(1988, "1995-11-29", 1988)]
    [InlineData(12_025, "1995-11-29", 1995)]
    [InlineData(null, "not-a-date", null)]
    [InlineData(null, "1450-01-01", null)]
    public void ResolveAppliesStoredThenDateThenPlausibility(
        int? storedYear,
        string? releaseDate,
        int? expected) =>
        Assert.Equal(
            expected,
            CatalogReleaseYear.Resolve(storedYear, releaseDate));

    private static CatalogTransferDocument Document(
        string name,
        string? releaseDate = null,
        int? releaseYear = null) => new()
        {
            Items =
        [
            new CatalogTransferItem
            {
                MediaType = "movie",
                Subtype = "movie",
                Name = name,
                Description = $"Description for {name}.",
                ReleaseDate = releaseDate,
                ReleaseYear = releaseYear
            }
        ]
        };
}

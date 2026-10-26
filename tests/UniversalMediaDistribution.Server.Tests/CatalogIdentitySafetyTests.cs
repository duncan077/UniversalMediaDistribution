using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class CatalogIdentitySafetyTests
{
    [Theory]
    [InlineData("Desconocido")]
    [InlineData(" Unknown ")]
    [InlineData("N/A")]
    [InlineData("Sin título")]
    [InlineData("---")]
    public void PlaceholderAliasesNeverIdentifyDifferentWorks(string placeholder)
    {
        var first = Book("Manual de Reeducación de Damas Nobles", placeholder);
        var second = Book("Magic Emperor", placeholder);
        Assert.DoesNotContain(placeholder.Trim().ToUpperInvariant(), CatalogTransferIdentity.GetNameKeys(first));
        Assert.Null(CatalogTransferIdentity.FindBestMatch([first], second));
        Assert.Equal(0, LevenshteinTitleMatcher.Similarity(placeholder, placeholder));
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        Assert.NotEqual(store.MergeScraped("a", "1", first, default).ItemId,
            store.MergeScraped("b", "2", second, default).ItemId);
        Assert.Equal(2, store.Page(null, 10, default).Document.Items.Count);
    }

    [Fact]
    public void SingleSecondarySynonymIsNotEnoughButPrimaryTranslationsStillConverge()
    {
        var first = Book("Noble Lady Reformation Guide", "Shared secondary title");
        Assert.Null(CatalogTransferIdentity.FindBestMatch([first], Book("Different work", "Shared secondary title")));
        Assert.Same(first, CatalogTransferIdentity.FindBestMatch([first], Book("Manual de Reeducación de Damas Nobles", first.Name)));
        Assert.Same(first, CatalogTransferIdentity.FindBestMatch([first], Book("Different translation", "Shared secondary title", first.Name)));
        Assert.Same(first, CatalogTransferIdentity.FindBestMatch([first], first));
        Assert.Null(CatalogTransferIdentity.FindBestMatch(
            [Book("First work", "Some title!", "Some title?")],
            Book("Second work", "Some title!", "Some title?")));
    }

    [Fact]
    public void AliasBridgeDoesNotJoinTwoPreviouslySeparateWorks()
    {
        using var scope = TestDirectory.Create();
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var first = Book("Noble Lady Reformation Guide");
        var second = Book("Magic Emperor");
        var a = store.MergeScraped("a", "1", first, default);
        var b = store.MergeScraped("b", "2", second, default);
        var bridge = Book("Unverified translation", first.Name, second.Name);
        Assert.Null(CatalogTransferIdentity.FindBestMatch([first, second], bridge));
        var c = store.MergeScraped("c", "3", bridge, default);
        Assert.NotEqual(a.ItemId, c.ItemId);
        Assert.NotEqual(b.ItemId, c.ItemId);
        Assert.Equal(3, store.Page(null, 10, default).Document.Items.Count);

        // Even an exact primary match updates only that row, not every row
        // mentioned in a questionable synonym list.
        var update = store.MergeScraped("d", "4", first with { AlternativeNames = bridge.AlternativeNames }, default);
        Assert.Equal(a.ItemId, update.ItemId);
        Assert.Equal(3, store.Page(null, 10, default).Document.Items.Count);
        Assert.Equal("Magic Emperor", Assert.Single(store.Page(null, 10, default).Document.Items, i => i.Id == b.ItemId).Name);
    }

    [Fact]
    public void TwoIndependentAliasesCanIdentifyTranslationsButFormatsAndYearsStaySeparate()
    {
        var first = Book("Spanish title", "English title", "Original title");
        var translated = Book("French title", "English title", "Original title");
        Assert.Same(first, CatalogTransferIdentity.FindBestMatch([first], translated));
        Assert.Null(CatalogTransferIdentity.FindBestMatch([first], translated with { Subtype = "novel" }));
        Assert.Null(CatalogTransferIdentity.FindBestMatch([first with { ReleaseYear = 2000 }], translated with { ReleaseYear = 2020 }));
    }

    [Fact]
    public void BoundProviderRefreshCannotAbsorbAnotherMatchingRow()
    {
        using var scope = TestDirectory.Create();
        var database = new ServerDatabase(scope.Path);
        var store = new SqliteOnlineCatalogStore(scope.Path);
        var first = Book("Noble Lady Reformation Guide");
        var second = Book("Magic Emperor");
        var a = store.MergeScraped("a", "1", first, default);
        var b = store.MergeScraped("b", "2", second, default);
        var queue = new CatalogCrawlerStore(database);
        queue.State("a");
        queue.Enqueue("a", [new("1", first)], DateTimeOffset.UtcNow, updates: true);
        queue.TitleSucceeded("a", "1", a.ItemId, DateTimeOffset.UtcNow);
        Assert.Equal(a.ItemId, store.MergeScraped("a", "1", Book(first.Name, second.Name), default).ItemId);
        Assert.Equal(2, store.Page(null, 10, default).Document.Items.Count);
        Assert.Equal(second.Name, Assert.Single(store.Page(null, 10, default).Document.Items, i => i.Id == b.ItemId).Name);
    }

    private static CatalogTransferItem Book(string name, params string[] aliases) => new()
    {
        MediaType = "book",
        Subtype = "manga",
        Name = name,
        AlternativeNames = aliases.Select(alias => new CatalogTransferAlias { Name = alias }).ToArray(),
        Units = [new CatalogTransferUnit { Kind = "chapter", DisplayNumber = "1" }]
    };
}

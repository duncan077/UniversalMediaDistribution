using UniversalMediaDistribution.Catalog.Transfer;

namespace UniversalMediaDistribution.Server.Tests;

public sealed class ScrapedCatalogAliasTests
{
    [Fact]
    public void DuplicateSixthAliasNoLongerRejectsTheScrapedTitle()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        var item = Title with
        {
            AlternativeNames =
            [
                new() { Name = "1 plus 1", Language = "en" },
                new() { Name = "One Plus One", Language = "en" },
                new() { Name = "1+1" },
                new() { Name = "Uno más uno", Language = "es" },
                new() { Name = "1 plus 1", Language = "fr" },
                new() { Name = " 1  PLUS  1 ", Language = "EN", AliasType = " OTHER " }
            ],
            Units = [new() { Kind = "chapter", DisplayNumber = "1" }]
        };
        Assert.Throws<CatalogTransferValidationException>(() => CatalogTransferValidator.Validate(new() { Items = [item] }));
        var result = catalog.MergeScraped("fixture", "one-plus-one", item, CancellationToken.None);
        var saved = Assert.Single(catalog.Page(null, 10, CancellationToken.None).Document.Items);
        Assert.Equal(result.ItemId, saved.Id);
        Assert.Equal("1 más 1", saved.Name);
        Assert.Equal("1", Assert.Single(saved.Units).DisplayNumber);
        Assert.Equal(item.AlternativeNames.Take(5), saved.AlternativeNames);
        Assert.Null(saved.Aliases);
        Assert.False(catalog.MergeScraped("fixture", "one-plus-one", item, CancellationToken.None).Changed);
        Assert.Equal(6, item.AlternativeNames.Count);
    }

    [Fact]
    public void LegacyAndCanonicalAliasFieldsUseTheSameUnicodeLanguageAndKindIdentity()
    {
        var item = Title with
        {
            OriginalName = "Original title",
            AlternativeNames = [new() { Name = "Café title", Language = null, AliasType = "original" }],
            Aliases =
            [
                new() { Name = "Cafe\u0301  TITLE", Language = "UND", AliasType = " ORIGINAL " },
                new() { Name = "Café title", Language = "es", AliasType = "original" },
                new() { Name = "Café title", AliasType = "provider" }
            ]
        };
        var sanitized = CatalogMetadataOnly.Sanitize(item);
        CatalogTransferValidator.Validate(new() { Items = [sanitized] });
        Assert.Equal(3, sanitized.AlternativeNames.Count);
        Assert.Equal(item.AlternativeNames[0], sanitized.AlternativeNames[0]);
        Assert.Equal("es", sanitized.AlternativeNames[1].Language);
        Assert.Equal("provider", sanitized.AlternativeNames[2].AliasType);
        Assert.Equal("Original title", sanitized.OriginalName);
        Assert.Null(sanitized.Aliases);
    }

    [Theory]
    [InlineData("", "en", "other")]
    [InlineData("Alias", "not_a_language", "other")]
    [InlineData("Alias", "en", "")]
    public void MalformedAliasesAreStillRejected(string name, string language, string kind)
    {
        var item = Title with { AlternativeNames = [new() { Name = name, Language = language, AliasType = kind }] };
        Assert.Throws<CatalogTransferValidationException>(() => CatalogTransferValidator.Validate(
            new() { Items = [CatalogMetadataOnly.Sanitize(item)] }));
    }

    [Fact]
    public async Task ManualUploadsStillRejectDuplicatesWithoutChangingTheStoredCatalog()
    {
        using var scope = TestDirectory.Create();
        var catalog = new SqliteOnlineCatalogStore(scope.Path);
        await catalog.SaveAsync(new() { Items = [Title] }, CancellationToken.None);
        var duplicate = new CatalogTransferAlias { Name = "Alias", Language = "es" };
        await Assert.ThrowsAsync<CatalogTransferValidationException>(() => catalog.SaveAsync(
            new() { Items = [Title with { AlternativeNames = [duplicate, duplicate] }] }, CancellationToken.None).AsTask());
        Assert.Empty(Assert.Single(catalog.Page(null, 10, CancellationToken.None).Document.Items).AlternativeNames);
    }

    private static readonly CatalogTransferItem Title = new() { Name = "1 más 1", MediaType = "book", Subtype = "manga" };
}

using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Domain.Tests.Catalog;

public sealed class CatalogItemTests
{
    [Fact]
    public void Constructor_NormalizesTaxonomiesAndLocalizedAlternativeNames()
    {
        var item = new CatalogItem(
            Guid.CreateVersion7(),
            MediaType.Book,
            "book.manga",
            "  Copper Skies  ",
            genres: [" Fantasy ", "fantasy", "Adventure"],
            categories: [" Manga ", "Comics", "manga"],
            alternativeNames:
            [
                new CatalogAlternativeName(" Cielos de Cobre ", "ES", "Translation"),
                new CatalogAlternativeName("Cielos de Cobre", "es", "translation"),
                new CatalogAlternativeName("Kopperu Sukai", "ja-Latn", "romanized")
            ]);

        Assert.Equal("Copper Skies", item.Name);
        Assert.Equal("COPPER SKIES", item.NameKey);
        Assert.Equal(["Adventure", "Fantasy"], item.Genres);
        Assert.Equal(["Comics", "Manga"], item.Categories);
        Assert.Equal(2, item.AlternativeNames.Count);
        Assert.Equal("es", item.AlternativeNames[0].Language);
        Assert.Equal("translation", item.AlternativeNames[0].Kind);
    }

    [Fact]
    public void Constructor_RejectsSubtypeFromAnotherMediaType()
    {
        Assert.Throws<ArgumentException>(() => new CatalogItem(
            Guid.CreateVersion7(),
            MediaType.Movie,
            "book.manga",
            "Wrong type"));
    }

    [Fact]
    public void Constructor_RejectsCategoriesForAudio()
    {
        Assert.Throws<ArgumentException>(() => new CatalogItem(
            Guid.CreateVersion7(),
            MediaType.Audio,
            "audio.album",
            "Categorized album",
            categories: ["Album"]));
    }

    [Fact]
    public void WebNovelSubtype_UsesBookChapterHierarchy()
    {
        var definition = MediaSubtypeDefinition.GetRequired("book.web_novel");
        var item = CatalogItem.Create(
            MediaType.Book,
            "book.web_novel",
            "Open web novel");

        Assert.Equal(MediaType.Book, definition.MediaType);
        Assert.Equal(HierarchyMode.Chapters, definition.HierarchyMode);
        Assert.Equal(ContentUnitKind.Chapter, definition.DefaultUnitKind);
        Assert.Equal("book.web_novel", item.SubtypeCode);
    }

    [Fact]
    public void Constructor_NormalizesContributorIdentityAndLanguageTags()
    {
        var contributor = new CatalogContributor(
            "  Open Ink  ",
            "Organization",
            "https://example.test/open-ink",
            credits:
            [
                new CatalogContributorCredit(" Publisher ", sortOrder: 1)
            ]);
        var item = CatalogItem.Create(
            MediaType.Book,
            "book.book",
            "Open book",
            contributors: [contributor]);

        Assert.Equal("organization:OPEN INK", contributor.StableKey);
        Assert.Equal("https://example.test/open-ink", contributor.Url?.AbsoluteUri);
        Assert.Equal("Publisher", Assert.Single(contributor.Credits).Role);
        Assert.True(contributor.IsDistributor);
        Assert.Same(contributor, Assert.Single(item.Contributors));
        Assert.Equal("es-ar", LanguageTag.Normalize(" ES-AR "));
        Assert.Equal("und", LanguageTag.Normalize(null));
    }

    [Theory]
    [InlineData("javascript:alert(1)")]
    [InlineData("https://user:secret@example.test/profile")]
    [InlineData("/relative")]
    public void Contributor_RejectsUnsafeUrl(string url)
    {
        Assert.Throws<ArgumentException>(() =>
            new CatalogContributor("Unsafe", url: url));
    }

    [Theory]
    [InlineData("-en")]
    [InlineData("en-")]
    [InlineData("en-123456789")]
    public void LanguageTag_RejectsInvalidValue(string language)
    {
        Assert.Throws<ArgumentException>(() => LanguageTag.Normalize(language));
    }
}

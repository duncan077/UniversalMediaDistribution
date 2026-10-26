using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Domain.Tests;

/// <summary>
/// This key is how two installations agree they are talking about the same
/// title. The client builds it to ask, the server builds it from a sync
/// snapshot to answer, and a difference between the two shows up as a friend
/// with no progress rather than as an error — so the rule is pinned here.
/// </summary>
public sealed class ContentItemKeyTests
{
    [Fact]
    public void SpacingAndCaseOfTheMediaTypeDoNotMatter()
    {
        Assert.Equal(
            ContentItemKey.Create("book", "Copper Skies"),
            ContentItemKey.Create("  BOOK ", "Copper Skies"));
    }

    [Fact]
    public void RepeatedAndSurroundingWhitespaceInTheNameDoesNotMatter()
    {
        Assert.Equal(
            ContentItemKey.Create("book", "Copper Skies"),
            ContentItemKey.Create("book", " Copper   Skies "));
    }

    /// <summary>
    /// The name is folded to upper case, so a key cannot be written out by
    /// hand from a title as it is displayed. Pinned because reading the key off
    /// a screen and typing it into a request is the obvious thing to try, and
    /// it silently matches nothing.
    /// </summary>
    [Fact]
    public void TheNameIsFoldedToUpperCase()
    {
        Assert.Equal(
            "book:COPPER SKIES",
            ContentItemKey.Create("book", "Copper Skies"));
        Assert.Equal(
            ContentItemKey.Create("book", "copper skies"),
            ContentItemKey.Create("book", "COPPER SKIES"));
    }

    [Fact]
    public void DifferentTitlesKeepDifferentKeys()
    {
        Assert.NotEqual(
            ContentItemKey.Create("book", "Copper Skies"),
            ContentItemKey.Create("book", "Iron Tide"));
    }

    /// <summary>
    /// A film and a book of the same name are two different things.
    /// </summary>
    [Fact]
    public void TheMediaTypeSeparatesTitlesThatShareAName()
    {
        Assert.NotEqual(
            ContentItemKey.Create("book", "Copper Skies"),
            ContentItemKey.Create("movie", "Copper Skies"));
    }

    /// <summary>
    /// The enum overload has to spell the media type the way the catalog
    /// stores it, or keys built from a card would miss keys built from a sync
    /// snapshot.
    /// </summary>
    [Fact]
    public void TheEnumOverloadMatchesTheStoredSpelling()
    {
        Assert.Equal(
            ContentItemKey.Create("book", "Copper Skies"),
            ContentItemKey.Create(MediaType.Book, "Copper Skies"));
        Assert.Equal(
            ContentItemKey.Create("movie", "Signal in the Dust"),
            ContentItemKey.Create(MediaType.Movie, "Signal in the Dust"));
    }

    [Fact]
    public void AnEmptyMediaTypeOrNameIsRejected()
    {
        Assert.Throws<ArgumentException>(
            () => ContentItemKey.Create("   ", "Copper Skies"));
        Assert.Throws<ArgumentException>(
            () => ContentItemKey.Create("book", "   "));
    }
}

using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.Domain.Tests;

public sealed class MediaSubtypeGroupTests
{
    [Theory]
    [InlineData("movie.anime_movie")]
    // OVAs are filed as anime series, so they follow the same shelf.
    [InlineData("movie.anime_series")]
    [InlineData("MOVIE.ANIME_SERIES")]
    public void AnimeSubtypesResolveToTheAnimeShelf(string subtypeCode)
    {
        Assert.True(MediaSubtypeGroups.IsAnime(subtypeCode));
        Assert.Equal(
            MediaSubtypeGroup.Anime,
            MediaSubtypeGroups.Resolve(subtypeCode));
    }

    [Theory]
    [InlineData("movie.movie")]
    [InlineData("movie.series")]
    [InlineData("movie.documentary")]
    [InlineData("movie.short")]
    public void EveryOtherMovieSubtypeStaysLiveAction(string subtypeCode)
    {
        Assert.False(MediaSubtypeGroups.IsAnime(subtypeCode));
        Assert.Equal(
            MediaSubtypeGroup.LiveAction,
            MediaSubtypeGroups.Resolve(subtypeCode));
    }

    [Fact]
    public void EverySubtypeCodeInTheGroupIsAKnownSubtype()
    {
        foreach (var code in new[] { "movie.anime_movie", "movie.anime_series" })
        {
            Assert.Equal(
                MediaType.Movie,
                MediaSubtypeDefinition.GetRequired(code).MediaType);
        }
    }

    [Fact]
    public void NoGroupMeansTheShelfIsNotSplitSoEverythingMatches()
    {
        Assert.True(MediaSubtypeGroups.Matches(
            group: null,
            MediaType.Movie,
            "movie.anime_movie"));
        Assert.True(MediaSubtypeGroups.Matches(
            group: null,
            MediaType.Movie,
            "movie.movie"));
    }

    [Fact]
    public void GroupsOnlyApplyToMoviesSoBooksAndAudioAreNeverExcluded()
    {
        Assert.True(MediaSubtypeGroups.Matches(
            MediaSubtypeGroup.Anime,
            MediaType.Book,
            "book.manga"));
        Assert.True(MediaSubtypeGroups.Matches(
            MediaSubtypeGroup.LiveAction,
            MediaType.Audio,
            "audio.album"));
    }

    [Fact]
    public void TheTwoMovieShelvesDoNotOverlap()
    {
        Assert.True(MediaSubtypeGroups.Matches(
            MediaSubtypeGroup.Anime,
            MediaType.Movie,
            "movie.anime_series"));
        Assert.False(MediaSubtypeGroups.Matches(
            MediaSubtypeGroup.Anime,
            MediaType.Movie,
            "movie.series"));
        Assert.True(MediaSubtypeGroups.Matches(
            MediaSubtypeGroup.LiveAction,
            MediaType.Movie,
            "movie.series"));
        Assert.False(MediaSubtypeGroups.Matches(
            MediaSubtypeGroup.LiveAction,
            MediaType.Movie,
            "movie.anime_series"));
    }

    [Fact]
    public void AnUnknownOrMissingSubtypeIsTreatedAsLiveAction()
    {
        Assert.False(MediaSubtypeGroups.IsAnime(null));
        Assert.False(MediaSubtypeGroups.IsAnime(string.Empty));
        Assert.Equal(
            MediaSubtypeGroup.LiveAction,
            MediaSubtypeGroups.Resolve("movie.unknown"));
    }
}

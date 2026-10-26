using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

/// <summary>
/// Guards how a provider's search results are ranked when several of them tie
/// on title similarity.
/// <para>
/// Captured live from pelisplus.autos and seriesflixhd.ink on 2026-08-01: a
/// search for "All American" returns "My All American" (a 2015 film),
/// "All American: Homecoming" (the spin-off) and "All American" itself, and
/// <see cref="LevenshteinTitleMatcher.SimilarityNormalized"/> scores all three
/// a flat 1 because each one contains, or is contained by, the query. The
/// picker took a single candidate ordered by provider id, so "pelicula/..."
/// beat "serie/..." and every episode lookup then failed against a film that
/// has no episodes — the title was on the site, and the app still offered no
/// provider.
/// </para>
/// </summary>
public sealed class ApiTitleCandidateRankingTests
{
    private const string Query = "All American";

    // Real provider titles and route ids, as returned by the live search.
    private const string ExactSeries = "All American";
    private const string SpinOffSeries = "All American: Homecoming";
    private const string SameNamedFilm = "My All American";

    [Theory]
    [InlineData(SpinOffSeries)]
    [InlineData(SameNamedFilm)]
    public void ContainmentTiesEveryVariantOnSimilarity(string providerTitle)
    {
        // Documents why the length gap is needed: similarity alone cannot
        // separate these, so a tie-break that ignores it is arbitrary.
        Assert.Equal(
            1d,
            LevenshteinTitleMatcher.Similarity(Query, providerTitle),
            precision: 6);
        Assert.Equal(
            1d,
            LevenshteinTitleMatcher.Similarity(Query, ExactSeries),
            precision: 6);
    }

    [Fact]
    public void TheExactTitleHasNoLengthGap()
    {
        Assert.Equal(
            0,
            LevenshteinTitleMatcher.NormalizedLengthGap(Query, ExactSeries));
    }

    [Theory]
    [InlineData(SpinOffSeries)]
    [InlineData(SameNamedFilm)]
    public void EveryOtherTitleRanksBehindTheExactOne(string providerTitle)
    {
        var exact =
            LevenshteinTitleMatcher.NormalizedLengthGap(Query, ExactSeries);
        var other =
            LevenshteinTitleMatcher.NormalizedLengthGap(Query, providerTitle);

        Assert.True(
            other > exact,
            $"'{providerTitle}' must rank behind '{ExactSeries}' " +
            $"(gap {other} should exceed {exact}).");
    }

    /// <summary>
    /// The end-to-end ordering, mirroring the picker: similarity first, then
    /// the length gap, then the provider id only to stay deterministic. The
    /// route ids are the live ones, and "pelicula/..." sorts before
    /// "serie/..." ordinally, which is exactly what used to win.
    /// </summary>
    [Fact]
    public void TheRequestedSeriesWinsOverAFilmThatSortsFirstByRoute()
    {
        (string Route, string Title)[] results =
        [
            ("pelicula/my-all-american-zorl", SameNamedFilm),
            ("serie/all-american-homecoming-zxiy", SpinOffSeries),
            ("serie/all-american-zabu", ExactSeries)
        ];

        var winner = results
            .OrderByDescending(result =>
                LevenshteinTitleMatcher.Similarity(Query, result.Title))
            .ThenBy(result =>
                LevenshteinTitleMatcher.NormalizedLengthGap(Query, result.Title))
            .ThenBy(result => result.Route, StringComparer.Ordinal)
            .First();

        Assert.Equal("serie/all-american-zabu", winner.Route);
    }

    /// <summary>
    /// Ordering by route alone is what shipped, so assert it really does pick
    /// the film. If this ever stops holding the regression above is no longer
    /// being exercised and the fixture needs refreshing.
    /// </summary>
    [Fact]
    public void OrderingByRouteAloneStillPicksTheFilm()
    {
        (string Route, string Title)[] results =
        [
            ("pelicula/my-all-american-zorl", SameNamedFilm),
            ("serie/all-american-homecoming-zxiy", SpinOffSeries),
            ("serie/all-american-zabu", ExactSeries)
        ];

        var winner = results
            .OrderByDescending(result =>
                LevenshteinTitleMatcher.Similarity(Query, result.Title))
            .ThenBy(result => result.Route, StringComparer.Ordinal)
            .First();

        Assert.Equal("pelicula/my-all-american-zorl", winner.Route);
    }

    /// <summary>
    /// SeriesFlix returned only the two series, where the spin-off sorts first
    /// by route. Picking it would have silently served a different show whose
    /// episode numbers happen to line up, which is worse than offering
    /// nothing.
    /// </summary>
    [Fact]
    public void TheSpinOffDoesNotWinWhenOnlySeriesRoutesAreReturned()
    {
        (string Route, string Title)[] results =
        [
            ("serie/all-american-homecoming-zxiy/", SpinOffSeries),
            ("serie/all-american-zabu/", ExactSeries)
        ];

        var winner = results
            .OrderByDescending(result =>
                LevenshteinTitleMatcher.Similarity(Query, result.Title))
            .ThenBy(result =>
                LevenshteinTitleMatcher.NormalizedLengthGap(Query, result.Title))
            .ThenBy(result => result.Route, StringComparer.Ordinal)
            .First();

        Assert.Equal("serie/all-american-zabu/", winner.Route);
    }

    /// <summary>
    /// A genuinely unrelated title still has to be rejected outright; the new
    /// tie-break must not promote anything past the similarity floor.
    /// </summary>
    [Fact]
    public void AnUnrelatedTitleStaysBelowTheSimilarityFloor()
    {
        Assert.True(
            LevenshteinTitleMatcher.Similarity(Query, "All Out!!") <
            LevenshteinTitleMatcher.DefaultMinimumSimilarity);
    }
}

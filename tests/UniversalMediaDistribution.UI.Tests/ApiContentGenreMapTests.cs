using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.Tests;

public sealed class ApiContentGenreMapTests
{
    [Fact]
    public void AnExplicitMapReturnsItsTokens()
    {
        var map = ApiContentGenreMap.Create(
            (ApiContentGenre.Action, "1"),
            (ApiContentGenre.ScienceFiction, "sci-fi"));

        Assert.True(map.TryGetToken(ApiContentGenre.Action, out var action));
        Assert.Equal("1", action);
        Assert.True(map.TryGetToken(
            ApiContentGenre.ScienceFiction,
            out var scienceFiction));
        Assert.Equal("sci-fi", scienceFiction);
        Assert.Equal(2, map.Count);
    }

    [Fact]
    public void AnUnmappedGenreIsNotGuessedAt()
    {
        // Deriving "isekai" from the key would be a plausible slug and a wrong
        // ID. Declining is what makes the provider skip rather than broaden.
        var map = ApiContentGenreMap.Create((ApiContentGenre.Action, "1"));

        Assert.False(map.TryGetToken(ApiContentGenre.Isekai, out var token));
        Assert.Null(token);
        Assert.False(map.Contains(ApiContentGenre.Isekai));
    }

    [Fact]
    public void ANullGenreResolvesToNothing() =>
        Assert.False(ApiContentGenreMap.Empty.TryGetToken(null, out _));

    [Fact]
    public void DuplicateAndBlankEntriesAreRejected()
    {
        Assert.Throws<ArgumentException>(() => ApiContentGenreMap.Create(
            (ApiContentGenre.Action, "1"),
            (ApiContentGenre.Action, "2")));
        Assert.Throws<ArgumentException>(() => ApiContentGenreMap.Create(
            (ApiContentGenre.Action, "   ")));
    }

    [Fact]
    public void UpstreamLabelsResolveThroughTheSharedAliases()
    {
        var map = ApiContentGenreMap.FromUpstreamLabels(
        [
            ("Acción", "1"),
            ("Ciencia Ficción", "7"),
            ("Cosas de la vida", "22"),
            ("Terror", "9")
        ]);

        Assert.True(map.TryGetToken(ApiContentGenre.Action, out var action));
        Assert.Equal("1", action);
        Assert.True(map.TryGetToken(
            ApiContentGenre.ScienceFiction,
            out var scienceFiction));
        Assert.Equal("7", scienceFiction);
        Assert.True(map.TryGetToken(
            ApiContentGenre.SliceOfLife,
            out var sliceOfLife));
        Assert.Equal("22", sliceOfLife);
        Assert.True(map.TryGetToken(ApiContentGenre.Horror, out var horror));
        Assert.Equal("9", horror);
    }

    [Fact]
    public void UpstreamLabelsThatMatchNothingAreSkipped()
    {
        var map = ApiContentGenreMap.FromUpstreamLabels(
        [
            ("Acción", "1"),
            ("Doramas coreanos", "40"),
            ("", "41"),
            ("Sin género", "42")
        ]);

        Assert.Equal(1, map.Count);
        Assert.True(map.Contains(ApiContentGenre.Action));
    }

    [Fact]
    public void TheFirstLabelForAGenreWins()
    {
        // Providers routinely list near-duplicate shelves; the earlier entry is
        // the canonical one.
        var map = ApiContentGenreMap.FromUpstreamLabels(
        [
            ("Accion", "1"),
            ("Acción", "99")
        ]);

        Assert.True(map.TryGetToken(ApiContentGenre.Action, out var token));
        Assert.Equal("1", token);
    }

    [Fact]
    public void ABlankTokenIsNotResolved() =>
        Assert.Equal(
            0,
            ApiContentGenreMap.FromUpstreamLabels([("Acción", "  ")]).Count);
}

namespace UniversalMediaDistribution.Domain.Catalog;

/// <summary>
/// Splits a media type into the shelves a reader actually browses. Anime and
/// live action share <see cref="MediaType.Movie"/> but rarely share a provider,
/// so the two are kept apart instead of mixed into one list.
/// </summary>
public enum MediaSubtypeGroup
{
    /// <summary>Anime movies, series and OVAs.</summary>
    Anime,

    /// <summary>Everything else filed under movies: films, series, documentaries, shorts.</summary>
    LiveAction
}

public static class MediaSubtypeGroups
{
    private static readonly string[] AnimeSubtypeCodes =
    [
        "movie.anime_movie",
        "movie.anime_series"
    ];

    /// <summary>
    /// OVAs are filed as anime series, so no separate subtype is needed here.
    /// </summary>
    public static bool IsAnime(string? subtypeCode) =>
        subtypeCode is not null &&
        AnimeSubtypeCodes.Contains(subtypeCode, StringComparer.OrdinalIgnoreCase);

    public static MediaSubtypeGroup Resolve(string? subtypeCode) =>
        IsAnime(subtypeCode)
            ? MediaSubtypeGroup.Anime
            : MediaSubtypeGroup.LiveAction;

    /// <summary>
    /// A null group means the caller is not splitting the shelf, so everything
    /// matches. Groups only apply to movies; other media types are unaffected.
    /// </summary>
    public static bool Matches(
        MediaSubtypeGroup? group,
        MediaType mediaType,
        string? subtypeCode) =>
        group is null ||
        mediaType is not MediaType.Movie ||
        Resolve(subtypeCode) == group;
}

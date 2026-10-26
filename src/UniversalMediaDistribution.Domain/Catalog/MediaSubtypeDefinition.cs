using System.Collections.ObjectModel;

namespace UniversalMediaDistribution.Domain.Catalog;

public sealed record MediaSubtypeDefinition(
    string Code,
    MediaType MediaType,
    string DisplayName,
    HierarchyMode HierarchyMode,
    ContentUnitKind? DefaultUnitKind)
{
    private static readonly IReadOnlyDictionary<string, MediaSubtypeDefinition> Definitions =
        new ReadOnlyDictionary<string, MediaSubtypeDefinition>(
            new Dictionary<string, MediaSubtypeDefinition>(StringComparer.OrdinalIgnoreCase)
            {
                ["movie.movie"] = new("movie.movie", MediaType.Movie, "Movie", HierarchyMode.Single, null),
                ["movie.series"] = new("movie.series", MediaType.Movie, "Series", HierarchyMode.Seasons, ContentUnitKind.Episode),
                ["movie.anime_movie"] = new("movie.anime_movie", MediaType.Movie, "Anime Movie", HierarchyMode.Single, null),
                ["movie.anime_series"] = new("movie.anime_series", MediaType.Movie, "Anime Series", HierarchyMode.Seasons, ContentUnitKind.Episode),
                ["movie.documentary"] = new("movie.documentary", MediaType.Movie, "Documentary", HierarchyMode.Single, null),
                ["movie.short"] = new("movie.short", MediaType.Movie, "Short", HierarchyMode.Single, null),
                ["book.book"] = new("book.book", MediaType.Book, "Book", HierarchyMode.Single, null),
                ["book.novel"] = new("book.novel", MediaType.Book, "Novel", HierarchyMode.Single, null),
                ["book.manga"] = new("book.manga", MediaType.Book, "Manga", HierarchyMode.Chapters, ContentUnitKind.Chapter),
                ["book.comic"] = new("book.comic", MediaType.Book, "Comic", HierarchyMode.Chapters, ContentUnitKind.Chapter),
                ["book.light_novel"] = new("book.light_novel", MediaType.Book, "Light Novel", HierarchyMode.Chapters, ContentUnitKind.Chapter),
                ["book.web_novel"] = new("book.web_novel", MediaType.Book, "Web Novel", HierarchyMode.Chapters, ContentUnitKind.Chapter),
                ["audio.album"] = new("audio.album", MediaType.Audio, "Music Album", HierarchyMode.Tracks, ContentUnitKind.Track),
                ["audio.single"] = new("audio.single", MediaType.Audio, "Single", HierarchyMode.Single, null),
                ["audio.audiobook"] = new("audio.audiobook", MediaType.Audio, "Audiobook", HierarchyMode.Chapters, ContentUnitKind.AudioChapter),
                ["audio.podcast"] = new("audio.podcast", MediaType.Audio, "Podcast", HierarchyMode.Seasons, ContentUnitKind.Episode),
                ["audio.radio_drama"] = new("audio.radio_drama", MediaType.Audio, "Radio Drama", HierarchyMode.Chapters, ContentUnitKind.AudioChapter)
            });

    public static IReadOnlyCollection<MediaSubtypeDefinition> All => Definitions.Values.ToArray();

    public static MediaSubtypeDefinition GetRequired(string code)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(code);

        return Definitions.TryGetValue(code, out var definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(code), code, "Unknown media subtype.");
    }

    public static bool IsCompatible(MediaType mediaType, string code) =>
        Definitions.TryGetValue(code, out var definition) && definition.MediaType == mediaType;
}

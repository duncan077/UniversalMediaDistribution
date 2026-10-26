using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Application.UserState;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.ViewModels;

internal enum CatalogHomeSectionKind
{
    LastSeen,
    MoviesAndSeries,
    Anime,
    BooksAndManga,
    Audio
}

/// <param name="Cards">The cards to show, already trimmed to the shelf limit.</param>
/// <param name="TotalCount">How many the shelf holds in full, trimming aside.</param>
internal sealed record CatalogHomeSection(
    CatalogHomeSectionKind Kind,
    IReadOnlyList<CatalogCard> Cards,
    int TotalCount)
{
    public bool HasMore => TotalCount > Cards.Count;
}

/// <summary>
/// Splits the home page into shelves so movies, anime, books and audio never
/// share one undivided grid. The incoming order is kept, because the caller has
/// already applied the user's filters and ordering; only "Last seen" reorders,
/// since recency is the whole point of that shelf.
/// </summary>
internal static class CatalogHomeSectionPlanner
{
    /// <summary>
    /// Keeps the shelf short enough to stay a shortcut rather than a second
    /// copy of the library. Titles beyond it still appear in their own shelf.
    /// </summary>
    internal const int LastSeenLimit = 12;

    /// <summary>
    /// Home previews each shelf; the full list lives on the shelf's own page.
    /// Without this, everything below the first shelf is a long scroll away.
    /// </summary>
    internal const int ShelfLimit = 12;

    internal static IReadOnlyList<CatalogHomeSection> Plan(
        IEnumerable<CatalogCard> orderedCards,
        IReadOnlyDictionary<Guid, CatalogProgressSummary> progressByItemId,
        int shelfLimit = ShelfLimit)
    {
        ArgumentNullException.ThrowIfNull(orderedCards);
        ArgumentNullException.ThrowIfNull(progressByItemId);
        ArgumentOutOfRangeException.ThrowIfLessThan(shelfLimit, 1);

        var cards = orderedCards.ToArray();
        var sections = new List<CatalogHomeSection>();

        var lastSeen = cards
            .Select(card => new
            {
                Card = card,
                Progress = progressByItemId.GetValueOrDefault(card.Id)
            })
            .Where(static value => value.Progress is not null)
            .OrderByDescending(static value => value.Progress!.Progress.UpdatedAt)
            .Take(LastSeenLimit)
            .Select(static value => value.Card)
            .ToArray();
        Add(CatalogHomeSectionKind.LastSeen, lastSeen);

        Add(
            CatalogHomeSectionKind.MoviesAndSeries,
            cards
                .Where(static card =>
                    card.MediaType is MediaType.Movie &&
                    !MediaSubtypeGroups.IsAnime(card.SubtypeCode))
                .ToArray());
        Add(
            CatalogHomeSectionKind.Anime,
            cards
                .Where(static card =>
                    card.MediaType is MediaType.Movie &&
                    MediaSubtypeGroups.IsAnime(card.SubtypeCode))
                .ToArray());
        Add(
            CatalogHomeSectionKind.BooksAndManga,
            cards
                .Where(static card => card.MediaType is MediaType.Book)
                .ToArray());
        Add(
            CatalogHomeSectionKind.Audio,
            cards
                .Where(static card => card.MediaType is MediaType.Audio)
                .ToArray());

        return sections;

        void Add(CatalogHomeSectionKind kind, IReadOnlyList<CatalogCard> shelf)
        {
            if (shelf.Count == 0)
            {
                return;
            }

            var shown = shelf.Take(shelfLimit).ToArray();
            sections.Add(new CatalogHomeSection(kind, shown, shelf.Count));
        }
    }

    internal static string GetTitleKey(CatalogHomeSectionKind kind) =>
        kind switch
        {
            CatalogHomeSectionKind.LastSeen => "HomeLastSeen",
            CatalogHomeSectionKind.MoviesAndSeries => "NavMovies",
            CatalogHomeSectionKind.Anime => "NavAnime",
            CatalogHomeSectionKind.BooksAndManga => "NavBooks",
            CatalogHomeSectionKind.Audio => "MediaTypeAudio",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

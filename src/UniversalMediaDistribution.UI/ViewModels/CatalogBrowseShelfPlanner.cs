using System.Collections.Concurrent;
using UniversalMediaDistribution.Application.Catalog;
using UniversalMediaDistribution.Catalog.ContentSources;

namespace UniversalMediaDistribution.UI.ViewModels;

internal enum CatalogBrowseShelfKind
{
    RecentlyAdded,
    RecentlyUpdated,
    Genre
}

/// <summary>
/// Identifies a shelf across refreshes so its view model — and with it the
/// cards already decoded into it — survives a rebuild.
/// </summary>
internal readonly record struct CatalogBrowseShelfKey(
    CatalogBrowseShelfKind Kind,
    string GenreKey)
{
    public static CatalogBrowseShelfKey For(CatalogBrowseShelfKind kind) =>
        new(kind, string.Empty);

    public static CatalogBrowseShelfKey For(ApiContentGenre genre) =>
        new(CatalogBrowseShelfKind.Genre, genre.Key);
}

/// <param name="Cards">The cards to show, already trimmed to the shelf limit.</param>
/// <param name="TotalCount">
/// How many the shelf stands for. For a genre this is the full local match
/// count, because the shelf is a preview of a larger set the viewer can open.
/// For the two recency shelves it equals what is shown: "recently added" is a
/// window onto the front of the library, not a subset with a rest to see.
/// </param>
internal sealed record CatalogBrowseShelf(
    CatalogBrowseShelfKey Key,
    ApiContentGenre? Genre,
    IReadOnlyList<CatalogCard> Cards,
    int TotalCount);

/// <summary>
/// Lays a media-type page out as rows rather than one long grid: what arrived
/// recently, what changed recently, then the genres the library actually holds
/// enough of to fill a row.
/// <para>
/// The incoming order is the caller's — filters and sorting have already been
/// applied — so only the two recency shelves reorder, and only by the date each
/// one is named after.
/// </para>
/// </summary>
internal static class CatalogBrowseShelfPlanner
{
    /// <summary>
    /// A row is a preview, not a second copy of the library. Genre rows hand
    /// the rest over to the genre search instead of growing.
    /// </summary>
    internal const int ShelfLimit = 12;

    /// <summary>
    /// Below this a row reads as a gap rather than a shelf, so the genre is
    /// left to the filter picker instead of taking a heading of its own.
    /// </summary>
    internal const int MinimumShelfSize = 3;

    /// <summary>
    /// Thirty-five genres would bury the page. The busiest ones win, which is
    /// also what makes the page look different for a manga library than for a
    /// film one.
    /// </summary>
    internal const int MaximumGenreShelves = 12;

    internal static IReadOnlyList<CatalogBrowseShelf> Plan(
        IEnumerable<CatalogCard> orderedCards,
        int shelfLimit = ShelfLimit,
        int maximumGenreShelves = MaximumGenreShelves)
    {
        ArgumentNullException.ThrowIfNull(orderedCards);
        ArgumentOutOfRangeException.ThrowIfLessThan(shelfLimit, 1);
        ArgumentOutOfRangeException.ThrowIfNegative(maximumGenreShelves);

        var cards = orderedCards.ToArray();
        if (cards.Length == 0)
        {
            return [];
        }

        var shelves = new List<CatalogBrowseShelf>();

        // OrderByDescending is stable, so ties fall back to the caller's order
        // rather than to an arbitrary one.
        AddRecency(
            CatalogBrowseShelfKind.RecentlyAdded,
            cards.OrderByDescending(static card =>
                card.CreatedAt ?? DateTimeOffset.MinValue));
        AddRecency(
            CatalogBrowseShelfKind.RecentlyUpdated,
            cards
                .Where(static card => HasBeenUpdatedSinceAdded(card))
                .OrderByDescending(static card =>
                    card.UpdatedAt ?? DateTimeOffset.MinValue));

        if (maximumGenreShelves > 0)
        {
            foreach (var shelf in PlanGenres(
                         cards,
                         shelfLimit,
                         maximumGenreShelves))
            {
                shelves.Add(shelf);
            }
        }

        return shelves;

        void AddRecency(
            CatalogBrowseShelfKind kind,
            IEnumerable<CatalogCard> ordered)
        {
            var shown = ordered.Take(shelfLimit).ToArray();
            if (shown.Length < MinimumShelfSize)
            {
                return;
            }

            shelves.Add(new CatalogBrowseShelf(
                CatalogBrowseShelfKey.For(kind),
                Genre: null,
                shown,
                shown.Length));
        }
    }

    /// <summary>
    /// Whether the item has changed since it entered the catalog — a new manga
    /// chapter or a new episode — rather than merely having been imported.
    /// </summary>
    internal static bool HasBeenUpdatedSinceAdded(CatalogCard card)
    {
        ArgumentNullException.ThrowIfNull(card);

        // A strict comparison, with no tolerance window. The import that first
        // stores an item stamps both columns from one timestamp, and a later
        // import moves updated_at only when what it carries actually differs,
        // so the two are equal until something really changes. The one-minute
        // window this used to allow only suppressed genuine changes that
        // happened to land in the first minute of a title's life.
        return card is { UpdatedAt: { } updated, CreatedAt: { } created } &&
            updated > created;
    }

    private static IEnumerable<CatalogBrowseShelf> PlanGenres(
        IReadOnlyList<CatalogCard> cards,
        int shelfLimit,
        int maximumGenreShelves)
    {
        // One pass over the cards rather than one per genre: a library of a few
        // thousand titles against thirty-five genres is otherwise a hundred
        // thousand label comparisons on every refresh.
        var matches = new Dictionary<ApiContentGenre, List<CatalogCard>>();
        foreach (var card in cards)
        {
            foreach (var genre in ResolveGenres(card))
            {
                if (!matches.TryGetValue(genre, out var members))
                {
                    members = [];
                    matches[genre] = members;
                }

                members.Add(card);
            }
        }

        var displayOrder = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var index = 0; index < ApiContentGenre.All.Count; index++)
        {
            displayOrder[ApiContentGenre.All[index].Key] = index;
        }

        return matches
            .Where(static entry => entry.Value.Count >= MinimumShelfSize)
            .OrderByDescending(static entry => entry.Value.Count)
            .ThenBy(entry =>
                displayOrder.GetValueOrDefault(entry.Key.Key, int.MaxValue))
            .ThenBy(static entry => entry.Key.Key, StringComparer.Ordinal)
            .Take(maximumGenreShelves)
            .Select(entry => new CatalogBrowseShelf(
                CatalogBrowseShelfKey.For(entry.Key),
                entry.Key,
                entry.Value.Take(shelfLimit).ToArray(),
                entry.Value.Count))
            .ToArray();
    }

    /// <summary>
    /// The shared genres an item's free-text labels mean.
    /// <para>
    /// Providers publish compound labels — "Acción y Aventura", "Sci-Fi /
    /// Fantasy" — that mean two genres each. <see cref="ApiContentGenre"/>
    /// deliberately refuses to split them, because as a <em>search</em> token a
    /// compound label is its own thing. Filing a card on a shelf is the other
    /// question, so the parts are matched here as well as the whole. The whole
    /// label is tried first, which keeps a real multi-word alias such as
    /// "Cosas de la vida" from being torn apart.
    /// </para>
    /// </summary>
    internal static IReadOnlyList<ApiContentGenre> ResolveGenres(
        CatalogCard card)
    {
        ArgumentNullException.ThrowIfNull(card);
        List<ApiContentGenre>? resolved = null;
        foreach (var label in card.Genres)
        {
            foreach (var candidate in SplitCompoundLabel(label))
            {
                if (MatchGenre(candidate) is not { } genre)
                {
                    continue;
                }

                resolved ??= [];
                if (!resolved.Contains(genre))
                {
                    resolved.Add(genre);
                }
            }
        }

        return resolved ?? (IReadOnlyList<ApiContentGenre>)[];
    }

    /// <summary>
    /// The genre a single candidate label means, remembered between calls.
    /// <para>
    /// Matching is a linear scan of every shared genre, and each comparison
    /// re-folds the candidate's separators and accents, so the same label was
    /// rebuilt and compared dozens of times per card and again on every
    /// refresh. Providers draw from a small fixed vocabulary, so the answer is
    /// worth keeping: a library-wide plan collapses to one scan per distinct
    /// label instead of one per card.
    /// </para>
    /// </summary>
    private static ApiContentGenre? MatchGenre(string candidate)
    {
        if (MatchedGenres.TryGetValue(candidate, out var cached))
        {
            return cached;
        }

        ApiContentGenre? match = null;
        foreach (var genre in ApiContentGenre.All)
        {
            if (genre.MatchesLabel(candidate))
            {
                match = genre;
                break;
            }
        }

        // Bounded against a provider that emits unique labels — a per-title
        // "genre" would otherwise grow this for the life of the process.
        if (MatchedGenres.Count < MaximumRememberedLabels)
        {
            MatchedGenres[candidate] = match;
        }

        return match;
    }

    private const int MaximumRememberedLabels = 4096;

    private static readonly ConcurrentDictionary<string, ApiContentGenre?>
        MatchedGenres = new(StringComparer.Ordinal);

    private static readonly char[] LabelSeparators = [',', '/', '|', ';'];

    /// <summary>
    /// The conjunctions providers join two genres with. None of them appears
    /// inside a genre alias, so splitting on one can only ever separate a
    /// compound label.
    /// </summary>
    private static readonly string[] LabelConjunctions =
        [" y ", " & ", " and ", " + "];

    private static IEnumerable<string> SplitCompoundLabel(string label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return [];
        }

        // The whole label leads, so a genuine multi-word alias such as
        // "Cosas de la vida" is matched before anything takes it apart.
        var candidates = new List<string> { label };
        candidates.AddRange(label.Split(
            LabelSeparators,
            StringSplitOptions.RemoveEmptyEntries |
            StringSplitOptions.TrimEntries));

        // Every conjunction, repeatedly: "Acción, Comedia y Romance" is three
        // genres, and stopping at the first one would drop the last.
        foreach (var conjunction in LabelConjunctions)
        {
            for (var index = 1; index < candidates.Count; index++)
            {
                var at = candidates[index].IndexOf(
                    conjunction,
                    StringComparison.OrdinalIgnoreCase);
                if (at < 0)
                {
                    continue;
                }

                var head = candidates[index][..at].Trim();
                var tail = candidates[index]
                    [(at + conjunction.Length)..]
                    .Trim();
                candidates[index] = head;
                candidates.Insert(index + 1, tail);
            }
        }

        // Hyphens last, and only as extra candidates on top of what is already
        // there. ApiContentGenre folds a hyphen into a word break, so
        // "Drama-Romance" collapses to "drama romance" and matches nothing —
        // but splitting on it up front would tear "Sci-Fi / Fantasy" into
        // "Sci"/"Fi"/"Fantasy" and lose science fiction, because that whole
        // label matches no alias either. Appending keeps the hyphenated part
        // intact for the aliases that need it.
        for (var index = candidates.Count - 1; index >= 0; index--)
        {
            var candidate = candidates[index];
            if (candidate.IndexOf('-') < 0)
            {
                continue;
            }

            foreach (var piece in candidate.Split(
                         '-',
                         StringSplitOptions.RemoveEmptyEntries |
                         StringSplitOptions.TrimEntries))
            {
                candidates.Add(piece);
            }
        }

        return candidates;
    }

    internal static string GetTitleKey(CatalogBrowseShelfKind kind) =>
        kind switch
        {
            CatalogBrowseShelfKind.RecentlyAdded => "ShelfRecentlyAdded",
            CatalogBrowseShelfKind.RecentlyUpdated => "ShelfRecentlyUpdated",
            _ => throw new ArgumentOutOfRangeException(nameof(kind))
        };
}

using System.Text;
using UniversalMediaDistribution.Catalog.ContentSources;
using UniversalMediaDistribution.Domain.Catalog;

namespace UniversalMediaDistribution.UI.ViewModels;

/// <summary>
/// One title, however many places it was found. The local library and every
/// provider that matched collapse into a single entry, because a search for a
/// film the user already has should not answer with the same film five times.
/// </summary>
internal sealed record SearchResultGroup(
    ContentSourceCatalogSearchResult Primary,
    IReadOnlyList<ContentSourceCatalogSearchResult> Alternates)
{
    public IReadOnlyList<ContentSourceCatalogSearchResult> Members =>
        [Primary, .. Alternates];

    /// <summary>
    /// Distinct providers behind this entry, counting the local library as one.
    /// </summary>
    public int SourceCount => 1 + Alternates.Count;

    public bool HasAlternates => Alternates.Count > 0;

    /// <summary>
    /// The online members that still need loading once the primary is open.
    /// </summary>
    public IEnumerable<ContentSourceCatalogSearchResult> OnlineAlternates =>
        Alternates.Where(static member => member.IsOnline);

    /// <summary>
    /// Stable across a refresh so a card view model can be reused while the
    /// group keeps the same members, and rebuilt when a provider joins it.
    /// </summary>
    public string Identity =>
        string.Join(
            '\u001f',
            Members
                .Select(static member => member.IsOnline
                    ? $"{member.Provider?.Id}:{member.ExternalId}"
                    : $"local:{member.Card.Id:N}")
                .Order(StringComparer.Ordinal));
}

internal static class SearchResultGrouping
{
    /// <summary>
    /// Stricter than the matcher's own default, which is tuned for binding a
    /// provider's title to a catalogued one where a near miss is recoverable.
    /// Here a wrong match silently hides a distinct title behind another, so
    /// the bar to collapse two search hits is higher. Deliberately local: the
    /// import and refresh paths keep the shared default.
    /// </summary>
    private const double MinimumTitleSimilarity = 0.80;

    /// <summary>
    /// Groups results whose titles match closely enough to be the same work.
    ///
    /// Providers spell the same title differently — punctuation, articles,
    /// a year in brackets, a transliteration — so exact keys leave duplicates
    /// on screen. Matching is confined to a single media type, and an exact
    /// normalized match short-circuits ahead of the edit distance so the
    /// common case never pays for it.
    /// </summary>
    public static IReadOnlyList<SearchResultGroup> Group(
        IReadOnlyList<ContentSourceCatalogSearchResult> results,
        double minimumSimilarity = MinimumTitleSimilarity)
    {
        ArgumentNullException.ThrowIfNull(results);
        var groups = new List<Bucket>();
        foreach (var result in results)
        {
            var name = result.Card.Name;
            if (string.IsNullOrWhiteSpace(name))
            {
                groups.Add(new Bucket(result));
                continue;
            }

            var key = NameNormalizer.Normalize(name);
            var bucket = groups.FirstOrDefault(candidate =>
                candidate.MediaType == result.Card.MediaType &&
                (result.Card.MediaType != MediaType.Book || candidate.SubtypeCode == result.Card.SubtypeCode) &&
                candidate.Matches(name, key, minimumSimilarity));
            if (bucket is null)
            {
                groups.Add(new Bucket(result, key));
                continue;
            }

            bucket.Add(result);
        }

        return groups.Select(static bucket => bucket.ToGroup()).ToArray();
    }

    /// <summary>
    /// Whether two titles name the same work, by the rule search grouping
    /// uses. Exposed so the behaviour can be pinned down directly: the cases
    /// that matter are sequels and spelling variants, not plumbing.
    /// </summary>
    internal static bool IsSameTitle(
        string left,
        string right,
        double minimumSimilarity = MinimumTitleSimilarity) =>
        IsSameNormalizedTitle(
            NormalizeTitle(left ?? string.Empty),
            NormalizeTitle(right ?? string.Empty),
            minimumSimilarity);

    /// <summary>
    /// A ratio alone is too generous on short titles: one letter between
    /// "ALIEN" and "ALIENS" scores 0.833 and clears any sensible threshold,
    /// yet they are different films and DigitsMatch cannot tell, because
    /// neither carries a digit. Requiring the absolute edit distance to be
    /// small as well keeps the ratio for long titles, where a single
    /// transliteration difference should still collapse, and refuses the
    /// plural-of-a-short-name case outright.
    /// </summary>
    private const int MaximumTitleEdits = 2;

    private static bool IsSameNormalizedTitle(
        string left,
        string right,
        double minimumSimilarity) =>
        left.Length > 0 &&
        right.Length > 0 &&
        DigitsMatch(left, right) &&
        Similarity(left, right) >= minimumSimilarity &&
        (string.Equals(left, right, StringComparison.Ordinal) ||
         Math.Abs(left.Length - right.Length) <= MaximumTitleEdits) &&
        !IsShortPluralOf(left, right);

    /// <summary>
    /// Whether one title is the other with a trailing "S" — the shape that
    /// distinguishes a film from its sequel far more often than it marks a
    /// spelling variant.
    /// </summary>
    private static bool IsShortPluralOf(string left, string right)
    {
        var (shorter, longer) = left.Length <= right.Length
            ? (left, right)
            : (right, left);
        return longer.Length == shorter.Length + 1 &&
            longer.EndsWith('S') &&
            longer.AsSpan(0, shorter.Length).SequenceEqual(shorter);
    }

    /// <summary>
    /// Letters and digits only, so spacing and punctuation stop mattering.
    /// The same shape the title matcher compares on.
    /// </summary>
    private static string NormalizeTitle(string value)
    {
        // NameNormalizer refuses a blank name, and a provider can answer with
        // one. An untitled result groups with nothing rather than throwing.
        if (string.IsNullOrWhiteSpace(value))
        {
            return string.Empty;
        }

        var normalized = NameNormalizer.Normalize(value);
        var result = new StringBuilder(normalized.Length);
        foreach (var character in normalized)
        {
            if (char.IsLetterOrDigit(character))
            {
                result.Append(character);
            }
        }

        return result.ToString();
    }

    /// <summary>
    /// Edit-distance similarity <em>without</em> the containment shortcut in
    /// <see cref="LevenshteinTitleMatcher.Similarity"/>, which answers 1
    /// whenever one title contains the other. That is right when binding a
    /// provider's title to an already catalogued one, where a near miss just
    /// retries — but wrong here, because "Dune" is contained in "Dune Part
    /// Two" and collapsing those hides one film behind another.
    /// </summary>
    private static double Similarity(string left, string right)
    {
        if (left.Length == 0 || right.Length == 0)
        {
            return 0;
        }

        if (string.Equals(left, right, StringComparison.Ordinal))
        {
            return 1;
        }

        return 1d -
            (double)LevenshteinTitleMatcher.Distance(left, right) /
            Math.Max(left.Length, right.Length);
    }

    /// <summary>
    /// True when both titles carry the same digits.
    ///
    /// A sequel differs from its original by a number and nothing else, and on
    /// a long title one character barely moves the edit distance — "El diablo
    /// viste a la moda" and "El diablo viste a la moda 2" score above any
    /// usable threshold. Years behave the same way, so a title carrying one
    /// stays on its own card: showing the same film twice is a far smaller
    /// cost than hiding a sequel behind the original.
    /// </summary>
    private static bool DigitsMatch(string left, string right)
    {
        var leftDigits = string.Concat(left.Where(char.IsDigit));
        var rightDigits = string.Concat(right.Where(char.IsDigit));
        return string.Equals(leftDigits, rightDigits, StringComparison.Ordinal);
    }

    private sealed class Bucket
    {
        private readonly List<ContentSourceCatalogSearchResult> _members;
        private readonly string _key;
        private readonly string _normalized;

        public Bucket(
            ContentSourceCatalogSearchResult first,
            string? key = null)
        {
            _members = [first];
            _normalized = NormalizeTitle(first.Card.Name ?? string.Empty);
            _key = key ?? string.Empty;
            MediaType = first.Card.MediaType;
            SubtypeCode = first.Card.SubtypeCode;
        }

        public MediaType MediaType { get; }
        public string SubtypeCode { get; }

        public bool Matches(string name, string key, double minimumSimilarity)
        {
            if (_key.Length > 0 &&
                string.Equals(_key, key, StringComparison.Ordinal))
            {
                return true;
            }

            if (_normalized.Length == 0)
            {
                return false;
            }

            return IsSameNormalizedTitle(
                _normalized,
                NormalizeTitle(name),
                minimumSimilarity);
        }

        public void Add(ContentSourceCatalogSearchResult result)
        {
            // The same provider can answer twice for one title; keep the first
            // it offered rather than listing it as two sources.
            if (result.IsOnline &&
                _members.Any(member =>
                    member.IsOnline &&
                    string.Equals(
                        member.Provider?.Id,
                        result.Provider?.Id,
                        StringComparison.Ordinal)))
            {
                return;
            }

            _members.Add(result);
        }

        public SearchResultGroup ToGroup()
        {
            // The local library wins the card: it already carries the user's
            // progress, favourite state and artwork. Otherwise the earliest
            // provider that answered leads, which is the order they streamed in.
            var primaryIndex = _members.FindIndex(static member =>
                !member.IsOnline);
            if (primaryIndex < 0)
            {
                primaryIndex = 0;
            }

            var primary = _members[primaryIndex];
            return new SearchResultGroup(
                primary,
                _members
                    .Where((_, index) => index != primaryIndex)
                    .ToArray());
        }
    }
}

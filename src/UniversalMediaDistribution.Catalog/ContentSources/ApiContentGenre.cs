using System.Collections.Frozen;
using System.Globalization;
using System.Text;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Stable, provider-independent genre key.
/// <para>
/// Upstream taxonomies drift, are localized, and disagree with each other, so a
/// key is deliberately not a C# enum: providers own an explicit map from this
/// key to their own token (a slug, a numeric ID, or a resolved remote ID) rather
/// than deriving one by slugifying a display name. Several real mappings are not
/// derivable that way — JKAnime spells slice-of-life <c>cosas-de-la-vida</c>.
/// </para>
/// <para>
/// Keys are lowercase ASCII words joined by single hyphens, which keeps them
/// safe in settings files, query strings, and test data without escaping.
/// </para>
/// </summary>
public sealed record ApiContentGenre
{
    private const int MinimumKeyLength = 2;
    private const int MaximumKeyLength = 40;

    private readonly string[] _matchTargets;

    private ApiContentGenre(string key, params string[] aliases)
    {
        Key = key;
        Aliases = aliases;

        // The key read as words is always matchable, so "slice-of-life" finds a
        // label of "Slice of Life" without anyone listing it as an alias.
        _matchTargets = aliases
            .Prepend(key)
            .Select(CollapseSeparators)
            .Where(static target => target.Length > 0)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string Key { get; }

    /// <summary>
    /// Display labels, across the languages the providers publish in, that mean
    /// this genre. Used to match the free-text genres stored on catalog items;
    /// a provider's own query token lives in that provider's map instead.
    /// </summary>
    public IReadOnlyList<string> Aliases { get; }

    public static ApiContentGenre Action { get; } = new("action", "accion");

    public static ApiContentGenre Adventure { get; } =
        new("adventure", "aventura", "aventuras");

    public static ApiContentGenre Comedy { get; } = new("comedy", "comedia");

    public static ApiContentGenre Crime { get; } =
        new("crime", "crimen", "policiaco");

    public static ApiContentGenre Documentary { get; } =
        new("documentary", "documental");

    public static ApiContentGenre Drama { get; } = new("drama", "dramatico");

    public static ApiContentGenre Ecchi { get; } = new("ecchi");

    public static ApiContentGenre Family { get; } =
        new("family", "familia", "familiar");

    public static ApiContentGenre Fantasy { get; } =
        new("fantasy", "fantasia");

    public static ApiContentGenre Harem { get; } = new("harem");

    public static ApiContentGenre Historical { get; } =
        new("historical", "historia", "historico");

    public static ApiContentGenre Horror { get; } =
        new("horror", "terror");

    public static ApiContentGenre Isekai { get; } = new("isekai");

    public static ApiContentGenre Josei { get; } = new("josei");

    public static ApiContentGenre Magic { get; } = new("magic", "magia");

    public static ApiContentGenre MartialArts { get; } =
        new("martial-arts", "artes marciales");

    public static ApiContentGenre Mecha { get; } = new("mecha", "mechas");

    public static ApiContentGenre Military { get; } =
        new("military", "militar", "belica");

    public static ApiContentGenre Music { get; } =
        new("music", "musica", "musical");

    public static ApiContentGenre Mystery { get; } =
        new("mystery", "misterio");

    public static ApiContentGenre Psychological { get; } =
        new("psychological", "psicologico");

    public static ApiContentGenre Reality { get; } =
        new("reality", "reality show", "telerrealidad");

    public static ApiContentGenre Romance { get; } =
        new("romance", "romantico", "romantica");

    public static ApiContentGenre School { get; } =
        new("school", "escolares", "escolar", "vida escolar");

    public static ApiContentGenre ScienceFiction { get; } =
        new("science-fiction", "sci-fi", "scifi", "ciencia ficcion");

    public static ApiContentGenre Seinen { get; } = new("seinen");

    public static ApiContentGenre Shoujo { get; } = new("shoujo", "shojo");

    public static ApiContentGenre Shounen { get; } = new("shounen", "shonen");

    public static ApiContentGenre SliceOfLife { get; } = new(
        "slice-of-life",
        "cosas de la vida",
        "recuentos de la vida",
        "vida cotidiana");

    public static ApiContentGenre Sports { get; } =
        new("sports", "deportes", "deporte");

    public static ApiContentGenre Supernatural { get; } =
        new("supernatural", "sobrenatural");

    public static ApiContentGenre Suspense { get; } =
        new("suspense", "suspenso");

    public static ApiContentGenre Thriller { get; } = new("thriller");

    public static ApiContentGenre Tragedy { get; } =
        new("tragedy", "tragedia");

    public static ApiContentGenre War { get; } =
        new("war", "guerra");

    public static ApiContentGenre Western { get; } =
        new("western", "oeste");

    /// <summary>
    /// The keys the UI offers, in display order. A provider maps whatever subset
    /// it can serve and declines the rest; no provider is expected to cover all
    /// of them.
    /// </summary>
    public static IReadOnlyList<ApiContentGenre> All { get; } =
    [
        Action,
        Adventure,
        Comedy,
        Crime,
        Documentary,
        Drama,
        Ecchi,
        Family,
        Fantasy,
        Harem,
        Historical,
        Horror,
        Isekai,
        Josei,
        Magic,
        MartialArts,
        Mecha,
        Military,
        Music,
        Mystery,
        Psychological,
        Reality,
        Romance,
        School,
        ScienceFiction,
        Seinen,
        Shoujo,
        Shounen,
        SliceOfLife,
        Sports,
        Supernatural,
        Suspense,
        Thriller,
        Tragedy,
        War,
        Western
    ];

    // Declared after All because static initializers run in textual order.
    private static readonly FrozenDictionary<string, ApiContentGenre> KeyIndex =
        All.ToFrozenDictionary(
            static genre => genre.Key,
            StringComparer.Ordinal);

    public override string ToString() => Key;

    // The key is the identity. Aliases and the precomputed match targets are
    // data hanging off it, and the compiler-generated record equality would
    // otherwise compare those arrays by reference and call two genres with the
    // same key different.
    public bool Equals(ApiContentGenre? other) =>
        other is not null &&
        string.Equals(Key, other.Key, StringComparison.Ordinal);

    public override int GetHashCode() =>
        StringComparer.Ordinal.GetHashCode(Key);

    /// <summary>
    /// Whether a free-text genre label from a catalog item means this genre.
    /// Case and accents are ignored, and hyphens, underscores, slashes and
    /// runs of whitespace all read as a single separator, so "Ciencia-Ficción"
    /// and "ciencia ficcion" both match. The comparison is whole-label: a
    /// compound label such as "Acción y Aventura" is its own thing and matches
    /// neither action nor adventure unless listed as an alias.
    /// </summary>
    public bool MatchesLabel(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
        {
            return false;
        }

        var candidate = CollapseSeparators(label);
        if (candidate.Length == 0)
        {
            return false;
        }

        foreach (var target in _matchTargets)
        {
            // IgnoreNonSpace folds the accents away without needing a Unicode
            // normalization form, which browser WebAssembly is picky about.
            if (CultureInfo.InvariantCulture.CompareInfo.Compare(
                    candidate,
                    target,
                    CompareOptions.IgnoreCase |
                    CompareOptions.IgnoreNonSpace) == 0)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Whether any of an item's genre labels means this genre.
    /// </summary>
    public bool MatchesAny(IEnumerable<string>? labels)
    {
        if (labels is null)
        {
            return false;
        }

        foreach (var label in labels)
        {
            if (MatchesLabel(label))
            {
                return true;
            }
        }

        return false;
    }

    private static string CollapseSeparators(string value)
    {
        var builder = new StringBuilder(value.Length);
        var pendingSpace = false;
        foreach (var character in value)
        {
            if (char.IsWhiteSpace(character) ||
                character is '-' or '_' or '/' or '.')
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Parses a genre key. Unknown-but-well-formed keys are allowed so a
    /// provider map can carry a genre before <see cref="All"/> catches up.
    /// </summary>
    public static ApiContentGenre Parse(string key)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);
        return TryParse(key, out var genre)
            ? genre
            : throw new ArgumentException(
                "A genre key must be 2 to 40 characters of lowercase ASCII " +
                "letters or digits, joined by single hyphens.",
                nameof(key));
    }

    public static bool TryParse(
        string? key,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)]
        out ApiContentGenre? genre)
    {
        genre = null;
        if (string.IsNullOrWhiteSpace(key))
        {
            return false;
        }

        var normalized = key.Trim().ToLowerInvariant();
        if (!IsValidKey(normalized))
        {
            return false;
        }

        // Reuse the catalog instance when there is one so reference and value
        // equality agree for the keys the UI hands around.
        genre = KeyIndex.TryGetValue(normalized, out var known)
            ? known
            : new ApiContentGenre(normalized);
        return true;
    }

    private static bool IsValidKey(ReadOnlySpan<char> key)
    {
        if (key.Length is < MinimumKeyLength or > MaximumKeyLength ||
            key[0] == '-' ||
            key[^1] == '-')
        {
            return false;
        }

        var previousWasHyphen = false;
        foreach (var character in key)
        {
            if (character == '-')
            {
                if (previousWasHyphen)
                {
                    return false;
                }

                previousWasHyphen = true;
                continue;
            }

            if (!char.IsAsciiLetterLower(character) &&
                !char.IsAsciiDigit(character))
            {
                return false;
            }

            previousWasHyphen = false;
        }

        return true;
    }
}

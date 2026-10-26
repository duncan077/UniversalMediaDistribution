using System.Collections.Concurrent;
using System.Collections.Frozen;

namespace UniversalMediaDistribution.Catalog.ContentSources;

/// <summary>
/// Maps the shared genre keys onto one provider's own query tokens — a slug, a
/// numeric ID, or a remote database ID.
/// <para>
/// Tokens are never derived by slugifying a key: several real mappings are not
/// derivable that way, and an ID cannot be guessed at all. A map is either
/// written out explicitly or resolved from the provider's own published genre
/// list, matched by display label.
/// </para>
/// </summary>
public sealed class ApiContentGenreMap
{
    private readonly FrozenDictionary<string, string> _tokensByKey;

    private ApiContentGenreMap(
        IEnumerable<KeyValuePair<string, string>> tokensByKey) =>
        _tokensByKey = tokensByKey.ToFrozenDictionary(StringComparer.Ordinal);

    public static ApiContentGenreMap Empty { get; } =
        new([]);

    public int Count => _tokensByKey.Count;

    public IReadOnlyCollection<string> Keys => _tokensByKey.Keys;

    /// <summary>
    /// An explicit map, for providers whose tokens are stable and documented.
    /// </summary>
    public static ApiContentGenreMap Create(
        params (ApiContentGenre Genre, string Token)[] entries)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (genre, token) in entries)
        {
            ArgumentNullException.ThrowIfNull(genre);
            if (string.IsNullOrWhiteSpace(token))
            {
                throw new ArgumentException(
                    $"Genre '{genre.Key}' was mapped to a blank token.",
                    nameof(entries));
            }

            if (!tokens.TryAdd(genre.Key, token.Trim()))
            {
                throw new ArgumentException(
                    $"Genre '{genre.Key}' was mapped twice.",
                    nameof(entries));
            }
        }

        return new ApiContentGenreMap(tokens);
    }

    /// <summary>
    /// Resolves a map from the provider's own genre list by matching each
    /// upstream display label against the shared keys. Preferred wherever the
    /// tokens are opaque IDs, because a hardcoded ID silently rots when the
    /// provider renumbers its taxonomy.
    /// <para>
    /// Labels that match nothing are skipped rather than guessed at, so an
    /// unmapped genre makes the provider decline the filter instead of
    /// answering a broader question.
    /// </para>
    /// </summary>
    public static ApiContentGenreMap FromUpstreamLabels(
        IEnumerable<(string Label, string Token)> upstream)
    {
        ArgumentNullException.ThrowIfNull(upstream);
        var tokens = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (label, token) in upstream)
        {
            if (string.IsNullOrWhiteSpace(label) ||
                string.IsNullOrWhiteSpace(token))
            {
                continue;
            }

            foreach (var genre in ApiContentGenre.All)
            {
                if (genre.MatchesLabel(label))
                {
                    // First label wins: providers routinely list near-duplicate
                    // genres, and the earlier one is the canonical shelf.
                    tokens.TryAdd(genre.Key, token.Trim());
                    break;
                }
            }
        }

        return new ApiContentGenreMap(tokens);
    }

    public bool TryGetToken(
        ApiContentGenre? genre,
        [System.Diagnostics.CodeAnalysis.NotNullWhen(true)] out string? token)
    {
        if (genre is null)
        {
            token = null;
            return false;
        }

        return _tokensByKey.TryGetValue(genre.Key, out token);
    }

    public bool Contains(ApiContentGenre genre)
    {
        ArgumentNullException.ThrowIfNull(genre);
        return _tokensByKey.ContainsKey(genre.Key);
    }
}

/// <summary>
/// Holds a resolved genre map per provider root, so the taxonomy is fetched
/// once rather than on every filtered search. A failed resolution is not kept:
/// the next search retries instead of being stuck with an empty map.
/// </summary>
internal sealed class ApiContentGenreMapCache
{
    private readonly ConcurrentDictionary<string, Task<ApiContentGenreMap>>
        _byRoot = new(StringComparer.OrdinalIgnoreCase);

    public async ValueTask<ApiContentGenreMap> GetAsync(
        Uri root,
        Func<CancellationToken, Task<ApiContentGenreMap>> resolveAsync,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(root);
        ArgumentNullException.ThrowIfNull(resolveAsync);
        var key = root.AbsoluteUri;
        var pending = _byRoot.GetOrAdd(
            key,
            _ => resolveAsync(cancellationToken));
        try
        {
            return await pending;
        }
        catch
        {
            // A transient failure must not poison the cache for the session.
            _byRoot.TryRemove(new KeyValuePair<string, Task<ApiContentGenreMap>>(
                key,
                pending));
            throw;
        }
    }
}

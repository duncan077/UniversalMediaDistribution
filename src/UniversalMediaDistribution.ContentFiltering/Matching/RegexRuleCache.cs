using System.Collections.Concurrent;
using System.Text.RegularExpressions;

namespace UniversalMediaDistribution.ContentFiltering.Matching;

/// <summary>
/// Most URL rules never match. Use a small interpreter to reject those before
/// creating the much larger symbolic matcher. Only the symbolic matcher can
/// accept a match, preserving NonBacktracking's supported-pattern semantics.
/// </summary>
internal sealed class RegexRuleCache(Func<int, (string Pattern, bool MatchCase)> readPattern)
{
    internal const int Capacity = 16;
    private static readonly TimeSpan PrefilterTimeout = TimeSpan.FromMilliseconds(1);
    private readonly ConcurrentDictionary<int, Lazy<Regex?>> _prefilters = new();
    private readonly Dictionary<int, (Regex? Regex, LinkedListNode<int> Node)> _matchers = [];
    private readonly LinkedList<int> _recency = new();
    private readonly object _gate = new();

    internal int MatcherCount { get { lock (_gate) return _matchers.Count; } }
    internal int PrefilterCount => _prefilters.Count;

    internal bool IsMatch(int index, ReadOnlySpan<char> input)
    {
        Regex? matcher = null;
        var cacheHit = false;
        lock (_gate)
        {
            if (_matchers.TryGetValue(index, out var cached))
            {
                _recency.Remove(cached.Node);
                _recency.AddLast(cached.Node);
                matcher = cached.Regex;
                cacheHit = true;
            }
        }

        // Regex supports concurrent matches; don't hold the cache lock while matching.
        if (cacheHit) return matcher?.IsMatch(input) == true;

        var prefilter = _prefilters.GetOrAdd(index, static (key, cache) =>
            new Lazy<Regex?>(() => cache.CreatePrefilter(key)), this).Value;
        if (prefilter is null) return false;
        try
        {
            if (!prefilter.IsMatch(input)) return false;
        }
        catch (RegexMatchTimeoutException)
        {
            // A timeout is inconclusive, never an allow/block decision. The
            // linear-time matcher still evaluates the original rule below.
        }

        lock (_gate)
        {
            if (_matchers.TryGetValue(index, out var cached))
            {
                _recency.Remove(cached.Node);
                _recency.AddLast(cached.Node);
                matcher = cached.Regex;
            }
            else
            {
                // Serialize construction to avoid duplicate large allocations
                // when several WebView requests encounter the same rule.
                matcher = CreateMatcher(prefilter);
                if (matcher is null)
                {
                    // Unsupported patterns stay rejected after LRU eviction.
                    _prefilters[index] = new Lazy<Regex?>(static () => null);
                }
                if (_matchers.Count == Capacity)
                {
                    _matchers.Remove(_recency.First!.Value);
                    _recency.RemoveFirst();
                }
                _matchers.Add(index, (matcher, _recency.AddLast(index)));
            }
        }
        return matcher?.IsMatch(input) == true;
    }

    private Regex? CreatePrefilter(int index)
    {
        var (pattern, matchCase) = readPattern(index);
        var options = RegexOptions.CultureInvariant;
        if (!matchCase) options |= RegexOptions.IgnoreCase;
        try { return new Regex(pattern, options, PrefilterTimeout); }
        catch (ArgumentException) { return null; }
    }

    private static Regex? CreateMatcher(Regex prefilter)
    {
        try
        {
            return new Regex(prefilter.ToString(), prefilter.Options | RegexOptions.NonBacktracking);
        }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
    }
}

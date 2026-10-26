using UniversalMediaDistribution.ContentFiltering.Matching;

namespace UniversalMediaDistribution.ContentFiltering.Compilation;

/// <summary>
/// Splits a written pattern into anchors plus a body, and picks the token the
/// ruleset will index it under.
/// </summary>
internal static class PatternAnalyzer
{
    /// <summary>
    /// Tokens so common that indexing a rule under one would put it in a
    /// bucket half the web probes. A rule with any other option is indexed
    /// there instead; one of these is only a last resort.
    /// </summary>
    private static readonly HashSet<string> WeakTokens =
        new(StringComparer.Ordinal)
        {
            "com",
            "net",
            "org",
            "www",
            "http",
            "https",
            "html",
            "index",
            "js",
            "php",
            "png",
            "jpg",
            "gif",
            "css",
            "img",
            "cdn",
            "static",
            "assets",
            "content"
        };

    /// <summary>
    /// Removes the <c>||</c>, leading <c>|</c>, and trailing <c>|</c> markers,
    /// reporting them as flags. The <c>^</c> separators stay in the body — the
    /// matcher interprets those.
    /// </summary>
    public static ReadOnlySpan<char> Normalize(
        ReadOnlySpan<char> pattern,
        out RecordFlags anchors)
    {
        anchors = RecordFlags.None;
        if (pattern.StartsWith("||", StringComparison.Ordinal))
        {
            anchors |= RecordFlags.AnchorHost;
            pattern = pattern[2..];
        }
        else if (pattern.Length > 0 && pattern[0] == '|')
        {
            anchors |= RecordFlags.AnchorStart;
            pattern = pattern[1..];
        }

        if (pattern.Length > 0 && pattern[^1] == '|')
        {
            anchors |= RecordFlags.AnchorEnd;
            pattern = pattern[..^1];
        }

        return pattern;
    }

    /// <summary>
    /// Chooses the token hash to index a pattern under, or reports that there
    /// is none and the rule has to be evaluated for every request.
    ///
    /// Only boundary-aligned runs are eligible — a run whose preceding pattern
    /// character is a non-token character or an anchor. That is the property
    /// that lets <see cref="FilterTokenizer"/> guarantee a URL emits the token
    /// of every rule that could match it.
    /// </summary>
    public static bool TrySelectToken(
        ReadOnlySpan<char> pattern,
        RecordFlags anchors,
        out int tokenHash)
    {
        tokenHash = 0;
        var anchoredStart =
            (anchors & (RecordFlags.AnchorHost | RecordFlags.AnchorStart)) != 0;

        var bestStart = -1;
        var bestLength = 0;
        var bestIsWeak = true;

        var index = 0;
        while (index < pattern.Length)
        {
            if (!FilterTokenizer.IsTokenChar(pattern[index]))
            {
                index++;
                continue;
            }

            var start = index;
            while (index < pattern.Length &&
                FilterTokenizer.IsTokenChar(pattern[index]))
            {
                index++;
            }

            var aligned = start == 0
                ? anchoredStart
                : pattern[start - 1] != '*' &&
                    !FilterTokenizer.IsTokenChar(pattern[start - 1]);
            if (!aligned)
            {
                continue;
            }

            var length = Math.Min(
                index - start,
                FilterTokenizer.MaxTokenLength);
            if (length < FilterTokenizer.MinTokenLength)
            {
                continue;
            }

            var isWeak = IsWeak(pattern.Slice(start, length));
            if (bestStart >= 0 && !IsBetter(isWeak, length, bestIsWeak, bestLength))
            {
                continue;
            }

            bestStart = start;
            bestLength = length;
            bestIsWeak = isWeak;
        }

        if (bestStart < 0)
        {
            return false;
        }

        tokenHash = FilterTokenizer.Hash(pattern.Slice(bestStart, bestLength));
        return true;
    }

    private static bool IsBetter(
        bool isWeak,
        int length,
        bool bestIsWeak,
        int bestLength) =>
        isWeak != bestIsWeak ? bestIsWeak : length > bestLength;

    private static readonly HashSet<string>.AlternateLookup<ReadOnlySpan<char>>
        WeakLookup = WeakTokens.GetAlternateLookup<ReadOnlySpan<char>>();

    private static bool IsWeak(ReadOnlySpan<char> token)
    {
        Span<char> lowered = stackalloc char[FilterTokenizer.MaxTokenLength];
        var length = token.ToLowerInvariant(lowered);
        return length > 0 && WeakLookup.Contains(lowered[..length]);
    }
}

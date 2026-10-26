using UniversalMediaDistribution.ContentFiltering.Compilation;

namespace UniversalMediaDistribution.ContentFiltering.Matching;

/// <summary>
/// Matches a compiled pattern against a URL.
///
/// Patterns are stored as ASCII bytes and compared straight against the URL's
/// characters, so a match costs no conversion and no allocation. The compiler
/// refuses non-ASCII patterns for exactly this reason.
/// </summary>
internal static class PatternMatcher
{
    public static bool Matches(
        ReadOnlySpan<byte> pattern,
        ReadOnlySpan<char> url,
        in UrlLayout layout,
        RecordFlags flags)
    {
        if (pattern.IsEmpty)
        {
            return false;
        }

        var matchCase = (flags & RecordFlags.MatchCase) != 0;
        var anchorEnd = (flags & RecordFlags.AnchorEnd) != 0;

        if ((flags & RecordFlags.AnchorStart) != 0)
        {
            return MatchAt(pattern, url, 0, anchorEnd, matchCase);
        }

        if ((flags & RecordFlags.AnchorHost) != 0)
        {
            // "||" anchors to a domain boundary: the start of the host, or any
            // point just past a dot inside it. Nothing else, which is what
            // keeps "||doubleclick.net^" off "doubleclick.net.example.org".
            if (MatchAt(pattern, url, layout.HostStart, anchorEnd, matchCase))
            {
                return true;
            }

            for (var index = layout.HostStart; index < layout.HostEnd; index++)
            {
                if (url[index] == '.' &&
                    MatchAt(pattern, url, index + 1, anchorEnd, matchCase))
                {
                    return true;
                }
            }

            return false;
        }

        for (var start = 0; start <= url.Length; start++)
        {
            if (MatchAt(pattern, url, start, anchorEnd, matchCase))
            {
                return true;
            }
        }

        return false;
    }

    private static bool MatchAt(
        ReadOnlySpan<byte> pattern,
        ReadOnlySpan<char> url,
        int start,
        bool anchorEnd,
        bool matchCase)
    {
        var urlIndex = start;
        for (var index = 0; index < pattern.Length; index++)
        {
            var current = (char)pattern[index];
            if (current == '*')
            {
                var rest = pattern[(index + 1)..];
                if (rest.IsEmpty)
                {
                    return true;
                }

                for (var next = urlIndex; next <= url.Length; next++)
                {
                    if (MatchAt(rest, url, next, anchorEnd, matchCase))
                    {
                        return true;
                    }
                }

                return false;
            }

            if (current == '^')
            {
                // A separator also matches the end of the URL, so a rule
                // written "||host^" still matches "https://host".
                if (urlIndex == url.Length)
                {
                    continue;
                }

                if (!IsSeparator(url[urlIndex]))
                {
                    return false;
                }

                urlIndex++;
                continue;
            }

            if (urlIndex >= url.Length ||
                !Equal(current, url[urlIndex], matchCase))
            {
                return false;
            }

            urlIndex++;
        }

        return !anchorEnd || urlIndex == url.Length;
    }

    /// <summary>
    /// The separator set filter syntax defines: anything that is not a letter,
    /// a digit, or one of <c>_ - . %</c>.
    /// </summary>
    private static bool IsSeparator(char value) =>
        !char.IsAsciiLetterOrDigit(value) &&
        value is not ('_' or '-' or '.' or '%');

    private static bool Equal(char pattern, char url, bool matchCase) =>
        matchCase
            ? pattern == url
            : Lower(pattern) == Lower(url);

    private static char Lower(char value) =>
        value is >= 'A' and <= 'Z' ? (char)(value + 32) : value;
}

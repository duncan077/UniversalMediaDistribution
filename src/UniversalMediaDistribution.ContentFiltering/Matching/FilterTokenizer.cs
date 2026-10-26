namespace UniversalMediaDistribution.ContentFiltering.Matching;

/// <summary>
/// Turns URLs and filter patterns into the token hashes that index the
/// ruleset. Matching a request means probing only the buckets its own tokens
/// name, which is what keeps a hundred thousand rules off the hot path.
///
/// The two sides have to agree, and the agreement is this: a pattern may only
/// claim a token that starts at a boundary — the pattern character before it is
/// a non-token character, or an anchor. A URL then emits every prefix of every
/// token run, from <see cref="MinTokenLength"/> up to
/// <see cref="MaxTokenLength"/> characters. A boundary-aligned pattern token
/// therefore always appears among the URL's tokens whenever the pattern can
/// match at all, so a bucket probe can never miss a rule that would have
/// matched.
/// </summary>
internal static class FilterTokenizer
{
    public const int MinTokenLength = 2;

    /// <summary>
    /// Longest prefix a URL run contributes. Pattern tokens are truncated to
    /// the same length, which is what makes long patterns such as
    /// <c>||googlesyndication.com^</c> indexable.
    /// </summary>
    public const int MaxTokenLength = 8;

    private const uint Seed = 2166136261u;
    private const uint Prime = 16777619u;

    /// <summary>
    /// Characters that make up a token. Deliberately narrow: letters, digits,
    /// and the percent sign that survives URL encoding.
    /// </summary>
    public static bool IsTokenChar(char value) =>
        (uint)(value - '0') <= 9 ||
        (uint)((value | 0x20) - 'a') <= 25 ||
        value == '%';

    public static int Hash(ReadOnlySpan<char> token)
    {
        var hash = Seed;
        for (var index = 0; index < token.Length; index++)
        {
            hash = Fold(hash, token[index]);
        }

        return unchecked((int)hash);
    }

    public static UrlTokenEnumerator EnumerateUrlTokens(
        ReadOnlySpan<char> url) => new(url);

    private static uint Fold(uint hash, char value)
    {
        var lower = (uint)(value is >= 'A' and <= 'Z' ? value + 32 : value);
        return (hash ^ lower) * Prime;
    }

    /// <summary>
    /// Walks a URL and yields the hash of every token prefix. Allocation-free
    /// by construction; the caller keeps the span.
    /// </summary>
    internal ref struct UrlTokenEnumerator
    {
        private readonly ReadOnlySpan<char> _url;
        private int _index;
        private int _runIndex;
        private int _runEnd;
        private int _taken;
        private uint _hash;

        internal UrlTokenEnumerator(ReadOnlySpan<char> url)
        {
            _url = url;
            _index = 0;
            _runIndex = 0;
            _runEnd = 0;
            _taken = 0;
            _hash = Seed;
            Current = 0;
        }

        public int Current { get; private set; }

        public bool MoveNext()
        {
            while (true)
            {
                if (_runIndex < _runEnd && _taken < MaxTokenLength)
                {
                    _hash = Fold(_hash, _url[_runIndex]);
                    _runIndex++;
                    _taken++;
                    if (_taken >= MinTokenLength)
                    {
                        Current = unchecked((int)_hash);
                        return true;
                    }

                    continue;
                }

                if (!TryStartRun())
                {
                    return false;
                }
            }
        }

        private bool TryStartRun()
        {
            while (_index < _url.Length && !IsTokenChar(_url[_index]))
            {
                _index++;
            }

            if (_index >= _url.Length)
            {
                return false;
            }

            var start = _index;
            while (_index < _url.Length && IsTokenChar(_url[_index]))
            {
                _index++;
            }

            _runIndex = start;
            _runEnd = _index;
            _taken = 0;
            _hash = Seed;
            return true;
        }
    }
}

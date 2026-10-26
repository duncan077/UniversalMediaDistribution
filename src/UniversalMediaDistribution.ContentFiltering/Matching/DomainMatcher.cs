namespace UniversalMediaDistribution.ContentFiltering.Matching;

/// <summary>
/// Evaluates the pipe- or comma-separated domain lists that <c>$domain=</c>,
/// <c>$to=</c>, <c>$denyallow=</c>, and cosmetic scopes are written with,
/// straight out of the pooled bytes.
/// </summary>
internal static class DomainMatcher
{
    /// <summary>
    /// Applies <c>$domain=</c> semantics: a negated entry that matches vetoes
    /// the rule, and if the list names any positive domain then one of them
    /// has to match.
    /// </summary>
    public static bool ScopeApplies(
        ReadOnlySpan<byte> list,
        ReadOnlySpan<char> host)
    {
        if (list.IsEmpty)
        {
            return true;
        }

        var hasPositive = false;
        var matchedPositive = false;
        foreach (var entry in Entries(list))
        {
            var negated = !entry.IsEmpty && entry[0] == (byte)'~';
            var domain = negated ? entry[1..] : entry;
            if (domain.IsEmpty || domain.Length == 1 && domain[0] == (byte)'*')
            {
                hasPositive = hasPositive || !negated;
                matchedPositive = matchedPositive || !negated;
                continue;
            }

            var matches = Covers(domain, host);
            if (negated)
            {
                if (matches)
                {
                    return false;
                }

                continue;
            }

            hasPositive = true;
            matchedPositive = matchedPositive || matches;
        }

        return !hasPositive || matchedPositive;
    }

    /// <summary>
    /// True when any positive entry covers the host. Used by
    /// <c>$denyallow=</c>, where a match means the rule steps aside.
    /// </summary>
    public static bool ContainsHost(
        ReadOnlySpan<byte> list,
        ReadOnlySpan<char> host)
    {
        foreach (var entry in Entries(list))
        {
            if (!entry.IsEmpty &&
                entry[0] != (byte)'~' &&
                Covers(entry, host))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// True when <paramref name="host"/> is the domain itself or one of its
    /// subdomains.
    /// </summary>
    public static bool Covers(ReadOnlySpan<byte> domain, ReadOnlySpan<char> host)
    {
        if (domain.IsEmpty || host.Length < domain.Length)
        {
            return false;
        }

        if (host.Length > domain.Length &&
            host[host.Length - domain.Length - 1] != '.')
        {
            return false;
        }

        var tail = host[^domain.Length..];
        for (var index = 0; index < domain.Length; index++)
        {
            var expected = domain[index];
            if (expected > 127 || Lower((char)expected) != Lower(tail[index]))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// True when the request host is the document host or a subdomain of it.
    /// This is the same-site test the embedded player has always used, and it
    /// is deliberately narrower than a registrable-domain comparison.
    /// </summary>
    public static bool IsSameSiteWithDocument(
        ReadOnlySpan<char> host,
        ReadOnlySpan<char> documentHost)
    {
        if (host.IsEmpty || documentHost.IsEmpty)
        {
            return false;
        }

        if (host.Equals(documentHost, StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return host.Length > documentHost.Length &&
            host[host.Length - documentHost.Length - 1] == '.' &&
            host[^documentHost.Length..].Equals(
                documentHost,
                StringComparison.OrdinalIgnoreCase);
    }

    public static EntryEnumerator Entries(ReadOnlySpan<byte> list) =>
        new(list);

    private static char Lower(char value) =>
        value is >= 'A' and <= 'Z' ? (char)(value + 32) : value;

    /// <summary>
    /// Walks a domain list split on <c>|</c> or <c>,</c> — filter syntax uses
    /// both, depending on the option.
    /// </summary>
    internal ref struct EntryEnumerator(ReadOnlySpan<byte> list)
    {
        private ReadOnlySpan<byte> _remaining = list;
        private bool _done;

        public ReadOnlySpan<byte> Current { get; private set; }

        public readonly EntryEnumerator GetEnumerator() => this;

        public bool MoveNext()
        {
            if (_done)
            {
                return false;
            }

            var index = _remaining.IndexOfAny((byte)'|', (byte)',');
            if (index < 0)
            {
                Current = _remaining;
                _done = true;
                return !Current.IsEmpty;
            }

            Current = _remaining[..index];
            _remaining = _remaining[(index + 1)..];
            return true;
        }
    }
}

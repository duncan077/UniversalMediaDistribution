namespace UniversalMediaDistribution.ContentFiltering.Matching;

/// <summary>
/// The offsets a matcher needs from a URL, found without allocating and
/// without <see cref="Uri"/>. Every field is an index into the span the caller
/// still owns.
/// </summary>
internal readonly struct UrlLayout
{
    private UrlLayout(
        bool isSupported,
        int hostStart,
        int hostEnd,
        int authorityEnd)
    {
        IsSupported = isSupported;
        HostStart = hostStart;
        HostEnd = hostEnd;
        AuthorityEnd = authorityEnd;
    }

    /// <summary>
    /// False for schemes filter lists do not describe (<c>data:</c>,
    /// <c>blob:</c>, <c>about:</c>, and anything else). Those are always
    /// allowed rather than guessed at.
    /// </summary>
    public bool IsSupported { get; }

    /// <summary>Index of the first host character, past any user info.</summary>
    public int HostStart { get; }

    /// <summary>Index just past the last host character, before any port.</summary>
    public int HostEnd { get; }

    /// <summary>Index of the path, query, or fragment that ends the authority.</summary>
    public int AuthorityEnd { get; }

    public static UrlLayout Unsupported { get; } = new(false, 0, 0, 0);

    public ReadOnlySpan<char> Host(ReadOnlySpan<char> url) =>
        url[HostStart..HostEnd];

    public static UrlLayout Parse(ReadOnlySpan<char> url)
    {
        var separator = url.IndexOf("://", StringComparison.Ordinal);
        if (separator <= 0 || !IsFilterableScheme(url[..separator]))
        {
            return Unsupported;
        }

        var authorityStart = separator + 3;
        if (authorityStart >= url.Length)
        {
            return Unsupported;
        }

        var authority = url[authorityStart..];
        var authorityLength = authority.IndexOfAny('/', '?', '#');
        if (authorityLength < 0)
        {
            authorityLength = authority.Length;
        }

        authority = authority[..authorityLength];
        var authorityEnd = authorityStart + authorityLength;

        // Credentials in the authority are rare but legal, and the host is what
        // every rule is written against.
        var credentials = authority.LastIndexOf('@');
        var hostStart = authorityStart +
            (credentials >= 0 ? credentials + 1 : 0);
        var host = url[hostStart..authorityEnd];
        if (host.IsEmpty)
        {
            return Unsupported;
        }

        var hostEnd = authorityEnd;
        if (host[0] == '[')
        {
            // An IPv6 literal keeps its brackets out of the host so that a
            // rule written against the address text still matches.
            var close = host.IndexOf(']');
            if (close < 0)
            {
                return Unsupported;
            }

            hostStart += 1;
            hostEnd = hostStart + close - 1;
        }
        else
        {
            var port = host.IndexOf(':');
            if (port >= 0)
            {
                hostEnd = hostStart + port;
            }
        }

        return hostEnd <= hostStart
            ? Unsupported
            : new UrlLayout(true, hostStart, hostEnd, authorityEnd);
    }

    private static bool IsFilterableScheme(ReadOnlySpan<char> scheme) =>
        scheme.Equals("http", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("https", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("ws", StringComparison.OrdinalIgnoreCase) ||
        scheme.Equals("wss", StringComparison.OrdinalIgnoreCase);
}
